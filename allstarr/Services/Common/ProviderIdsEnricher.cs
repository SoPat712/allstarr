using System.Text.Json;

namespace allstarr.Services.Common;

/// <summary>
/// Normalizes and enriches Jellyfin item ProviderIds metadata.
/// </summary>
public static class ProviderIdsEnricher
{
    public static void EnsureSpotifyProviderIds(
        Dictionary<string, object?> item,
        string? spotifyId,
        string? spotifyAlbumId = null)
    {
        if (item == null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(spotifyId) && string.IsNullOrWhiteSpace(spotifyAlbumId))
        {
            return;
        }

        var providerIds = GetOrCreateProviderIds(item);

        if (!string.IsNullOrWhiteSpace(spotifyId) && !providerIds.ContainsKey("Spotify"))
        {
            providerIds["Spotify"] = spotifyId.Trim();
        }

        if (!string.IsNullOrWhiteSpace(spotifyAlbumId) && !providerIds.ContainsKey("SpotifyAlbum"))
        {
            providerIds["SpotifyAlbum"] = spotifyAlbumId.Trim();
        }
    }

    private static Dictionary<string, string> GetOrCreateProviderIds(Dictionary<string, object?> item)
    {
        if (!item.TryGetValue("ProviderIds", out var rawProviderIds) || rawProviderIds == null)
        {
            var created = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            item["ProviderIds"] = created;
            return created;
        }

        if (rawProviderIds is Dictionary<string, string> stringDict)
        {
            if (!ReferenceEquals(stringDict.Comparer, StringComparer.OrdinalIgnoreCase))
            {
                var normalized = new Dictionary<string, string>(stringDict, StringComparer.OrdinalIgnoreCase);
                item["ProviderIds"] = normalized;
                return normalized;
            }

            return stringDict;
        }

        var converted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (rawProviderIds is Dictionary<string, object?> objectDict)
        {
            foreach (var (key, value) in objectDict)
            {
                var str = ConvertToString(value);
                if (str != null)
                {
                    converted[key] = str;
                }
            }

            item["ProviderIds"] = converted;
            return converted;
        }

        if (rawProviderIds is JsonElement jsonElement && jsonElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in jsonElement.EnumerateObject())
            {
                var str = prop.Value.ValueKind == JsonValueKind.String
                    ? prop.Value.GetString()
                    : prop.Value.GetRawText();

                if (str != null)
                {
                    converted[prop.Name] = str;
                }
            }

            item["ProviderIds"] = converted;
            return converted;
        }

        item["ProviderIds"] = converted;
        return converted;
    }

    private static string? ConvertToString(object? value)
    {
        if (value == null)
        {
            return null;
        }

        return value switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
            JsonElement je => je.GetRawText(),
            _ => value.ToString()
        };
    }
}
