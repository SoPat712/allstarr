using System.Globalization;
using allstarr.Core.Favorites;
using allstarr.Core.Music;
using allstarr.Core.Protocols.Subsonic;
using allstarr.Services.Subsonic;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

public partial class SubsonicController
{
    [HttpGet, HttpPost]
    [Route("rest/getIndexes")]
    [Route("rest/getIndexes.view")]
    public Task<IActionResult> GetIndexes() => ReadSavedBrowseAsync("indexes");

    [HttpGet, HttpPost]
    [Route("rest/getAlbumList2")]
    [Route("rest/getAlbumList2.view")]
    public Task<IActionResult> GetAlbumList2() => ReadSavedBrowseAsync("albumList2");

    [HttpGet, HttpPost]
    [Route("rest/getRandomSongs")]
    [Route("rest/getRandomSongs.view")]
    public Task<IActionResult> GetRandomSongs() => ReadSavedBrowseAsync("randomSongs");

    [HttpGet, HttpPost]
    [Route("rest/getSongsByGenre")]
    [Route("rest/getSongsByGenre.view")]
    public Task<IActionResult> GetSongsByGenre() => ReadSavedBrowseAsync("songsByGenre");

    [HttpGet, HttpPost]
    [Route("rest/getGenres")]
    [Route("rest/getGenres.view")]
    public Task<IActionResult> GetGenres() => ReadSavedBrowseAsync("genres");

    private async Task<IActionResult> ReadSavedBrowseAsync(string container)
    {
        var parameters = await ExtractAllParameters();
        var format = parameters.GetValueOrDefault("f", "xml");
        IReadOnlyList<FavoriteViewItem> saved = [];
        IReadOnlyDictionary<string, DateTimeOffset> starred = new Dictionary<string, DateTimeOffset>();
        if (!parameters.HasNonEmptyValue("musicFolderId") && CurrentProtocolContext.CanRunUserScopedWork &&
            HttpContext.RequestServices.GetService<SavedLibraryReader>() is { } library)
        {
            try
            {
                saved = await library.ReadForRequestAsync(HttpContext);
                IReadOnlyList<FavoriteViewItem> favorites = _favoriteViews == null ? [] : await _favoriteViews.ReadForRequestAsync(HttpContext);
                starred = favorites.ToDictionary(item => item.ItemId, item => item.FavoritedAt, StringComparer.Ordinal);
                if (container == "albumList2" && parameters.GetValueOrDefault("type") == "starred")
                    saved = saved.Where(item => starred.ContainsKey(item.ItemId)).ToArray();
                saved = SubsonicSavedBrowseAdapter.Select(saved, container, parameters);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            { _logger.LogWarning("Saved library browsing is unavailable ({ExceptionType})", exception.GetType().Name); }
        }
        if (saved.Count == 0) return await RelayMutation(Request.Path.Value!.TrimStart('/'), parameters);
        var headers = new HeaderDictionary(Request.Headers.Where(header =>
                !header.Key.Equals("If-None-Match", StringComparison.OrdinalIgnoreCase) &&
                !header.Key.Equals("If-Modified-Since", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(header => header.Key, header => header.Value, StringComparer.OrdinalIgnoreCase));
        var offset = container is "albumList2" or "songsByGenre"
            ? SubsonicSavedBrowseAdapter.Number(parameters, "offset", 0, int.MaxValue) : 0;
        var limitName = container == "songsByGenre" ? "count" : "size";
        var size = SubsonicSavedBrowseAdapter.Number(parameters, limitName, 10, 500);
        var paged = container is "albumList2" or "songsByGenre";
        var nativeParameters = container == "indexes" ? parameters.SetValue("ifModifiedSince", "0") : parameters;
        var pages = new List<SubsonicProxyResponse>();
        try
        {
            var remaining = (long)offset + size;
            long start = 0;
            do
            {
                var count = (int)Math.Min(500, remaining);
                var request = paged ? nativeParameters.SetValue("offset", start.ToString(CultureInfo.InvariantCulture))
                    .SetValue(limitName, count.ToString(CultureInfo.InvariantCulture)) : nativeParameters;
                var native = await _proxyService.RelayRawAsync(Request.Path.Value!.TrimStart('/'), request,
                    HttpContext.RequestAborted, headers);
                pages.Add(native);
                if (!native.IsSuccessStatusCode || !paged) break;
                var received = SubsonicSavedBrowseAdapter.Count(native, container, format);
                if (received < count || received <= 0) break;
                start += received;
                remaining -= received;
            } while (remaining > 0);
            var merged = SubsonicSavedBrowseAdapter.Merge(pages, saved, container, format, parameters, _responseBuilder, starred);
            if (ReferenceEquals(pages[0], merged) && paged)
                return await RelayMutation(Request.Path.Value!.TrimStart('/'), parameters);
            if (!ReferenceEquals(pages[0], merged)) Response.Headers.CacheControl = "no-store";
            return _relayProtocolAdapter.CreateResult(merged, $"application/{format}");
        }
        catch (HttpRequestException)
        { return _responseBuilder.CreateError(format, 0, "Error connecting to Subsonic server"); }
    }
}
