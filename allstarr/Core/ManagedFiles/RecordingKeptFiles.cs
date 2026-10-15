using System.Text.Json.Serialization;
using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Matching;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.ManagedFiles;

public sealed record KeptRecordingPublication(
    Guid RecordingId,
    Guid InitiatingUserId,
    Guid DurableJobId,
    Guid SourceArtifactId,
    string SourceProviderId,
    string ReferenceKey,
    ManagedTrackPathValues Track)
{
    public int AttemptNumber { get; init; }
    public KeptRecordingMetadata? Metadata { get; init; }
}

public sealed record KeptRecordingFile(Guid RecordingId, Guid ManagedFileId, long Length,
    string ContentSha256, string MimeType, [property: JsonIgnore] string Path);

public sealed class RecordingKeptRouteEntity
{
    public Guid CanonicalRecordingId { get; set; }
    public Guid ManagedFileId { get; set; }
    public Guid PublishedByUserId { get; set; }
    public Guid PublicationJobId { get; set; }
    public Guid SourceArtifactId { get; set; }
    public string? DisplayMetadataJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Revision { get; set; }
}

public sealed partial class RecordingKeptFiles(
    IDbContextFactory<AllstarrDbContext> factory,
    ProviderDownloadArtifactResolver artifacts,
    IServiceScopeFactory scopes,
    ManagedTrackPlacementOptions placement,
    ITrackIdentityService identities,
    IPlatformClock clock,
    ILogger<RecordingKeptFiles>? logger = null)
{
    public async Task<KeptRecordingFile> PublishAsync(KeptRecordingPublication publication,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publication);
        if (publication.InitiatingUserId == Guid.Empty || publication.DurableJobId == Guid.Empty ||
            publication.RecordingId == Guid.Empty || publication.SourceArtifactId == Guid.Empty || publication.AttemptNumber < 1 ||
            string.IsNullOrWhiteSpace(publication.ReferenceKey) || publication.ReferenceKey.Length > 1000 ||
            publication.ReferenceKey.StartsWith("pending:kept:", StringComparison.Ordinal))
            throw Denied();
        var metadata = SerializeMetadata(publication.Metadata ?? new(publication.Track.Title,
            [publication.Track.Artist], publication.Track.Album));
        var proof = await AuthorizeAsync(publication, cancellationToken);
        var source = await artifacts.FindByJobAsync(publication.DurableJobId, publication.SourceProviderId, cancellationToken);
        if (source == null || source.Id != publication.SourceArtifactId || source.State != ProviderDownloadArtifactState.Verified ||
            source.OwnerUserId != publication.InitiatingUserId || source.SourceExternalTrackId == null)
            throw Denied();
        var actor = new ProviderActorContext(ProviderActorKind.SystemJob, null,
            durableJobId: publication.DurableJobId, actingForUserId: publication.InitiatingUserId);
        var execution = new ProviderExecutionContext(actor, source.ProviderId, proof.Account,
            new(new(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, false),
                ProviderExplicitContentPolicy.Allow, true, true, true),
            "kept-publication", proof.CorrelationId, clock.UtcNow.AddMinutes(5), cancellationToken);
        var link = await identities.ResolveAsync(execution,
            new(source.ProviderId, ProviderResourceKind.Track, source.SourceExternalTrackId), cancellationToken);
        if (link == null || link.Verification is not (ProviderIdentityVerification.Verified or ProviderIdentityVerification.Pinned) ||
            link.VerificationMethod is "automatic-suggestion" or
                ManualTrackAuthorityPolicy.ReleasedProviderVerificationMethod or ManualTrackAuthorityPolicy.ReplacedProviderVerificationMethod)
            throw Denied();
        var recording = await identities.ResolveRecordingAsync(actor, publication.RecordingId.ToString("D"), cancellationToken);
        var sourceRecording = await identities.ResolveRecordingAsync(actor, link.CanonicalRecordingId.ToString("D"), cancellationToken);
        if (recording == null || sourceRecording?.Id != recording.Id) throw Denied();

        await using var scope = scopes.CreateAsyncScope();
        var files = scope.ServiceProvider.GetRequiredService<FilePlacementService>();
        var ownership = scope.ServiceProvider.GetRequiredService<IManagedFileOwnershipStore>();
        var records = scope.ServiceProvider.GetRequiredService<IManagedFileRemovalStore>();
        var db = scope.ServiceProvider.GetRequiredService<AllstarrDbContext>();
        var pendingPrefix = $"pending:kept:{publication.DurableJobId:N}:";
        var interestHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(publication.ReferenceKey)));
        var pendingKey = $"{pendingPrefix}{recording.Id:N}:{publication.AttemptNumber}:{interestHash}";
        ManagedFileRecord? prepared = null;
        var provisional = false;
        try
        {
            var existing = await FindCandidateAsync(db, files, records, recording.Id, false, cancellationToken);
            if (existing != null) prepared = existing.File;
            else
            {
                var extension = Path.GetExtension(source.SourcePath);
                var placed = await files.PlaceAsync(new(
                    new(placement.RootId, Path.GetFullPath(placement.RootPath), null), source.SourcePath,
                    placement.PathTemplate, publication.Track with { Extension = string.IsNullOrWhiteSpace(extension) ? ".bin" : extension },
                    null, ManagedFileScopeKey.Create(null, placement.RootId), true, true, source.ContentSha256, source.Length)
                {
                    ReferenceKey = pendingKey,
                    DestinationIsImmutable = true
                }, cancellationToken);
                prepared = placed.File;
                provisional = true;
            }
            KeptRecordingFile result;
            await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
            {
                var refreshed = await AuthorizeAsync(publication, cancellationToken);
                if (!SameSource(proof.Artifact, refreshed.Artifact)) throw Denied();
                var currentLink = await db.ProviderTrackIdentities.AsNoTracking()
                    .SingleOrDefaultAsync(item => item.Id == link.LinkId, cancellationToken);
                if (currentLink == null || currentLink.Verification != link.Verification ||
                    currentLink.VerificationMethod != link.VerificationMethod || currentLink.Scope != link.Scope ||
                    currentLink.ProviderAccountId != link.ProviderAccountId || currentLink.ProviderId != source.ProviderId ||
                    currentLink.ExternalId != source.SourceExternalTrackId) throw Denied();
                var currentRecording = await identities.ResolveRecordingAsync(actor,
                    currentLink.CanonicalRecordingId.ToString("D"), cancellationToken);
                if (currentRecording?.Id != recording.Id) throw Denied();

                var winner = await FindCandidateAsync(db, files, records, recording.Id, false, cancellationToken);
                var serving = winner?.File ?? prepared;
                var mime = winner?.MimeType ?? source.MimeType;
                await ownership.AddReferenceAsync(serving.Id, new(Guid.CreateVersion7(), serving.Id, null,
                    serving.ScopeKey, publication.ReferenceKey, clock.UtcNow), cancellationToken);
                foreach (var fileId in new[] { prepared.Id, serving.Id }.Distinct())
                {
                    var pending = await db.ManagedFileReferences.AsNoTracking().Where(item =>
                        item.ManagedFileId == fileId && item.ReleasedAt == null &&
                        item.ReferenceKey.StartsWith(pendingPrefix)).Select(item => item.ReferenceKey).ToArrayAsync(cancellationToken);
                    foreach (var key in pending)
                        await ownership.ReleaseReferenceAsync(fileId, key, cancellationToken);
                }
                if (winner == null)
                {
                    var route = await db.Set<RecordingKeptRouteEntity>()
                        .SingleOrDefaultAsync(item => item.CanonicalRecordingId == recording.Id, cancellationToken);
                    if (route == null)
                    {
                        route = new() { CanonicalRecordingId = recording.Id, CreatedAt = clock.UtcNow };
                        db.Add(route);
                    }
                    route.ManagedFileId = serving.Id;
                    route.PublishedByUserId = publication.InitiatingUserId;
                    route.PublicationJobId = publication.DurableJobId;
                    route.SourceArtifactId = publication.SourceArtifactId;
                    route.DisplayMetadataJson = metadata;
                    route.UpdatedAt = clock.UtcNow;
                    route.Revision++;
                    db.AuditEvents.Add(new()
                    {
                        Id = Guid.CreateVersion7(),
                        ActorUserId = publication.InitiatingUserId,
                        Category = "kept-recording",
                        Action = "published",
                        Outcome = "success",
                        CorrelationId = proof.CorrelationId,
                        SubjectType = "recording",
                        SubjectId = recording.Id.ToString("D"),
                        CreatedAt = clock.UtcNow,
                        DetailsJson = System.Text.Json.JsonSerializer.Serialize(new { recordingId = recording.Id, managedFileId = serving.Id })
                    });
                    await db.SaveChangesAsync(cancellationToken);
                }
                await transaction.CommitAsync(cancellationToken);
                result = ToResult(recording.Id, serving, mime);
            }
            if (provisional) await CleanupPendingAsync(prepared, pendingKey);
            return result;
        }
        catch
        {
            if (provisional && prepared != null) await CleanupPendingAsync(prepared, pendingKey);
            throw;
        }
    }

    public async Task<KeptRecordingFile?> FindBySourceAsync(ProviderActorContext actor,
        ProviderExternalResourceId source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        source.RequireOwner(source.ProviderId, ProviderResourceKind.Track);
        if (actor.EffectiveUserId == null) return null;
        try
        {
            var recordingId = await ResolveKeptRecordingIdAsync(actor,
                $"ext-{source.ProviderId}-song-{source.Value}", cancellationToken);
            return recordingId == null ? null : await FindAsync(actor, recordingId.Value, cancellationToken);
        }
        catch (UnauthorizedAccessException) { return null; }
    }

    public async Task<KeptRecordingFile?> FindAsync(ProviderActorContext actor, Guid recordingId,
        CancellationToken cancellationToken = default)
    {
        if (actor.EffectiveUserId == null || recordingId == Guid.Empty) return null;
        try
        {
            var recording = await identities.ResolveRecordingAsync(actor, recordingId.ToString("D"), cancellationToken);
            if (recording == null) return null;
            await using var scope = scopes.CreateAsyncScope();
            var candidate = await FindCandidateAsync(scope.ServiceProvider.GetRequiredService<AllstarrDbContext>(),
                scope.ServiceProvider.GetRequiredService<FilePlacementService>(),
                scope.ServiceProvider.GetRequiredService<IManagedFileRemovalStore>(), recording.Id, true, cancellationToken);
            return candidate == null ? null : ToResult(recording.Id, candidate.File, candidate.MimeType);
        }
        catch (UnauthorizedAccessException) { return null; }
    }

    private async Task<Candidate?> FindCandidateAsync(AllstarrDbContext db, FilePlacementService files,
        IManagedFileRemovalStore records, Guid recordingId, bool skipUnavailable, CancellationToken cancellationToken)
    {
        // Recording merges flatten all earlier aliases onto the surviving recording.
        var routes = await (from route in db.Set<RecordingKeptRouteEntity>().AsNoTracking()
                            join recording in db.CanonicalRecordings.AsNoTracking() on route.CanonicalRecordingId equals recording.Id
                            where recording.Id == recordingId || recording.MergedIntoId == recordingId
                            orderby route.CanonicalRecordingId == recordingId descending, route.CreatedAt, route.ManagedFileId
                            select route).ToArrayAsync(cancellationToken);
        foreach (var route in routes)
        {
            var file = await records.GetAsync(route.ManagedFileId, cancellationToken);
            if (file == null) continue;
            try
            {
                ValidateHousehold(file);
                await files.VerifyOwnedFileAsync(file, cancellationToken);
                var mime = await db.Set<ProviderDownloadArtifactEntity>().AsNoTracking()
                    .Where(item => item.Id == route.SourceArtifactId).Select(item => item.MimeType).SingleAsync(cancellationToken);
                return new(file, mime);
            }
            catch (Exception error) when (skipUnavailable && error is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        return null;
    }

    private async Task CleanupPendingAsync(ManagedFileRecord file, string pendingKey)
    {
        try
        {
            await using var cleanup = scopes.CreateAsyncScope();
            var db = cleanup.ServiceProvider.GetRequiredService<AllstarrDbContext>();
            if (await db.ManagedFileReferences.AsNoTracking().AnyAsync(item => item.ManagedFileId == file.Id &&
                item.ReferenceKey == pendingKey && item.ReleasedAt == null))
                await cleanup.ServiceProvider.GetRequiredService<IManagedFileOwnershipStore>()
                    .ReleaseReferenceAsync(file.Id, pendingKey, CancellationToken.None);
            await cleanup.ServiceProvider.GetRequiredService<ManagedFileRemovalService>()
                .RemoveUnpublishedAsync(file.Id, file.ScopeKey, CancellationToken.None);
        }
        catch (Exception error)
        {
            logger?.LogWarning("Unpublished kept cleanup did not finish ({ExceptionType})", error.GetType().Name);
        }
    }

    private sealed record Candidate(ManagedFileRecord File, string? MimeType);

    private async Task<(ProviderAccountContext? Account, string CorrelationId, ProviderDownloadArtifactEntity Artifact)> AuthorizeAsync(
        KeptRecordingPublication request, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var job = await db.Jobs.AsNoTracking().SingleOrDefaultAsync(item => item.Id == request.DurableJobId &&
            item.OwnerUserId == request.InitiatingUserId && item.State == DurableJobState.Running &&
            item.AttemptCount == request.AttemptNumber &&
            item.CancellationRequestedAt == null, cancellationToken);
        var artifact = await db.Set<ProviderDownloadArtifactEntity>().AsNoTracking().SingleOrDefaultAsync(item =>
            item.Id == request.SourceArtifactId && item.DurableJobId == request.DurableJobId &&
            item.OwnerUserId == request.InitiatingUserId && item.ProviderId == request.SourceProviderId &&
            item.State == ProviderDownloadArtifactState.Verified && item.SourceExternalTrackId != null, cancellationToken);
        if (job == null || artifact == null || !await db.Users.AnyAsync(item =>
                item.Id == request.InitiatingUserId && item.Enabled, cancellationToken)) throw Denied();
        await AuthorizeFavoritePublicationAsync(db, job, request, cancellationToken);
        if (!await db.Set<ProviderDownloadWorkspaceEntity>().AsNoTracking().AnyAsync(workspace =>
                workspace.Id == artifact.WorkspaceRecordId && workspace.WorkspaceId == artifact.WorkspaceId &&
                workspace.OwnerUserId == artifact.OwnerUserId && workspace.DurableJobId == artifact.DurableJobId &&
                workspace.ProviderId == artifact.ProviderId && workspace.ProviderAccountId == artifact.ProviderAccountId,
                cancellationToken)) throw Denied();
        ProviderAccountContext? account = null;
        if (artifact.ProviderAccountId is { } accountId)
        {
            var saved = await db.ProviderAccounts.AsNoTracking().SingleOrDefaultAsync(item => item.Id == accountId &&
                item.ProviderId == artifact.ProviderId && item.Enabled && item.Revision == artifact.SourceAccountRevision &&
                (item.OwnerUserId == null || item.OwnerUserId == request.InitiatingUserId), cancellationToken);
            if (saved == null) throw Denied();
            account = new(saved.Id, saved.ProviderId, saved.Scope, saved.Revision,
                ownerUserId: saved.OwnerUserId, secretReferenceId: saved.SecretReferenceId);
        }
        else if (artifact.SourceAccountRevision != null) throw Denied();
        return (account, job.CorrelationId, artifact);
    }

    private static bool SameSource(ProviderDownloadArtifactEntity first, ProviderDownloadArtifactEntity second) =>
        first.Id == second.Id && first.OwnerUserId == second.OwnerUserId && first.DurableJobId == second.DurableJobId &&
        first.ProviderId == second.ProviderId && first.ProviderAccountId == second.ProviderAccountId &&
        first.SourceExternalTrackId == second.SourceExternalTrackId && first.SourceAccountRevision == second.SourceAccountRevision &&
        first.ContentSha256 == second.ContentSha256 && first.Length == second.Length &&
        first.WorkspaceRecordId == second.WorkspaceRecordId && first.WorkspaceId == second.WorkspaceId &&
        first.RelativePath == second.RelativePath &&
        first.MimeType == second.MimeType && first.Container == second.Container && first.Codec == second.Codec;

    private static void ValidateHousehold(ManagedFileRecord file)
    {
        if (file.OwnerUserId != null || file.SourceJobId != null || !file.IsManaged)
            throw Denied();
    }
    private static KeptRecordingFile ToResult(Guid recordingId, ManagedFileRecord file, string? mime) =>
        new(recordingId, file.Id, file.Length, file.ContentSha256, mime ?? "application/octet-stream", file.CanonicalPath);
    private static UnauthorizedAccessException Denied() => new("The source is not authorized for household kept publication.");
}
