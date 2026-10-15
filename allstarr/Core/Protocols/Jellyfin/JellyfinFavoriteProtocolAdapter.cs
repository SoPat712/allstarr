using System.Text.Json;
using System.Text.Json.Nodes;

namespace allstarr.Core.Protocols.Jellyfin;

public static class JellyfinFavoriteProtocolAdapter
{
    public static JsonObject? Merge(JsonDocument native, IReadOnlyList<Dictionary<string, object?>> favorites,
        int start, int size, string sortBy, string sortOrder)
    {
        if (JsonNode.Parse(native.RootElement.GetRawText()) is not JsonObject root || root["Items"] is not JsonArray nativeItems ||
            root["TotalRecordCount"] is not JsonValue count || !count.TryGetValue<long>(out var total))
            return null;
        var existing = nativeItems.OfType<JsonObject>().Select(item => item["Id"]?.ToString())
            .ToHashSet(StringComparer.Ordinal);
        var added = favorites.Where(item => !existing.Contains(item["Id"]?.ToString()))
            .Select(item => System.Text.Json.JsonSerializer.SerializeToNode(item)!).ToArray();
        var sort = sortBy.Split(',')[0];
        IEnumerable<JsonNode?> page;
        if (sort.Equals("SortName", StringComparison.OrdinalIgnoreCase) || sort.Equals("Name", StringComparison.OrdinalIgnoreCase))
        {
            var rows = nativeItems.Select((item, index) => (Item: item, Native: 0, Index: index))
                .Concat(added.Select((item, index) => (Item: (JsonNode?)item, Native: 1, Index: index)));
            var descending = sortOrder.Split(',')[0].Equals("Descending", StringComparison.OrdinalIgnoreCase);
            Func<(JsonNode? Item, int Native, int Index), string?> name = row => row.Item?["SortName"]?.ToString() ?? row.Item?["Name"]?.ToString();
            var ordered = descending ? rows.OrderByDescending(name, StringComparer.OrdinalIgnoreCase) :
                rows.OrderBy(name, StringComparer.OrdinalIgnoreCase);
            page = ordered.ThenBy(row => row.Native).ThenBy(row => row.Index).Skip(start).Take(size).Select(row => row.Item);
        }
        else
        {
            var nativePage = nativeItems.Skip(start).Take(size).ToArray();
            page = nativePage;
            if ((long)start + nativePage.Length >= total)
                page = page.Concat(added.Skip((int)Math.Min(int.MaxValue, Math.Max(0L, start - total))).Take(size - nativePage.Length));
        }
        root["Items"] = new JsonArray(page.Select(item => item?.DeepClone()).ToArray());
        root["TotalRecordCount"] = total + added.Length;
        root["StartIndex"] = start;
        return root;
    }
}
