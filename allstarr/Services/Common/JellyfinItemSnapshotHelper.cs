using System.Text.Json;
using allstarr.Models.Domain;

namespace allstarr.Services.Common;

/// <summary>
/// Stores and restores raw Jellyfin item snapshots on local songs for cache safety.
/// </summary>
public static class JellyfinItemSnapshotHelper
{
    private const string RawItemKey = "RawItem";

    public static void StoreRawItemSnapshot(Song song, JsonElement item)
    {
        var rawItem = DeserializeDictionary(item.GetRawText());
        if (rawItem == null)
        {
            return;
        }

        song.JellyfinMetadata ??= new Dictionary<string, object?>();
        song.JellyfinMetadata[RawItemKey] = rawItem;
    }

    public static bool HasRawItemSnapshot(Song? song)
    {
        return song?.JellyfinMetadata?.ContainsKey(RawItemKey) == true;
    }

    public static bool TryGetClonedRawItemSnapshot(Song? song, out Dictionary<string, object?> rawItem)
    {
        rawItem = new Dictionary<string, object?>();

        if (song?.JellyfinMetadata == null ||
            !song.JellyfinMetadata.TryGetValue(RawItemKey, out var snapshot) ||
            snapshot == null)
        {
            return false;
        }

        var normalized = snapshot switch
        {
            Dictionary<string, object?> dict => DeserializeDictionary(JsonSerializer.Serialize(dict)),
            JsonElement { ValueKind: JsonValueKind.Object } json => DeserializeDictionary(json.GetRawText()),
            _ => DeserializeDictionary(JsonSerializer.Serialize(snapshot))
        };

        if (normalized == null)
        {
            return false;
        }

        rawItem = normalized;
        return true;
    }

    private static Dictionary<string, object?>? DeserializeDictionary(string json)
    {
        return JsonSerializer.Deserialize<Dictionary<string, object?>>(json);
    }
}
