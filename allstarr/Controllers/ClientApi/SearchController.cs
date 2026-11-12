using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Nodes;
using allstarr.Core.Capabilities;
using allstarr.Core.ClientApi;
using allstarr.Core.Matching;
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
    IApplicationCache cache, TimeProvider clock, LibraryProviderResolver libraryProvider, IProviderRegistry registry,
    ILibrarySpellings? spellings = null) : ClientApiControllerBase
{
    [HttpGet]
    [EndpointName("streamSearch")]
    [EndpointDescription("Streams external Jellyfin-shaped items with a lowercase numeric score from the merged search relevance scorer. " +
        "Every item also has source, the provider id that produced it or the library id for library items, and sourceName, its display name. Same-named library and service artists or albums are separate items. " +
        "In ranked snapshots, a library artist that matched songs or a person's answer tie to service artists is one combined item: source allstarr, sourceName Allstarr, and alsoOn, an array of {source, sourceName, id} listing the library artist first and then those service artists, which are not listed separately. " +
        "Besides the library artist, each name has at most one streaming artist: same-named artists from other services join it. In ranked snapshots that makes it a combined item the same way, its own part first in alsoOn; the default shape leaves them out. " +
        "In ranked snapshots, a service artist with a library artist's name that it is not tied to has namesakes, an array of {id, name} of those library artists. " +
        "The default shape emits {provider,items} batches and a final {done:true,late} line. " +
        "With shape=ranked, every line replaces the previous snapshot: {revision,done,providers,top,items,sections}, including library (Jellyfin) matches in top, items and sections ranked by merged search relevance scorer rules. " +
        "Revisions increase from 1; sections contains only requested songs, albums, artists and playlists, each capped at limit. " +
        "Items mixes requested types using the merged search ranking and is also capped at limit. Omitted limit defaults to 40 for ranked snapshots and 20 for provider batches. " +
        "Top selects an exact or near-exact artist, otherwise the best item, and also appears in its section; an owned exact match wins top over external lookalikes and an exact-name artist remains pinned. " +
        "Items retains top where scoring places it; clients rendering top separately skip that Id in items. " +
        "Items and sections rank by score with a five-point margin required to reorder existing items; native rows win ties. " +
        "The first snapshot waits for the preferred provider plus another answer, or 400 ms; intermediate snapshots are at least 250 ms apart. " +
        "The final snapshot flushes immediately when all answers are in, with done:true and late; earlier snapshots omit late. Provider answers are accepted for 2.75 seconds, " +
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
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(2750), clock);
        // The final response can outlive provider work, but still stops when the client disconnects.
        using var publishingDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(3), clock);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(disconnected, deadline.Token);
        var token = stop.Token;
        HttpContext.RequestAborted = token;
        Response.ContentType = "application/x-ndjson";
        Response.Headers.CacheControl = "no-store, no-transform";
        Response.Headers["X-Accel-Buffering"] = "no";
        HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        var headers = Request.Headers;
        // Every item names where it came from, so apps can label a library artist and a same-named service artist apart.
        var library = await libraryProvider.ResolveAsync(HttpContext, Execution);
        var native = new NativeSearch(proxy, headers, Execution, spellings);
        var nativeTask = Task.Run(() => native.ReadAsync(query, limit, requested, token, includePlaylists: ranked), token);
        var sentIds = new HashSet<string>(StringComparer.Ordinal);
        var sentSongs = new List<Song>();
        var sentAlbums = new HashSet<string>(StringComparer.Ordinal);
        var sentArtists = new List<Artist>();
        var seenArtists = new List<Artist>();
        // Per listed artist row: the services already shown on it, its own first.
        var alsoOnSources = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        using var publishingStop = CancellationTokenSource.CreateLinkedTokenSource(disconnected, publishingDeadline.Token);
        RankedSearchSnapshots? snapshots = null;
        Task? libraryTask = null;
        Task<IReadOnlyDictionary<string, IReadOnlyList<LibraryArtistLink>>>? savedLinks = null;
        var queuedLinks = new HashSet<string>(StringComparer.Ordinal);
        if (ranked)
        {
            // Service artists that matched songs tie to a library artist are listed on it as alsoOn rather than as rows.
            savedLinks = Task.Run(async () => await music.SavedArtistLinksAsync(Execution, (await nativeTask)
                .Where(item => item.Kind == MusicItemKind.Artist).Select(item => item.Name).ToArray(), token), token);
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
                        var links = await savedLinks!;
                        var libraryRows = BuildLibraryRows(query, owned, requested, limit, library, links, native.Spelled, token);
                        if (libraryRows.Count > 0)
                            await snapshots.AddLibraryAsync(libraryRows, token, MusicSearchService.LinkedKeys(links), native.Spelled);
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
                    var projected = await music.ProjectSearchSongsAsync(Execution, results.Songs, native, cancellation).WaitAsync(cancellation);
                    var projectedSongs = projected.Where(item => item.Song != null).Select(item => item.Song!).ToArray();
                    var knownDurationTitles = nativeSongs.Concat(projectedSongs)
                        .Where(song => (song.PreciseDuration?.TotalSeconds ?? song.Duration) is > 0)
                        .Select(song => SearchRelevanceRanker.RecordingTitle(song.Title, song.Artist, song.AlbumArtist)).ToHashSet(StringComparer.Ordinal);
                    var relevance = new Dictionary<object, double>(ReferenceEqualityComparer.Instance);
                    double Cached(object item, Func<double> read)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (!relevance.TryGetValue(item, out var score)) relevance[item] = score = read();
                        return score;
                    }
                    double SongScore(Song song)
                    {
                        var score = Cached(song, () => SearchRelevanceRanker.ScoreSong(query, song, cancellationToken: cancellation));
                        return (song.PreciseDuration?.TotalSeconds ?? song.Duration) is not > 0 &&
                            knownDurationTitles.Contains(SearchRelevanceRanker.RecordingTitle(song.Title, song.Artist, song.AlbumArtist))
                                ? RankedSearchRow.WithKnownDuration(score) : score;
                    }
                    double AlbumScore(Album album) => Cached(album, () => SearchRelevanceRanker.ScoreAlbum(query, album, cancellation));
                    double ArtistScore(Artist artist) => Cached(artist, () => SearchRelevanceRanker.Score(query, artist.Name, cancellationToken: cancellation));
                    double PlaylistScore(ExternalPlaylist playlist) => Cached(playlist, () => SearchRelevanceRanker.Score(query, playlist.Name, playlist.CuratorName, cancellationToken: cancellation));
                    var items = new List<RankedSearchRow>();
                    var alsoOn = new List<(string Id, JsonObject Entry)>();
                    // Service artists that open to nothing stay out; same-named ones are checked for next time.
                    seenArtists.AddRange(results.Artists);
                    music.ProbeArtists(Execution, seenArtists, owned.Where(item => item.Kind == MusicItemKind.Artist).Select(item => item.Name));
                    var providerName = registry.TryGet(provider, out var descriptor) ? descriptor!.DisplayName : provider;
                    if (savedLinks is { IsCompletedSuccessfully: true })
                    {
                        // A same-named library artist without links gets song matching queued so a later search can tie them.
                        var serviceNames = results.Artists.Select(artist => Normalize(artist.Name)).ToHashSet(StringComparer.Ordinal);
                        foreach (var name in owned.Where(item => item.Kind == MusicItemKind.Artist && !savedLinks.Result.ContainsKey(item.Name) &&
                                     serviceNames.Contains(Normalize(item.Name))).Select(item => item.Name))
                            if (queuedLinks.Add(name)) await music.QueueArtistLinkAsync(Execution, name, cancellation);
                    }
                    void Add(Dictionary<string, object?> item, double score, string section, Song? song = null, Artist? artist = null,
                        Album? album = null, ExternalPlaylist? playlist = null)
                    {
                        item["score"] = score;
                        var local = song?.IsLocal == true;
                        item["source"] = local ? library.Id : provider;
                        item["sourceName"] = local ? library.Name : providerName;
                        var row = new RankedSearchRow(JsonSerializer.SerializeToNode(item)!.AsObject(), section, song, artist, album, playlist);
                        row.Scores[query] = song != null ? relevance[song] : score;
                        items.Add(row);
                    }
                    foreach (var song in projectedSongs.OrderByDescending(SongScore).Take(limit))
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (nativeSongs.Any(known => { cancellation.ThrowIfCancellationRequested(); return MusicSearchService.MetadataRecordingsMatch(known, song); }) ||
                            sentSongs.Any(known =>
                            {
                                cancellation.ThrowIfCancellationRequested(); return known.Id == song.Id ||
                                song.CanonicalRecordingId is { } recording && known.CanonicalRecordingId == recording ||
                                MusicSearchService.MetadataRecordingsMatch(known, song);
                            })) continue;
                        if (!sentIds.Add("Audio:" + song.Id)) continue;
                        sentSongs.Add(song);
                        Add(builder.ConvertSongToJellyfinItem(song), SongScore(song), "songs", song);
                    }
                    foreach (var album in results.Albums.OrderByDescending(AlbumScore).Take(limit))
                        // Library albums and artists never hide a same-named service result; only repeats from other services do.
                        if (sentAlbums.Add(AlbumKey(album.Title, album.Artist)) &&
                            sentIds.Add("MusicAlbum:" + album.Id)) Add(builder.ConvertAlbumToJellyfinItem(album), AlbumScore(album), "albums", album: album);
                    foreach (var artist in results.Artists.Where(artist => !music.IsEmptyArtist(artist))
                                 .OrderByDescending(ArtistScore).Take(limit))
                    {
                        if (sentIds.Contains("MusicArtist:" + artist.Id)) continue;
                        // Each name has one streaming artist; a same-named one from any service joins it instead of being its own row.
                        if (sentArtists.FirstOrDefault(sent => Normalize(sent.Name) == Normalize(artist.Name)) is { } twin)
                        {
                            // One entry per service: several same-named accounts on one service add nothing for the app to show.
                            if (alsoOnSources[twin.Id].Add(provider))
                                alsoOn.Add((twin.Id, new JsonObject { ["source"] = provider, ["sourceName"] = providerName, ["id"] = artist.Id }));
                            continue;
                        }
                        sentIds.Add("MusicArtist:" + artist.Id);
                        sentArtists.Add(artist);
                        alsoOnSources[artist.Id] = [provider];
                        Add(requested.SetEquals(["MusicArtist"]) ? builder.ConvertArtistForQuery(artist) : builder.ConvertArtistToJellyfinItem(artist),
                            ArtistScore(artist), "artists", artist: artist);
                    }
                    foreach (var playlist in playlists.OrderByDescending(PlaylistScore).Take(limit))
                        if (sentIds.Add("Playlist:" + playlist.Id)) Add(builder.ConvertPlaylistToAlbumItem(playlist),
                            PlaylistScore(playlist), "playlists", playlist: playlist);
                    var shaped = new JsonResult(new { Items = items.OrderByDescending(item => item.Score).Select(item => item.Item).ToArray() });
                    await itemFilter.PrepareAsync(shaped, HttpContext).WaitAsync(cancellation);
                    var rows = JsonSerializer.SerializeToNode(shaped.Value)!["Items"];
                    if (snapshots == null) await WriteAsync(new { provider, items = rows }, cancellation);
                    else
                    {
                        var byId = items.ToDictionary(item => item.Item["Id"]!.GetValue<string>());
                        var prepared = rows!.AsArray().OfType<JsonObject>().Select(row =>
                            byId[row["Id"]!.GetValue<string>()] with { Item = row }).ToArray();
                        await snapshots.AddAsync(provider, prepared, cancellation, alsoOn);
                    }
                }, token);
            if (libraryTask != null)
            {
                try { await libraryTask.WaitAsync(token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch (Exception) { }
            }
            if (snapshots == null) await WriteAsync(new { done = true, late }, disconnected);
            else
            {
                snapshots.Complete(late);
                await publishing!.WaitAsync(publishingStop.Token);
            }
            if (!token.IsCancellationRequested)
                Response.OnCompleted(async () =>
                {
                    try
                    {
                        if (nativeTask.IsCompletedSuccessfully)
                            await music.RecordArtistCandidatesAsync(Execution, nativeTask.Result
                                .Where(item => item.Kind == MusicItemKind.Artist).Select(item => item.Name).ToArray(), seenArtists, disconnected);
                    }
                    catch (OperationCanceledException) when (disconnected.IsCancellationRequested) { }
                });
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
        cancellationToken.ThrowIfCancellationRequested();
        await JsonSerializer.SerializeAsync(Response.Body, value, cancellationToken: cancellationToken);
        await Response.WriteAsync("\n", cancellationToken);
        await Response.Body.FlushAsync(cancellationToken);
    }

    internal const string CombinedSource = "allstarr", CombinedSourceName = "Allstarr";

    private static string Normalize(string? value) => FuzzyMatcher.NormalizeForMatching(value ?? "");
    private static string AlbumKey(string title, string? artist) => Normalize(title) + "\u001f" + Normalize(artist);

    private List<RankedSearchRow> BuildLibraryRows(string query,
        IReadOnlyList<NativeMusicItem<Dictionary<string, object?>>> owned,
        HashSet<string> requested, int limit, ItemDetailsProvider source,
        IReadOnlyDictionary<string, IReadOnlyList<LibraryArtistLink>> links, string? spelled, CancellationToken token)
    {
        // Items found through the library's own spelling of the query are scored against that spelling too.
        var relevance = new Dictionary<object, Dictionary<string, double>>(ReferenceEqualityComparer.Instance);
        double Cached(object item, string text, Func<double> read)
        {
            token.ThrowIfCancellationRequested();
            if (!relevance.TryGetValue(item, out var scores)) relevance[item] = scores = new(StringComparer.Ordinal);
            if (!scores.TryGetValue(text, out var score)) scores[text] = score = read();
            return score;
        }
        double Best(object item, Func<string, double> score) => spelled == null ? Cached(item, query, () => score(query)) :
            Math.Max(Cached(item, query, () => score(query)), Cached(item, spelled, () => score(spelled)));
        var rows = new List<RankedSearchRow>();
        void AddNative(JsonObject item, string section, NativeMusicItem<Dictionary<string, object?>> native)
        {
            token.ThrowIfCancellationRequested();
            var row = new RankedSearchRow(item, section, Native: AsObjectPayload(native));
            row.Scores[query] = native.Kind == MusicItemKind.Album && native.AlbumMetadata != null
                ? SearchRelevanceRanker.Score(query, native.Name, native.ArtistText, cancellationToken: token) : relevance[native][query];
            if (native.Kind == MusicItemKind.Song && spelled != null) row.Scores[spelled] = relevance[native][spelled];
            rows.Add(row);
        }
        if (requested.Contains("Audio"))
        {
            var songs = owned.Where(item => item.Kind == MusicItemKind.Song &&
                Execution.Policy?.Includes(item.ExplicitContentLyrics) != false);
            double SongScore(NativeMusicItem<Dictionary<string, object?>> s) => Best(s, q => SearchRelevanceRanker.ScoreSong(q, s.Name, s.ArtistText, s.AlbumText, s.AlbumArtistText, s.DurationSeconds, provenRecording: true, cancellationToken: token));
            foreach (var song in songs.OrderByDescending(SongScore).Take(limit))
            {
                var score = SongScore(song);
                var itemNode = JsonSerializer.SerializeToNode(song.Payload)!.AsObject();
                itemNode["score"] = score;
                itemNode["source"] = source.Id;
                itemNode["sourceName"] = source.Name;
                AddNative(itemNode, "songs", song);
            }
        }
        if (requested.Contains("MusicAlbum"))
        {
            var albums = owned.Where(item => item.Kind == MusicItemKind.Album &&
                (Execution.Policy?.Preferences.ExplicitFilter != "CleanOnly" || Execution.Policy.Includes(item.ExplicitContentLyrics)));
            double AlbumScore(NativeMusicItem<Dictionary<string, object?>> a) => Best(a, q => a.AlbumMetadata != null ? SearchRelevanceRanker.ScoreAlbum(q, a.AlbumMetadata, token) : SearchRelevanceRanker.Score(q, a.Name, a.ArtistText, cancellationToken: token));
            foreach (var album in albums.OrderByDescending(AlbumScore).Take(limit))
            {
                var score = AlbumScore(album);
                var itemNode = JsonSerializer.SerializeToNode(album.Payload)!.AsObject();
                itemNode["score"] = score;
                itemNode["source"] = source.Id;
                itemNode["sourceName"] = source.Name;
                AddNative(itemNode, "albums", album);
            }
        }
        if (requested.Contains("MusicArtist"))
        {
            var artists = owned.Where(item => item.Kind == MusicItemKind.Artist);
            foreach (var artist in artists.OrderByDescending(a => Best(a, q => SearchRelevanceRanker.Score(q, a.Name, a.ArtistText, cancellationToken: token))).Take(limit))
            {
                var score = Best(artist, q => SearchRelevanceRanker.Score(q, artist.Name, artist.ArtistText, cancellationToken: token));
                var itemNode = JsonSerializer.SerializeToNode(artist.Payload)!.AsObject();
                itemNode["score"] = score;
                itemNode["source"] = source.Id;
                itemNode["sourceName"] = source.Name;
                // A library artist that songs tie to service artists is one combined entry: labelled Allstarr, with every part in alsoOn.
                if (links.GetValueOrDefault(artist.Name) is { Count: > 0 } alsoOn)
                {
                    itemNode["alsoOn"] = JsonSerializer.SerializeToNode(new[] { new { source = source.Id, sourceName = source.Name, id = artist.Id } }
                        .Concat(alsoOn.Select(link => new
                        {
                            source = link.ProviderId,
                            sourceName = registry.TryGet(link.ProviderId, out var descriptor) ? descriptor!.DisplayName : link.ProviderId,
                            id = ProtocolProviderGateway.ProtocolItemId(new(link.ProviderId, ProviderResourceKind.Artist, link.ArtistId))
                        })));
                    itemNode["source"] = CombinedSource;
                    itemNode["sourceName"] = CombinedSourceName;
                }
                AddNative(itemNode, "artists", artist);
            }
        }
        if (requested.Contains("Playlist"))
        {
            var playlists = owned.Where(item => item.Kind == MusicItemKind.Playlist);
            foreach (var playlist in playlists.OrderByDescending(p => Best(p, q => SearchRelevanceRanker.Score(q, p.Name, p.ArtistText, cancellationToken: token))).Take(limit))
            {
                var score = Best(playlist, q => SearchRelevanceRanker.Score(q, playlist.Name, playlist.ArtistText, cancellationToken: token));
                var itemNode = JsonSerializer.SerializeToNode(playlist.Payload)!.AsObject();
                itemNode["score"] = score;
                itemNode["source"] = source.Id;
                itemNode["sourceName"] = source.Name;
                AddNative(itemNode, "playlists", playlist);
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

    private sealed class NativeSearch(JellyfinProxyService proxy, IHeaderDictionary headers, ProtocolExecutionContext context,
        ILibrarySpellings? spellings = null) : INativeMusicSearch<Dictionary<string, object?>>
    {
        /// <summary>The library's own spelling of the query, when it differs ("cybersex" → "Cyber Sex").</summary>
        public string? Spelled { get; private set; }

        public async Task<IReadOnlyList<NativeMusicItem<Dictionary<string, object?>>>> ReadAsync(string query, int limit,
            HashSet<string> types, CancellationToken token, bool includePlaylists = false)
        {
            var respelled = spellings?.RespellAsync(context, query, token) ?? Task.FromResult<string?>(null);
            var found = await QueryAsync(query, limit, types, token, includePlaylists);
            // The media server can't split a joined word, so it is asked again with the library's own spelling.
            try { Spelled = await respelled; }
            catch (Exception error) when (error is not OperationCanceledException) { Spelled = null; }
            if (Spelled == null) return found;
            return found.Concat(await QueryAsync(Spelled, limit, types, token, includePlaylists))
                .DistinctBy(item => item.Id, StringComparer.Ordinal).ToArray();
        }

        private async Task<IReadOnlyList<NativeMusicItem<Dictionary<string, object?>>>> QueryAsync(string query, int limit,
            HashSet<string> types, CancellationToken token, bool includePlaylists)
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
