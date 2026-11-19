using System.Diagnostics;
using System.Text.Json;
using allstarr.Controllers.ClientApi;
using allstarr.Core.ClientApi;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Services.Local;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Core.ClientApiV2;

/// <summary>Listener-scoped search reads using the shared ranked stream and stored lyrics index.</summary>
public sealed class V2SearchEndpoints(V2SearchStream stream, V2TrackReader tracks, LyricsSearchReader lyrics,
    ILogger<V2SearchEndpoints>? logger = null)
{
    private static readonly IReadOnlySet<string> Types = new HashSet<string>(StringComparer.Ordinal) { "songs", "albums", "artists", "playlists" };

    // Turning a snapshot's songs into tracks is what this stream adds to the ranked one; a slow first one is worth a line.
    private static readonly TimeSpan SlowFirstRead = TimeSpan.FromMilliseconds(250);

    public async Task<IActionResult> StreamAsync(HttpContext http, ProtocolExecutionContext execution, string? q, int limit, string? types)
    {
        var query = q?.Trim() ?? "";
        if (query.Length is < 1 or > 200 || limit is < 1 or > 200 ||
            !V2Query.TryNames(types, Types, out var selected) || (types != null || http.Request.Query.ContainsKey("types")) && selected.Count == 0 ||
            new[] { "q", "limit", "types" }.Any(key => http.Request.Query[key].Count > 1)) return new BadRequestResult();
        if (types == null) selected = new(Types, StringComparer.Ordinal);
        var requested = selected.Select(type => type switch { "songs" => "Audio", "albums" => "MusicAlbum", "artists" => "MusicArtist", _ => "Playlist" })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var read = new Dictionary<string, V2Track?>(StringComparer.Ordinal);
        var started = Stopwatch.GetTimestamp();
        var untimed = true;
        return await stream.StreamAsync(http, execution, query, limit, requested, true, async (batch, token) =>
        {
            var rows = batch.Sections.Values.SelectMany(rows => rows).Concat(batch.Items)
                .Concat(batch.Top == null ? [] : new[] { batch.Top }).DistinctBy(row => Id(row)).ToArray();
            var songs = rows.Where(row => row.Section == "songs" && !read.ContainsKey(Id(row))).ToArray();
            if (songs.Length > 0)
            {
                var metadata = songs.Where(row => row.Song != null).ToDictionary(Id, row => row.Song!, StringComparer.Ordinal);
                var current = new ProtocolExecutionContext(execution.Protocol, execution.BackendInstanceId, execution.VerifiedBackendPrincipalId,
                    execution.Principal, execution.CorrelationId, execution.Deadline, token, execution.Client)
                { Policy = execution.Policy };
                // The first read is timed on its own, apart from the services still answering beside it.
                using var timing = untimed ? RequestTiming.Begin() : null;
                var result = await tracks.ReadWithSourcesAsync(http, current, songs.Select(Id).ToArray(), false, metadata);
                var byId = Index(result.Response.Tracks);
                foreach (var row in songs) read[Id(row)] = byId.GetValueOrDefault(Id(row));
                if (timing?.Snapshot("search") is { } took)
                    logger?.Log(took.TotalMs >= SlowFirstRead.TotalMilliseconds ? LogLevel.Information : LogLevel.Debug,
                        "Search read its first {Songs} songs in {ReadMs:0} ms, {WaitedMs:0} ms into the search ({Timing})",
                        songs.Length, took.TotalMs, Stopwatch.GetElapsedTime(started).TotalMilliseconds - took.TotalMs, took.Header);
                untimed = false;
            }
            var hits = rows.Select(row => Project(row, read.GetValueOrDefault(Id(row)))).Where(hit => hit.HasValue)
                .Select(hit => hit!.Value).ToDictionary(hit => hit.Key, hit => hit.Value);
            V2SearchHit[] Hits(IEnumerable<RankedSearchRow> values) => OneHitPerTrack(values
                .Select(row => (Source: Id(row), Hit: hits.GetValueOrDefault(Id(row)))).Where(pair => pair.Hit != null)
                .Select(pair => (pair.Source, pair.Hit!)));
            var items = Hits(batch.Items);
            var sections = batch.Sections.ToDictionary(pair => pair.Key, pair => Hits(pair.Value));
            var first = batch.Top == null ? null : hits.GetValueOrDefault(Id(batch.Top));
            // The top hit carries the same alsoOn as its row in the lists.
            var top = first == null ? null : sections.Values.SelectMany(values => values).Concat(items)
                .FirstOrDefault(hit => hit.Kind == first.Kind && hit.Id == first.Id) ?? first;
            await V2SearchStream.WriteResponseAsync(http, new V2SearchSnapshot(batch.Revision, batch.Late != null, batch.Providers,
                top, items, sections, batch.Late), token);
        });
    }

    public async Task<V2Result> LyricsAsync(HttpContext http, ProtocolExecutionContext execution, string? q, int limit)
    {
        var query = q?.Trim() ?? "";
        if (query.Length is < 2 or > 200 || limit is < 1 or > 50 ||
            new[] { "q", "limit" }.Any(key => http.Request.Query[key].Count > 1)) return new(400, null);
        var result = await lyrics.ReadAsync(http, execution, query, limit);
        var found = await tracks.ReadAsync(http, execution, result.Results.Select(hit => hit.ItemId).ToArray(), false);
        var byId = Index(found.Tracks);
        // A service song whose lyrics were found before anyone played it has no stored details yet; its hit names it.
        var named = result.Results.Where(hit => hit.IsExternal && !byId.ContainsKey(hit.ItemId))
            .Select(hit => (Hit: hit, Parsed: LocalLibraryService.ParseExternalResource(hit.ItemId)))
            .Where(item => item.Parsed is { isExternal: true, type: "song", provider: not null, externalId: not null })
            .DistinctBy(item => item.Hit.ItemId)
            .ToDictionary(item => item.Hit.ItemId, item => new Song
            {
                Id = item.Hit.ItemId,
                Title = item.Hit.Title,
                Artist = item.Hit.Artist,
                Artists = [item.Hit.Artist],
                Album = item.Hit.Album ?? "",
                AlbumId = item.Hit.AlbumId,
                ExternalProvider = item.Parsed.provider,
                ExternalId = item.Parsed.externalId
            }, StringComparer.Ordinal);
        if (named.Count > 0)
            foreach (var pair in Index((await tracks.ReadWithSourcesAsync(http, execution, named.Keys.ToArray(), false, named)).Response.Tracks))
                byId.TryAdd(pair.Key, pair.Value);
        var hits = result.Results.Where(hit => byId.ContainsKey(hit.ItemId)).Select(hit =>
            new V2LyricsSearchHit(byId[hit.ItemId], hit.Line, hit.Highlights, hit.Score, hit.Source)).ToArray();
        return new(200, new V2LyricsSearchResponse(hits, result.Truncated));
    }

    private static Dictionary<string, V2Track> Index(IReadOnlyList<V2Track> values) => values
        .SelectMany(track => track.Aliases.Prepend(track.Id).Select(id => (Id: id, Track: track)))
        .GroupBy(item => item.Id, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First().Track, StringComparer.Ordinal);

    private static string Id(RankedSearchRow row) => row.Item["Id"]!.GetValue<string>();

    // Song rows the track read finds to be one recording share a track id, so they are one hit: the first keeps its place
    // and lists the others' sources in alsoOn.
    private static V2SearchHit[] OneHitPerTrack(IEnumerable<(string Source, V2SearchHit Hit)> values)
    {
        var result = new List<(string Source, V2SearchHit Hit)>();
        foreach (var (source, hit) in values)
        {
            var index = hit.Kind == "track" ? result.FindIndex(known => known.Hit.Kind == "track" && known.Hit.Id == hit.Id) : -1;
            if (index < 0)
            {
                result.Add((source, hit));
                continue;
            }
            var known = result[index];
            var also = (known.Hit.AlsoOn ?? Own(known.Hit, known.Source)).ToList();
            foreach (var entry in hit.AlsoOn ?? Own(hit, source))
                if (also.All(item => item.Source != entry.Source)) also.Add(entry);
            result[index] = (known.Source, known.Hit with { AlsoOn = also });
        }
        return result.Select(item => item.Hit).ToArray();
    }

    private static IReadOnlyList<V2SearchSource> Own(V2SearchHit hit, string source) =>
        V2Ids.IsValidSource(source, V2IdKind.Track) ? [new(hit.Source, hit.SourceName, V2Ids.TrackSource(source))] : [];

    private static KeyValuePair<string, V2SearchHit>? Project(RankedSearchRow row, V2Track? track)
    {
        var item = JsonSerializer.SerializeToElement(row.Item);
        var source = Id(row);
        var name = row.Artist?.Name ?? row.Album?.Title ?? row.Playlist?.Name ?? row.Native?.Name ?? Text(item, "Name") ?? "";
        var external = row.Native == null;
        var artwork = V2ArtworkUrls.For(source, item.TryGetProperty("ImageTags", out var tags) ? Text(tags, "Primary") : null);
        var kind = row.Section switch { "songs" => "track", "albums" => "album", "artists" => "artist", _ => "playlist" };
        var id = kind switch
        {
            "track" => track?.Id,
            "album" => V2Ids.IsValidSource(source, V2IdKind.Album) ? V2Ids.Album(source) : null,
            "artist" => V2Ids.IsValidSource(source, V2IdKind.Artist) ? V2Ids.Artist(source) : null,
            _ => V2Ids.IsValidSource(source, V2IdKind.Playlist) ? V2Ids.Playlist(source) : null
        };
        if (id == null) return null;
        var hit = new V2SearchHit(id, kind, row.Score, Text(item, "source") ?? "", Text(item, "sourceName") ?? "");
        IReadOnlyList<V2SearchSource>? Copies(V2IdKind copyKind, Func<string, string> v2) => Entries(item, "alsoOn")
            .Where(entry => Text(entry, "id") is { } value && V2Ids.IsValidSource(value, copyKind))
            .Select(entry => new V2SearchSource(Text(entry, "source") ?? "", Text(entry, "sourceName") ?? "", v2(Text(entry, "id")!)))
            .ToArray() is { Length: > 0 } listed ? listed : null;
        hit = kind switch
        {
            "track" => hit with { Track = track, AlsoOn = Copies(V2IdKind.Track, V2Ids.TrackSource) },
            "album" => hit with
            {
                Album = new(id, row.Edition == null ? name : $"{name} ({row.Edition})", V2TrackReader.Refs(item, "AlbumArtists"),
                    V2TrackReader.Int(item, "ProductionYear"), external, artwork)
                { Explicit = row.Album?.IsExplicit },
                AlsoOn = Copies(V2IdKind.Album, V2Ids.Album)
            },
            "artist" => hit with
            {
                Artist = new(id, name, external, artwork, Entries(item, "alsoOn")
                .Where(entry => Text(entry, "id") is { } value && V2Ids.IsValidSource(value, V2IdKind.Artist))
                .Select(entry => new V2SearchSource(Text(entry, "source") ?? "", Text(entry, "sourceName") ?? "", V2Ids.Artist(Text(entry, "id")!))).ToArray(),
                Entries(item, "namesakes").Where(entry => Text(entry, "id") is { } value && V2Ids.IsValidSource(value, V2IdKind.Artist))
                    .Select(entry => new V2Ref(V2Ids.Artist(Text(entry, "id")!), Text(entry, "name") ?? "")).ToArray())
            },
            _ => hit with
            {
                Playlist = new(id, name, row.Playlist?.CuratorName ?? Text(item, "AlbumArtist"),
                row.Playlist?.TrackCount ?? V2TrackReader.Int(item, "ChildCount"), external, artwork)
            }
        };
        return new(source, hit);
    }

    private static string? Text(JsonElement item, string name) => V2TrackReader.String(item, name);
    private static IEnumerable<JsonElement> Entries(JsonElement item, string name) =>
        item.TryGetProperty(name, out var values) && values.ValueKind == JsonValueKind.Array ? values.EnumerateArray() : [];
}
