using System.Globalization;
using allstarr.Core.Protocols.Jellyfin;
using allstarr.Core.Favorites;
using allstarr.Core.Protocols;
using allstarr.Services.Jellyfin;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

public partial class JellyfinController
{
    private async Task<IActionResult> ReadFavoriteItemsAsync(string? searchTerm, string? includeItemTypes,
        int startIndex, int limit, string? userId, string? nativeEndpoint = null)
    {
        var context = HttpContext.RequireProtocolExecutionContext();
        IReadOnlyList<FavoriteViewItem> saved = [];
        if (_favoriteViews != null && (userId == null || userId == context.VerifiedBackendPrincipalId))
        {
            try { saved = await _favoriteViews.ReadForRequestAsync(HttpContext); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            { _logger.LogWarning("Saved favorites are unavailable ({ExceptionType})", exception.GetType().Name); }
        }
        var types = (includeItemTypes ?? JellyfinMusicEndpointPolicy.DefaultMusicItemTypes)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ids = Request.Query["Ids"].ToString().Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(FavoriteViewReader.ItemComparer(saved));
        var favorites = saved.Where(item => ids.Count == 0 || ids.Contains(item.ItemId))
            .Where(item => JellyfinFavoriteScope.Matches(item, Request.Query, _settings.LibraryId))
            .Where(item => string.IsNullOrWhiteSpace(searchTerm) || item.Display.Title.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ||
                item.Display.Artists.Any(artist => artist.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)) ||
                item.Display.Album?.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) == true)
            .Select(_responseBuilder.ConvertFavoriteToJellyfinItem)
            .Where(item => types.Contains(item["Type"]?.ToString() ?? "")).ToArray();
        var query = Request.Query.ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        query["IncludeItemTypes"] = string.Join(',', types);
        if (searchTerm != null) query["SearchTerm"] = searchTerm;
        var start = Math.Max(0, startIndex);
        var size = Math.Max(0, limit);
        IHeaderDictionary headers = Request.Headers;
        if (favorites.Length != 0)
        {
            query["StartIndex"] = "0";
            query["Limit"] = Math.Min(int.MaxValue, (long)start + size).ToString(CultureInfo.InvariantCulture);
            headers = new HeaderDictionary(Request.Headers.Where(header =>
                    !header.Key.Equals("If-None-Match", StringComparison.OrdinalIgnoreCase) &&
                    !header.Key.Equals("If-Modified-Since", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(header => header.Key, header => header.Value, StringComparer.OrdinalIgnoreCase));
        }
        var endpoint = nativeEndpoint ?? (userId == null ? "Items" : $"Users/{Uri.EscapeDataString(userId)}/Items");
        var (native, status) = JellyfinFavoriteScope.RestrictNativeQuery(query)
            ? (System.Text.Json.JsonSerializer.SerializeToDocument(new { Items = Array.Empty<object>(), TotalRecordCount = 0, StartIndex = start }), 200)
            : await _proxyService.GetJsonAsync(endpoint, query, headers);
        if (native == null || status != 200 || favorites.Length == 0) return HandleProxyResponse(native, status);
        var root = JellyfinFavoriteProtocolAdapter.Merge(native, favorites, start, size,
            Request.Query["SortBy"].ToString(), Request.Query["SortOrder"].ToString());
        if (root == null) return HandleProxyResponse(native, status);
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Remove("ETag");
        Response.Headers.Remove("Last-Modified");
        return new ContentResult
        {
            Content = NativeItemText.PreserveJson(native.RootElement.GetRawText(), root.ToJsonString(), "Id"),
            ContentType = "application/json",
            StatusCode = status
        };
    }
}
