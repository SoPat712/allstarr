using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using allstarr.Core.Favorites;
using allstarr.Core.Protocols;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace allstarr.Filters;

public sealed class SubsonicFavoriteResponseFilter(FavoriteViewReader favorites) : IAsyncResultFilter
{
    private static readonly HashSet<string> MusicElements = new(StringComparer.Ordinal)
    {
        "subsonic-response", "searchResult", "searchResult2", "searchResult3", "song", "album", "artist",
        "entry", "child", "directory", "playlist", "albumList", "albumList2", "starred", "starred2",
        "artists", "indexes", "index", "similarSongs", "similarSongs2", "topSongs", "randomSongs", "songsByGenre",
        "artistInfo", "artistInfo2", "similarArtist", "playQueue"
    };

    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        var http = context.HttpContext;
        if (context.Result is JsonResult json)
        {
            var root = JsonSerializer.SerializeToNode(json.Value);
            if (root != null && await HydrateJsonAsync(root, http))
            {
                json.Value = root;
                Uncache(http);
            }
        }
        else if (context.Result is ContentResult { Content: { } content } result)
        {
            var type = result.ContentType?.Split(';')[0].Trim();
            if (type == "application/json")
            {
                var root = JsonNode.Parse(content);
                if (root != null && await HydrateJsonAsync(root, http))
                {
                    result.Content = NativeItemText.PreserveJson(content, root.ToJsonString(), "id");
                    Uncache(http);
                }
            }
            else if (type == "application/xml")
            {
                var document = XDocument.Parse(content, LoadOptions.PreserveWhitespace);
                var items = document.Root == null ? [] : Items(document.Root).ToArray();
                if (items.Length != 0)
                {
                    var saved = await ReadAsync(http);
                    foreach (var item in items) item.SetAttributeValue("starred", saved.GetValueOrDefault(item.Attribute("id")!.Value));
                    result.Content = NativeItemText.PreserveXml(content, document.ToString(SaveOptions.DisableFormatting));
                    Uncache(http);
                }
            }
        }
        await next();
    }

    private async Task<Dictionary<string, string>> ReadAsync(HttpContext http) =>
        (await favorites.ReadForRequestAsync(http)).ToDictionary(item => item.ItemId,
            item => item.FavoritedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), StringComparer.Ordinal);

    private async Task<bool> HydrateJsonAsync(JsonNode root, HttpContext http)
    {
        var items = Items(root).ToArray();
        if (items.Length == 0) return false;
        var saved = await ReadAsync(http);
        foreach (var item in items)
        {
            if (saved.TryGetValue(item["id"]!.GetValue<string>(), out var timestamp)) item["starred"] = timestamp;
            else item.Remove("starred");
        }
        return true;
    }

    private static IEnumerable<JsonObject> Items(JsonNode node)
    {
        if (node is JsonObject item)
        {
            if (item["id"] is JsonValue id && id.TryGetValue<string>(out var value) &&
                FavoriteDisplaySnapshot.Resource(value) != null) yield return item;
            foreach (var child in item.Where(pair => MusicElements.Contains(pair.Key)).Select(pair => pair.Value).OfType<JsonNode>())
                foreach (var nested in Items(child)) yield return nested;
        }
        else if (node is JsonArray array)
            foreach (var child in array.OfType<JsonNode>())
                foreach (var nested in Items(child)) yield return nested;
    }

    private static IEnumerable<XElement> Items(XElement item)
    {
        if (item.Attribute("id") is { } id && FavoriteDisplaySnapshot.Resource(id.Value) != null) yield return item;
        foreach (var child in item.Elements().Where(child => MusicElements.Contains(child.Name.LocalName)))
            foreach (var nested in Items(child)) yield return nested;
    }

    private static void Uncache(HttpContext http)
    {
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.Remove("ETag");
        http.Response.Headers.Remove("Last-Modified");
    }
}
