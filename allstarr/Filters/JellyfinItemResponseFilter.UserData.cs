using System.Text.Json.Nodes;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Filters;

public sealed partial class JellyfinItemResponseFilter
{
    private async Task HydrateItemUserDataAsync(JsonObject[] items, HttpContext http)
    {
        if (userData == null) return;
        var saved = await userData.ReadForRequestAsync(http, items.Select(item => item["Id"]!.GetValue<string>()));
        foreach (var item in items)
        {
            var id = item["Id"]!.GetValue<string>();
            if (ProviderUserDataStore.IsProviderItem(id) && item["UserData"] is JsonObject data)
                MusicUserDataProjection.Jellyfin(data, saved.GetValueOrDefault(id) ?? new());
        }
    }

    private async Task<bool> HydrateStandaloneUserDataAsync(JsonNode root, IActionResult result, HttpContext http)
    {
        if (userData == null || root is not JsonObject data || data["ItemId"] is not JsonValue value ||
            !value.TryGetValue<string>(out var id) || !ProviderUserDataStore.IsProviderItem(id) ||
            !data.ContainsKey("PlaybackPositionTicks") || !data.ContainsKey("IsFavorite")) return false;
        var saved = await userData.ReadForRequestAsync(http, [id]);
        MusicUserDataProjection.Jellyfin(data, saved.GetValueOrDefault(id) ?? new());
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.Remove("ETag");
        http.Response.Headers.Remove("Last-Modified");
        if (result is JsonResult json) json.Value = root;
        else if (result is ContentResult content)
            content.Content = NativeItemText.PreserveJson(content.Content!, root.ToJsonString(), "Id");
        return true;
    }
}
