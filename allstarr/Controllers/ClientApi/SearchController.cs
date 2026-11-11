using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Nodes;
using allstarr.Core.Capabilities;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Filters;
using allstarr.Models.Domain;
using allstarr.Models.Subsonic;
using allstarr.Services.Common;
using allstarr.Services.Jellyfin;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers.ClientApi;

[Route("allstarr/v1/search")]
public sealed class SearchController(IProtocolProviderGateway providers, MusicSearchService music,
    JellyfinProxyService proxy, JellyfinResponseBuilder builder, JellyfinItemResponseFilter itemFilter,
    IApplicationCache cache, TimeProvider clock) : ClientApiControllerBase
{
    [HttpGet]
    [EndpointName("streamSearch")]
    [EndpointDescription("Streams external Jellyfin-shaped items with a lowercase numeric score from the merged search relevance scorer. " +
        "The default shape emits {provider,items} batches and a final {done:true,late} line. " +
        "With shape=ranked, every line replaces the previous snapshot: {revision,done,providers,top,items,sections}, including library (Jellyfin) matches in top, items and sections ranked by merged search relevance scorer rules. " +
        "Revisions increase from 1; sections contains only requested songs, albums, artists and playlists, each capped at limit. " +
        "Items mixes requested types using the merged search ranking and is also capped at limit. Omitted limit defaults to 40 for ranked snapshots and 20 for provider batches. " +
        "Top selects an exact or near-exact artist, otherwise the best item, and also appears in its section; an owned exact match wins top over external lookalikes and an exact-name artist remains pinned. " +
        "Items retains top where scoring places it; clients rendering top separately skip that Id in items. " +
        "Items and sections rank by score with a five-point margin required to reorder existing items; native rows win ties. " +
        "The first snapshot waits for the preferred provider plus another answer, or 400 ms; later snapshots are at least 250 ms apart. " +
        "The final snapshot always flushes with done:true and late; earlier snapshots omit late. Provider work stops at ten seconds, " +
        "with up to 250 ms more for the final flush. Stable item Ids allow clients to animate changes without sorting. " +
        "Snapshot types: revision is an integer, done is a boolean, providers and late are arrays of provider-id strings, " +
        "top is an item or null, items is an item array, and sections is an object whose songs, albums, artists and playlists values are item arrays. " +
        "Missing keys mean empty: missing arrays or section keys are empty arrays, a missing sections object is empty, and a missing or null top has no item. " +
        "Items retain the existing Jellyfin item JSON plus numeric score and a stable string Id (library items keep their native Ids). All renders top followed by items, deduplicated by Id. " +
        "The searchStreamRanked capability is advertised only when the ranked snapshot shape is available; searchStreamRankedLibrary is advertised when library matches are included.")]
    [ProducesResponseType<string>(200, "application/x-ndjson")]
    public async Task<IActionResult> Get([FromQuery, Required] string q, [FromQuery, Range(1, 200)] int limit = 20,
        [FromQuery] string? types = null, [FromQuery] string? shape = null)
    {
        var query = q.Trim();
        var requested = (types ?? "Audio,MusicAlbum,MusicArtist,Playlist")
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ranked = string.Equals(shape, "ranked", StringComparison.OrdinalIgnoreCase);
        if (ranked && !Request.Query.ContainsKey("limit")) limit = 40;
        if (shape != null && !ranked || query.Length is < 1 or > 200 || requested.Count == 0 ||
            requested.Any(type => !new[] { "Audio", "MusicAlbum", "MusicArtist", "Playlist" }.Contains(type, StringComparer.OrdinalIgnoreCase)))
            return BadRequest();
        var disconnected = HttpContext.RequestAborted;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10), clock);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(disconnected, deadline.Token);
        var token = stop.Token;
        HttpContext.RequestAborted = token;
        Response.ContentType = "application/x-ndjson";
        Response.Headers.CacheControl = "no-store, no-transform";
        Response.Headers["X-Accel-Buffering"] = "no";
        HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        var headers = Request.Headers;
        var native = new NativeSearch(proxy, headers, Execution);
        var nativeTask = Task.Run(() => native.ReadAsync(query, limit, requested, token, includePlaylists: ranked), token);
        var sentIds = new HashSet<string>(StringComparer.Ordinal);
        var sentSongs = new List<Song>();
        var sentAlbums = new HashSet<string>(StringComparer.Ordinal);
        var sentArtists = new HashSet<string>(StringComparer.Ordinal);
        using var publishingStop = CancellationTokenSource.CreateLinkedTokenSource(disconnected);
        RankedSearchSnapshots? snapshots = null;
        Task? libraryTask = null;
        if (ranked)
        {
            var capability = requested.SetEquals(["Playlist"]) ? ProviderCapabilityKind.Playlist : ProviderCapabilityKind.Metadata;
            var available = providers.GetProviderOrder(capability) ?? [];
            var preferred = (Execution.Policy?.ApplyProviderAvailability(capability, available) ?? available).FirstOrDefault();
            var sections = new[] { ("Audio", "songs"), ("MusicAlbum", "albums"), ("MusicArtist", "artists"), ("Playlist", "playlists") }
                .Where(kind => requested.Contains(kind.Item1)).Select(kind => kind.Item2).ToArray();
            snapshots = new(query, limit, sections, preferred, clock, WriteAsync, async (rows, cancellation) =>
            {
                var artists = rows.Where(row => row.Artist != null).Select(row => row.Artist!).ToArray();
                var albums = rows.Where(row => row.Album != null).Select(row => row.Album!).ToArray();
                var songs = rows.Where(row => row.Song != null).Select(row => row.Song!).ToArray();
                await ArtistArtworkCache.PrepareSearchAsync(cache, Execution, [], artists, albums, songs).WaitAsync(cancellation);
                foreach (var row in rows.Where(row => row.Artist != null))
                    row.Item["ImageTags"] = JsonSerializer.SerializeToNode(builder.ConvertArtistToJellyfinItem(row.Artist!).GetValueOrDefault("ImageTags"));
            });
            libraryTask = Task.Run(async () =>
            {
                try
                {
                    var owned = await nativeTask;
                    if (owned.Count > 0)
                    {
                        var libraryRows = BuildLibraryRows(query, owned, requested, limit);
                        if (libraryRows.Count > 0)
                            await snapshots.AddLibraryAsync(libraryRows, token);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch (Exception) { }
            }, token);
        }
        var publishing = snapshots?.RunAsync(publishingStop.Token);
        IReadOnlyList<string> late = [];
        try
        {
            late = await providers.StreamSearchAsync(Execution, query, limit, requested.Contains("Audio"),
                requested.Contains("MusicAlbum"), requested.Contains("MusicArtist"), requested.Contains("Playlist"),
                async (provider, results, playlists, cancellation) =>
                {
                    var owned = ranked
                        ? (nativeTask.IsCompletedSuccessfully ? nativeTask.Result : [])
                        : await nativeTask.WaitAsync(cancellation);
                    await ArtistArtworkCache.PrepareSearchAsync(cache, Execution,
                        owned.Where(item => item.Kind == MusicItemKind.Artist).Select(item => item.Payload), results.Artists,
                        results.Albums, results.Songs);
                    var nativeSongs = owned.Where(item => item.Kind == MusicItemKind.Song && Execution.Policy?.Includes(item.ExplicitContentLyrics) != false)
                        .Select(item => new Song
                        {
                            Title = item.Name,
                            Artist = item.ArtistText ?? "",
                            Album = item.AlbumText ?? "",
                            PreciseDuration = item.DurationSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null,
                            ExplicitContentLyrics = item.ExplicitContentLyrics
                        }).ToArray();
                    var ownedAlbums = owned.Where(item => item.Kind == MusicItemKind.Album &&
                        (Execution.Policy?.Preferences.ExplicitFilter != "CleanOnly" || Execution.Policy.Includes(item.ExplicitContentLyrics)))
                        .Select(item => AlbumKey(item.Name, item.AlbumMetadata?.Artist ?? item.ArtistText)).ToHashSet(StringComparer.Ordinal);
                    var ownedArtists = owned.Where(item => item.Kind == MusicItemKind.Artist).Select(item => Normalize(item.Name)).ToHashSet(StringComparer.Ordinal);
                    var projected = await music.ProjectSearchSongsAsync(Execution, results.Songs, native, cancellation).WaitAsync(cancellation);
                    var projectedSongs = projected.Where(item => item.Song != null).Select(item => item.Song!).ToArray();
                    var knownDurationTitles = nativeSongs.Concat(projectedSongs)
                        .Where(song => (song.PreciseDuration?.TotalSeconds ?? song.Duration) is > 0)
                        .Select(song => SearchRelevanceRanker.RecordingTitle(song.Title, song.Artist, song.AlbumArtist)).ToHashSet(StringComparer.Ordinal);
                    double SongScore(Song song) => SearchRelevanceRanker.ScoreSong(query, song,
                        knownDurationTitles.Contains(SearchRelevanceRanker.RecordingTitle(song.Title, song.Artist, song.AlbumArtist)));
                    var items = new List<RankedSearchRow>();
                    void Add(Dictionary<string, object?> item, double score, string section, Song? song = null, Artist? artist = null,
                        Album? album = null, ExternalPlaylist? playlist = null)
                    {
                        item["score"] = score;
                        items.Add(new(JsonSerializer.SerializeToNode(item)!.AsObject(), section, song, artist, album, playlist));
                    }
                    foreach (var song in projectedSongs.OrderByDescending(SongScore).Take(limit))
                    {
                        if (nativeSongs.Any(known => MusicSearchService.MetadataRecordingsMatch(known, song)) ||
                            sentSongs.Any(known => known.Id == song.Id ||
                                song.CanonicalRecordingId is { } recording && known.CanonicalRecordingId == recording ||
                                MusicSearchService.MetadataRecordingsMatch(known, song))) continue;
                        if (!sentIds.Add("Audio:" + song.Id)) continue;
                        sentSongs.Add(song);
                        Add(builder.ConvertSongToJellyfinItem(song), SongScore(song), "songs", song);
                    }
                    foreach (var album in results.Albums.OrderByDescending(album => SearchRelevanceRanker.ScoreAlbum(query, album)).Take(limit))
                        if (!ownedAlbums.Contains(AlbumKey(album.Title, album.Artist)) && sentAlbums.Add(AlbumKey(album.Title, album.Artist)) &&
                            sentIds.Add("MusicAlbum:" + album.Id)) Add(builder.ConvertAlbumToJellyfinItem(album), SearchRelevanceRanker.ScoreAlbum(query, album), "albums", album: album);
                    foreach (var artist in results.Artists.OrderByDescending(artist => SearchRelevanceRanker.Score(query, artist.Name)).Take(limit))
                        if (!ownedArtists.Contains(Normalize(artist.Name)) && sentArtists.Add(Normalize(artist.Name)) && sentIds.Add("MusicArtist:" + artist.Id))
                            Add(requested.SetEquals(["MusicArtist"]) ? builder.ConvertArtistForQuery(artist) : builder.ConvertArtistToJellyfinItem(artist),
                                SearchRelevanceRanker.Score(query, artist.Name), "artists", artist: artist);
                    foreach (var playlist in playlists.OrderByDescending(playlist => SearchRelevanceRanker.Score(query, playlist.Name, playlist.CuratorName)).Take(limit))
                        if (sentIds.Add("Playlist:" + playlist.Id)) Add(builder.ConvertPlaylistToAlbumItem(playlist),
                            SearchRelevanceRanker.Score(query, playlist.Name, playlist.CuratorName), "playlists", playlist: playlist);
                    var shaped = new JsonResult(new { Items = items.OrderByDescending(item => item.Score).Select(item => item.Item).ToArray() });
                    await itemFilter.PrepareAsync(shaped, HttpContext).WaitAsync(cancellation);
                    var rows = JsonSerializer.SerializeToNode(shaped.Value)!["Items"];
                    if (snapshots == null) await WriteAsync(new { provider, items = rows }, cancellation);
                    else
                    {
                        var byId = items.ToDictionary(item => item.Item["Id"]!.GetValue<string>());
                        var prepared = rows!.AsArray().OfType<JsonObject>().Select(row =>
                            byId[row["Id"]!.GetValue<string>()] with { Item = row }).ToArray();
                        await snapshots.AddAsync(provider, prepared, cancellation);
                    }
                }, token);
            if (libraryTask != null)
            {
                try { await libraryTask; }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch (Exception) { }
            }
            if (snapshots == null) await WriteAsync(new { done = true, late }, disconnected);
            else
            {
                snapshots.Complete(late);
                await publishing!;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (IOException) when (disconnected.IsCancellationRequested) { }
        finally
        {
            await publishingStop.CancelAsync();
            if (publishing is { IsCompleted: false })
                try { await publishing; }
                catch (OperationCanceledException) when (publishingStop.IsCancellationRequested) { }
            await stop.CancelAsync();
            HttpContext.RequestAborted = disconnected;
        }
        return new EmptyResult();
    }

    private async Task WriteAsync(object value, CancellationToken cancellationToken)
    {
        await Response.WriteAsync(JsonSerializer.Serialize(value) + "\n", cancellationToken);
        await Response.Body.FlushAsync(cancellationToken);
    }

    private static string Normalize(string? value) => FuzzyMatcher.NormalizeForMatching(value ?? "");
    private static string AlbumKey(string title, string? artist) => Normalize(title) + "\u001f" + Normalize(artist);

    private List<RankedSearchRow> BuildLibraryRows(string query,
        IReadOnlyList<NativeMusicItem<Dictionary<string, object?>>> owned,
        HashSet<string> requested, int limit)
    {
        var rows = new List<RankedSearchRow>();
        if (requested.Contains("Audio"))
        {
            var songs = owned.Where(item => item.Kind == MusicItemKind.Song &&
                Execution.Policy?.Includes(item.ExplicitContentLyrics) != false);
            foreach (var song in songs.OrderByDescending(s => SearchRelevanceRanker.ScoreSong(query, s.Name, s.ArtistText, s.AlbumText, s.AlbumArtistText, s.DurationSeconds, provenRecording: true, hasKnownDurationVersion: s.DurationSeconds is > 0)).Take(limit))
            {
                var score = SearchRelevanceRanker.ScoreSong(query, song.Name, song.ArtistText, song.AlbumText, song.AlbumArtistText, song.DurationSeconds, provenRecording: true, hasKnownDurationVersion: song.DurationSeconds is > 0);
                var itemNode = JsonSerializer.SerializeToNode(song.Payload)!.AsObject();
                itemNode["score"] = score;
                rows.Add(new(itemNode, "songs", Native: AsObjectPayload(song)));
            }
        }
        if (requested.Contains("MusicAlbum"))
        {
            var albums = owned.Where(item => item.Kind == MusicItemKind.Album &&
                (Execution.Policy?.Preferences.ExplicitFilter != "CleanOnly" || Execution.Policy.Includes(item.ExplicitContentLyrics)));
            foreach (var album in albums.OrderByDescending(a => a.AlbumMetadata != null ? SearchRelevanceRanker.ScoreAlbum(query, a.AlbumMetadata) : SearchRelevanceRanker.Score(query, a.Name, a.ArtistText)).Take(limit))
            {
                var score = album.AlbumMetadata != null ? SearchRelevanceRanker.ScoreAlbum(query, album.AlbumMetadata) : SearchRelevanceRanker.Score(query, album.Name, album.ArtistText);
                var itemNode = JsonSerializer.SerializeToNode(album.Payload)!.AsObject();
                itemNode["score"] = score;
                rows.Add(new(itemNode, "albums", Native: AsObjectPayload(album)));
            }
        }
        if (requested.Contains("MusicArtist"))
        {
            var artists = owned.Where(item => item.Kind == MusicItemKind.Artist);
            foreach (var artist in artists.OrderByDescending(a => SearchRelevanceRanker.Score(query, a.Name, a.ArtistText)).Take(limit))
            {
                var score = SearchRelevanceRanker.Score(query, artist.Name, artist.ArtistText);
                var itemNode = JsonSerializer.SerializeToNode(artist.Payload)!.AsObject();
                itemNode["score"] = score;
                rows.Add(new(itemNode, "artists", Native: AsObjectPayload(artist)));
            }
        }
        if (requested.Contains("Playlist"))
        {
            var playlists = owned.Where(item => item.Kind == MusicItemKind.Playlist);
            foreach (var playlist in playlists.OrderByDescending(p => SearchRelevanceRanker.Score(query, p.Name, p.ArtistText)).Take(limit))
            {
                var score = SearchRelevanceRanker.Score(query, playlist.Name, playlist.ArtistText);
                var itemNode = JsonSerializer.SerializeToNode(playlist.Payload)!.AsObject();
                itemNode["score"] = score;
                rows.Add(new(itemNode, "playlists", Native: AsObjectPayload(playlist)));
            }
        }
        return rows;
    }

    private static NativeMusicItem<object> AsObjectPayload<T>(NativeMusicItem<T> item) where T : class =>
        new(item.Kind, item.Id, item.Name, item.ArtistText, item.Payload)
        {
            AlbumMetadata = item.AlbumMetadata,
            DurationSeconds = item.DurationSeconds,
            ExplicitContentLyrics = item.ExplicitContentLyrics,
            AlbumText = item.AlbumText,
            AlbumArtistText = item.AlbumArtistText
        };

    private sealed class NativeSearch(JellyfinProxyService proxy, IHeaderDictionary headers, ProtocolExecutionContext context)
        : INativeMusicSearch<Dictionary<string, object?>>
    {
        public async Task<IReadOnlyList<NativeMusicItem<Dictionary<string, object?>>>> ReadAsync(string query, int limit,
            HashSet<string> types, CancellationToken token, bool includePlaylists = false)
        {
            if (!includePlaylists && types.SetEquals(["Playlist"])) return [];
            try
            {
                var queryTypes = includePlaylists ? types : types.Where(type => !type.Equals("Playlist", StringComparison.OrdinalIgnoreCase));
                var (body, status) = await proxy.GetJsonAsync($"Users/{Uri.EscapeDataString(context.VerifiedBackendPrincipalId)}/Items",
                    new()
                    {
                        ["SearchTerm"] = query,
                        ["IncludeItemTypes"] = string.Join(',', queryTypes),
                        ["Fields"] = "ProviderIds",
                        ["Recursive"] = "true",
                        ["Limit"] = limit.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    }, headers, token);
                using (body)
                    return status == 200 && body?.RootElement.TryGetProperty("Items", out var rows) == true
                        ? rows.EnumerateArray().Select(item => JellyfinController.ToNativeSearchItem(item.Deserialize<Dictionary<string, object?>>() ?? []))
                            .OfType<NativeMusicItem<Dictionary<string, object?>>>().ToArray() : [];
            }
            catch (HttpRequestException) { return []; }
        }

        public Task<IReadOnlyList<NativeMusicItem<Dictionary<string, object?>>>> SearchAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<NativeMusicItem<Dictionary<string, object?>>>>([]);

        public async Task<IReadOnlyList<NativeMusicItem<Dictionary<string, object?>>>> GetSongsAsync(
            IReadOnlyList<string> ids, CancellationToken cancellationToken)
        {
            if (ids.Count == 0) return [];
            var (body, status) = await proxy.GetJsonAsync($"Users/{Uri.EscapeDataString(context.VerifiedBackendPrincipalId)}/Items",
                new()
                {
                    ["Ids"] = string.Join(',', ids),
                    ["IncludeItemTypes"] = "Audio",
                    ["Limit"] = ids.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                }, headers, cancellationToken);
            using (body)
            {
                if (status != 200 || body == null || !body.RootElement.TryGetProperty("Items", out var rows)) return [];
                return rows.EnumerateArray().Select(item => JellyfinController.ToNativeSearchItem(item.Deserialize<Dictionary<string, object?>>() ?? []))
                    .OfType<NativeMusicItem<Dictionary<string, object?>>>()
                    .Where(item => item.Kind == MusicItemKind.Song && ids.Contains(item.Id, StringComparer.Ordinal))
                    .DistinctBy(item => item.Id, StringComparer.Ordinal).ToArray();
            }
        }

        public async Task<NativeMusicItem<Dictionary<string, object?>>?> GetSongAsync(string id, CancellationToken cancellationToken)
        {
            var (body, status) = await proxy.GetJsonAsync($"Users/{Uri.EscapeDataString(context.VerifiedBackendPrincipalId)}/Items/{Uri.EscapeDataString(id)}",
                null, headers, cancellationToken);
            using (body)
            {
                if (status != 200 || body == null || !body.RootElement.TryGetProperty("Id", out var found) || found.GetString() != id)
                    return null;
                var item = JellyfinController.ToNativeSearchItem(body.RootElement.Deserialize<Dictionary<string, object?>>() ?? []);
                return item?.Kind == MusicItemKind.Song ? item : null;
            }
        }
    }
}
