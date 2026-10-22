using System.Text.Json.Nodes;
using allstarr.Core.Music;

namespace allstarr.Filters;

public sealed partial class JellyfinItemResponseFilter
{
    private static async Task HydrateSavedMembershipAsync(IEnumerable<JsonObject> items, HttpContext http)
    {
        var reader = http.RequestServices?.GetService<SavedLibraryReader>();
        if (reader == null) return;
        var dates = (await reader.ReadForRequestAsync(http)).ToDictionary(item => item.ItemId, item => item.FavoritedAt, StringComparer.Ordinal);
        foreach (var item in items)
            if (item["Id"]?.GetValue<string>() is { } id && dates.TryGetValue(id, out var added))
                item["DateCreated"] = added.ToString("O");
    }
}
