using System.Globalization;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Services.Common;

namespace allstarr.Core.Providers.Lyrics;

public abstract class ChineseWordLyricsCapabilityAdapter : MetadataLyricsCapabilityAdapter
{
    protected override bool RequiresArtist => true;
    protected override TimeSpan LookupBudget => TimeSpan.FromSeconds(3);
    private protected override LyricsSpeculationMode Speculation => LyricsSpeculationMode.RecentHealthy;
    protected override string CacheScope(ProviderLyricsRequest request) => "native-words:v1:";

    private protected ChineseWordLyricsCapabilityAdapter(HttpClient http, IApplicationCache? cache, TimeProvider clock,
        MetadataLyricsFailureCache failures, ILogger? logger, Func<TimeSpan>? lyricsTtl)
        : base(http, cache, clock, failures, logger, lyricsTtl) { }

    protected sealed override async Task<ProviderOutcome<ProviderLyricsResult>> ReadLyricsAsync(ProviderLyricsRequest request, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(request.TrackTitle) || request.ArtistNames.Count == 0 ||
            request.DurationSeconds is not > 0 && string.IsNullOrWhiteSpace(request.Isrc)) return Unavailable();
        var response = await GetAsync(SearchUri(request), token, "application/json");
        if (response.Error != null) return ProviderOutcome<ProviderLyricsResult>.Failure(response.Error);
        if (string.IsNullOrWhiteSpace(response.Content)) return Unavailable();
        using var document = JsonDocument.Parse(response.Content, new JsonDocumentOptions { MaxDepth = 16 });
        var candidates = ReadCandidates(document.RootElement).Where(candidate => MatchesCandidate(request, candidate)).Take(3).ToArray();
        foreach (var candidate in candidates)
        {
            var download = await DownloadAsync(candidate, request, token);
            if (download.Error != null) return ProviderOutcome<ProviderLyricsResult>.Failure(download.Error);
            if (string.IsNullOrWhiteSpace(download.Text)) continue;
            ProviderOutcome<ProviderLyricsResult> parsed;
            try { parsed = ParseNative(download.Text); }
            catch (FormatException) { return ContractFailure(); }
            if (!parsed.IsSuccess) return parsed;
            var value = parsed.RequireValue();
            if (value.Availability != ProviderLyricsAvailabilityState.Available) continue;
            if (value.Metadata is { } metadata && !MatchesIdentity(request, metadata.Title ?? candidate.Title,
                    metadata.Artist ?? candidate.Artist, candidate.DurationSeconds, metadata.Album ?? candidate.Album)) continue;
            return ProviderOutcome<ProviderLyricsResult>.Success(value with
            {
                Metadata = new(candidate.Id, candidate.Title, candidate.Artist, candidate.Album,
                    candidate.DurationSeconds is { } duration ? (long)Math.Round(duration * 1000) : null)
            });
        }
        return Unavailable();
    }

    protected abstract Uri SearchUri(ProviderLyricsRequest request);
    protected abstract IEnumerable<LyricCandidate> ReadCandidates(JsonElement root);
    protected abstract Task<(string? Text, ProviderError? Error)> DownloadAsync(LyricCandidate candidate, ProviderLyricsRequest request, CancellationToken token);
    protected abstract ProviderOutcome<ProviderLyricsResult> ParseNative(string content);

    protected static bool MatchesCandidate(ProviderLyricsRequest request, LyricCandidate candidate) =>
        candidate.Id.Length > 0 && (candidate.DurationSeconds == null || candidate.DurationSeconds is > 0 and <= 86_400) &&
        (request.DurationSeconds is not > 0 || candidate.DurationSeconds is > 0) &&
        MatchesIdentity(request, candidate.Title, candidate.Artist, candidate.DurationSeconds, candidate.Album) &&
        IsrcMatches(request, candidate.Isrc);

    protected static bool IsrcMatches(ProviderLyricsRequest request, string? returned) =>
        string.IsNullOrWhiteSpace(request.Isrc) || string.IsNullOrWhiteSpace(returned) ||
        string.Equals(request.Isrc.Trim(), returned.Trim(), StringComparison.OrdinalIgnoreCase);

    protected static JsonElement Object(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
            throw new JsonException("Missing lyric response object.");
        return value;
    }

    protected static JsonElement Array(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 100)
            throw new JsonException("Missing or oversized lyric candidates.");
        return value;
    }

    protected static string? Text(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object) throw new JsonException("Invalid lyric response item.");
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw new JsonException("Invalid lyric response text.");
        return value.GetString();
    }

    protected static string Id(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value)) return string.Empty;
        var id = value.ValueKind == JsonValueKind.String ? value.GetString() :
            value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) && number > 0 ? value.GetRawText() : null;
        return id is { Length: > 0 and <= 200 } && !id.Any(char.IsControl) ? id : string.Empty;
    }

    protected static double? Number(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object) throw new JsonException("Invalid lyric response item.");
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number))
            throw new JsonException("Invalid lyric response number.");
        return number;
    }

    protected static void RequireCode(JsonElement parent, string name, int expected)
    {
        if (Number(parent, name) != expected) throw new JsonException("Unexpected lyric response status.");
    }

    protected static string Credits(JsonElement item, string property) => string.Join(", ", Array(item, property).EnumerateArray()
        .Select(artist => Text(artist, "name")).Where(name => !string.IsNullOrWhiteSpace(name)));

    protected static string Keyword(ProviderLyricsRequest request) => Uri.EscapeDataString(request.TrackTitle!.Trim() + " " + string.Join(" ", request.ArtistNames));

    protected sealed record LyricCandidate(string Id, string Title, string Artist, string? Album, double? DurationSeconds,
        string? Isrc = null, string? AccessKey = null);
}

