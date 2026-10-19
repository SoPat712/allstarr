using System.Text.Json;
using System.Text.Json.Nodes;
using allstarr.Core.Playlists;
using allstarr.Core.Protocols;
using allstarr.Services.Jellyfin;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace allstarr.Filters;

public sealed class JellyfinItemResponseFilter(JellyfinProxyService proxy) : IAsyncResultFilter
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
        foreach (var item in items)
        {
            item.Remove("ServerId");
            item.Insert(Math.Min(1, item.Count), "ServerId", JsonValue.Create(serverId));
            JellyfinItemFields.ForRequest(http).Apply(item);
        }
        if (result is JsonResult jsonResult) jsonResult.Value = root;
        else if (result is ContentResult contentResult) contentResult.Content = root.ToJsonString();
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
