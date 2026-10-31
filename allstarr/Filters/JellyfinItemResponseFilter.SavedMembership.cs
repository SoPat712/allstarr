using System.Text.Json.Nodes;
using allstarr.Core.Favorites;
using allstarr.Core.Music;
using allstarr.Services.Jellyfin;

namespace allstarr.Filters;

public sealed partial class JellyfinItemResponseFilter
{
    private static async Task HydrateSavedMembershipAsync(IEnumerable<JsonObject> items, HttpContext http)
    {
        var datedItems = items.Where(item => item["Type"]?.GetValue<string>() != "Playlist").ToArray();
        if (datedItems.Length == 0 || !JellyfinItemFields.ForRequest(http).Includes("DateCreated")) return;
        var reader = http.RequestServices?.GetService<SavedLibraryReader>();
        if (reader == null) return;
        var saved = await reader.ReadForRequestAsync(http);
        var comparer = FavoriteViewReader.ItemComparer(saved);
        var dates = saved.GroupBy(item => item.ItemId, comparer).ToDictionary(group => group.Key, group => group.Min(item => item.FavoritedAt), comparer);
        foreach (var item in datedItems)
            if (item["Id"]?.GetValue<string>() is { } id && dates.TryGetValue(id, out var added))
                item["DateCreated"] = added.ToString("O");
    }
}