public sealed class NetEaseWordLyricsCapabilityAdapter : ChineseWordLyricsCapabilityAdapter
{
    public const string HttpClientName = "NetEaseLyrics";
    private static readonly MetadataLyricsFailureCache Failures = new(TimeProvider.System);
    public override string ProviderId => "neteaselyrics";

    public NetEaseWordLyricsCapabilityAdapter(HttpClient http, IApplicationCache? cache, Func<TimeSpan>? lyricsTtl = null,
        ILogger<NetEaseWordLyricsCapabilityAdapter>? logger = null)
        : base(http, cache, TimeProvider.System, Failures, logger, lyricsTtl) { }

    protected override Uri SearchUri(ProviderLyricsRequest request) =>
        new("https://music.163.com/api/search/get?s=" + Keyword(request) + "&type=1&limit=10&offset=0");

    protected override IEnumerable<LyricCandidate> ReadCandidates(JsonElement root)
    {
        RequireCode(root, "code", 200);
        var result = Object(root, "result");
        if (!result.TryGetProperty("songs", out _) && Number(result, "songCount") == 0) yield break;
        foreach (var song in Array(result, "songs").EnumerateArray())
        {
            var title = Text(song, "name");
            var artist = Credits(song, song.TryGetProperty("artists", out _) ? "artists" : "ar");
            var duration = Number(song, "duration") ?? Number(song, "dt");
            var album = song.TryGetProperty("album", out var item) || song.TryGetProperty("al", out item)
                ? Text(item, "name") : null;
            yield return new(Id(song, "id"), title ?? string.Empty, artist, album, duration / 1000, Text(song, "isrc"));
        }
    }

    protected override async Task<(string? Text, ProviderError? Error)> DownloadAsync(LyricCandidate candidate, ProviderLyricsRequest request, CancellationToken token)
    {
        var response = await GetAsync(new("https://music.163.com/api/song/lyric/v1?id=" + Uri.EscapeDataString(candidate.Id) +
            "&cp=false&lv=0&kv=0&tv=0&rv=0&yv=0&ytv=0&yrv=0"), token, "application/json");
        if (response.Error != null || response.Content == null) return response;
        using var document = JsonDocument.Parse(response.Content, new JsonDocumentOptions { MaxDepth = 16 });
        var root = document.RootElement;
        RequireCode(root, "code", 200);
        if (!IsrcMatches(request, Text(root, "isrc")) || !root.TryGetProperty("yrc", out var yrc) || yrc.ValueKind == JsonValueKind.Null)
            return (null, null);
        return (Text(yrc, "lyric"), null);
    }

