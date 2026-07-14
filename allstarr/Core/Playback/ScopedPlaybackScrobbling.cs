using allstarr.Core.Intelligence;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Playback;

public sealed record ScopedPlaybackTrack(string Title, string Artist, string? Album, long DurationMilliseconds);
public interface IExactScopePlaybackScrobbleTarget
{
    string ProviderId { get; }
    Task<bool> IsConfiguredAsync(IntelligenceScope scope, CancellationToken cancellationToken);
    Task DeliverAsync(IntelligenceScope scope, PlaybackTransition transition, ScopedPlaybackTrack track,
        long? positionTicks, DateTimeOffset observedAt, string signalKey, CancellationToken cancellationToken);
}

public sealed class ScopedPlaybackScrobbleDelivery(IDbContextFactory<AllstarrDbContext> factory,
    IEnumerable<IExactScopePlaybackScrobbleTarget> targets, IPlaybackDeliveryCheckpointStore checkpoints) : IScopedPlaybackScrobbleDelivery
{
    public async Task DeliverAsync(PlaybackSignalPayload payload, CancellationToken cancellationToken)
    {
        if (payload.Transition == PlaybackTransition.Progress) return;
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var id = payload.ItemId.StartsWith("backend:", StringComparison.Ordinal) ? payload.ItemId[8..] : payload.ItemId;
        var item = await db.LibraryTracks.AsNoTracking().SingleOrDefaultAsync(track => track.TenantId == payload.Scope.TenantId &&
            track.OwnerUserId == payload.Scope.OwnerUserId && track.Protocol == payload.Scope.Protocol &&
            track.BackendInstanceId == payload.Scope.BackendInstanceId && track.LibraryScopeId == payload.Scope.LibraryScopeId &&
            (track.BackendItemId == id || track.CanonicalRecordingId != null && track.CanonicalRecordingId.ToString() == id), cancellationToken);
        if (item == null) return;
        var track = new ScopedPlaybackTrack(item.Title, item.Artist, item.Album, item.DurationMilliseconds);
        if (payload.Transition is PlaybackTransition.Stop or PlaybackTransition.InferredStop &&
            !EligibleForCompletedScrobble(track.DurationMilliseconds, payload.PositionTicks)) return;
        foreach (var target in targets)
        {
            if (!await target.IsConfiguredAsync(payload.Scope, cancellationToken)) continue;
            if (await checkpoints.IsCompletedAsync(payload.Scope.TenantId, payload.Scope.OwnerUserId, payload.SignalKey, target.ProviderId, cancellationToken)) continue;
            await target.DeliverAsync(payload.Scope, payload.Transition, track, payload.PositionTicks, payload.ObservedAt, payload.SignalKey, cancellationToken);
            await checkpoints.MarkCompletedAsync(payload.Scope.TenantId, payload.Scope.OwnerUserId, payload.SignalKey, target.ProviderId, cancellationToken);
        }
    }

    public static bool EligibleForCompletedScrobble(long durationMilliseconds, long? positionTicks)
    {
        var durationSeconds = durationMilliseconds / 1000d;
        if (durationSeconds < 30 || positionTicks is null || positionTicks < 0) return false;
        var playedSeconds = positionTicks.Value / (double)TimeSpan.TicksPerSecond;
        return playedSeconds >= Math.Min(durationSeconds / 2d, 240d);
    }
}
