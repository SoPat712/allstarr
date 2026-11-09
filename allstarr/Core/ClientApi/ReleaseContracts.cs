using System.Text.Json.Serialization;

namespace allstarr.Core.ClientApi;

public sealed record ClientReleaseArtist(
    [property: JsonPropertyName("artistId")] string? ArtistId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("imageUrl")] string? ImageUrl = null);

public sealed record ClientRelease(
    [property: JsonPropertyName("releaseId")] string ReleaseId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("releaseDate")] string? ReleaseDate,
    [property: JsonPropertyName("artist")] ClientReleaseArtist Artist,
    [property: JsonPropertyName("albumId")] string AlbumId,
    [property: JsonPropertyName("coverUrl")] string CoverUrl);

public sealed record ClientReleases(
    [property: JsonPropertyName("releases")] IReadOnlyList<ClientRelease> Releases,
    [property: JsonPropertyName("followedArtists")] IReadOnlyList<ClientReleaseArtist> FollowedArtists);

public sealed record ClientArtistFollow(
    [property: JsonPropertyName("followed")] bool Followed,
    [property: JsonPropertyName("artist")] ClientReleaseArtist Artist);
