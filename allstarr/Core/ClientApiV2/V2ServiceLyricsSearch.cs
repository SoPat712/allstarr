using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using allstarr.Core.Caching;
using allstarr.Core.ClientApi;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Providers.Lyrics;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.ClientApiV2;

/// <summary>Finds songs by a lyric line at lyrics services, and the playable song each one is.</summary>
public sealed partial class V2ServiceLyricsSearch(IHttpClientFactory clients, IApplicationCache cache, V2TrackReader tracks,
    IProtocolProviderGateway providers, IDbContextFactory<AllstarrDbContext> factory,
    [FromKeyedServices(ListeningLibraries.Key)] IBackendLibraryAccessResolver libraries)
{
    private static readonly TimeSpan ServiceBudget = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan MatchBudget = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Kept = TimeSpan.FromMinutes(30);
    // An answer missing a service is kept briefly, so a repeated line is quick without hiding the service for long.
    private static readonly TimeSpan KeptPartly = TimeSpan.FromMinutes(2);
    private static readonly (string Id, string Name, string Client)[] Services =
    [
        ("neteaselyrics", "NetEase", NetEaseWordLyricsCapabilityAdapter.HttpClientName),
        ("qqlyrics", "QQ Music", QqWordLyricsCapabilityAdapter.HttpClientName)
    ];

    internal sealed record Found(string Title, string Artist, string Line, IReadOnlyList<LyricsSearchHighlight> Highlights, string ProviderId);

    internal sealed record Asked(Found[] Hits, V2ServiceLyricsProvider[] Providers);

    /// <summary>
    /// With resolve false the services' hits come back at once without tracks; the same request again returns them,
    /// in the same order, with their playable songs, reusing the services' answer.
    /// </summary>
    public async Task<V2Result> SearchAsync(HttpContext http, ProtocolExecutionContext execution, string? q, int limit, bool resolve = true)
    {
        var query = q?.Trim() ?? "";
        if (query.Length is < 2 or > 200 || limit is < 1 or > 20 ||
            new[] { "q", "limit", "resolve" }.Any(key => http.Request.Query[key].Count > 1)) return new(400, null);
        var owner = execution.Actor?.EffectiveUserId ?? execution.Principal?.UserId;
        var hash = CanonicalCatalogKeys.Hash(query.ToLowerInvariant());
        var key = $"lyrics:services:v2:{owner}:{limit}:{hash}";
        if (resolve && await cache.GetAsync<V2ServiceLyricsSearchResponse>(key) is { } cached) return new(200, cached);
        var asked = await AskAllAsync(execution, query, limit, $"lyrics:services:hits:v1:{owner}:{limit}:{hash}");
        var matched = resolve ? await MatchAsync(http, execution, asked.Hits) : new V2Track?[asked.Hits.Length];
        var response = new V2ServiceLyricsSearchResponse(asked.Hits.Select((hit, index) => new V2ServiceLyricsHit(hit.Title, hit.Artist,
            new(hit.Line), hit.Highlights, Provider(hit.ProviderId), matched[index])).ToArray(), asked.Providers)
        { Resolved = resolve };
        if (resolve) await cache.SetAsync(key, response, Complete(asked) ? Kept : KeptPartly);
        return new(200, response);
    }

    private async Task<Asked> AskAllAsync(ProtocolExecutionContext execution, string query, int limit, string key)
    {
        if (await cache.GetAsync<Asked>(key) is { } cached) return cached;
        var asked = Services.Where(service => execution.Policy?.IsEnabled(service.Id) != false).ToArray();
        var answers = await Task.WhenAll(asked.Select(async service => (Service: service, Hits: await AskAsync(service, query, execution.CancellationToken))));
        var states = Services.Select(service => answers.FirstOrDefault(answer => answer.Service.Id == service.Id) is { Service.Id: not null } answer
            ? new V2ServiceLyricsProvider(service.Id, service.Name, answer.Hits == null ? "unavailable" : "ok")
            : new V2ServiceLyricsProvider(service.Id, service.Name, "off")).ToArray();
        var result = new Asked(Rank(answers.SelectMany(answer => answer.Hits ?? []), query).Take(limit).ToArray(), states);
        await cache.SetAsync(key, result, Complete(result) ? Kept : KeptPartly);
        return result;
    }

    private static bool Complete(Asked asked) => asked.Providers.All(state => state.State != "unavailable");

    private static V2Provider Provider(string id)
    {
        var url = $"/allstarr/v2/providers/{Uri.EscapeDataString(id)}/icon";
        return new(id, Services.Single(service => service.Id == id).Name, url, url + "?variant=glyph");
    }

    /// <summary>Lines with the whole phrase come first; the same song found twice keeps its first, best hit.</summary>
    internal static IEnumerable<Found> Rank(IEnumerable<Found> hits, string query)
    {
        var phrase = Normalize(query);
        return hits.Select((hit, order) => (Hit: hit, Order: order, Whole: Normalize(hit.Line).Contains(phrase, StringComparison.Ordinal)))
            .OrderByDescending(item => item.Whole).ThenBy(item => item.Order)
            .Select(item => item.Hit).DistinctBy(hit => SongKey(hit.Title) + "|" + SongKey(FirstArtist(hit.Artist)));
    }

    private async Task<IReadOnlyList<Found>?> AskAsync((string Id, string Name, string Client) service, string query, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(ServiceBudget);
        try
        {
            using var client = clients.CreateClient(service.Client);
            using var request = service.Id == "neteaselyrics"
                ? new HttpRequestMessage(HttpMethod.Post, "https://music.163.com/api/cloudsearch/pc")
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["s"] = query, ["type"] = "1006", ["limit"] = "10", ["offset"] = "0" })
                }
                : new HttpRequestMessage(HttpMethod.Get, "https://c.y.qq.com/soso/fcgi-bin/client_search_cp?t=7&format=json&n=10&p=1&w=" + Uri.EscapeDataString(query));
            request.Headers.TryAddWithoutValidation("Referer", service.Id == "neteaselyrics" ? "https://music.163.com" : "https://y.qq.com");
            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
            using var response = await client.SendAsync(request, deadline.Token);
            if (!response.IsSuccessStatusCode) return null;
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token));
            return service.Id == "neteaselyrics" ? NetEase(json.RootElement, query) : Qq(json.RootElement, query);
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or InvalidOperationException ||
            error is OperationCanceledException && !token.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>NetEase marks the matching words with &lt;b&gt;; the first marked line is the hit.</summary>
    internal static IReadOnlyList<Found> NetEase(JsonElement root, string query)
    {
        if (!root.TryGetProperty("result", out var result) || !result.TryGetProperty("songs", out var songs) || songs.ValueKind != JsonValueKind.Array) return [];
        var hits = new List<Found>();
        foreach (var song in songs.EnumerateArray())
        {
            var title = Text(song, "name");
            var artist = song.TryGetProperty("ar", out var artists) && artists.ValueKind == JsonValueKind.Array
                ? string.Join(", ", artists.EnumerateArray().Select(item => Text(item, "name")).Where(name => name.Length > 0)) : "";
            if (title.Length == 0 || artist.Length == 0 || !song.TryGetProperty("lyrics", out var lyrics) || lyrics.ValueKind != JsonValueKind.Array) continue;
            var lines = lyrics.EnumerateArray().Where(line => line.ValueKind == JsonValueKind.String).Select(line => line.GetString()!.Trim()).ToArray();
            var marked = lines.FirstOrDefault(line => line.Contains("<b>", StringComparison.OrdinalIgnoreCase));
            if (marked == null) continue;
            var (text, highlights) = Unmark(marked);
            if (text.Length > 0) hits.Add(new(title, artist, text, highlights.Count > 0 ? highlights : Highlight(text, query), "neteaselyrics"));
        }
        return hits;
    }

    /// <summary>QQ Music returns each song's whole lyrics; the hit is the first line holding the phrase.</summary>
    internal static IReadOnlyList<Found> Qq(JsonElement root, string query)
    {
        if (!root.TryGetProperty("data", out var data) || !data.TryGetProperty("lyric", out var lyric) ||
            !lyric.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array) return [];
        var phrase = Normalize(query);
        var hits = new List<Found>();
        foreach (var song in list.EnumerateArray())
        {
            var title = Text(song, "songname");
            var artist = song.TryGetProperty("singer", out var singers) && singers.ValueKind == JsonValueKind.Array
                ? string.Join(", ", singers.EnumerateArray().Select(item => Text(item, "name")).Where(name => name.Length > 0)) : "";
            var lines = Text(song, "content").Replace("\\n", "\n", StringComparison.Ordinal).Split('\n').Select(line => line.Trim());
            var line = lines.FirstOrDefault(line => phrase.Length > 0 && Normalize(line).Contains(phrase, StringComparison.Ordinal));
            if (title.Length > 0 && artist.Length > 0 && line != null) hits.Add(new(title, artist, line, Highlight(line, query), "qqlyrics"));
        }
        return hits;
    }

    private async Task<V2Track?[]> MatchAsync(HttpContext http, ProtocolExecutionContext execution, IReadOnlyList<Found> hits)
    {
        var ids = new string?[hits.Count];
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(execution.CancellationToken);
        deadline.CancelAfter(MatchBudget);
        using var gate = new SemaphoreSlim(4);
        try
        {
            var access = await libraries.ResolveAsync(execution, deadline.Token);
            await Task.WhenAll(hits.Select(async (hit, index) =>
            {
                await gate.WaitAsync(deadline.Token);
                // A service search still running at the deadline is left to finish on its own; the answer doesn't wait.
                try { ids[index] = await LibraryCopyAsync(execution, access, hit, deadline.Token) ?? await ServiceCopyAsync(execution, hit).WaitAsync(deadline.Token); }
                // A song that can't be matched in time stays title and artist only.
                catch (Exception error) when (error is HttpRequestException or InvalidOperationException ||
                    error is OperationCanceledException && !execution.CancellationToken.IsCancellationRequested)
                { }
                finally { gate.Release(); }
            }));
        }
        catch (OperationCanceledException) when (!execution.CancellationToken.IsCancellationRequested) { }
        var wanted = ids.OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
        if (wanted.Length == 0) return new V2Track?[hits.Count];
        var read = await tracks.ReadAsync(http, execution, wanted, false);
        var byId = read.Tracks.SelectMany(track => track.Aliases.Prepend(track.Id).Select(id => (Id: id, Track: track)))
            .GroupBy(item => item.Id, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First().Track, StringComparer.Ordinal);
        return ids.Select(id => id != null && byId.TryGetValue(id, out var track) ? track : null).ToArray();
    }

    private async Task<string?> LibraryCopyAsync(ProtocolExecutionContext execution, BackendLibraryAccess access, Found hit, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var start = hit.Title.Length > 3 ? hit.Title[..3].ToLowerInvariant() : hit.Title.ToLowerInvariant();
        var candidates = await LibraryTrackAccess.Query(db, execution, access).Where(track => track.Title.ToLower().StartsWith(start))
            .Select(track => new { track.BackendItemId, track.Title, track.Artist }).Take(200).ToArrayAsync(token);
        return candidates.FirstOrDefault(track => Same(hit, track.Title, track.Artist))?.BackendItemId;
    }

    private async Task<string?> ServiceCopyAsync(ProtocolExecutionContext execution, Found hit)
    {
        var songs = await providers.SearchPlayableSongsAsync(execution, $"{hit.Title} {FirstArtist(hit.Artist)}", 5);
        return songs.FirstOrDefault(song => Same(hit, song.Title, song.Artist))?.Id;
    }

    /// <summary>Same title once versions in brackets are set aside, and the hit's first artist among the song's artists.</summary>
    internal static bool Same(Found hit, string? title, string? artist) =>
        title != null && artist != null && SongKey(title) == SongKey(hit.Title) &&
        SongKey(artist).Contains(SongKey(FirstArtist(hit.Artist)), StringComparison.Ordinal);

    private static string FirstArtist(string artist) => artist.Split([", ", " & ", "/"], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? artist;

    private static string SongKey(string value) => Normalize(Brackets().Replace(value, " "));

    internal static string Normalize(string value)
    {
        var text = new StringBuilder(value.Length);
        foreach (var character in value.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            text.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : ' ');
        }
        return string.Join(' ', text.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static (string Text, IReadOnlyList<LyricsSearchHighlight> Highlights) Unmark(string marked)
    {
        var text = new StringBuilder();
        var highlights = new List<LyricsSearchHighlight>();
        var start = -1;
        for (var index = 0; index < marked.Length;)
        {
            if (string.Compare(marked, index, "<b>", 0, 3, StringComparison.OrdinalIgnoreCase) == 0) { start = text.Length; index += 3; continue; }
            if (string.Compare(marked, index, "</b>", 0, 4, StringComparison.OrdinalIgnoreCase) == 0)
            {
                if (start >= 0 && text.Length > start) highlights.Add(new(start, text.Length - start));
                start = -1;
                index += 4;
                continue;
            }
            text.Append(marked[index++]);
        }
        return (text.ToString().Trim(), highlights);
    }

    private static IReadOnlyList<LyricsSearchHighlight> Highlight(string line, string query)
    {
        var at = line.IndexOf(query.Trim(), StringComparison.OrdinalIgnoreCase);
        return at < 0 ? [] : [new(at, query.Trim().Length)];
    }

    private static string Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!.Trim() : "";

    [GeneratedRegex(@"[\(\[][^\)\]]*[\)\]]", RegexOptions.CultureInvariant)]
    private static partial Regex Brackets();
}
