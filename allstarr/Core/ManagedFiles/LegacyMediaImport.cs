using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Jobs;
using allstarr.Core.Matching;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.ManagedFiles;

public sealed record LegacyMediaImportPayload(Guid ImportId);

public sealed class LegacyMediaImport(IDbContextFactory<AllstarrDbContext> factory, IServiceScopeFactory scopes,
    DurableJobQueue jobs, ITrackIdentityService identities, IPlatformClock clock, LegacyMediaInventory inventory)
{
    public const string JobType = "configuration.legacy-media";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal Task<DurableJobEnqueueResult> StageAsync(AllstarrDbContext db, Guid importId, Guid administrator,
        string correlationId, CancellationToken token) => jobs.EnqueueInExistingTransactionAsync(db,
            new DurableJobEnqueueRequest<LegacyMediaImportPayload>(JobType, Key(importId), new(importId), administrator,
                MaxAttempts: 12, CorrelationId: correlationId), token);

    public async Task<DurableJobCompletion> ExecuteAsync(DurableJobExecutionContext execution, CancellationToken token)
    {
        var claim = execution.Claim;
        var payload = claim.Payload.Deserialize<LegacyMediaImportPayload>();
        if (payload == null || payload.ImportId == Guid.Empty) return DurableJobCompletion.Failure("legacy_media_invalid", "Preview the legacy files again.");
        LegacyMediaPlan plan;
        await using (var db = await factory.CreateDbContextAsync(token))
        {
            await RequireCurrentAsync(db, claim, payload.ImportId, token);
            var receipt = await db.LegacyEnvImports.AsNoTracking().SingleAsync(item => item.Id == payload.ImportId, token);
            using var provenance = JsonDocument.Parse(receipt.ProvenanceJson);
            plan = provenance.RootElement.GetProperty("media").Deserialize<LegacyMediaPlan>(JsonOptions)
                ?? throw new InvalidOperationException("The saved media inventory is unavailable.");
        }
        var completed = 0;
        var failed = 0;
        foreach (var snapshot in plan.Files)
        {
            token.ThrowIfCancellationRequested();
            try { await AdoptAsync(claim, payload.ImportId, snapshot, token); }
            catch (Exception error) when (error is IOException or ArgumentException)
            { failed++; }
            await execution.ReportProgressAsync(new("adopting", "Verifying original files without moving or changing them.",
                ++completed, plan.Files.Count), token);
        }
        return failed == 0 ? DurableJobCompletion.Success() : DurableJobCompletion.Failure("legacy_media_needs_review",
            $"{failed} files changed or could not be verified. Check the mounts and create a new preview; verified originals stay protected.");
    }

    private async Task AdoptAsync(DurableJobClaim claim, Guid importId, LegacyMediaFileSnapshot snapshot, CancellationToken token)
    {
        if (!inventory.IsAllowed(snapshot.RootPath)) throw new IOException("The legacy root is no longer mounted for adoption.");
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AllstarrDbContext>();
        var files = scope.ServiceProvider.GetRequiredService<FilePlacementService>();
        await RequireCurrentAsync(db, claim, importId, token);
        var prior = await (from adoption in db.Set<LegacyMediaAdoptionRecord>().AsNoTracking()
                           join file in db.ManagedFiles.AsNoTracking() on adoption.ManagedFileId equals file.Id
                           where file.CanonicalPath == snapshot.Path
                           select file).SingleOrDefaultAsync(token);
        if (prior != null)
        {
            if (prior.RootId != snapshot.RootId || prior.TargetRootPath != snapshot.RootPath || prior.Length != snapshot.Length)
                throw new IOException("The legacy path has a different adoption record.");
            files.VerifyOwnedFileIdentity(EfManagedFileOwnershipStore.Map(prior)!, token);
            return;
        }
        files.ValidateLegacySnapshot(snapshot);
        var prepared = await files.PrepareLegacyAdoptionAsync(snapshot, clock.UtcNow, token);
        Guid? recordingId = null;
        if (snapshot.ProviderId != null && snapshot.ExternalId != null)
        {
            var actor = new ProviderActorContext(ProviderActorKind.SystemJob, null, durableJobId: claim.JobId, actingForUserId: claim.OwnerUserId);
            var context = new ProviderExecutionContext(actor, snapshot.ProviderId, null,
                new(new(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, false), ProviderExplicitContentPolicy.Allow, true, true, true),
                "legacy-media-adoption", claim.CorrelationId, clock.UtcNow.AddMinutes(2), token);
            recordingId = await identities.RememberSourceAsync(context,
                new(snapshot.ProviderId, ProviderResourceKind.Track, snapshot.ExternalId),
                $"ext-{snapshot.ProviderId}-song-{snapshot.ExternalId}", ProviderIdentityScope.Catalog, cancellationToken: token);
        }
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await RequireCurrentAsync(db, claim, importId, token);
        files.ValidateLegacySnapshot(snapshot);
        var fileRecord = await files.AdoptLegacyFileAsync(prepared, snapshot, $"legacy-original:{prepared.Id:N}", token);
        if (!await db.Set<LegacyMediaAdoptionRecord>().AnyAsync(item => item.ManagedFileId == fileRecord.Id, token))
        {
            db.Add(new LegacyMediaAdoptionRecord
            {
                Id = Guid.CreateVersion7(),
                ImportId = importId,
                ManagedFileId = fileRecord.Id,
                CanonicalRecordingId = recordingId,
                ProviderId = snapshot.ProviderId,
                ExternalId = snapshot.ExternalId,
                Kept = snapshot.Kept,
                MetadataJson = JsonSerializer.Serialize(snapshot.Metadata),
                CreatedAt = clock.UtcNow
            });
            await db.SaveChangesAsync(token);
        }
        await RequireCurrentAsync(db, claim, importId, token);
        files.ValidateLegacySnapshot(snapshot);
        await transaction.CommitAsync(token);
    }

    private static async Task RequireCurrentAsync(AllstarrDbContext db, DurableJobClaim claim, Guid importId, CancellationToken token)
    {
        var valid = await (from job in db.Jobs.AsNoTracking()
                           join user in db.Users.AsNoTracking() on job.OwnerUserId equals (Guid?)user.Id
                           join receipt in db.LegacyEnvImports.AsNoTracking() on (Guid?)user.Id equals receipt.ActorUserId
                           where job.Id == claim.JobId && job.Type == JobType && job.IdempotencyKey == Key(importId) &&
                                 job.State == DurableJobState.Running && job.AttemptCount == claim.AttemptNumber &&
                                 job.CancellationRequestedAt == null && job.OwnerUserId == claim.OwnerUserId &&
                                 job.ProviderAccountId == null && job.ProviderCapability == null &&
                                 receipt.Id == importId && user.Enabled && user.IsAdmin
                           select job.Id).AnyAsync(token);
        if (!valid) throw new UnauthorizedAccessException("The administrator or import execution is no longer current.");
    }

    private static string Key(Guid importId) => $"legacy-media:{importId:N}";
}

public sealed class LegacyMediaImportJobHandler(LegacyMediaImport import) : IDurableJobHandler
{
    public string JobType => LegacyMediaImport.JobType;
    public Task<DurableJobCompletion> ExecuteAsync(DurableJobExecutionContext context, CancellationToken cancellationToken) =>
        import.ExecuteAsync(context, cancellationToken);
}
