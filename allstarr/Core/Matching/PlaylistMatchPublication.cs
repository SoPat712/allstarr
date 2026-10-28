using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Identity;
using allstarr.Core.Jobs;
using allstarr.Core.Operations;
using allstarr.Core.Playlists;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Matching;

public sealed record PlaylistMatchPublicationPayload(Guid PlaylistLinkId, long LinkRevision,
    Guid SourceSnapshotId, Dictionary<Guid, Guid> Decisions, string Scope = TrackRematchScopes.Publication);

public sealed class PlaylistMatchPublication(IDbContextFactory<AllstarrDbContext> factory,
    DurableJobQueue jobs, PlaylistOrchestrationService orchestration, IPlatformClock clock)
{
    public async Task<int> QueueAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var rows = await (from link in db.PlaylistLinks.AsNoTracking()
                          join user in db.Users.AsNoTracking() on link.OwnerUserId equals user.Id
                          join source in db.PlaylistSourceSnapshots.AsNoTracking() on link.Id equals source.PlaylistLinkId
                          join entry in db.PlaylistSourceEntries.AsNoTracking() on source.Id equals entry.PlaylistSourceSnapshotId
                          join snapshot in db.ExternalMetadataSnapshots.AsNoTracking() on entry.ExternalMetadataSnapshotId equals snapshot.Id
                          join decision in db.TrackMatches.AsNoTracking() on snapshot.Id equals decision.ExternalSnapshotId
                          where user.Enabled && user.BackendType == link.TargetProtocol &&
                                user.BackendInstanceId == link.TargetBackendInstanceId &&
                                source.OwnerUserId == user.Id && snapshot.OwnerUserId == user.Id && decision.OwnerUserId == user.Id &&
                                source.ProviderAccountId == link.ProviderAccountId && snapshot.ProviderAccountId == link.ProviderAccountId &&
                                (snapshot.Protocol == "opensubsonic" ? "subsonic" : snapshot.Protocol) == user.BackendType && snapshot.BackendInstanceId == user.BackendInstanceId &&
                                snapshot.BackendPrincipalId == user.BackendPrincipalId &&
                                source.PublishedAt != null && !db.PlaylistSourceSnapshots.Any(other =>
                                    other.PlaylistLinkId == link.Id && other.PublishedAt != null && other.SnapshotVersion > source.SnapshotVersion) &&
                                decision.State == TrackMatchState.Accepted &&
                                decision.MatcherVersion == TrackMatchDecisionEngine.AlgorithmVersion &&
                                decision.SourceSnapshotVersion == snapshot.SnapshotVersion &&
                                !db.TrackMatches.Any(other => other.ExternalSnapshotId == snapshot.Id && other.DecisionVersion > decision.DecisionVersion) &&
                                entry.PublishedTrackMatchId != decision.Id
                          select new
                          {
                              LinkId = link.Id,
                              link.Revision,
                              link.OwnerUserId,
                              SourceId = source.Id,
                              ExternalId = snapshot.Id,
                              DecisionId = decision.Id
                          }).ToArrayAsync(cancellationToken);
        if (rows.Length == 0) return 0;
        var protectedIds = await ManualTrackOverrides.ProtectedSnapshotIdsAsync(db,
            rows.Select(row => row.ExternalId).Distinct().ToArray(), cancellationToken);
        var queued = 0;
        foreach (var group in rows.Where(row => !protectedIds.Contains(row.ExternalId))
                     .GroupBy(row => new { row.LinkId, row.Revision, row.OwnerUserId, row.SourceId }))
        {
            foreach (var batch in group.DistinctBy(row => row.ExternalId).OrderBy(row => row.ExternalId).Chunk(50))
            {
                var decisions = batch.ToDictionary(row => row.ExternalId, row => row.DecisionId);
                var payload = new PlaylistMatchPublicationPayload(group.Key.LinkId, group.Key.Revision, group.Key.SourceId, decisions);
                var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)))).ToLowerInvariant();
                var result = await jobs.EnqueueAsync(new DurableJobEnqueueRequest<PlaylistMatchPublicationPayload>(
                    TrackRematchJobHandler.Type, $"playlist-publication:{fingerprint}", payload, group.Key.OwnerUserId,
                    CorrelationId: $"playlist-publication:{fingerprint[..16]}"), cancellationToken);
                if (result.Created) queued++;
            }
        }
        return queued;
    }

    public async Task<DurableJobCompletion> ExecuteAsync(DurableJobExecutionContext context, CancellationToken cancellationToken)
    {
        PlaylistMatchPublicationPayload? payload;
        try { payload = context.Claim.Payload.Deserialize<PlaylistMatchPublicationPayload>(); }
        catch (JsonException) { payload = null; }
        if (payload == null || payload.Scope != TrackRematchScopes.Publication || payload.PlaylistLinkId == Guid.Empty ||
            payload.SourceSnapshotId == Guid.Empty || payload.Decisions is not { Count: > 0 } ||
            context.Claim.OwnerUserId is not { } owner || context.Claim.ProviderAccountId != null || context.Claim.ProviderCapability != null)
            return DurableJobCompletion.Failure("playlist_publication_invalid", "The playlist publication request is invalid.");
        if (!(await jobs.ReauthorizeAsync(context.Claim, cancellationToken)).Authorized) return DurableJobCompletion.Cancelled();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == owner && item.Enabled, cancellationToken);
        var link = await db.PlaylistLinks.AsNoTracking().SingleOrDefaultAsync(item => item.Id == payload.PlaylistLinkId &&
            item.OwnerUserId == owner && item.Revision == payload.LinkRevision, cancellationToken);
        if (user == null || link == null || user.BackendInstanceId != link.TargetBackendInstanceId || user.BackendType != link.TargetProtocol)
        {
            await QueueAsync(cancellationToken);
            return DurableJobCompletion.Success();
        }
        if (!await db.PlaylistSourceSnapshots.AsNoTracking().AnyAsync(item => item.Id == payload.SourceSnapshotId &&
            item.PlaylistLinkId == link.Id && item.OwnerUserId == owner && item.ProviderAccountId == link.ProviderAccountId &&
            item.PublishedAt != null && !db.PlaylistSourceSnapshots.Any(other => other.PlaylistLinkId == link.Id &&
                other.PublishedAt != null && other.SnapshotVersion > item.SnapshotVersion), cancellationToken))
        {
            await QueueAsync(cancellationToken);
            return DurableJobCompletion.Success();
        }
        var ids = payload.Decisions.Keys.ToArray();
        var protectedIds = await ManualTrackOverrides.ProtectedSnapshotIdsAsync(db, ids, cancellationToken);
        var decisions = await DurablePlaylistProjectionReader.LoadLatestRematchDecisionsAsync(db, owner, ids, cancellationToken);
        var sources = await db.ExternalMetadataSnapshots.AsNoTracking().Where(item => ids.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        if (protectedIds.Count > 0 || ids.Any(id => !decisions.TryGetValue(id, out var decision) ||
            decision.Id != payload.Decisions[id] || decision.State != TrackMatchState.Accepted ||
            decision.MatcherVersion != TrackMatchDecisionEngine.AlgorithmVersion ||
            !sources.TryGetValue(id, out var source) || decision.SourceSnapshotVersion != source.SnapshotVersion))
        {
            await QueueAsync(cancellationToken);
            return DurableJobCompletion.Success();
        }
        var protocol = user.BackendType == "jellyfin" ? ProtocolKind.Jellyfin : ProtocolKind.Subsonic;
        var execution = new ProtocolExecutionContext(protocol, user.BackendInstanceId, user.BackendPrincipalId,
            new AllstarrPrincipal(user.Id, user.BackendType, user.BackendInstanceId, user.BackendPrincipalId, user.DisplayName, user.IsAdmin),
            context.Claim.CorrelationId, clock.UtcNow.AddMinutes(2), cancellationToken);
        try
        {
            await orchestration.PublishExistingDecisionsAsync(execution, link.Id, link.Revision,
                payload.SourceSnapshotId, payload.Decisions.Keys.ToArray(), cancellationToken, payload.Decisions);
        }
        catch (DbUpdateConcurrencyException)
        {
            return DurableJobCompletion.Retry("playlist_publication_changed",
                "The playlist changed during publication and will be checked again.", TimeSpan.FromSeconds(2));
        }
        catch (UnauthorizedAccessException)
        {
            await QueueAsync(cancellationToken);
        }
        return DurableJobCompletion.Success();
    }
}
