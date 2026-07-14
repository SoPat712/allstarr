using allstarr.Core.Storage;
using allstarr.Services.Lyrics;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Playback;

public sealed class PlaybackLyricsPrefetch(IDbContextFactory<AllstarrDbContext> factory,
    LyricsOrchestrator orchestrator) : IPlaybackLyricsPrefetch
{
    public async Task PrefetchAsync(PlaybackSignalPayload payload, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var itemId = payload.ItemId.StartsWith("backend:", StringComparison.Ordinal) ? payload.ItemId[8..] : payload.ItemId;
        var track = await db.LibraryTracks.AsNoTracking().SingleOrDefaultAsync(item =>
            item.TenantId == payload.Scope.TenantId && item.OwnerUserId == payload.Scope.OwnerUserId &&
            item.Protocol == payload.Scope.Protocol && item.BackendInstanceId == payload.Scope.BackendInstanceId &&
            item.LibraryScopeId == payload.Scope.LibraryScopeId && item.BackendItemId == itemId, cancellationToken);
        if (track == null) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await orchestrator.PrefetchLyricsAsync(track.Title, [track.Artist], track.Album,
            (int)Math.Clamp(track.DurationMilliseconds / 1000, 0, int.MaxValue)).WaitAsync(timeout.Token);
    }
}
