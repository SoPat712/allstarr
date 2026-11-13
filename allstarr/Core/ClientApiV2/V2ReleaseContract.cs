using System.Text.Json.Serialization;

namespace allstarr.Core.ClientApiV2;

public sealed record V2ReleaseArtist(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("imageUrl")] string? ImageUrl);

public sealed record V2Release(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("releaseDate")] string? ReleaseDate,
    [property: JsonPropertyName("artist")] V2ReleaseArtist Artist,
    [property: JsonPropertyName("albumId")] string AlbumId,
    [property: JsonPropertyName("coverUrl")] string CoverUrl);

public sealed record V2Releases(
    [property: JsonPropertyName("releases")] IReadOnlyList<V2Release> Releases,
    [property: JsonPropertyName("followedArtists")] IReadOnlyList<V2ReleaseArtist> FollowedArtists);

public sealed record V2ArtistFollow(
    [property: JsonPropertyName("followed")] bool Followed,
    [property: JsonPropertyName("artist")] V2ReleaseArtist Artist);