    protected override ProviderOutcome<ProviderLyricsResult> ParseNative(string content) => ChineseWordLyricsParser.ParseYrc(content, ProviderId);
}

public sealed class QqWordLyricsCapabilityAdapter : ChineseWordLyricsCapabilityAdapter
{
    public const string HttpClientName = "QqLyrics";
    private static readonly MetadataLyricsFailureCache Failures = new(TimeProvider.System);
    public override string ProviderId => "qqlyrics";

    public QqWordLyricsCapabilityAdapter(HttpClient http, IApplicationCache? cache, Func<TimeSpan>? lyricsTtl = null,
        ILogger<QqWordLyricsCapabilityAdapter>? logger = null)
        : base(http, cache, TimeProvider.System, Failures, logger, lyricsTtl) { }

    protected override Uri SearchUri(ProviderLyricsRequest request) => MusicUri(new
    {
        req_1 = new
        {
            module = "music.search.SearchCgiService",
            method = "DoSearchForQQMusicDesktop",
            param = new { num_per_page = 10, page_num = 1, query = request.TrackTitle!.Trim() + " " + string.Join(" ", request.ArtistNames), search_type = 0 }
        }
    });

    protected override IEnumerable<LyricCandidate> ReadCandidates(JsonElement root)
    {
        var data = ResponseData(root);
        foreach (var song in Array(Object(Object(data, "body"), "song"), "list").EnumerateArray())
        {
            var title = Text(song, "title") ?? Text(song, "name") ?? string.Empty;
            var subtitle = Text(song, "subtitle");
            if (!string.IsNullOrWhiteSpace(subtitle)) title += " (" + subtitle + ")";
            yield return new(Id(song, "id"), title, Credits(song, "singer"), Text(Object(song, "album"), "name"),
                Number(song, "interval"), Text(song, "isrc"));
        }
    }

    protected override async Task<(string? Text, ProviderError? Error)> DownloadAsync(LyricCandidate candidate, ProviderLyricsRequest request, CancellationToken token)
    {
        if (!long.TryParse(candidate.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var songId)) return (null, null);
        var response = await GetAsync(MusicUri(new
        {
            req_1 = new
            {
                module = "music.musichallSong.PlayLyricInfo",
                method = "GetPlayLyricInfo",
                param = new
                {
                    songID = songId,
                    songName = Base64(candidate.Title),
                    singerName = Base64(candidate.Artist),
                    albumName = Base64(candidate.Album ?? string.Empty),
                    interval = (int)Math.Round(candidate.DurationSeconds ?? 0),
                    qrc = 1,
                    crypt = 1,
                    ct = 19,
                    cv = 0
                }
            }
        }), token, "application/json");
        if (response.Error != null || response.Content == null) return response;
        using var document = JsonDocument.Parse(response.Content, new JsonDocumentOptions { MaxDepth = 16 });
        var data = ResponseData(document.RootElement);
        if (data.TryGetProperty("songID", out var returnedId) && returnedId.ValueKind != JsonValueKind.Null && Id(data, "songID") != candidate.Id ||
            !IsrcMatches(request, Text(data, "isrc")))
            return (null, null);
        var returnedTitle = Text(data, "songName");
        var returnedArtist = Text(data, "singerName");
        if (!string.IsNullOrWhiteSpace(returnedTitle) && !string.IsNullOrWhiteSpace(returnedArtist) &&
            !MatchesIdentity(request, DecodeBase64(returnedTitle), DecodeBase64(returnedArtist), candidate.DurationSeconds)) return (null, null);
        var lyrics = Text(data, "lyric");
        if (string.IsNullOrWhiteSpace(lyrics) || Number(data, "qrc") == 0) return (null, null);
        if (Number(data, "qrc") != 1 || Number(data, "crypt") != 1) throw new JsonException("Unexpected native lyric encoding.");
        return (lyrics, null);
    }

