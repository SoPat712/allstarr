using System.Text.Json;
using allstarr.Services.Spotify;

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

    public static bool ContainsUnavailableExternalItems(IEnumerable<Dictionary<string, object?>> items)
    {
        return items.Any(LooksLikeUnavailableExternalItem);
    }

    public static List<Dictionary<string, object?>> RemoveUnavailableExternalItems(
        IEnumerable<Dictionary<string, object?>> items)
    {
        return items.Where(item => !LooksLikeUnavailableExternalItem(item)).ToList();
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

    public static bool LooksLikeUnavailableExternalItem(IReadOnlyDictionary<string, object?> item)
    {
        if (!string.Equals(GetString(item, "ServerId"), SyntheticServerId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var id = GetString(item, "Id");
        if (!string.IsNullOrWhiteSpace(id) &&
            !ExternalTrackPlaybackPolicy.CanUseForPlayback("unknown", id))
        {
            return true;
        }

        if (!item.TryGetValue("ProviderIds", out var providerIds) || providerIds == null)
        {
            return false;
        }

        return providerIds switch
        {
            IReadOnlyDictionary<string, string> values => values.Keys.Any(IsUnavailableProvider),
            JsonElement { ValueKind: JsonValueKind.Object } element =>
                element.EnumerateObject().Any(property => IsUnavailableProvider(property.Name)),
            _ => false
        };
    }

    private static bool IsUnavailableProvider(string provider) =>
        !ExternalTrackPlaybackPolicy.CanUseForPlayback(provider);

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
