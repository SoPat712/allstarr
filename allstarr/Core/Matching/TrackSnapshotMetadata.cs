using System.Text.Json;

namespace allstarr.Core.Matching;

public static class TrackSnapshotMetadata
{
    public static string? Artist(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        foreach (var key in new[] { "artists", "Artists" })
        {
            if (!root.TryGetProperty(key, out var values) || values.ValueKind != JsonValueKind.Array) continue;
            var names = values.EnumerateArray()
                .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : Text(item, "name", "Name"))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (names.Length > 0) return string.Join(", ", names);
        }
        return Text(root, "artist", "Artist", "primaryArtist", "PrimaryArtist");
    }

    private static string? Text(JsonElement value, params string[] keys)
    {
        if (value.ValueKind != JsonValueKind.Object) return null;
        foreach (var key in keys)
            if (value.TryGetProperty(key, out var text) && text.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(text.GetString())) return text.GetString();
        return null;
    }
}