    protected override ProviderOutcome<ProviderLyricsResult> ParseNative(string content) =>
        ChineseWordLyricsParser.ParseQrc(ChineseWordLyricsParser.DecodeQrc(content), ProviderId);

    private static JsonElement ResponseData(JsonElement root)
    {
        RequireCode(root, "code", 0);
        var result = Object(root, "req_1");
        RequireCode(result, "code", 0);
        return Object(result, "data");
    }

    private static Uri MusicUri<T>(T body) => new("https://u.y.qq.com/cgi-bin/musicu.fcg?data=" + Uri.EscapeDataString(JsonSerializer.Serialize(body)));
    private static string Base64(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    private static string DecodeBase64(string value)
    {
        try { return new UTF8Encoding(false, true).GetString(Convert.FromBase64String(value)); }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException)
        { throw new JsonException("Invalid lyric identity encoding."); }
    }
}

public sealed class KugouWordLyricsCapabilityAdapter : ChineseWordLyricsCapabilityAdapter
{
    public const string HttpClientName = "KugouLyrics";
    private static readonly MetadataLyricsFailureCache Failures = new(TimeProvider.System);
    public override string ProviderId => "kugoulyrics";

    public KugouWordLyricsCapabilityAdapter(HttpClient http, IApplicationCache? cache, Func<TimeSpan>? lyricsTtl = null,
        ILogger<KugouWordLyricsCapabilityAdapter>? logger = null)
        : base(http, cache, TimeProvider.System, Failures, logger, lyricsTtl) { }

    protected override Uri SearchUri(ProviderLyricsRequest request) => new("https://lyrics.kugou.com/search?ver=1&man=yes&client=pc&keyword=" + Keyword(request) +
        (request.DurationSeconds is > 0 ? "&duration=" + ((long)request.DurationSeconds.Value * 1000).ToString(CultureInfo.InvariantCulture) : string.Empty));

    protected override IEnumerable<LyricCandidate> ReadCandidates(JsonElement root)
    {
        RequireCode(root, "status", 200);
        foreach (var candidate in Array(root, "candidates").EnumerateArray())
        {
            var key = Text(candidate, "accesskey");
            if (string.IsNullOrWhiteSpace(key) || key.Length > 500) continue;
            yield return new(Id(candidate, "id"), Text(candidate, "song") ?? string.Empty,
                Text(candidate, "singer") ?? string.Empty, null, Number(candidate, "duration") / 1000,
                Text(candidate, "isrc"), key);
        }
    }

    protected override async Task<(string? Text, ProviderError? Error)> DownloadAsync(LyricCandidate candidate, ProviderLyricsRequest request, CancellationToken token)
    {
        var response = await GetAsync(new("https://lyrics.kugou.com/download?ver=1&client=pc&id=" + Uri.EscapeDataString(candidate.Id) +
            "&accesskey=" + Uri.EscapeDataString(candidate.AccessKey!) + "&fmt=krc&charset=utf8"), token, "application/json");
        if (response.Error != null || response.Content == null) return response;
        using var document = JsonDocument.Parse(response.Content, new JsonDocumentOptions { MaxDepth = 16 });
        var root = document.RootElement;
        RequireCode(root, "status", 200);
        if (root.TryGetProperty("id", out var returnedId) && returnedId.ValueKind != JsonValueKind.Null && Id(root, "id") != candidate.Id ||
            !IsrcMatches(request, Text(root, "isrc")))
            return (null, null);
        return (Text(root, "content"), null);
    }

    protected override ProviderOutcome<ProviderLyricsResult> ParseNative(string content) =>
        ChineseWordLyricsParser.ParseKrc(ChineseWordLyricsParser.DecodeKrc(content), ProviderId);
}
