using allstarr.Services.Lyrics;

namespace allstarr.Core.Playback;

public sealed class PlaybackLyricsPrefetch(IPlaybackTrackResolver tracks,
    LyricsOrchestrator orchestrator) : IPlaybackLyricsPrefetch
{
    public async Task PrefetchAsync(PlaybackSignalPayload payload, CancellationToken cancellationToken)
    {
        var track = await tracks.ResolveAsync(payload, cancellationToken);
        if (track?.DurationMilliseconds == null) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await orchestrator.PrefetchLyricsAsync(track.Title, [track.Artist], track.Album,
            (int)Math.Clamp(track.DurationMilliseconds.Value / 1000, 0, int.MaxValue)).WaitAsync(timeout.Token);
    }
}
