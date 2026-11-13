using System.Text.Json;
using allstarr.Controllers.ClientApi;
using allstarr.Core.ClientApi;
using allstarr.Core.Protocols;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Core.ClientApiV2;

/// <summary>Listener-scoped search reads using the shared ranked stream and stored lyrics index.</summary>
public sealed class V2SearchEndpoints(V2SearchStream stream, V2TrackReader tracks, LyricsSearchReader lyrics)
{
    private static readonly IReadOnlySet<string> Types = new HashSet<string>(StringComparer.Ordinal) { "songs", "albums", "artists", "playlists" };

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
                var result = await tracks.ReadWithSourcesAsync(http, current, songs.Select(Id).ToArray(), false, metadata);
                var byId = Index(result.Response.Tracks);
                foreach (var row in songs) read[Id(row)] = byId.GetValueOrDefault(Id(row));
            }
            var hits = rows.Select(row => Project(row, read.GetValueOrDefault(Id(row)))).Where(hit => hit.HasValue)
                .Select(hit => hit!.Value).ToDictionary(hit => hit.Key, hit => hit.Value);
            V2SearchHit[] Hits(IEnumerable<RankedSearchRow> values) => values.Select(row => hits.GetValueOrDefault(Id(row))).OfType<V2SearchHit>().ToArray();
            await V2SearchStream.WriteResponseAsync(http, new V2SearchSnapshot(batch.Revision, batch.Late != null, batch.Providers,
                batch.Top == null ? null : hits.GetValueOrDefault(Id(batch.Top)), Hits(batch.Items),
                batch.Sections.ToDictionary(pair => pair.Key, pair => Hits(pair.Value)), batch.Late), token);
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
        var hits = result.Results.Where(hit => byId.ContainsKey(hit.ItemId)).Select(hit =>
            new V2LyricsSearchHit(byId[hit.ItemId], hit.Line, hit.Highlights, hit.Score, hit.Source)).ToArray();
        return new(200, new V2LyricsSearchResponse(hits, result.Truncated));
    }

    private static Dictionary<string, V2Track> Index(IReadOnlyList<V2Track> values) => values
        .SelectMany(track => track.Aliases.Prepend(track.Id).Select(id => (Id: id, Track: track)))
        .GroupBy(item => item.Id, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First().Track, StringComparer.Ordinal);

    private static string Id(RankedSearchRow row) => row.Item["Id"]!.GetValue<string>();

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
        hit = kind switch
        {
            "track" => hit with { Track = track },
            "album" => hit with { Album = new(id, name, V2TrackReader.Refs(item, "AlbumArtists"), V2TrackReader.Int(item, "ProductionYear"), external, artwork) },
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
