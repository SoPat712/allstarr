using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Filters;
using allstarr.Models.Domain;
using allstarr.Services.Common;
using allstarr.Services.Jellyfin;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers.ClientApi;

[Route("allstarr/v1/search")]
public sealed class SearchController(IProtocolProviderGateway providers, MusicSearchService music,
    JellyfinProxyService proxy, JellyfinResponseBuilder builder, JellyfinItemResponseFilter itemFilter) : ClientApiControllerBase
{
    [HttpGet]
    [EndpointName("streamSearch")]
    [ProducesResponseType<string>(200, "application/x-ndjson")]
    public async Task<IActionResult> Get([FromQuery, Required] string q, [FromQuery, Range(1, 200)] int limit = 20,
        [FromQuery] string? types = null)
    {
        var query = q.Trim();
        var requested = (types ?? "Audio,MusicAlbum,MusicArtist,Playlist")
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (query.Length is < 1 or > 200 || requested.Count == 0 ||
            requested.Any(type => !new[] { "Audio", "MusicAlbum", "MusicArtist", "Playlist" }.Contains(type, StringComparer.OrdinalIgnoreCase)))
            return BadRequest();
        var disconnected = HttpContext.RequestAborted;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(disconnected, deadline.Token);
        var token = stop.Token;
        HttpContext.RequestAborted = token;
        Response.ContentType = "application/x-ndjson";
        Response.Headers.CacheControl = "no-store, no-transform";
        Response.Headers["X-Accel-Buffering"] = "no";
        HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        var native = new NativeSearch(proxy, HttpContext, Execution);
        var nativeTask = native.ReadAsync(query, limit, requested, token);
        var sentIds = new HashSet<string>(StringComparer.Ordinal);
        var sentSongs = new List<Song>();
        var sentAlbums = new HashSet<string>(StringComparer.Ordinal);
        var sentArtists = new HashSet<string>(StringComparer.Ordinal);
        IReadOnlyList<string> late = [];
        try
        {
            late = await providers.StreamSearchAsync(Execution, query, limit, requested.Contains("Audio"),
                requested.Contains("MusicAlbum"), requested.Contains("MusicArtist"), requested.Contains("Playlist"),
                async (provider, results, playlists, cancellation) =>
                {
                    var owned = await nativeTask.WaitAsync(cancellation);
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
                    var items = new List<Dictionary<string, object?>>();
                    foreach (var song in projected.Where(item => item.Song != null).Select(item => item.Song!))
                    {
                        if (nativeSongs.Any(known => MusicSearchService.MetadataRecordingsMatch(known, song)) ||
                            sentSongs.Any(known => known.Id == song.Id ||
                                song.CanonicalRecordingId is { } recording && known.CanonicalRecordingId == recording ||
                                MusicSearchService.MetadataRecordingsMatch(known, song))) continue;
                        if (!sentIds.Add("Audio:" + song.Id)) continue;
                        sentSongs.Add(song);
                        items.Add(builder.ConvertSongToJellyfinItem(song));
                    }
                    foreach (var album in results.Albums)
                        if (!ownedAlbums.Contains(AlbumKey(album.Title, album.Artist)) && sentAlbums.Add(AlbumKey(album.Title, album.Artist)) &&
                            sentIds.Add("MusicAlbum:" + album.Id)) items.Add(builder.ConvertAlbumToJellyfinItem(album));
                    foreach (var artist in results.Artists)
                        if (!ownedArtists.Contains(Normalize(artist.Name)) && sentArtists.Add(Normalize(artist.Name)) && sentIds.Add("MusicArtist:" + artist.Id))
                            items.Add(requested.SetEquals(["MusicArtist"]) ? builder.ConvertArtistForQuery(artist) : builder.ConvertArtistToJellyfinItem(artist));
                    foreach (var playlist in playlists)
                        if (sentIds.Add("Playlist:" + playlist.Id)) items.Add(builder.ConvertPlaylistToAlbumItem(playlist));
                    var shaped = new JsonResult(new { Items = items });
                    await itemFilter.PrepareAsync(shaped, HttpContext).WaitAsync(cancellation);
                    var rows = JsonSerializer.SerializeToNode(shaped.Value)!["Items"];
                    await WriteAsync(new { provider, items = rows }, cancellation);
                }, token);
            await WriteAsync(new { done = true, late }, disconnected);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (IOException) when (disconnected.IsCancellationRequested) { }
        finally
        {
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

    private sealed class NativeSearch(JellyfinProxyService proxy, HttpContext http, ProtocolExecutionContext context)
        : INativeMusicSearch<Dictionary<string, object?>>
    {
        public async Task<IReadOnlyList<NativeMusicItem<Dictionary<string, object?>>>> ReadAsync(string query, int limit,
            HashSet<string> types, CancellationToken token)
        {
            if (types.SetEquals(["Playlist"])) return [];
            try
            {
                var (body, status) = await proxy.GetJsonAsync($"Users/{Uri.EscapeDataString(context.VerifiedBackendPrincipalId)}/Items",
                    new()
                    {
                        ["SearchTerm"] = query,
                        ["IncludeItemTypes"] = string.Join(',', types.Where(type => !type.Equals("Playlist", StringComparison.OrdinalIgnoreCase))),
                        ["Recursive"] = "true",
                        ["Limit"] = limit.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    }, http.Request.Headers, token);
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
                }, http.Request.Headers, cancellationToken);
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
                null, http.Request.Headers, cancellationToken);
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
