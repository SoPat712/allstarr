using allstarr.Core.Jobs;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Playback;

public sealed class PlaybackScrobbleAcceptance(IDbContextFactory<AllstarrDbContext> factory,
    DurableJobQueue jobs, IEnumerable<IExactScopePlaybackScrobbleTarget> targets)
{
    public async Task<DurableJobEnqueueResult> EnqueueAsync(DurableJobEnqueueRequest<PlaybackSignalPayload> request,
        CancellationToken token)
    {
        var payload = request.Payload;
        await using (var lookup = await factory.CreateDbContextAsync(token))
        {
            var existing = await lookup.Jobs.AsNoTracking().SingleOrDefaultAsync(x => x.Type == request.Type &&
                x.OwnerUserId == payload.Scope.OwnerUserId && x.IdempotencyKey == request.IdempotencyKey, token);
            if (existing != null) return new(existing.Id, false);
        }
        var selected = new List<(IBoundPlaybackScrobbleTarget Target, allstarr.Core.Capabilities.ProviderAccountContext Account)>();
        if (payload.RelayExternally)
            foreach (var target in targets.OfType<IBoundPlaybackScrobbleTarget>())
                if (await target.FindDestinationAsync(payload.Scope, token) is { } account)
                    selected.Add((target, account));
        await using var db = await factory.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var completionKey = ScopedPlaybackScrobbleDelivery.CheckpointKey(payload with { Transition = PlaybackTransition.Submission });
        var frozenTargets = await db.PlaybackDeliveryCheckpoints.Where(x => x.OwnerUserId == payload.Scope.OwnerUserId &&
            x.SignalKey == completionKey).Select(x => x.TargetId).ToListAsync(token);
        payload = payload with { DestinationTargets = frozenTargets.Concat(selected.Select(x => x.Target.ProviderId)).Distinct().ToArray() };
        var result = await jobs.EnqueueInExistingTransactionAsync(db, request with { Payload = payload }, token);
        if (result.Created)
        {
            // Bind both the current update and the eventual completed listen in the acceptance transaction.

            var currentKey = ScopedPlaybackScrobbleDelivery.CheckpointKey(payload);
            foreach (var (target, account) in selected)
            {
                var prior = await db.PlaybackDeliveryCheckpoints.AsNoTracking().SingleOrDefaultAsync(x =>
                    x.OwnerUserId == payload.Scope.OwnerUserId && x.SignalKey == completionKey && x.TargetId == target.ProviderId, token);
                var frozen = prior?.DestinationJson == null ? account :
                    System.Text.Json.JsonSerializer.Deserialize<allstarr.Core.Capabilities.ProviderAccountContext>(prior.DestinationJson)!;
                if (prior == null)
                {
                    var completion = BoundPlaybackScrobbleDelivery.New(payload, completionKey, PlaybackScrobbleDeliveryKind.Completed, target.ProviderId, frozen);
                    if (currentKey != completionKey) completion.SignalPayloadJson = null;
                    db.Add(completion);
                }
                if (currentKey != completionKey && !await db.PlaybackDeliveryCheckpoints.AnyAsync(x =>
                        x.OwnerUserId == payload.Scope.OwnerUserId && x.SignalKey == currentKey && x.TargetId == target.ProviderId, token))
                    db.Add(BoundPlaybackScrobbleDelivery.New(payload, currentKey, PlaybackScrobbleDeliveryKind.NowPlaying, target.ProviderId, frozen));
            }
        }
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return result;
    }
}
