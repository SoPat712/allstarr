using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using allstarr.Core.Capabilities;
using allstarr.Services.Common;

namespace allstarr.Core.Providers.Lyrics;

public sealed class AmllLyricsCapabilityAdapter(HttpClient http, IApplicationCache? cache, Func<TimeSpan>? lyricsTtl = null)
    : MetadataLyricsCapabilityAdapter(http, cache, TimeProvider.System, new(TimeProvider.System), null, lyricsTtl)
{
    public const string HttpClientName = "AmllLyrics";
    private const string Root = "https://raw.githubusercontent.com/amll-dev/amll-ttml-db/main/";
    private readonly SemaphoreSlim _indexGate = new(1, 1);
    private IReadOnlyList<Entry>? _index;
    private DateTimeOffset _expires;
    private sealed record Entry(string File, Dictionary<string, string[]> Metadata);
    public override string ProviderId => "amll";
    protected override bool RequiresArtist => false;
    protected override int MaximumResponseBytes => 8_000_000;
    protected override string CacheScope(ProviderLyricsRequest request) =>
        request.ProviderTrackId.Value.StartsWith("spotify:", StringComparison.Ordinal) ||
        request.ProviderTrackId.Value.StartsWith("apple:", StringComparison.Ordinal) ||
        request.ProviderTrackId.Value.StartsWith("netease:", StringComparison.Ordinal) ||
        request.ProviderTrackId.Value.StartsWith("qq:", StringComparison.Ordinal)
            ? "platform:" + Hash(request.ProviderTrackId.Value) + ":" : "";
    protected override TimeSpan LookupBudget => TimeSpan.FromSeconds(3);
    private protected override LyricsSpeculationMode Speculation => LyricsSpeculationMode.RecentHealthy;

    protected override async Task<ProviderOutcome<ProviderLyricsResult>> ReadLyricsAsync(ProviderLyricsRequest request, CancellationToken token)
    {
        if (request.DurationSeconds is not > 0 && string.IsNullOrWhiteSpace(request.Isrc) && CacheScope(request).Length == 0) return Unavailable();
        await _indexGate.WaitAsync(token);
        try
        {
            if (_index == null || _expires <= Clock.GetUtcNow())
            {
                var response = await GetAsync(new(Root + "metadata/raw-lyrics-index.jsonl"), token, "application/json");
                if (response.Error != null) return ProviderOutcome<ProviderLyricsResult>.Failure(response.Error);
                if (response.Content == null) return Unavailable();
                var entries = new List<Entry>();
                foreach (var row in response.Content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    using var document = JsonDocument.Parse(row, new JsonDocumentOptions { MaxDepth = 12 });
                    var root = document.RootElement;
                    if (!root.TryGetProperty("rawLyricFile", out var file) || file.ValueKind != JsonValueKind.String ||
                        !root.TryGetProperty("metadata", out var metadata) || metadata.ValueKind != JsonValueKind.Array) continue;
                    var name = file.GetString()!;
                    if (!name.EndsWith(".ttml", StringComparison.Ordinal) || name.Contains("..", StringComparison.Ordinal) ||
                        name.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.')) continue;
                    var values = new Dictionary<string, string[]>(StringComparer.Ordinal);
                    foreach (var item in metadata.EnumerateArray())
                        if (item.ValueKind == JsonValueKind.Array && item.GetArrayLength() == 2 &&
                            item[0].ValueKind == JsonValueKind.String && item[1].ValueKind == JsonValueKind.Array)
                            values[item[0].GetString()!] = item[1].EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String)
                                .Select(value => value.GetString()!).ToArray();
                    entries.Add(new(name, values));
                }
                _index = entries;
                _expires = Clock.GetUtcNow().AddHours(6);
            }
        }
        finally { _indexGate.Release(); }

        var candidates = _index!.Reverse().Where(entry => Exact(entry, request)).ToArray();
        var exact = candidates.Length > 0;
        if (!exact) candidates = _index!.Reverse().Where(entry => request.DurationSeconds is > 0 && CompatibleIsrc(entry, request) &&
            MatchesIdentity(request, Values(entry, "musicName").FirstOrDefault(), string.Join(", ", Values(entry, "artists")), album: Values(entry, "album").FirstOrDefault())).ToArray();
        foreach (var entry in candidates.Take(2))
        {
            var response = await GetAsync(new(Root + "raw-lyrics/" + entry.File), token, "application/ttml+xml");
            if (response.Error != null) return ProviderOutcome<ProviderLyricsResult>.Failure(response.Error);
            if (response.Content == null) continue;
            var result = ParseTtml(response.Content);
            if (!result.IsSuccess) return result;
            var duration = DocumentDuration(response.Content);
            if (request.DurationSeconds is > 0 && (duration == null && !exact || duration != null && Math.Abs(duration.Value - request.DurationSeconds.Value) > 5)) continue;
            return ProviderOutcome<ProviderLyricsResult>.Success(result.RequireValue() with
            {
                Metadata = new(entry.File, Values(entry, "musicName").FirstOrDefault(), string.Join(", ", Values(entry, "artists")),
                    Values(entry, "album").FirstOrDefault(), duration.HasValue ? (long)(duration.Value * 1000) : null)
            });
        }
        return Unavailable();
    }

    private static string[] Values(Entry entry, string key) => entry.Metadata.GetValueOrDefault(key) ?? [];
    private static bool CompatibleIsrc(Entry entry, ProviderLyricsRequest request) => string.IsNullOrWhiteSpace(request.Isrc) ||
        !Values(entry, "isrc").Any(value => !string.IsNullOrWhiteSpace(value)) || Values(entry, "isrc").Contains(request.Isrc, StringComparer.OrdinalIgnoreCase);
    private static bool Exact(Entry entry, ProviderLyricsRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Isrc) && Values(entry, "isrc").Contains(request.Isrc, StringComparer.OrdinalIgnoreCase)) return true;
        if (!CompatibleIsrc(entry, request)) return false;
        var id = request.ProviderTrackId.Value.Split(':', 2);
        var key = id[0] switch { "spotify" => "spotifyId", "apple" => "appleMusicId", "netease" => "ncmMusicId", "qq" => "qqMusicId", _ => "" };
        return id.Length == 2 && Values(entry, key).Contains(id[1], StringComparer.Ordinal);
    }

    private static double? DocumentDuration(string xml)
    {
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2_000_000 });
        var body = XDocument.Load(reader).Descendants().FirstOrDefault(element => element.Name.LocalName == "body");
        return body == null ? null : TtmlLyricsParser.ReadTime(body, "dur")?.TotalSeconds;
    }
}
