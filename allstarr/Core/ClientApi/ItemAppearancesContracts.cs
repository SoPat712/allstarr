using System.Text.Json.Serialization;

namespace allstarr.Core.ClientApi;

public sealed record ClientItemAppearances(
    [property: JsonPropertyName("itemId")] string ItemId,
    [property: JsonPropertyName("songCount")] int SongCount,
    [property: JsonPropertyName("appearances")] IReadOnlyList<ClientItemAppearance> Appearances);

public sealed record ClientItemAppearance(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("subtitle")] string? Subtitle,
    [property: JsonPropertyName("coverUrl")] string CoverUrl,
    [property: JsonPropertyName("matchedSongCount")] int MatchedSongCount,
    [property: JsonPropertyName("playlistId")] string? PlaylistId);
