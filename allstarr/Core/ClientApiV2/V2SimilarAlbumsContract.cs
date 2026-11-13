using System.Text.Json.Serialization;

namespace allstarr.Core.ClientApiV2;

public sealed record V2SimilarAlbumsResponse(
    [property: JsonPropertyName("albumId")] string AlbumId,
    [property: JsonPropertyName("basedOn")] string? BasedOn,
    [property: JsonPropertyName("albums")] IReadOnlyList<V2SimilarAlbum> Albums);

public sealed record V2SimilarAlbum(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("artists")] IReadOnlyList<V2Ref> Artists,
    [property: JsonPropertyName("year")] int? Year,
    [property: JsonPropertyName("isExternal")] bool IsExternal,
    [property: JsonPropertyName("artwork")] V2Artwork? Artwork,
    [property: JsonPropertyName("songCount")] int? SongCount,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("provider")] V2Provider? Provider);
