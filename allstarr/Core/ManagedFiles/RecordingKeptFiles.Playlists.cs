using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Jobs;
using allstarr.Core.Playlists;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.ManagedFiles;

public sealed partial class RecordingKeptFiles
{
    internal async Task StagePlaylistInterestsAsync(AllstarrDbContext db, PlaylistLinkRecord link,
        BackendLibraryAccessContext access, string correlationId, CancellationToken cancellationToken,
        bool deleting = false)
    {
        if (db.Database.CurrentTransaction == null)
            throw new InvalidOperationException("Kept playlist interests require the playlist transaction.");
        var prefix = $"playlist:{link.Id:N}:";
        var store = new EfManagedFileOwnershipStore(db);
        var desired = new Dictionary<string, Guid>(StringComparer.Ordinal);
        DurablePlaylistProjection? projection = null;
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == link.OwnerUserId, cancellationToken);
        if (user is not { Enabled: true } || user.BackendType != link.TargetProtocol ||
            user.BackendInstanceId != link.TargetBackendInstanceId) throw Denied();
        if (!deleting && link.TrackRetention == PlaylistTrackRetention.KeepAll)
        {
            if (libraryAccess == null) throw Denied();
            projection = await new DurablePlaylistProjectionReader(factory, libraryAccess)
                .ReadForRetentionAsync(db, link.OwnerUserId, link.Id, access, cancellationToken);
            var actor = new ProviderActorContext(ProviderActorKind.User, user.Id,
                new(user.BackendType, user.BackendInstanceId, user.BackendPrincipalId));
            foreach (var entry in projection?.Entries ?? [])
            {
                if (entry.RouteKind != "external" || entry.RouteProviderId == null) continue;
                var key = prefix + PlaylistTrackRetentionQueue.RetentionKey(link.Id, entry.RouteProviderId, entry.ExternalId);
                var recordingId = await ResolvePlaylistRecordingAsync(actor, entry, cancellationToken);
                var fileId = recordingId == null ? Guid.Empty : await (from route in db.Set<RecordingKeptRouteEntity>().AsNoTracking()
                                                                       join recording in db.CanonicalRecordings.AsNoTracking() on route.CanonicalRecordingId equals recording.Id
                                                                       join file in db.ManagedFiles.AsNoTracking() on route.ManagedFileId equals file.Id
                                                                       where (recording.Id == recordingId || recording.MergedIntoId == recordingId) && file.RemovedAt == null
                                                                       orderby route.CanonicalRecordingId == recordingId descending, route.CreatedAt, route.ManagedFileId
                                                                       select file.Id).FirstOrDefaultAsync(cancellationToken);
                desired[key] = fileId;
            }
        }

