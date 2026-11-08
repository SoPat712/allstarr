using System.Text.Json.Serialization;

namespace allstarr.Core.ClientApi;

public sealed record ClientSimilarAlbums(
    [property: JsonPropertyName("itemId")] string ItemId,
    [property: JsonPropertyName("basedOn")] string? BasedOn,
    [property: JsonPropertyName("albums")] IReadOnlyList<ClientSimilarAlbum> Albums);

public sealed record ClientSimilarAlbum(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("artist")] string Artist,
    [property: JsonPropertyName("artistId")] string? ArtistId,
    [property: JsonPropertyName("year")] int? Year,
    [property: JsonPropertyName("songCount")] int? SongCount,
    [property: JsonPropertyName("coverUrl")] string? CoverUrl,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("isExternal")] bool IsExternal,
    [property: JsonPropertyName("provider")] ItemDetailsProvider? Provider);
