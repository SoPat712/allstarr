using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using allstarr.Core.Capabilities;
using allstarr.Core.Music;
using allstarr.Services.Common;

namespace allstarr.Core.Providers.Lyrics;

public sealed partial class PaxsenixCapabilityAdapter : PublicLyricsCapabilityAdapter
{
    public const string HttpClientName = "PaxsenixLyrics";
    private readonly PaxsenixRequestPacer _pacer;
    private readonly TimeProvider _clock;
    public override string ProviderId => "paxsenix";
    protected override TimeProvider Clock => _clock;
    protected override TimeSpan LookupBudget => TimeSpan.FromSeconds(18);
    protected override TimeSpan RequestTimeout => TimeSpan.FromSeconds(8);

    public PaxsenixCapabilityAdapter(HttpClient http, IApplicationCache? cache, Func<TimeSpan>? lyricsTtl = null)
        : this(http, cache, PaxsenixRequestPacer.Shared, TimeProvider.System, lyricsTtl) { }

    internal PaxsenixCapabilityAdapter(HttpClient http, IApplicationCache? cache,
        PaxsenixRequestPacer pacer, TimeProvider clock, Func<TimeSpan>? lyricsTtl = null)
        : base(http, cache, lyricsTtl)
    {
        _pacer = pacer;
        _clock = clock;
    }

    protected override Task BeforeRequestAsync(CancellationToken token) => _pacer.WaitAsync(token);

