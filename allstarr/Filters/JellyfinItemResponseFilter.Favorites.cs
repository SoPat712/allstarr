using System.Text.Json.Nodes;
using allstarr.Core.Favorites;

namespace allstarr.Filters;

public sealed partial class JellyfinItemResponseFilter
{
    private static void FilterProviderFavorites(JsonNode node, bool favorite)
    {
        if (node is JsonObject root)
        {
            if (root["Items"] is JsonArray items)
            {
                var excluded = items.OfType<JsonObject>().Where(item =>
                    item["Id"] is JsonValue id && id.TryGetValue<string>(out var value) &&
                    FavoriteDisplaySnapshot.Resource(value) != null && item["UserData"] is JsonObject data &&
                    data["IsFavorite"]?.GetValue<bool>() != favorite).ToArray();
                foreach (var item in excluded) items.Remove(item);
                if (root["TotalRecordCount"] is JsonValue count && count.TryGetValue<long>(out var total))
                    root["TotalRecordCount"] = Math.Max(0, total - excluded.Length);
            }
            foreach (var child in root.Select(pair => pair.Value).OfType<JsonNode>()) FilterProviderFavorites(child, favorite);
        }
        else if (node is JsonArray array)
            foreach (var child in array.OfType<JsonNode>()) FilterProviderFavorites(child, favorite);
    }
}
