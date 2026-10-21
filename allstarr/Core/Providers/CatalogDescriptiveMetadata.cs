using System.Text.Json;
using allstarr.Models.Domain;

namespace allstarr.Core.Providers;

internal static class CatalogDescriptiveMetadata
{
    public static List<Artist> EmbeddedAlbumArtists(JsonElement track, string provider) =>
        track.TryGetProperty("album", out var album) ? AlbumArtists(album, provider) : [];

    public static List<Artist> AlbumArtists(JsonElement album, string provider)
    {
        if (album.ValueKind != JsonValueKind.Object || !album.TryGetProperty("artist", out var artist) ||
            artist.ValueKind != JsonValueKind.Object || Text(artist, "name") is not { } name) return [];
        var id = artist.TryGetProperty("id", out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number
            ? value.ToString() : null;
        return [new Artist
        {
            Id = string.IsNullOrWhiteSpace(id) ? string.Empty : $"ext-{provider}-artist-{id}",
            Name = name,
            ExternalProvider = provider,
            ExternalId = id
        }];
    }

    public static string? Text(JsonElement element, string key)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(key, out var value)) return null;
        return value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString() : null;
    }

    public static string? Label(JsonElement album) => Text(album, "label") ??
        (album.TryGetProperty("label", out var label) ? Text(label, "name") : null);
}
