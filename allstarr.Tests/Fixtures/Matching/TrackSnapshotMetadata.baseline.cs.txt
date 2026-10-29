using System.Text.Json;

namespace allstarr.Core.Matching;

public static class TrackSnapshotMetadata
{
    public static string? MusicBrainzRecordingId(JsonElement root) =>
        Guid.TryParse(Text(root, "musicBrainzRecordingId", "MusicBrainzRecordingId"), out var id) && id != Guid.Empty
            ? id.ToString("D") : null;

    public static bool HasDisplayLabels(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            return !string.IsNullOrWhiteSpace(Text(document.RootElement, "title", "Title", "name", "Name")) &&
                   !string.IsNullOrWhiteSpace(Artist(document.RootElement));
        }
        catch (JsonException) { return false; }
    }

    public static bool? IsExplicit(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        return (root.TryGetProperty("isExplicit", out var value) ||
                root.TryGetProperty("IsExplicit", out value)) &&
               value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;
    }

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
