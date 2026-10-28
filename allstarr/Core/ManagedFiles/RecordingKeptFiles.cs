using System.Text.Json.Serialization;
using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Matching;
using allstarr.Core.Jobs;
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
    string ContentSha256, string MimeType, [property: JsonIgnore] string Path)
{
    public ProviderMediaFormat? Media { get; init; }
}

public sealed record KeptRecordingRead(KeptRecordingFile File, [property: JsonIgnore] Stream? Content);

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
    ILogger<RecordingKeptFiles>? logger = null,
    DurableJobQueue? jobs = null,
    allstarr.Core.Protocols.IBackendLibraryAccessResolver? libraryAccess = null,
    allstarr.Core.Music.IProviderAudioVerifier? audioVerifier = null)
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
        var display = publication.Metadata ?? new(publication.Track.Title,
            [publication.Track.Artist], publication.Track.Album);
        display = display with
        {
            Track = display.Track ?? publication.Track.Track,
            AlbumArtist = display.AlbumArtist ?? publication.Track.AlbumArtist
        };
        var track = publication.Track with { Track = display.Track, AlbumArtist = display.AlbumArtist };
        var proof = await AuthorizeAsync(publication, cancellationToken);
        var source = await artifacts.FindByJobAsync(publication.DurableJobId, publication.SourceProviderId, cancellationToken);
        if (source == null || source.Id != publication.SourceArtifactId || source.State != ProviderDownloadArtifactState.Verified ||
            source.OwnerUserId != publication.InitiatingUserId || source.SourceExternalTrackId == null)
            throw Denied();
        var metadata = SerializeMetadata(display with { Artwork = new(source.ProviderId, source.SourceExternalTrackId) });
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
        await (audioVerifier ?? new allstarr.Core.Music.ProviderAudioVerifier()).VerifyFileAsync(source.SourcePath,
            new(source.MimeType ?? "application/octet-stream", source.Container ?? "unknown", source.Codec ?? "unknown",
                source.Bitrate, source.SampleRate, source.BitDepth, source.Channels),
            publication.Metadata?.DurationMilliseconds / 1000d, cancellationToken);

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
            var unavailable = new List<ManagedFileRecord>();
            var existing = await FindCandidateAsync(db, files, records, recording.Id, true, cancellationToken,
                unavailable: unavailable);
            if (existing != null) prepared = existing.File;
            else
            {
                var extension = Path.GetExtension(source.SourcePath);
                var placementRequest = new ManagedFilePlacementRequest(
                    new(placement.RootId, Path.GetFullPath(placement.RootPath), null), source.SourcePath,
                    KeptPathTemplate(placement.PathTemplate, track.Track), track with { Extension = string.IsNullOrWhiteSpace(extension) ? ".bin" : extension },
                    null, ManagedFileScopeKey.Create(null, placement.RootId), true, true, source.ContentSha256, source.Length)
                {
                    ReferenceKey = pendingKey,
                    DestinationIsImmutable = true
                };
                var placed = unavailable.Count == 0
                    ? await files.PlaceAsync(placementRequest, cancellationToken)
                    : await files.PlaceFreshHouseholdAsync(placementRequest, cancellationToken);
                prepared = placed.File;
                provisional = true;
            }
            KeptRecordingFile result;
            await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
            {
                await FenceFavoritePublicationAsync(db, publication, cancellationToken);
                var refreshed = await AuthorizeAsync(publication, cancellationToken, proof.PlaylistAccess);
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

                unavailable.Clear();
                var winner = await FindCandidateAsync(db, files, records, recording.Id, true, cancellationToken,
                    unavailable: unavailable);
                var serving = winner?.File ?? prepared;
                var mime = winner?.MimeType ?? source.MimeType;
                await files.VerifyOwnedFileAsync(serving, cancellationToken);
                var replacement = winner is { Route: null }
                    ? (CanonicalReplaced: false, RetiredFiles: (IReadOnlyList<Guid>)[])
                    : await ReplaceUnavailableKeptFilesAsync(db, ownership, unavailable,
                        serving, recording.Id, winner?.Route, publication, proof.CorrelationId, cancellationToken);
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
                if (winner == null || replacement.CanonicalReplaced)
                {
                    var route = await db.Set<RecordingKeptRouteEntity>()
                        .SingleOrDefaultAsync(item => item.CanonicalRecordingId == recording.Id, cancellationToken);
                    if (route == null)
                    {
                        route = new() { CanonicalRecordingId = recording.Id, CreatedAt = clock.UtcNow };
                        db.Add(route);
                    }
                    route.ManagedFileId = serving.Id;
                    route.PublishedByUserId = winner?.Route?.PublishedByUserId ?? publication.InitiatingUserId;
                    route.PublicationJobId = winner?.Route?.PublicationJobId ?? publication.DurableJobId;
                    route.SourceArtifactId = winner?.Route?.SourceArtifactId ?? publication.SourceArtifactId;
                    var qualityJson = await db.Set<ProviderDownloadArtifactEntity>().Where(item => item.Id == route.SourceArtifactId)
                        .Select(item => item.QualityCheckJson).SingleAsync(cancellationToken);
                    route.DisplayMetadataJson = AudioQualityCheckJobHandler.WithCheck(metadata, AudioQualityCheckJobHandler.ReadCheck(qualityJson));
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
                await CancelCleanupAsync(db, serving.Id, clock.UtcNow, cancellationToken);
                foreach (var fileId in replacement.RetiredFiles)
                {
                    if (jobs == null) throw new InvalidOperationException("The kept cleanup queue is unavailable.");
                    await CancelCleanupAsync(db, fileId, clock.UtcNow, cancellationToken);
                    await jobs.EnqueueInExistingTransactionAsync(db,
                        new DurableJobEnqueueRequest<KeptRecordingCleanupPayload>(KeptCleanupJobType,
                            $"kept:{fileId:N}:replacement:{publication.DurableJobId:N}",
                            new(recording.Id, fileId), publication.InitiatingUserId,
                            CorrelationId: proof.CorrelationId), cancellationToken);
                }
                if (jobs != null)
                    await jobs.EnqueueInExistingTransactionAsync(db,
                        AudioQualityCheckJobHandler.Request(source.Id, publication.InitiatingUserId), cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                result = ToResult(recording.Id, serving, mime);
            }
            if (provisional) await CleanupPendingAsync(prepared, pendingKey);
            await EnsureLyricsAsync(result, publication.InitiatingUserId, proof.CorrelationId, cancellationToken);
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

    internal async Task<KeptRecordingFile?> FindForLyricsAsync(ProviderActorContext actor, string itemId,
        CancellationToken cancellationToken)
    {
        if (actor.EffectiveUserId == null) return null;
        try
        {
            var recordingId = await ResolveKeptRecordingIdAsync(actor, itemId, cancellationToken);
            if (recordingId == null) return null;
            await using var scope = scopes.CreateAsyncScope();
            var candidate = await FindCandidateAsync(scope.ServiceProvider.GetRequiredService<AllstarrDbContext>(),
                scope.ServiceProvider.GetRequiredService<FilePlacementService>(),
                scope.ServiceProvider.GetRequiredService<IManagedFileRemovalStore>(), recordingId.Value, true,
                cancellationToken, verifyContent: false);
            return candidate == null ? null : ToResult(recordingId.Value, candidate.File, candidate.MimeType);
        }
        catch (UnauthorizedAccessException) { return null; }
    }

    public async Task<KeptRecordingRead?> OpenForPlaybackAsync(ProviderActorContext actor,
        ProviderExternalResourceId source, bool headOnly, CancellationToken cancellationToken = default)
    {
        source.RequireOwner(source.ProviderId, ProviderResourceKind.Track);
        if (actor.EffectiveUserId == null) return null;
        try
        {
            var recordingId = await ResolveKeptRecordingIdAsync(actor,
                $"ext-{source.ProviderId}-song-{source.Value}", cancellationToken);
            if (recordingId == null) return null;
            await using var scope = scopes.CreateAsyncScope();
            var files = scope.ServiceProvider.GetRequiredService<FilePlacementService>();
            var candidate = await FindCandidateAsync(scope.ServiceProvider.GetRequiredService<AllstarrDbContext>(), files,
                scope.ServiceProvider.GetRequiredService<IManagedFileRemovalStore>(), recordingId.Value, true, cancellationToken,
                verifyContent: false);
            if (candidate == null) return null;
            var result = ToResult(recordingId.Value, candidate.File, candidate.MimeType);
            result = result with
            {
                Media = await ReadMediaAsync(
                scope.ServiceProvider.GetRequiredService<AllstarrDbContext>(), result.ManagedFileId,
                result.ContentSha256, result.Length, cancellationToken)
            };
            var stream = files.OpenOwnedPlaybackRead(candidate.File, cancellationToken);
            if (headOnly) { await stream.DisposeAsync(); stream = null; }
            return new(result, stream);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    private static async Task<ProviderMediaFormat?> ReadMediaAsync(AllstarrDbContext db,
        Guid fileId, string hash, long length, CancellationToken cancellationToken)
    {
        var source = await (from route in db.Set<RecordingKeptRouteEntity>().AsNoTracking()
                            join artifact in db.Set<ProviderDownloadArtifactEntity>().AsNoTracking()
                                on route.SourceArtifactId equals artifact.Id
                            where route.ManagedFileId == fileId && artifact.ContentSha256 == hash && artifact.Length == length
                            select artifact).FirstOrDefaultAsync(cancellationToken);
        return source == null ? null : new(source.MimeType ?? "application/octet-stream",
            source.Container ?? "unknown", source.Codec ?? "unknown", source.Bitrate is > 0 ? source.Bitrate : null,
            source.SampleRate is > 0 ? source.SampleRate : null, source.BitDepth is > 0 ? source.BitDepth : null,
            source.Channels is > 0 ? source.Channels : null);
    }

    private async Task<Candidate?> FindCandidateAsync(AllstarrDbContext db, FilePlacementService files,
        IManagedFileRemovalStore records, Guid recordingId, bool skipUnavailable, CancellationToken cancellationToken,
        bool verifyContent = true, ICollection<ManagedFileRecord>? unavailable = null)
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
                if (verifyContent) await files.VerifyOwnedFileAsync(file, cancellationToken);
                else files.VerifyOwnedFileIdentity(file, cancellationToken);
                var mime = await db.Set<ProviderDownloadArtifactEntity>().AsNoTracking()
                    .Where(item => item.Id == route.SourceArtifactId).Select(item => item.MimeType).SingleAsync(cancellationToken);
                return new(file, mime, route);
            }
            catch (Exception error) when (skipUnavailable && error is IOException or UnauthorizedAccessException or ArgumentException)
            {
                unavailable?.Add(file);
            }
        }
        return await FindLegacyCandidateAsync(db, files, records, recordingId, skipUnavailable, verifyContent, cancellationToken);
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

    private sealed record Candidate(ManagedFileRecord File, string? MimeType, RecordingKeptRouteEntity? Route);

    private async Task<(ProviderAccountContext? Account, string CorrelationId, ProviderDownloadArtifactEntity Artifact,
        allstarr.Core.Protocols.BackendLibraryAccessContext? PlaylistAccess)> AuthorizeAsync(
        KeptRecordingPublication request, CancellationToken cancellationToken,
        allstarr.Core.Protocols.BackendLibraryAccessContext? playlistAccess = null)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var job = await db.Jobs.AsNoTracking().SingleOrDefaultAsync(item => item.Id == request.DurableJobId &&
            item.OwnerUserId == request.InitiatingUserId && item.State == DurableJobState.Running &&
            item.AttemptCount == request.AttemptNumber &&
            item.CancellationRequestedAt == null, cancellationToken);
        var artifact = await db.Set<ProviderDownloadArtifactEntity>().AsNoTracking().SingleOrDefaultAsync(item =>
            item.Id == request.SourceArtifactId && item.DurableJobId == request.DurableJobId &&
            item.OwnerUserId == request.InitiatingUserId && item.ProviderId == request.SourceProviderId &&
            item.State == ProviderDownloadArtifactState.Verified && item.SupersededAt == null &&
            item.SourceExternalTrackId != null, cancellationToken);
        if (job == null || artifact == null || !await db.Users.AnyAsync(item =>
                item.Id == request.InitiatingUserId && item.Enabled, cancellationToken)) throw Denied();
        await AuthorizeFavoritePublicationAsync(db, job, request, cancellationToken);
        playlistAccess = await AuthorizePlaylistPublicationAsync(db, job, request, playlistAccess, cancellationToken);
        await AuthorizeExplicitKeepPublicationAsync(db, job, request, cancellationToken);
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
        return (account, job.CorrelationId, artifact, playlistAccess);
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
