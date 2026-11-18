using System.Collections.Concurrent;
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
    // One hit's search keeps the services that answered in time; waiting for the slowest would pass the whole budget.
    private static readonly TimeSpan SongSearchBudget = TimeSpan.FromSeconds(3);
    // A song search the time limit cut off keeps going; the next ask for the same line takes its answer instead of starting over.
    private static readonly ConcurrentDictionary<string, Task<string?>> Searching = new(StringComparer.Ordinal);
    private static readonly TimeSpan BackgroundBudget = TimeSpan.FromSeconds(30);
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

    internal const string Matched = "matched", Unmatched = "none", Pending = "pending";

    /// <summary>Each hit's song id and whether its lookup finished; a lookup cut off by the deadline stays pending.</summary>
    internal sealed record Matching(string?[] Ids, string[] States);

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
        var asked = await AskAllAsync(execution, query, limit, $"lyrics:services:hits:v1:{owner}:{limit}:{hash}");
        var (matched, states) = resolve ? await MatchAsync(http, execution, asked, $"lyrics:services:songs:v2:{owner}:{limit}:{hash}")
            : (new V2Track?[asked.Hits.Length], Enumerable.Repeat(Pending, asked.Hits.Length).ToArray());
        var results = asked.Hits.Select((hit, index) => new V2ServiceLyricsHit(hit.Title, hit.Artist,
            new(hit.Line), hit.Highlights, Provider(hit.ProviderId), matched[index])
        { MatchState = states[index] }).ToArray();
        var response = new V2ServiceLyricsSearchResponse(results, asked.Providers) { Resolved = resolve };
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

    // Only the songs' ids are kept, so a repeated line reads its tracks fresh; lookups the deadline cut off are tried again.
    private async Task<(V2Track?[] Tracks, string[] States)> MatchAsync(HttpContext http, ProtocolExecutionContext execution, Asked asked, string key)
    {
        var hits = asked.Hits;
        var kept = await cache.GetAsync<Matching>(key) is { } saved && saved.Ids.Length == hits.Length && saved.States.Length == hits.Length ? saved : null;
        var ids = kept?.Ids.ToArray() ?? new string?[hits.Length];
        var states = kept?.States.ToArray() ?? Enumerable.Repeat(Pending, hits.Length).ToArray();
        var open = Enumerable.Range(0, hits.Length).Where(index => states[index] == Pending).ToArray();
        if (open.Length > 0)
        {
            await FindAsync(execution, hits, open, ids, states, key);
            await cache.SetAsync(key, new Matching(ids, states), states.Contains(Pending) || !Complete(asked) ? KeptPartly : Kept);
        }
        var wanted = ids.OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
        if (wanted.Length == 0) return (new V2Track?[hits.Length], states);
        var read = await tracks.ReadAsync(http, execution, wanted, false);
        var byId = read.Tracks.SelectMany(track => track.Aliases.Prepend(track.Id).Select(id => (Id: id, Track: track)))
            .GroupBy(item => item.Id, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First().Track, StringComparer.Ordinal);
        var found = ids.Select(id => id != null && byId.TryGetValue(id, out var track) ? track : null).ToArray();
        return (found, states.Select((state, index) => state == Matched && found[index] == null ? Unmatched : state).ToArray());
    }

    private async Task FindAsync(ProtocolExecutionContext execution, IReadOnlyList<Found> hits, int[] open, string?[] ids, string[] states, string key)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(execution.CancellationToken);
        deadline.CancelAfter(MatchBudget);
        using var gate = new SemaphoreSlim(4);
        try
        {
            // The library is read first and once, so a library copy never waits behind slow service searches.
            var library = await LibraryCopiesAsync(execution, open.Select(index => hits[index]).ToArray(), deadline.Token);
            await Task.WhenAll(open.Select(async index =>
            {
                var hit = hits[index];
                if (library.FirstOrDefault(track => Same(hit, track.Title, track.Artist)) is { } local)
                {
                    (ids[index], states[index]) = (local.BackendItemId, Matched);
                    return;
                }
                await gate.WaitAsync(deadline.Token);
                // A service search still running at the deadline is left to finish on its own; the answer doesn't wait.
                try
                {
                    var slot = key + ":" + index.ToString(CultureInfo.InvariantCulture);
                    ids[index] = await Searching.GetOrAdd(slot, _ => Search(execution, hit, key, index, slot)).WaitAsync(deadline.Token);
                    states[index] = ids[index] == null ? Unmatched : Matched;
                }
                // A song that can't be matched in time stays pending for the next request.
                catch (Exception error) when (error is HttpRequestException or InvalidOperationException ||
                    error is OperationCanceledException && !execution.CancellationToken.IsCancellationRequested)
                { }
                finally { gate.Release(); }
            }));
        }
        catch (OperationCanceledException) when (!execution.CancellationToken.IsCancellationRequested) { }
    }

    private sealed record LibraryCopy(string BackendItemId, string Title, string Artist);

    // One read of the listener's library titles serves every hit.
    private async Task<IReadOnlyList<LibraryCopy>> LibraryCopiesAsync(ProtocolExecutionContext execution, IReadOnlyList<Found> hits, CancellationToken token)
    {
        if (hits.Count == 0) return [];
        var access = await libraries.ResolveAsync(execution, token);
        var keys = hits.Select(hit => SongKey(hit.Title)).ToHashSet(StringComparer.Ordinal);
        await using var db = await factory.CreateDbContextAsync(token);
        var titles = await LibraryTrackAccess.Query(db, execution, access)
            .Select(track => new LibraryCopy(track.BackendItemId, track.Title, track.Artist)).ToArrayAsync(token);
        return titles.Where(track => keys.Contains(SongKey(track.Title))).ToArray();
    }

    // Runs apart from the request, so it can finish after the answer was sent; its result then waits for the next ask.
    private Task<string?> Search(ProtocolExecutionContext execution, Found hit, string key, int index, string slot)
    {
        var budget = new CancellationTokenSource(BackgroundBudget);
        var search = ServiceCopyAsync(V2AutoplayReader.Bounded(execution, DateTimeOffset.UtcNow.Add(BackgroundBudget), budget.Token), hit);
        _ = search.ContinueWith(async done =>
        {
            budget.Dispose();
            if (!done.IsCompletedSuccessfully)
            {
                Searching.TryRemove(slot, out _);
                return;
            }
            if (await cache.GetAsync<Matching>(key) is { } kept && index < kept.States.Length && kept.States[index] == Pending)
            {
                kept.Ids[index] = done.Result;
                kept.States[index] = done.Result == null ? Unmatched : Matched;
                await cache.SetAsync(key, kept, KeptPartly);
            }
            await Task.Delay(TimeSpan.FromMinutes(2));
            Searching.TryRemove(slot, out _);
        }, TaskScheduler.Default).Unwrap();
        return search;
    }

    private async Task<string?> ServiceCopyAsync(ProtocolExecutionContext execution, Found hit)
    {
        var songs = await providers.SearchPlayableSongsWithinAsync(execution, $"{hit.Title} {FirstArtist(hit.Artist)}", 5, SongSearchBudget);
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
