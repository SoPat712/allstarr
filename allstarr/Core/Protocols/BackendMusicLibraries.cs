using System.Text.Json;

namespace allstarr.Core.Protocols;

public static class BackendMusicLibraries
{
    public const string SelectionKey = "Backend:MusicLibraryIds";

    public static string[] Read(ProtocolKind protocol, JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("The backend library response is invalid.");
        JsonElement libraries;
        if (protocol == ProtocolKind.Jellyfin)
        {
            if (!root.TryGetProperty("Items", out libraries) || libraries.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("The Jellyfin library response is invalid.");
            return libraries.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.Object &&
                    item.TryGetProperty("CollectionType", out var type) &&
                    type.ValueKind == JsonValueKind.String &&
                    string.Equals(type.GetString(), "music", StringComparison.OrdinalIgnoreCase))
                .Select(item => ReadId(item, "Id"))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        }

        if (protocol != ProtocolKind.Subsonic ||
            !root.TryGetProperty("subsonic-response", out var envelope) || envelope.ValueKind != JsonValueKind.Object ||
            !envelope.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String ||
            status.GetString() != "ok" ||
            !envelope.TryGetProperty("musicFolders", out var folders) || folders.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("The Subsonic library response is invalid.");
        if (!folders.TryGetProperty("musicFolder", out libraries)) return [];
        if (libraries.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("The Subsonic library response is invalid.");
        return libraries.EnumerateArray().Select(item => ReadId(item, "id"))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    public static string[] Select(IEnumerable<string> available, IConfiguration? configuration, ProtocolKind protocol)
    {
        var configured = configuration?[SelectionKey];
        if (configured == null && protocol == ProtocolKind.Jellyfin)
            configured = configuration?["Jellyfin:LibraryId"];
        var selected = (configured ?? string.Empty).Split([',', '\n', '\r'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
        return available.Where(id => selected.Count == 0 || selected.Contains(id))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    private static string ReadId(JsonElement item, string property)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(property, out var id) ||
            id.ValueKind is not (JsonValueKind.String or JsonValueKind.Number))
            throw new InvalidOperationException("The backend library identifier is invalid.");
        var value = id.ValueKind == JsonValueKind.String ? id.GetString() : id.GetRawText();
        if (string.IsNullOrWhiteSpace(value) || value.Length > 300)
            throw new InvalidOperationException("The backend library identifier is invalid.");
        return value;
    }
}