        var now = clock.UtcNow;
        foreach (var (key, fileId) in desired)
        {
            if (fileId == Guid.Empty) continue;
            await store.StageReferenceAsync(fileId, key, now, cancellationToken);
            await CancelCleanupAsync(db, fileId, now, cancellationToken);
        }
        var existing = await db.ManagedFileReferences.AsNoTracking()
            .Where(item => item.ReleasedAt == null && item.ReferenceKey.StartsWith(prefix)).ToArrayAsync(cancellationToken);
        var operationId = Guid.CreateVersion7();
        foreach (var reference in existing)
        {
            if (desired.GetValueOrDefault(reference.ReferenceKey) == reference.ManagedFileId) continue;
            await StageReleaseInterestAsync(db, reference.ReferenceKey, link.OwnerUserId, operationId, now,
                correlationId, cancellationToken, reference.ManagedFileId);
        }
        var running = await db.Jobs.Where(item => item.Type == PlaylistTrackRetentionJobHandler.JobTypeName &&
            item.OwnerUserId == link.OwnerUserId && (item.State == DurableJobState.Pending ||
                item.State == DurableJobState.RetryScheduled || item.State == DurableJobState.Running)).ToArrayAsync(cancellationToken);
        foreach (var job in running)
        {
            PlaylistTrackRetentionJobPayload? payload;
            try { payload = JsonSerializer.Deserialize<PlaylistTrackRetentionJobPayload>(job.PayloadJson); }
            catch (JsonException) { continue; }
            if (payload?.PlaylistLinkId != link.Id || projection?.SnapshotId == payload.SourceSnapshotId &&
                desired.ContainsKey(PlaylistKey(payload))) continue;
            job.CancellationRequestedAt = now;
            if (job.State != DurableJobState.Running)
            {
                job.State = DurableJobState.Cancelled;
                job.CompletedAt = now;
            }
            job.UpdatedAt = now;
            job.Revision++;
        }
        if (projection != null)
        {
            if (jobs == null) throw new InvalidOperationException("The playlist retention queue is unavailable.");
            await new PlaylistTrackRetentionQueue(jobs, new(factory, libraryAccess!), factory)
                .EnqueueProjectionInTransactionAsync(db, link, projection, correlationId, cancellationToken);
        }
    }

    public async Task<KeptRecordingFile?> RetainPlaylistAsync(DurableJobClaim claim,
        CancellationToken cancellationToken = default)
    {
        if (libraryAccess == null || claim.OwnerUserId is not { } owner) throw Denied();
        var access = await libraryAccess.ResolveUserAsync(owner, cancellationToken);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AllstarrDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var proof = await FindPlaylistAsync(db, claim.JobId, owner, claim.AttemptNumber, access, cancellationToken);
        if (proof == null) throw Denied();
        var candidate = await FindCandidateAsync(db, scope.ServiceProvider.GetRequiredService<FilePlacementService>(),
            scope.ServiceProvider.GetRequiredService<IManagedFileRemovalStore>(), proof.RecordingId, true, cancellationToken);
        if (candidate == null) return null;
        var store = new EfManagedFileOwnershipStore(db);
        await store.StageReferenceAsync(candidate.File.Id, PlaylistKey(proof.Payload), clock.UtcNow, cancellationToken);
        await CancelCleanupAsync(db, candidate.File.Id, clock.UtcNow, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ToResult(proof.RecordingId, candidate.File, candidate.MimeType);
    }

    public async Task<KeptRecordingFile> PublishPlaylistAsync(DurableJobClaim claim,
        VerifiedProviderDownloadArtifact artifact, CancellationToken cancellationToken = default)
    {
        if (libraryAccess == null || claim.OwnerUserId is not { } owner) throw Denied();
        var access = await libraryAccess.ResolveUserAsync(owner, cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var proof = await FindPlaylistAsync(db, claim.JobId, owner, claim.AttemptNumber, access, cancellationToken)
            ?? throw Denied();
        var entry = proof.Entry;
        var artists = entry.Artists.Count > 0 ? entry.Artists.ToArray() : ["Unknown artist"];
        return await PublishAsync(new(proof.RecordingId, owner, claim.JobId, artifact.Id, artifact.ProviderId,
            PlaylistKey(proof.Payload), new(entry.Title, artists[0], entry.Album))
        {
            AttemptNumber = claim.AttemptNumber,
            Metadata = new(entry.Title, artists, entry.Album, entry.DurationMilliseconds)
        }, cancellationToken);
    }

    private async Task<BackendLibraryAccessContext?> AuthorizePlaylistPublicationAsync(AllstarrDbContext db,
        DurableJobRecord job, KeptRecordingPublication request, BackendLibraryAccessContext? access,
        CancellationToken cancellationToken)
    {
        if (job.Type != PlaylistTrackRetentionJobHandler.JobTypeName)
        {
            if (request.ReferenceKey.StartsWith("playlist:", StringComparison.Ordinal)) throw Denied();
            return null;
        }
        if (libraryAccess == null) throw Denied();
        access ??= await libraryAccess.ResolveUserAsync(request.InitiatingUserId, cancellationToken);
        var proof = await FindPlaylistAsync(db, job.Id, request.InitiatingUserId, request.AttemptNumber, access,
            cancellationToken);
        if (proof == null || request.ReferenceKey != PlaylistKey(proof.Payload)) throw Denied();
        var requested = await identities.ResolveRecordingAsync(proof.Actor, request.RecordingId.ToString("D"), cancellationToken);
        if (requested?.Id != proof.RecordingId) throw Denied();
        return access;
    }

    private async Task<PlaylistProof?> FindPlaylistAsync(AllstarrDbContext db, Guid jobId, Guid ownerId,
        int attempt, BackendLibraryAccessContext access, CancellationToken cancellationToken)
    {
        if (libraryAccess == null) return null;
        var job = await db.Jobs.AsNoTracking().SingleOrDefaultAsync(item => item.Id == jobId &&
            item.Type == PlaylistTrackRetentionJobHandler.JobTypeName && item.OwnerUserId == ownerId &&
            item.State == DurableJobState.Running && item.AttemptCount == attempt && item.CancellationRequestedAt == null,
            cancellationToken);
        if (job == null) return null;
        PlaylistTrackRetentionJobPayload? payload;
        try { payload = JsonSerializer.Deserialize<PlaylistTrackRetentionJobPayload>(job.PayloadJson); }
        catch (JsonException) { return null; }
        if (!PlaylistTrackRetentionJobHandler.Valid(payload)) return null;
        var request = payload!;
        var link = await db.PlaylistLinks.AsNoTracking().SingleOrDefaultAsync(item =>
            item.Id == request.PlaylistLinkId && item.OwnerUserId == ownerId && item.TrackRetention == PlaylistTrackRetention.KeepAll,
            cancellationToken);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == ownerId && item.Enabled, cancellationToken);
        if (link == null || user == null || user.BackendType != link.TargetProtocol ||
            user.BackendInstanceId != link.TargetBackendInstanceId || request.RetentionKey !=
            PlaylistTrackRetentionQueue.RetentionKey(link.Id, request.ProviderId, request.ExternalId)) return null;
        var source = await (from entry in db.PlaylistSourceEntries.AsNoTracking()
                            join snapshot in db.PlaylistSourceSnapshots.AsNoTracking() on entry.PlaylistSourceSnapshotId equals snapshot.Id
                            join metadata in db.ExternalMetadataSnapshots.AsNoTracking() on entry.ExternalMetadataSnapshotId equals metadata.Id
                            where entry.Id == request.SourceEntryId && snapshot.Id == request.SourceSnapshotId &&
                                  snapshot.PlaylistLinkId == link.Id && snapshot.OwnerUserId == ownerId &&
                                  snapshot.ProviderAccountId == link.ProviderAccountId && snapshot.PublishedAt != null &&
                                  metadata.OwnerUserId == ownerId && metadata.ProviderAccountId == link.ProviderAccountId &&
                                  metadata.Protocol == link.TargetProtocol && metadata.BackendInstanceId == link.TargetBackendInstanceId &&
                                  metadata.BackendPrincipalId == user.BackendPrincipalId
                            select entry).SingleOrDefaultAsync(cancellationToken);
        if (source == null) return null;
        var projection = await new DurablePlaylistProjectionReader(factory, libraryAccess)
            .ReadForRetentionAsync(db, ownerId, link.Id, access, cancellationToken);
        if (projection?.SnapshotId != request.SourceSnapshotId) return null;
        var current = projection.Entries.SingleOrDefault(item => item.Position == source.SourcePosition &&
            item.ExternalSnapshotId == source.ExternalMetadataSnapshotId);
        if (current == null || current.RouteKind != "external" || current.RouteProviderId != request.ProviderId ||
            current.ExternalId != request.ExternalId) return null;
        var actor = new ProviderActorContext(ProviderActorKind.User, ownerId,
            new(user.BackendType, user.BackendInstanceId, user.BackendPrincipalId));
        var recordingId = await ResolvePlaylistRecordingAsync(actor, current, cancellationToken);
        return recordingId == null ? null : new(request, current, recordingId.Value, actor);
    }

    private async Task<Guid?> ResolvePlaylistRecordingAsync(ProviderActorContext actor,
        DurablePlaylistEntryProjection entry, CancellationToken cancellationToken)
    {
        var current = await identities.ResolveRecordingAsync(actor,
            $"ext-{entry.RouteProviderId}-song-{entry.ExternalId}", cancellationToken);
        if (current != null) return current.Id;
        if (entry.ProviderRoutes.FirstOrDefault()?.IsManual == true)
            return await ResolveKeptRecordingIdAsync(actor,
                $"ext-{entry.RouteProviderId}-song-{entry.ExternalId}", cancellationToken);
        if (entry.CanonicalRecordingId is not { } known) return null;
        // Imported, owned decisions remain usable after their source account is retired.
        // New downloads still require the actual selected account and revision.
        return (await identities.ResolveRecordingAsync(actor, known.ToString("D"), cancellationToken))?.Id;
    }

    private static string PlaylistKey(PlaylistTrackRetentionJobPayload payload) =>
        $"playlist:{payload.PlaylistLinkId:N}:{payload.RetentionKey}";

    private sealed record PlaylistProof(PlaylistTrackRetentionJobPayload Payload,
        DurablePlaylistEntryProjection Entry, Guid RecordingId, ProviderActorContext Actor);
}
