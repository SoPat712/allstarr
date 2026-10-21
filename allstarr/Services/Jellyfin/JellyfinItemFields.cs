using System.Text.Json.Nodes;

namespace allstarr.Services.Jellyfin;

internal sealed class JellyfinItemFields(IQueryCollection? query, bool includeAllFields = false)
{
    private readonly HashSet<string> _fields = (query?["Fields"].ToString() ?? string.Empty)
        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static JellyfinItemFields ForRequest(HttpContext? http, IQueryCollection? query = null,
        bool includeAllFields = false) =>
        new(query ?? http?.Request.Query, includeAllFields || !IsItemListRequest(http));

    private static bool IsItemListRequest(HttpContext? http)
    {
        var path = http?.Request.Path.Value;
        if (string.IsNullOrEmpty(path)) return true;
        var endpoint = path.TrimEnd('/').Split('/').Last();
        return ListEndpoints.Contains(endpoint);
    }

    public bool Includes(string field) => includeAllFields || _fields.Contains(field);

    public Dictionary<string, object?> Apply(Dictionary<string, object?> item)
    {
        foreach (var key in item.Keys.ToArray())
        {
            if (ShouldOmit(key, item[key] == null))
                item.Remove(key);
        }
        return item;
    }

    public void Apply(JsonObject item)
    {
        foreach (var key in item.Select(pair => pair.Key).ToArray())
        {
            if (ShouldOmit(key, item[key] == null))
                item.Remove(key);
        }
    }

    private bool ShouldOmit(string key, bool isNull)
    {
        var field = key switch { "GenreItems" => "Genres", "AlbumCount" => "ItemCounts", _ => key };
        return OptionalFields.Contains(field) && !Includes(field) ||
            UnsupportedFields.Contains(key) || isNull && key != "ChannelId";
    }

    private static readonly HashSet<string> ListEndpoints = new(StringComparer.OrdinalIgnoreCase)
    {
        "Items", "Albums", "Artists", "AlbumArtists", "Latest", "Resume", "Suggestions",
        "Similar", "InstantMix", "Search", "Episodes", "Seasons", "NextUp", "Upcoming",
        "Recommendations", "Views", "UserViews", "MediaFolders"
    };

    private static readonly HashSet<string> OptionalFields = new(StringComparer.Ordinal)
    {
        "Genres", "DateCreated", "MediaSources", "MediaStreams", "ParentId", "ProviderIds",
        "Path", "SortName", "Overview", "People", "Tags", "PrimaryImageAspectRatio",
        "CanDelete", "CanDownload", "ChildCount", "ItemCounts", "Studios"
    };

    private static readonly HashSet<string> UnsupportedFields = new(StringComparer.Ordinal)
    {
        "AudioInfo", "BasicSyncInfo", "SupportsSync", "ParentBackdropItemId", "ParentBackdropImageTags",
        "ParentLogoItemId", "ParentLogoImageTag"
    };
}
