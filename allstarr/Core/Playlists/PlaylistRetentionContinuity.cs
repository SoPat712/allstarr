using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Matching;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Playlists;

internal static class PlaylistRetentionContinuity
{
    internal static PlaylistTrackRetentionJobPayload? ReadPayload(DurableJobRecord job)
    {
        try { return JsonSerializer.Deserialize<PlaylistTrackRetentionJobPayload>(job.PayloadJson); }
        catch (JsonException) { return null; }
    }

    internal static async Task<IReadOnlyDictionary<Guid, string>> CompatibleAsync(AllstarrDbContext db,
        ITrackIdentityService identities, PlaylistLinkRecord link, UserRecord user,
        DurablePlaylistProjection? projection, IReadOnlyDictionary<int, Guid?> targets,
        IReadOnlyCollection<DurableJobRecord> jobs, CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, string>();
        if (projection == null) return result;
        var candidates = jobs.Select(job => (Job: job, Payload: ReadPayload(job)))
            .Where(item => item.Job.CancellationRequestedAt == null &&
                PlaylistTrackRetentionJobHandler.Valid(item.Payload) && item.Payload!.PlaylistLinkId == link.Id &&
                item.Payload.RetentionKey == PlaylistTrackRetentionQueue.RetentionKey(link.Id,
                    item.Payload.ProviderId, item.Payload.ExternalId)).ToArray();
        if (candidates.Length == 0) return result;
        var sourceIds = candidates.Select(item => item.Payload!.SourceEntryId).Distinct().ToArray();
        var accepted = await (from entry in db.PlaylistSourceEntries.AsNoTracking()
                              join snapshot in db.PlaylistSourceSnapshots.AsNoTracking() on entry.PlaylistSourceSnapshotId equals snapshot.Id
                              join metadata in db.ExternalMetadataSnapshots.AsNoTracking() on entry.ExternalMetadataSnapshotId equals metadata.Id
                              where EF.Parameter(sourceIds).Contains(entry.Id) && snapshot.PlaylistLinkId == link.Id &&
                                    snapshot.OwnerUserId == user.Id && snapshot.ProviderAccountId == link.ProviderAccountId && snapshot.PublishedAt != null &&
                                    metadata.OwnerUserId == user.Id && metadata.ProviderAccountId == link.ProviderAccountId &&
                                    metadata.Protocol == link.TargetProtocol && metadata.BackendInstanceId == link.TargetBackendInstanceId &&
                                    metadata.BackendPrincipalId == user.BackendPrincipalId && metadata.ResourceKind == "track"
                              select new { entry.Id, SnapshotId = snapshot.Id, metadata.ProviderId, metadata.ExternalIdHash })
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        var current = await (from entry in db.PlaylistSourceEntries.AsNoTracking()
                             join metadata in db.ExternalMetadataSnapshots.AsNoTracking() on entry.ExternalMetadataSnapshotId equals metadata.Id
                             where entry.PlaylistSourceSnapshotId == projection.SnapshotId && metadata.OwnerUserId == user.Id &&
                                   metadata.ProviderAccountId == link.ProviderAccountId && metadata.Protocol == link.TargetProtocol &&
                                   metadata.BackendInstanceId == link.TargetBackendInstanceId && metadata.BackendPrincipalId == user.BackendPrincipalId &&
                                   metadata.ResourceKind == "track"
                             select new { entry.Id, entry.SourcePosition, entry.ExternalMetadataSnapshotId, metadata.ProviderId, metadata.ExternalIdHash })
            .ToArrayAsync(cancellationToken);
        var byPosition = projection.Entries.ToDictionary(item => item.Position);
        var bindings = new HashSet<(string SourceProvider, string SourceHash, string Provider, string ExternalId, Guid Target)>();
        var legacyRows = new HashSet<(Guid EntryId, string Provider, string ExternalId)>();
        foreach (var row in current)
        {
            if (!byPosition.TryGetValue(row.SourcePosition, out var entry) || entry.ExternalSnapshotId != row.ExternalMetadataSnapshotId ||
                entry.RouteKind != "external" || entry.RouteProviderId == null) continue;
            legacyRows.Add((row.Id, entry.RouteProviderId, entry.ExternalId));
            if (targets.GetValueOrDefault(row.SourcePosition) is { } target)
                bindings.Add((row.ProviderId, row.ExternalIdHash, entry.RouteProviderId, entry.ExternalId, target));
        }
        var frozenIds = candidates.Select(item => item.Payload!.TargetCanonicalRecordingId).OfType<Guid>()
            .Select(item => item.ToString("D")).Distinct(StringComparer.Ordinal).ToArray();
        var actor = new ProviderActorContext(ProviderActorKind.User, user.Id,
            new(user.BackendType, user.BackendInstanceId, user.BackendPrincipalId));
        var frozen = frozenIds.Length == 0 ? new Dictionary<string, CanonicalRecordingIdentity?>() :
            await identities.ResolveRecordingsAsync(actor, frozenIds, cancellationToken);
        foreach (var candidate in candidates)
        {
            var payload = candidate.Payload!;
            if (!accepted.TryGetValue(payload.SourceEntryId, out var origin) || origin.SnapshotId != payload.SourceSnapshotId) continue;
            if (payload.TargetCanonicalRecordingId is { } id)
            {
                var target = frozen.GetValueOrDefault(id.ToString("D"))?.Id;
                if (target == null || !bindings.Contains((origin.ProviderId, origin.ExternalIdHash,
                        payload.ProviderId, payload.ExternalId, target.Value))) continue;
            }
            else
            {
                // Old payloads have no frozen target and remain limited to their accepted source row.
                if (payload.SourceSnapshotId != projection.SnapshotId ||
                    !legacyRows.Contains((payload.SourceEntryId, payload.ProviderId, payload.ExternalId))) continue;
            }
            result[candidate.Job.Id] = payload.RetentionKey;
        }
        return result;
    }
}
