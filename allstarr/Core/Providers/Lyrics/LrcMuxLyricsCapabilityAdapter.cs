using allstarr.Core.Caching;
using System.Text.Json;
using allstarr.Core.Capabilities;

namespace allstarr.Core.Providers.Lyrics;

public sealed class LrcMuxLyricsCapabilityAdapter(HttpClient http, IApplicationCache? cache, Func<TimeSpan>? lyricsTtl = null)
    : MetadataLyricsCapabilityAdapter(http, cache, TimeProvider.System, new(TimeProvider.System), null, lyricsTtl)
{
    public const string HttpClientName = "LrcMuxLyrics";
    public override string ProviderId => "lrcmux";
    protected override TimeSpan LookupBudget => TimeSpan.FromSeconds(3);
    private protected override LyricsSpeculationMode Speculation => LyricsSpeculationMode.RecentHealthy;
    protected override async Task<ProviderOutcome<ProviderLyricsResult>> ReadLyricsAsync(ProviderLyricsRequest request, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(request.TrackTitle) || request.ArtistNames.Count == 0 ||
            request.DurationSeconds is not > 0 && string.IsNullOrWhiteSpace(request.Isrc)) return Unavailable();
        var response = await GetAsync(new("https://api.lrcmux.dev/get?" + MetadataQuery(request, "title")), token, "application/json");
        if (response.Error != null) return ProviderOutcome<ProviderLyricsResult>.Failure(response.Error);
        if (response.Content == null) return Unavailable();
        using var document = JsonDocument.Parse(response.Content, new JsonDocumentOptions { MaxDepth = 16 });
        var root = document.RootElement;
        if (!root.TryGetProperty("track", out var track) || !root.TryGetProperty("lines", out var lines) || lines.ValueKind != JsonValueKind.Array) return ContractFailure();
        var title = Text(track, "title");
        var artist = Text(track, "artist");
        var duration = track.TryGetProperty("duration", out var seconds) && seconds.ValueKind == JsonValueKind.Number && seconds.TryGetDouble(out var number) ? number : (double?)null;
        if (request.DurationSeconds is > 0 && duration == null || !MatchesIdentity(request, title, artist, duration, Text(track, "album"))) return Unavailable();
        var isrc = Text(track, "isrc");
        if (request.DurationSeconds is not > 0 && (string.IsNullOrWhiteSpace(request.Isrc) ||
            string.IsNullOrWhiteSpace(isrc) || !string.Equals(request.Isrc, isrc, StringComparison.OrdinalIgnoreCase))) return Unavailable();
        if (duration.HasValue && (!double.IsFinite(duration.Value) || duration.Value <= 0)) return Unavailable();
        if (!string.IsNullOrWhiteSpace(request.Isrc) && !string.IsNullOrWhiteSpace(isrc) && !string.Equals(request.Isrc, isrc, StringComparison.OrdinalIgnoreCase)) return Unavailable();
        var wordTimed = root.TryGetProperty("meta", out var meta) && Text(meta, "level") == "word";
        var timed = new List<ProviderLyricsTimedLine>();
        var plain = new List<string>();
        foreach (var line in lines.EnumerateArray())
        {
            var text = Text(line, "text");
            if (string.IsNullOrWhiteSpace(text)) continue;
            plain.Add(text);
            if (!line.TryGetProperty("start", out var start) || start.ValueKind != JsonValueKind.Number || !start.TryGetInt64(out var ms)) continue;
            var durationMs = End(line) is { } end ? end - ms : (long?)null;
            var words = new List<ProviderLyricsTimedSegment>();
            if (wordTimed && line.TryGetProperty("words", out var segments) && segments.ValueKind == JsonValueKind.Array)
                foreach (var word in segments.EnumerateArray())
                {
                    var wordStart = word.GetProperty("start").GetInt64();
                    words.Add(new(Text(word, "text") ?? "", TimeSpan.FromMilliseconds(wordStart),
                        End(word) is { } wordEnd ? TimeSpan.FromMilliseconds(wordEnd - wordStart) : null));
                }
            timed.Add(new(text, TimeSpan.FromMilliseconds(ms), durationMs.HasValue ? TimeSpan.FromMilliseconds(durationMs.Value) : null, words));
        }
        if (plain.Count == 0) return Unavailable();
        var format = plain.Count == timed.Count ? ProviderLyricsFormat.LineTimed : ProviderLyricsFormat.PlainText;
        var content = format == ProviderLyricsFormat.PlainText ? string.Join('\n', plain) : string.Join('\n', timed.Select(line =>
        {
            var ms = (long)line.Start.TotalMilliseconds;
            return FormattableString.Invariant($"[{ms / 60_000:00}:{ms / 1000 % 60:00}.{ms % 1000:000}]{line.Text}");
        }));
        return ProviderOutcome<ProviderLyricsResult>.Success(new(ProviderLyricsAvailabilityState.Available, ProviderId, format, content,
            "sha256:" + Hash(JsonSerializer.Serialize(new { content, timed })), format == ProviderLyricsFormat.LineTimed ? timed : null)
        { Metadata = new(null, title, artist, Text(track, "album"), duration.HasValue ? (long)(duration.Value * 1000) : null) });
    }
    private static string? Text(JsonElement element, string key) => element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static long? End(JsonElement element) => element.TryGetProperty("end", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var ms) ? ms : null;
}
