using allstarr.Core.ClientApi;
using allstarr.Core.Protocols;
using allstarr.Core.Settings;

namespace allstarr.Core.ClientApiV2;

/// <summary>A route's answer before it is written: a status and, for success, the body.</summary>
public readonly record struct V2Result(int Status, object? Body);

/// <summary>The track routes, shared by their controller and the batch call so both answer the same way.</summary>
public sealed class V2TrackEndpoints(V2TrackReader reader, V2TrackViewReader views, ItemLyricsReader lyrics,
    LyricsTranslation translation, IDurableRuntimeSettings settings, V2AutoplayReader autoplay, TimeProvider clock)
{
    public static readonly IReadOnlySet<string> Includes = new HashSet<string>(StringComparer.Ordinal) { "album" };

    public async Task<V2Result> TranslationAsync(HttpContext http, ProtocolExecutionContext execution, string id, string? to)
    {
        if (!(bool)(await settings.GetAsync("Lyrics:Translation:Enabled", execution.CancellationToken)).Value) return new(404, null);
        if (!LyricsTranslation.TryLanguage(to, out var language)) return new(400, null);
        var (result, sources) = await reader.ReadWithSourcesAsync(http, execution, [id], false);
        if (result.Tracks.Count == 0) return new(404, null);
        var trackId = result.Tracks[0].Id;
        var rich = await lyrics.ReadAsync(http, execution, sources[trackId]);
        if (rich == null) return new(404, null);
        return new(200, await translation.TranslateAsync(trackId, rich.Lines.Select(line => line.Text).ToArray(), language, execution.CancellationToken));
    }

    public async Task<V2Result> ListAsync(HttpContext http, ProtocolExecutionContext execution, string? ids, string? include)
    {
        if (!V2Query.TryIds(ids, out var requested) || !V2Query.TryNames(include, Includes, out var included)) return new(400, null);
        return new(200, await reader.ReadAsync(http, execution, requested, included.Contains("album")));
    }

    public async Task<V2Result> GetAsync(HttpContext http, ProtocolExecutionContext execution, string id, string? include, string? view, string? tier = null)
    {
        if (!V2Query.TryNames(include, Includes, out var included) || !V2Query.TryNames(view, V2TrackViewReader.Names, out var requested) ||
            tier != null && !V2PlaybackReader.ValidTier(tier))
            return new(400, null);
        var deadline = requested.Contains("autoplay") ? PageDeadline(execution) : execution.Deadline;
        using var timeout = requested.Contains("autoplay") ? new CancellationTokenSource(Remaining(deadline), clock) : null;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(execution.CancellationToken, timeout?.Token ?? execution.CancellationToken);
        var bounded = timeout == null ? execution : V2AutoplayReader.Bounded(execution, deadline, stop.Token);
        (V2TracksResponse result, IReadOnlyDictionary<string, string> sources) readTrack;
        try { readTrack = await reader.ReadWithSourcesAsync(http, bounded, [id], included.Contains("album")).WaitAsync(stop.Token); }
        catch (OperationCanceledException) when (!execution.CancellationToken.IsCancellationRequested) { return new(503, null); }
        var (result, sources) = readTrack;
        if (result.Tracks.Count == 0) return new(404, null);
        var track = result.Tracks[0];
        var response = new V2TrackResponse(track) { Albums = result.Albums };
        if (requested.Count == 0) return new(200, response);
        (V2TrackViews Views, IReadOnlyDictionary<string, string> Status) readViews;
        try { readViews = await views.ReadAsync(http, bounded, sources[track.Id], requested, track, tier ?? "high").WaitAsync(stop.Token); }
        catch (OperationCanceledException) when (!execution.CancellationToken.IsCancellationRequested)
        {
            readViews = (new(), requested.Where(name => name != "autoplay").ToDictionary(name => name, _ => "unavailable", StringComparer.Ordinal));
        }
        var (read, status) = readViews;
        if (requested.Contains("autoplay"))
        {
            var (page, partial) = await autoplay.ReadAsync(http, V2AutoplayReader.Bounded(execution, deadline, execution.CancellationToken), track, sources[track.Id], new(id, 0, 0, []), 25);
            read = read with { Autoplay = page };
            status = new Dictionary<string, string>(status) { ["autoplay"] = partial ? "partial" : page.Tracks.Count == 0 ? "none" : "ok" };
        }
        return new(200, response with { Views = read, ViewStatus = status });
    }

    public async Task<V2Result> AutoplayAsync(HttpContext http, ProtocolExecutionContext execution, string id, string? cursor, int limit)
    {
        if (limit is < 1 or > 50 || !V2AutoplayReader.TryCursor(id, cursor, out var position)) return new(400, null);
        var deadline = PageDeadline(execution);
        using var timeout = new CancellationTokenSource(Remaining(deadline), clock);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(execution.CancellationToken, timeout.Token);
        var bounded = V2AutoplayReader.Bounded(execution, deadline, stop.Token);
        (V2TracksResponse result, IReadOnlyDictionary<string, string> sources) readTrack;
        try { readTrack = await reader.ReadWithSourcesAsync(http, bounded, [id], false).WaitAsync(stop.Token); }
        catch (OperationCanceledException) when (!execution.CancellationToken.IsCancellationRequested) { return new(503, null); }
        var (result, sources) = readTrack;
        if (result.Tracks.Count == 0) return new(cursor == null ? 404 : 400, null);
        var track = result.Tracks[0];
        if (cursor != null && position.Seed != V2AutoplayReader.Identity(track)) return new(400, null);
        var (page, _) = await autoplay.ReadAsync(http, V2AutoplayReader.Bounded(execution, deadline, execution.CancellationToken), track, sources[track.Id], position, limit);
        return new(200, page);
    }

    private DateTimeOffset PageDeadline(ProtocolExecutionContext execution) =>
        execution.Deadline < clock.GetUtcNow().AddSeconds(5) ? execution.Deadline : clock.GetUtcNow().AddSeconds(5);

    private TimeSpan Remaining(DateTimeOffset deadline)
    {
        var duration = deadline - clock.GetUtcNow();
        return duration > TimeSpan.Zero ? duration : TimeSpan.Zero;
    }
}
