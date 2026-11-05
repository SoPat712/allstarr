using System.Text.Json;

namespace allstarr.Services.Jellyfin;

internal static class JellyfinItemArtwork
{
    internal static string PrimaryImageTag(string itemId, bool isLocal) => isLocal ? itemId : $"{itemId}-art-v3";

    internal static string PrimaryImageUrl(string itemId, string tag) =>
        $"/Items/{Uri.EscapeDataString(itemId)}/Images/Primary?tag={Uri.EscapeDataString(tag)}";

    internal static string? PrimaryImageUrl(JsonElement item, string itemId) =>
        PrimaryImage(item, itemId) is { } image ? PrimaryImageUrl(image.ItemId, image.Tag) : null;

    internal static string? PrimaryImageItemId(JsonElement item, string itemId) => PrimaryImage(item, itemId)?.ItemId;

    private static (string ItemId, string Tag)? PrimaryImage(JsonElement item, string itemId)
    {
        if (item.TryGetProperty("ImageTags", out var tags) && tags.ValueKind == JsonValueKind.Object &&
            Text(tags, "Primary") is { } primary && !string.IsNullOrWhiteSpace(primary))
            return (itemId, primary);
        var albumId = Text(item, "AlbumId");
        var albumTag = Text(item, "AlbumPrimaryImageTag");
        return !string.IsNullOrWhiteSpace(albumId) && !string.IsNullOrWhiteSpace(albumTag) ? (albumId, albumTag) : null;
    }

    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