    protected override async Task<ProviderOutcome<ProviderLyricsResult>> ReadAsync(ProviderLyricsRequest request, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(request.TrackTitle) || request.ArtistNames.Count == 0) return Unavailable();
        var query = Uri.EscapeDataString(request.TrackTitle + " " + string.Join(" ", request.ArtistNames));
        foreach (var catalog in new[] { "qq", "netease", "kugou" })
        {
            var search = await GetAsync(new($"https://lyrics.paxsenix.org/{catalog}/search?q={query}"), token);
            if (search.Error != null) return ProviderOutcome<ProviderLyricsResult>.Failure(search.Error);
            if (search.Content == null) continue;
            using var document = JsonDocument.Parse(search.Content, new JsonDocumentOptions { MaxDepth = 16 });
            var id = Candidates(document.RootElement).Take(100)
                .Select(item => (Id: Identifier(item, catalog), Score: MatchScore(item, request)))
                .Where(item => item.Id != null && item.Score >= 0)
                .OrderByDescending(item => item.Score).Select(item => item.Id).FirstOrDefault();
            if (id == null) continue;
            var response = await GetAsync(new($"https://lyrics.paxsenix.org/{catalog}/lyrics?id={Uri.EscapeDataString(id)}&v=1" +
                (catalog is "netease" or "kugou" ? "&word=true" : "")), token);
            if (response.Error != null) return ProviderOutcome<ProviderLyricsResult>.Failure(response.Error);
            if (response.Content == null) continue;
            using var lyrics = JsonDocument.Parse(response.Content, new JsonDocumentOptions { MaxDepth = 16 });
            var text = LyricsText(lyrics.RootElement);
            if (text == null) return ContractFailure();
            if (string.IsNullOrWhiteSpace(text)) continue;
            text = text.Trim();
            if (text.StartsWith("<tt", StringComparison.Ordinal) || text.StartsWith("<?xml", StringComparison.Ordinal))
                return ParseTtml(text);
            if (text.StartsWith('<')) return ContractFailure();
            if (MillisecondLine().IsMatch(text)) text = ConvertWordTiming(text);
            var timed = MusicLyrics.ParseSyncedLines(text).Count > 0;
            return ProviderOutcome<ProviderLyricsResult>.Success(new(ProviderLyricsAvailabilityState.Available, ProviderId,
                timed ? ProviderLyricsFormat.LineTimed : ProviderLyricsFormat.PlainText, text, "sha256:" + Hash(text)));
        }
        return Unavailable();
    }

    private static IEnumerable<JsonElement> Candidates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
                foreach (var candidate in Candidates(item)) yield return candidate;
            yield break;
        }
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("Expected search results.");
        if (new[] { "id", "songId", "songmid", "hash" }.Any(key => value.TryGetProperty(key, out _)))
        {
            yield return value;
            yield break;
        }
        var found = false;
        foreach (var key in new[] { "results", "data", "result", "items", "tracks", "songs" })
        {
            if (!value.TryGetProperty(key, out var nested)) continue;
            found = true;
            if (nested.ValueKind == JsonValueKind.Null) continue;
            foreach (var candidate in Candidates(nested)) yield return candidate;
        }
        if (!found) throw new JsonException("Expected search results.");
    }

    private static string? Identifier(JsonElement item, string catalog) => Text(item, catalog switch
    {
        "qq" => ["songmid", "songId", "id"],
        "kugou" => ["hash", "id"],
        _ => ["id", "songId"]
    });

    private static double MatchScore(JsonElement item, ProviderLyricsRequest request)
    {
        var title = Text(item, ["title", "name", "songname", "songName"]);
        var artists = new List<string>();
        foreach (var key in new[] { "artists", "artist", "singer", "ar", "author" })
        {
            if (!item.TryGetProperty(key, out var value)) continue;
            ReadArtists(value, artists);
            if (artists.Count > 0) break;
        }
        if (title == null || artists.Count == 0) return -1;
        var expectedArtists = string.Join(", ", request.ArtistNames);
        var actualArtists = string.Join(", ", artists);
        if (!FuzzyMatcher.TitleAnnotationsMatch(request.TrackTitle!, expectedArtists, title, actualArtists) ||
            !FuzzyMatcher.SemanticVersionTags(request.TrackTitle).SetEquals(FuzzyMatcher.SemanticVersionTags(title))) return -1;
        var titleScore = FuzzyMatcher.CalculateSimilarity(request.TrackTitle!, title);
        var expectedTitle = FuzzyMatcher.NormalizeTitleForMatching(request.TrackTitle!, expectedArtists);
        var actualTitle = FuzzyMatcher.NormalizeTitleForMatching(title, actualArtists);
        if (Math.Min(expectedTitle.Length, actualTitle.Length) < Math.Max(expectedTitle.Length, actualTitle.Length) * 0.9) return -1;
        var artistScore = FuzzyMatcher.CalculateArtistMatchScore(request.ArtistNames.ToList(), artists[0], artists.Skip(1).ToList());
        if (titleScore < 90 || artistScore < 90) return -1;
        double durationScore = 0;
        if (request.DurationSeconds is > 0)
        {
            foreach (var key in new[] { "dt", "duration_ms", "durationSeconds", "duration", "interval" })
            {
                if (!item.TryGetProperty(key, out var value)) continue;
                if (!double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)) return -1;
                if (key is "dt" or "duration_ms") seconds /= 1000;
                if (!double.IsFinite(seconds) || seconds <= 0 || seconds > 86400) return -1;
                var difference = Math.Abs(seconds - request.DurationSeconds.Value);
                if (difference > 5) return -1;
                durationScore = 5 - difference;
                break;
            }
        }
        return titleScore + artistScore + durationScore;
    }

    private static void ReadArtists(JsonElement value, List<string> artists)
    {
        if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) ReadArtists(item, artists);
        else if (value.ValueKind == JsonValueKind.String)
            artists.AddRange(FuzzyMatcher.SplitCredits(value.GetString()));
        else if (value.ValueKind == JsonValueKind.Object && Text(value, ["name", "title"]) is { } name)
            artists.Add(name);
    }

    private static string? Text(JsonElement value, string[] keys)
    {
        foreach (var key in keys)
            if (value.TryGetProperty(key, out var item) && item.ValueKind is JsonValueKind.String or JsonValueKind.Number &&
                !string.IsNullOrWhiteSpace(item.ToString())) return item.ToString().Trim();
        return null;
    }

    private static string? LyricsText(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) return value.GetString();
        if (value.ValueKind == JsonValueKind.Array)
            return value.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String)
                ? string.Join('\n', value.EnumerateArray().Select(item => item.GetString())) : null;
        if (value.ValueKind != JsonValueKind.Object) return null;
        string? empty = null;
        foreach (var key in new[] { "ttml", "enhancedLyrics", "yrc", "krc", "syncedLyrics", "lrc", "lyrics", "plainLyrics", "lyric", "content", "data", "result" })
        {
            if (!value.TryGetProperty(key, out var nested)) continue;
            var text = LyricsText(nested);
            if (!string.IsNullOrWhiteSpace(text)) return text;
            if (text != null) empty = text;
        }
        return empty;
    }

    private static string ConvertWordTiming(string text)
    {
        var output = new StringBuilder();
        foreach (var line in text.Replace("\r", "", StringComparison.Ordinal).Split('\n'))
        {
            var header = MillisecondLine().Match(line);
            if (!header.Success)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (MetadataLine().IsMatch(line))
                {
                    output.Append(line).Append('\n');
                    continue;
                }
                throw new JsonException("Invalid word timing line.");
            }
            var start = Milliseconds(header.Groups[1].Value);
            var duration = Milliseconds(header.Groups[2].Value);
            if (start + duration > 86_400_000) throw new JsonException("Invalid word timing duration.");
            var words = MillisecondWord().Matches(line, header.Length);
            if (words.Count == 0 || words[0].Index != header.Length) throw new JsonException("Expected word timing.");
            output.Append(Timestamp(start, '['));
            long previousEnd = start;
            for (var index = 0; index < words.Count; index++)
            {
                var word = words[index];
                var relative = word.Groups[3].Success;
                var wordStart = Milliseconds(word.Groups[relative ? 3 : 1].Value) + (relative ? start : 0);
                var wordEnd = wordStart + Milliseconds(word.Groups[relative ? 4 : 2].Value);
                if (wordStart < previousEnd || wordEnd > start + duration) throw new JsonException("Invalid word timing range.");
                var end = index + 1 < words.Count ? words[index + 1].Index : line.Length;
                output.Append(Timestamp(wordStart, '<')).Append(line.AsSpan(word.Index + word.Length, end - word.Index - word.Length));
                if (index + 1 == words.Count) output.Append(Timestamp(wordEnd, '<'));
                previousEnd = wordEnd;
            }
            output.Append('\n');
        }
        return output.ToString().TrimEnd();
    }

    private static long Milliseconds(string value)
    {
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var ms) || ms > 86_400_000)
            throw new JsonException("Invalid lyric timestamp.");
        return ms;
    }

    private static string Timestamp(long ms, char opening) =>
        FormattableString.Invariant($"{opening}{ms / 60_000:00}:{ms / 1000 % 60:00}.{ms % 1000:000}{(opening == '[' ? ']' : '>')}");

    [GeneratedRegex(@"^\[(\d+),(\d+)\]", RegexOptions.Multiline)]
    private static partial Regex MillisecondLine();
    [GeneratedRegex(@"\((\d+),(\d+),\d+\)|<(\d+),(\d+),\d+>")]
    private static partial Regex MillisecondWord();
    [GeneratedRegex(@"^\[[a-zA-Z]+:[^\r\n]*\]$")]
    private static partial Regex MetadataLine();
}

internal sealed class PaxsenixRequestPacer(TimeProvider clock, TimeSpan? interval = null)
{
    internal static readonly PaxsenixRequestPacer Shared = new(TimeProvider.System);
    private readonly SemaphoreSlim _requests = new(1, 1);
    private long? _lastRequest;

    public async Task WaitAsync(CancellationToken token)
    {
        await _requests.WaitAsync(token);
        try
        {
            var spacing = interval ?? TimeSpan.FromSeconds(2);
            if (_lastRequest is { } previous)
            {
                var elapsed = clock.GetElapsedTime(previous);
                if (elapsed < spacing) await Task.Delay(spacing - elapsed, clock, token);
            }
            token.ThrowIfCancellationRequested();
            _lastRequest = clock.GetTimestamp();
        }
        finally { _requests.Release(); }
    }
}
