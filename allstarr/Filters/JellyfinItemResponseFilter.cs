using System.Text.Json;
using System.Text.Json.Nodes;
using allstarr.Core.Playlists;
using allstarr.Core.Favorites;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Services.Jellyfin;
using allstarr.Services.Common;
using allstarr.Services.Local;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace allstarr.Filters;

public sealed partial class JellyfinItemResponseFilter(JellyfinProxyService proxy, IMediaAssetResolver? artwork = null,
    FavoriteViewReader? favorites = null, ProviderUserDataStore? userData = null) : IAsyncResultFilter
{
    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        try
        {
            await PrepareAsync(context.Result, context.HttpContext);
        }
        catch (HttpRequestException)
        {
            context.Result = new StatusCodeResult(StatusCodes.Status502BadGateway);
        }
        await next();
    }

    internal async Task PrepareAsync(IActionResult result, HttpContext http)
    {
        var root = result switch
        {
            JsonResult json => JsonSerializer.SerializeToNode(json.Value),
            ContentResult { ContentType: "application/json", Content: { } content } => JsonNode.Parse(content),
            _ => null
        };
        if (root == null) return;
        if (await HydrateStandaloneUserDataAsync(root, result, http)) return;
        var allItems = BaseItems(root).ToArray();
        var items = allItems.Where(IsExternal).ToArray();
        if (items.Length == 0) return;

        var instance = http.GetProtocolExecutionContext()?.BackendInstanceId;
        if (instance == null) return;
        var nativeServerId = allItems.Where(item => !IsExternal(item))
            .Select(item => item["ServerId"]?.GetValue<string>()).FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));
        if (nativeServerId != null) proxy.RememberServerId(instance, nativeServerId);
        var serverId = await proxy.GetServerIdAsync(instance, http.RequestAborted);
        http.Items[JellyfinResponseBuilder.ServerIdItemKey] = serverId;
        var saved = favorites == null ? [] : (await favorites.ReadForRequestAsync(http))
            .Select(item => item.ItemId).ToHashSet(StringComparer.Ordinal);
        await HydrateItemUserDataAsync(items, http);
        foreach (var item in items)
        {
            item.Remove("ServerId");
            item.Insert(Math.Min(1, item.Count), "ServerId", JsonValue.Create(serverId));
            if (artwork != null && item["ImageTags"]?["Primary"] is JsonValue primary &&
                primary.TryGetValue<string>(out var tag) && !string.IsNullOrWhiteSpace(tag))
            {
                var parsed = LocalLibraryService.ParseExternalResource(item["Id"]!.GetValue<string>());
                if (parsed.isExternal && await artwork.GetCachedBlurHashAsync(new MediaAssetIdentity(
                    http.GetProtocolExecutionContext()?.Actor?.EffectiveUserId, null,
                    parsed.provider!, parsed.type!, parsed.externalId!)) is { Length: > 0 } hash)
                    item["ImageBlurHashes"] = new JsonObject { ["Primary"] = new JsonObject { [tag] = hash } };
            }
            var id = item["Id"]!.GetValue<string>();
            if (FavoriteDisplaySnapshot.Resource(id) != null && item["UserData"] is JsonObject userData)
                userData["IsFavorite"] = saved.Contains(id);
            JellyfinItemFields.ForRequest(http).Apply(item);
        }
        if (bool.TryParse(http.Request.Query["IsFavorite"], out var favoriteOnly))
            FilterProviderFavorites(root, favoriteOnly);
        else if (http.Request.Query["Filters"].ToString().Split(',', StringSplitOptions.TrimEntries)
            .Contains("IsFavorite", StringComparer.OrdinalIgnoreCase))
            FilterProviderFavorites(root, true);
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.Remove("ETag");
        http.Response.Headers.Remove("Last-Modified");
        if (result is JsonResult jsonResult) jsonResult.Value = root;
        else if (result is ContentResult contentResult)
            contentResult.Content = JellyfinQueryResponseRows.Preserve(
                NativeItemText.PreserveJson(contentResult.Content!, root.ToJsonString(), "Id"), http);
    }

    private static bool IsExternal(JsonObject item)
    {
        var id = item["Id"]!.GetValue<string>();
        return id.StartsWith("ext-", StringComparison.OrdinalIgnoreCase) ||
            id.StartsWith(PlaylistVirtualizationService.IdPrefix, StringComparison.Ordinal) ||
            id.StartsWith(PlaylistVirtualizationService.UnresolvedItemPrefix, StringComparison.Ordinal);
    }

    private static IEnumerable<JsonObject> BaseItems(JsonNode node)
    {
        if (node is JsonObject item)
        {
            if (!item.ContainsKey("ItemId") && item["Id"] is JsonValue id && id.TryGetValue<string>(out _) &&
                item["Type"] is JsonValue type && type.TryGetValue<string>(out var kind) &&
                kind is "Audio" or "MusicAlbum" or "MusicArtist" or "Playlist")
            {
                yield return item;
                if (!IsExternal(item)) yield break;
            }
            foreach (var child in item.Select(pair => pair.Value).OfType<JsonNode>())
                foreach (var nested in BaseItems(child)) yield return nested;
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array.OfType<JsonNode>())
                foreach (var nested in BaseItems(child)) yield return nested;
        }
    }
}
