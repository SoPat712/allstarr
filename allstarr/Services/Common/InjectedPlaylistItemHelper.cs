using System.Text.Json;

namespace allstarr.Services.Common;

/// <summary>
/// Detects invalid injected playlist items so local Jellyfin tracks stay raw.
/// </summary>
public static class InjectedPlaylistItemHelper
{
    private const string SyntheticServerId = "allstarr";

    public static bool ContainsSyntheticLocalItems(IEnumerable<Dictionary<string, object?>> items)
    {
        return items.Any(LooksLikeSyntheticLocalItem);
    }

    public static bool ContainsLocalItemsMissingGenreMetadata(IEnumerable<Dictionary<string, object?>> items)
    {
        return items.Any(LooksLikeLocalItemMissingGenreMetadata);
    }

    public static bool ContainsLegacyExternalSourceLabels(IEnumerable<Dictionary<string, object?>> items)
    {
        return items.Any(LooksLikeLegacyExternalSourceLabeledItem);
    }

    public static bool LooksLikeSyntheticLocalItem(IReadOnlyDictionary<string, object?> item)
    {
        var id = GetString(item, "Id");
        if (string.IsNullOrWhiteSpace(id) || IsExternalItemId(id))
        {
            return false;
        }

        var serverId = GetString(item, "ServerId");
        return string.Equals(serverId, SyntheticServerId, StringComparison.OrdinalIgnoreCase);
    }

    public static bool LooksLikeLocalItemMissingGenreMetadata(IReadOnlyDictionary<string, object?> item)
    {
        var id = GetString(item, "Id");
        if (string.IsNullOrWhiteSpace(id) || IsExternalItemId(id) || LooksLikeSyntheticLocalItem(item))
        {
            return false;
        }

        return !HasNonNullValue(item, "Genres") || !HasNonNullValue(item, "GenreItems");
    }

    public static bool LooksLikeLegacyExternalSourceLabeledItem(IReadOnlyDictionary<string, object?> item)
    {
        var id = GetString(item, "Id");
        if (!NeedsProviderSpecificSourceLabel(id))
        {
            return false;
        }

        var name = GetString(item, "Name");
        return name?.EndsWith(" [S]", StringComparison.Ordinal) == true ||
               name?.EndsWith(" [S] [E]", StringComparison.Ordinal) == true;
    }

    private static bool IsExternalItemId(string itemId)
    {
        return itemId.StartsWith("ext-", StringComparison.OrdinalIgnoreCase);
    }

    private static bool NeedsProviderSpecificSourceLabel(string? itemId)
    {
        return !string.IsNullOrWhiteSpace(itemId) &&
               (itemId.StartsWith("ext-deezer-", StringComparison.OrdinalIgnoreCase) ||
                itemId.StartsWith("ext-qobuz-", StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasNonNullValue(IReadOnlyDictionary<string, object?> item, string key)
    {
        if (!item.TryGetValue(key, out var value) || value == null)
        {
            return false;
        }

        return value switch
        {
            JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => false,
            _ => true
        };
    }

    private static string? GetString(IReadOnlyDictionary<string, object?> item, string key)
    {
        if (!item.TryGetValue(key, out var value) || value == null)
        {
            return null;
        }

        return value switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            JsonElement { ValueKind: JsonValueKind.Number } element => element.ToString(),
            _ => value.ToString()
        };
    }
}
