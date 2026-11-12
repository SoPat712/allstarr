using allstarr.Core.ClientApi;
using allstarr.Core.Protocols;

namespace allstarr.Core.ClientApiV2;

/// <summary>Reads the views of one track with the same readers as the v1 routes, so both answer alike.</summary>
public sealed class V2TrackViewReader(ItemLyricsReader lyrics, ItemMatchReader matches, V2PlaybackReader playback, ILogger<V2TrackViewReader> logger)
{
    public static readonly IReadOnlySet<string> Names = new HashSet<string>(StringComparer.Ordinal) { "lyrics", "lyricsSources", "match", "autoplay", "playback" };

    /// <param name="source">The library or service item the track was read from.</param>
    public async Task<(V2TrackViews Views, IReadOnlyDictionary<string, string> Status)> ReadAsync(HttpContext http,
        ProtocolExecutionContext execution, string source, IReadOnlySet<string> requested, V2Track track, string tier)
    {
        var status = new Dictionary<string, string>(StringComparer.Ordinal);
        var views = new V2TrackViews();
        // The views share this request's per-request caches, so they are read one after another.
        if (requested.Contains("lyrics"))
            views = views with { Lyrics = await ReadAsync("lyrics", () => lyrics.ReadAsync(http, execution, source)) };
        if (requested.Contains("lyricsSources"))
            views = views with { LyricsSources = await ReadAsync("lyricsSources", () => lyrics.SourcesAsync(http, execution, source)) };
        if (requested.Contains("match"))
            views = views with { Match = await ReadAsync("match", () => matches.ReadAsync(http, execution, source)) };
        if (requested.Contains("playback"))
            views = views with
            {
                Playback = await ReadAsync<V2PlaybackResponse>("playback", async () =>
                await playback.ResolveTrackAsync(http, execution, track, source, new() { TrackId = track.Id, Tier = tier, Purpose = "prefetch" }))
            };
        return (views, status);

        async Task<T?> ReadAsync<T>(string name, Func<Task<T?>> read) where T : class
        {
            try
            {
                var value = await read();
                status[name] = value == null ? "none" : "ok";
                return value;
            }
            catch (Exception error) when (error is UnauthorizedAccessException or KeyNotFoundException or ArgumentException)
            {
                status[name] = "none";
            }
            catch (Exception error) when (error is not OperationCanceledException || !execution.CancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(error, "Track view {View} is unavailable", name);
                status[name] = "unavailable";
            }
            return null;
        }
    }
}
