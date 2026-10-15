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
            var entries = (projection?.Entries ?? []).Where(item =>
                item.RouteKind == "external" && item.RouteProviderId != null).ToArray();
            var recordings = await ResolvePlaylistRecordingsAsync(db, actor, entries, cancellationToken);
            var targets = recordings.Values.OfType<Guid>().Distinct().ToArray();
            var routes = await (from route in db.Set<RecordingKeptRouteEntity>().AsNoTracking()
                                join recording in db.CanonicalRecordings.AsNoTracking() on route.CanonicalRecordingId equals recording.Id
                                join file in db.ManagedFiles.AsNoTracking() on route.ManagedFileId equals file.Id
                                where file.RemovedAt == null && (EF.Parameter(targets).Contains(recording.Id) ||
                                    recording.MergedIntoId.HasValue && EF.Parameter(targets).Contains(recording.MergedIntoId.Value))
                                orderby route.CreatedAt, route.ManagedFileId
                                select new { RecordingId = recording.Id, recording.MergedIntoId, FileId = file.Id })
                .ToArrayAsync(cancellationToken);
            var files = routes.SelectMany(item => item.MergedIntoId is { } merged
                    ? new[] { (Target: item.RecordingId, Direct: true, item.FileId), (Target: merged, Direct: false, item.FileId) }
                    : new[] { (Target: item.RecordingId, Direct: true, item.FileId) })
                .GroupBy(item => item.Target).ToDictionary(group => group.Key,
                    group => group.OrderByDescending(item => item.Direct).First().FileId);
            foreach (var entry in entries)
            {
                var key = prefix + PlaylistTrackRetentionQueue.RetentionKey(link.Id, entry.RouteProviderId!, entry.ExternalId);
                desired[key] = recordings[entry.Position] is { } recordingId ? files.GetValueOrDefault(recordingId) : Guid.Empty;
            }
        }

        var now = clock.UtcNow;
        var references = desired.Where(item => item.Value != Guid.Empty)
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        await store.StageReferencesAsync(references, now, cancellationToken);
        await CancelCleanupsAsync(db, references.Values.Distinct().ToArray(), now, cancellationToken);
        var existing = await db.ManagedFileReferences.AsNoTracking()
            .Where(item => item.ReleasedAt == null && item.ReferenceKey.StartsWith(prefix)).ToArrayAsync(cancellationToken);
        var obsolete = existing.Where(item => desired.GetValueOrDefault(item.ReferenceKey) != item.ManagedFileId)
            .Select(item => item.Id).ToArray();
        var released = await store.StageReleaseReferencesAsync(obsolete, now, cancellationToken);
        if (released.Length > 0)
        {
            var saved = await db.ManagedFileReferences.Where(item => EF.Parameter(released).Contains(item.ManagedFileId) && item.ReleasedAt == null)
                .ToArrayAsync(cancellationToken);
            var active = saved.Concat(db.ManagedFileReferences.Local).Where(item => item.ReleasedAt == null)
                .Select(item => item.ManagedFileId).ToHashSet();
            var unreferenced = released.Where(item => !active.Contains(item)).ToArray();
            var abandoned = await db.Set<RecordingKeptRouteEntity>().AsNoTracking()
                .Where(item => EF.Parameter(unreferenced).Contains(item.ManagedFileId)).OrderBy(item => item.CanonicalRecordingId)
                .Select(item => new { item.ManagedFileId, item.CanonicalRecordingId }).ToArrayAsync(cancellationToken);
            if (abandoned.Length > 0)
            {
                if (jobs == null) throw new InvalidOperationException("The kept cleanup queue is unavailable.");
                var operationId = Guid.CreateVersion7();
                var requests = abandoned.GroupBy(item => item.ManagedFileId).Select(group => group.First())
                    .Select(item => new DurableJobEnqueueRequest<KeptRecordingCleanupPayload>(KeptCleanupJobType,
                        $"kept:{item.ManagedFileId:N}:{operationId:N}:{now.UtcTicks}", new(item.CanonicalRecordingId, item.ManagedFileId),
                        link.OwnerUserId, AvailableAt: now.AddHours(24), CorrelationId: correlationId)).ToArray();
                await jobs.EnqueueManyInExistingTransactionAsync(db, requests, cancellationToken);
            }
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
        var current = await new DurablePlaylistProjectionReader(factory, libraryAccess)
            .ReadRetentionEntryAsync(db, ownerId, link.Id, request.SourceSnapshotId, source.Id, access,
                cancellationToken);
        if (current?.ExternalSnapshotId != source.ExternalMetadataSnapshotId) return null;
        if (current == null || current.RouteKind != "external" || current.RouteProviderId != request.ProviderId ||
            current.ExternalId != request.ExternalId) return null;
        var actor = new ProviderActorContext(ProviderActorKind.User, ownerId,
            new(user.BackendType, user.BackendInstanceId, user.BackendPrincipalId));
        var recordingId = await ResolvePlaylistRecordingAsync(actor, current, cancellationToken);
        return recordingId == null ? null : new(request, current, recordingId.Value, actor);
    }

    private async Task<IReadOnlyDictionary<int, Guid?>> ResolvePlaylistRecordingsAsync(AllstarrDbContext db,
        ProviderActorContext actor, IReadOnlyList<DurablePlaylistEntryProjection> entries, CancellationToken cancellationToken)
    {
        static string PublicId(DurablePlaylistEntryProjection entry) => $"ext-{entry.RouteProviderId}-song-{entry.ExternalId}";
        var publicIds = entries.Select(PublicId).Concat(entries.Where(item =>
                item.ProviderRoutes.FirstOrDefault()?.IsManual != true && item.CanonicalRecordingId.HasValue)
            .Select(item => item.CanonicalRecordingId!.Value.ToString("D"))).Distinct(StringComparer.Ordinal).ToArray();
        var known = await identities.ResolveRecordingsAsync(actor, publicIds, cancellationToken);
        var manual = entries.Where(item => item.ProviderRoutes.FirstOrDefault()?.IsManual == true &&
                known.GetValueOrDefault(PublicId(item)) == null)
            .Select(item => allstarr.Services.Local.LocalLibraryService.ParseExternalResource(PublicId(item)))
            .Where(item => item.isExternal && item.type == "song").ToArray();
        var providers = manual.Select(item => item.provider).Distinct().ToArray();
        var externalIds = manual.Select(item => item.externalId).Distinct().ToArray();
        var published = await (from route in db.Set<RecordingKeptRouteEntity>().AsNoTracking()
                               join artifact in db.Set<ProviderDownloadArtifactEntity>().AsNoTracking() on route.SourceArtifactId equals artifact.Id
                               join recording in db.CanonicalRecordings.AsNoTracking() on route.CanonicalRecordingId equals recording.Id
                               where EF.Parameter(providers).Contains(artifact.ProviderId) && EF.Parameter(externalIds).Contains(artifact.SourceExternalTrackId)
                               select new { artifact.ProviderId, artifact.SourceExternalTrackId, RecordingId = recording.MergedIntoId ?? recording.Id })
            .Distinct().ToArrayAsync(cancellationToken);
        var historical = published.GroupBy(item => (item.ProviderId, item.SourceExternalTrackId))
            .ToDictionary(group => group.Key, group => group.Select(item => item.RecordingId).Distinct().ToArray());
        var result = new Dictionary<int, Guid?>();
        foreach (var entry in entries)
        {
            Guid? recordingId = known.GetValueOrDefault(PublicId(entry))?.Id;
            if (recordingId == null && entry.ProviderRoutes.FirstOrDefault()?.IsManual == true)
            {
                var parsed = allstarr.Services.Local.LocalLibraryService.ParseExternalResource(PublicId(entry));
                if (parsed.provider != null && parsed.externalId != null &&
                    historical.TryGetValue((parsed.provider, parsed.externalId), out var matches) && matches.Length == 1)
                    recordingId = matches[0];
            }
            else if (recordingId == null && entry.CanonicalRecordingId is { } canonical)
                recordingId = known.GetValueOrDefault(canonical.ToString("D"))?.Id;
            result[entry.Position] = recordingId;
        }
        return result;
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
