using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Jobs;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.ManagedFiles;

public sealed record CachedKeepSource(Guid MappingId, long Revision);

public sealed record RecordingKeepJobPayload(Guid RecordingId, string ProviderId, string ExternalId,
    string Protocol, string BackendInstanceId, string BackendPrincipalId, KeptRecordingMetadata Metadata);

public sealed partial class RecordingKeptFiles
{
    public const string ExplicitKeepJobType = "recording.keep";

    public async Task<DurableJobEnqueueResult> RequestKeepAsync(ProtocolExecutionContext execution,
        ProviderExternalResourceId source, KeptRecordingMetadata metadata, Guid requestId,
        CancellationToken cancellationToken = default, CachedKeepSource? cachedSource = null)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (jobs == null) throw new InvalidOperationException("The kept track queue is unavailable.");
        source.RequireOwner(source.ProviderId, ProviderResourceKind.Track);
        if (requestId == Guid.Empty) throw new ArgumentException("A request ID is required.");
        _ = SerializeMetadata(metadata);
        var actor = execution.RequireActor();
        var recordingId = await ResolveKeptRecordingIdAsync(actor,
            $"ext-{source.ProviderId}-song-{source.Value}", cancellationToken)
            ?? throw new InvalidOperationException("The track's recording identity is not ready. Refresh the track and try again.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var user = await RequireKeepAdministratorAsync(db, execution, cancellationToken);
        if (cachedSource != null)
            await ValidateCachedKeepSourceAsync(db, user.Id, source, cachedSource, cancellationToken);
        if (await ResolveKeptRecordingIdAsync(actor, $"ext-{source.ProviderId}-song-{source.Value}", cancellationToken) != recordingId)
            throw new InvalidOperationException("The track's recording identity changed. Refresh the track and try again.");
        var payload = new RecordingKeepJobPayload(recordingId, source.ProviderId, source.Value,
            user.BackendType, user.BackendInstanceId, user.BackendPrincipalId, metadata);
        var result = await jobs.EnqueueInExistingTransactionAsync(db,
            new DurableJobEnqueueRequest<RecordingKeepJobPayload>(ExplicitKeepJobType,
                $"keep:{requestId:N}", payload, user.Id, MaxAttempts: 12, MaxDeferrals: 100,
                CorrelationId: execution.CorrelationId), cancellationToken);
        if (result.Created)
        {
            var fileId = await (from route in db.Set<RecordingKeptRouteEntity>().AsNoTracking()
                                join recording in db.CanonicalRecordings.AsNoTracking() on route.CanonicalRecordingId equals recording.Id
                                join file in db.ManagedFiles.AsNoTracking() on route.ManagedFileId equals file.Id
                                where (recording.Id == recordingId || recording.MergedIntoId == recordingId) && file.RemovedAt == null
                                orderby route.CanonicalRecordingId == recordingId descending, route.CreatedAt, route.ManagedFileId
                                select file.Id).FirstOrDefaultAsync(cancellationToken);
            if (fileId != Guid.Empty)
            {
                await new EfManagedFileOwnershipStore(db).StageReferenceAsync(fileId,
                    ExplicitKeepKey(user.Id, payload.RecordingId), clock.UtcNow, cancellationToken);
                await CancelCleanupAsync(db, fileId, clock.UtcNow, cancellationToken);
            }
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<KeptRecordingFile?> RetainRequestedKeepAsync(DurableJobClaim claim,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AllstarrDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var proof = await FindKeepAsync(db, claim.JobId, claim.OwnerUserId, claim.AttemptNumber, cancellationToken)
            ?? throw Denied();
        var candidate = await FindCandidateAsync(db, scope.ServiceProvider.GetRequiredService<FilePlacementService>(),
            scope.ServiceProvider.GetRequiredService<IManagedFileRemovalStore>(), proof.RecordingId, true, cancellationToken);
        if (candidate == null) return null;
        await new EfManagedFileOwnershipStore(db).StageReferenceAsync(candidate.File.Id,
            ExplicitKeepKey(proof.OwnerUserId, proof.Payload.RecordingId), clock.UtcNow, cancellationToken);
        await CancelCleanupAsync(db, candidate.File.Id, clock.UtcNow, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var result = ToResult(proof.RecordingId, candidate.File, candidate.MimeType);
        await EnsureLyricsAsync(result, proof.OwnerUserId, claim.CorrelationId, cancellationToken);
        return result;
    }

    public async Task<KeptRecordingFile> PublishRequestedKeepAsync(DurableJobClaim claim,
        VerifiedProviderDownloadArtifact artifact, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var proof = await FindKeepAsync(db, claim.JobId, claim.OwnerUserId, claim.AttemptNumber, cancellationToken)
            ?? throw Denied();
        var metadata = proof.Payload.Metadata;
        return await PublishAsync(new(proof.RecordingId, proof.OwnerUserId, claim.JobId, artifact.Id, artifact.ProviderId,
            ExplicitKeepKey(proof.OwnerUserId, proof.Payload.RecordingId),
            new(metadata.Title, metadata.Artists.FirstOrDefault() ?? "Unknown artist", metadata.Album,
                metadata.AlbumArtist, Track: metadata.Track))
        { AttemptNumber = claim.AttemptNumber, Metadata = metadata }, cancellationToken);
    }

    public async Task<int> ReleaseKeepForFileAsync(ProtocolExecutionContext execution, Guid managedFileId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var user = await RequireKeepAdministratorAsync(db, execution, cancellationToken);
        var file = await db.ManagedFiles.AsNoTracking().SingleOrDefaultAsync(item => item.Id == managedFileId &&
            item.RemovedAt == null && item.IsManaged && item.OwnerUserId == null && item.SourceJobId == null &&
            item.RootId == placement.RootId && item.ScopeKey == ManagedFileScopeKey.Create(null, placement.RootId), cancellationToken)
            ?? throw Denied();
        var prefix = $"keep:user:{user.Id:N}:";
        var keys = await db.ManagedFileReferences.AsNoTracking().Where(item => item.ManagedFileId == file.Id &&
            item.ReleasedAt == null && item.ReferenceKey.StartsWith(prefix)).Select(item => item.ReferenceKey).ToArrayAsync(cancellationToken);
        var now = clock.UtcNow;
        var operation = Guid.CreateVersion7();
        foreach (var key in keys)
            await StageReleaseInterestAsync(db, key, user.Id, operation, now, execution.CorrelationId, cancellationToken, file.Id);
        var pending = await db.Jobs.Where(item => item.Type == ExplicitKeepJobType && item.OwnerUserId == user.Id &&
            (item.State == DurableJobState.Pending || item.State == DurableJobState.RetryScheduled || item.State == DurableJobState.Running))
            .ToArrayAsync(cancellationToken);
        foreach (var job in pending)
        {
            var payload = ReadKeepPayload(job.PayloadJson);
            if (payload == null || !keys.Contains(ExplicitKeepKey(user.Id, payload.RecordingId), StringComparer.Ordinal)) continue;
            job.CancellationRequestedAt = now;
            if (job.State != DurableJobState.Running)
            {
                job.State = DurableJobState.Cancelled;
                job.CompletedAt = now;
            }
            job.UpdatedAt = now;
            job.Revision++;
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return keys.Length;
    }

    private async Task AuthorizeExplicitKeepPublicationAsync(AllstarrDbContext db, DurableJobRecord job,
        KeptRecordingPublication request, CancellationToken cancellationToken)
    {
        var reserved = request.ReferenceKey.StartsWith("keep:user:", StringComparison.Ordinal);
        var payload = job.Type == ExplicitKeepJobType ? ReadKeepPayload(job.PayloadJson) : null;
        if (!reserved && payload == null) return;
        var proof = await FindKeepAsync(db, job.Id, request.InitiatingUserId, request.AttemptNumber, cancellationToken);
        if (!reserved || proof == null || request.ReferenceKey != ExplicitKeepKey(proof.OwnerUserId, proof.Payload.RecordingId))
            throw Denied();
        var requested = await identities.ResolveRecordingAsync(proof.Actor, request.RecordingId.ToString("D"), cancellationToken);
        if (requested?.Id != proof.RecordingId) throw Denied();
    }

    private async Task<KeepProof?> FindKeepAsync(AllstarrDbContext db, Guid jobId, Guid? ownerUserId,
        int attempt, CancellationToken cancellationToken)
    {
        var job = await db.Jobs.AsNoTracking().SingleOrDefaultAsync(item => item.Id == jobId && item.Type == ExplicitKeepJobType &&
            item.OwnerUserId == ownerUserId && item.State == DurableJobState.Running && item.AttemptCount == attempt &&
            item.CancellationRequestedAt == null, cancellationToken);
        if (job == null || ownerUserId == null) return null;
        var payload = ReadKeepPayload(job.PayloadJson);
        if (payload == null) return null;
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == ownerUserId && item.Enabled && item.IsAdmin &&
            item.BackendType == payload.Protocol && item.BackendInstanceId == payload.BackendInstanceId &&
            item.BackendPrincipalId == payload.BackendPrincipalId, cancellationToken);
        if (user == null) return null;
        var actor = new ProviderActorContext(ProviderActorKind.User, user.Id,
            new(user.BackendType, user.BackendInstanceId, user.BackendPrincipalId));
        var recording = await identities.ResolveRecordingAsync(actor, payload.RecordingId.ToString("D"), cancellationToken);
        var source = await identities.ResolveRecordingAsync(actor,
            $"ext-{payload.ProviderId}-song-{payload.ExternalId}", cancellationToken);
        if (recording == null || source != null && source.Id != recording.Id) return null;
        return new(payload, user.Id, recording.Id, actor);
    }

    internal static RecordingKeepJobPayload? ReadKeepPayload(string json)
    {
        RecordingKeepJobPayload? payload;
        try { payload = JsonSerializer.Deserialize<RecordingKeepJobPayload>(json); }
        catch (JsonException) { return null; }
        return payload is
        {
            RecordingId: var id, ProviderId.Length: > 0 and <= 100,
            ExternalId.Length: > 0 and <= 500, Protocol.Length: > 0 and <= 30, BackendInstanceId.Length: > 0 and <= 200,
            BackendPrincipalId.Length: > 0 and <= 500, Metadata: not null
        } && id != Guid.Empty && ValidMetadata(payload.Metadata)
            ? payload : null;
    }

    private static async Task ValidateCachedKeepSourceAsync(AllstarrDbContext db, Guid owner,
        ProviderExternalResourceId source, CachedKeepSource proof, CancellationToken cancellationToken)
    {
        var mapping = await db.DownloadedSongMappings.AsNoTracking().SingleOrDefaultAsync(item =>
            item.Id == proof.MappingId && item.Revision == proof.Revision && item.ProviderId == source.ProviderId &&
            item.ExternalId == source.Value, cancellationToken)
            ?? throw new InvalidOperationException("The cached track changed. Refresh the list and try again.");
        if (!DownloadedSongMappingScope.TryResolve(mapping, out var scope))
            throw new InvalidOperationException("The cached track's account scope changed.");
        if (mapping.ProviderAccountId is { } accountId)
        {
            var account = await db.ProviderAccounts.AsNoTracking().SingleOrDefaultAsync(item => item.Id == accountId &&
                item.ProviderId == source.ProviderId, cancellationToken);
            if (account == null || account.OwnerUserId != null && account.OwnerUserId != owner) throw Denied();
            if (scope.Representation == DownloadedSongRepresentation.Original && scope.SourceAccountRevision != account.Revision)
                throw new InvalidOperationException("The cached track's account scope changed.");
        }
    }

    private static async Task<UserRecord> RequireKeepAdministratorAsync(AllstarrDbContext db,
        ProtocolExecutionContext execution, CancellationToken cancellationToken)
    {
        var principal = execution.Principal;
        return principal is { IsAdministrator: true } ? await db.Users.AsNoTracking().SingleOrDefaultAsync(item =>
            item.Id == principal.UserId && item.Enabled && item.IsAdmin && item.BackendType == principal.BackendType &&
            item.BackendInstanceId == execution.BackendInstanceId && item.BackendPrincipalId == execution.VerifiedBackendPrincipalId,
            cancellationToken) ?? throw Denied() : throw Denied();
    }

    private static string ExplicitKeepKey(Guid owner, Guid recording) => $"keep:user:{owner:N}:{recording:N}";
    private sealed record KeepProof(RecordingKeepJobPayload Payload, Guid OwnerUserId, Guid RecordingId, ProviderActorContext Actor);
}
