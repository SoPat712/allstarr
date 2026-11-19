using System.ComponentModel;
using System.Text.Json.Serialization;

namespace allstarr.Core.ClientApiV2;

public sealed record V2ReleaseArtist(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("imageUrl")] string? ImageUrl)
{
    [JsonPropertyName("libraryId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), Description("The media server artist when it is in the library.")]
    public string? LibraryId => V2Ids.LibrarySource(Id, V2IdKind.Artist);

    [JsonPropertyName("aliases"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), Description("The older service id of an artist that is not in the library, which the media-server routes accept.")]
    public IReadOnlyList<string>? Aliases => V2Ids.ServiceSource(Id, V2IdKind.Artist) is { } source ? [source] : null;
}

public sealed record V2Release(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("releaseDate")] string? ReleaseDate,
    [property: JsonPropertyName("artist")] V2ReleaseArtist Artist,
    [property: JsonPropertyName("albumId")] string AlbumId,
    [property: JsonPropertyName("coverUrl")] string CoverUrl)
{
    [JsonPropertyName("albumLibraryId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), Description("The media server album when it is in the library.")]
    public string? AlbumLibraryId => V2Ids.LibrarySource(AlbumId, V2IdKind.Album);

    [JsonPropertyName("albumAliases"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), Description("The older service id of an album that is not in the library, which the media-server routes accept.")]
    public IReadOnlyList<string>? AlbumAliases => V2Ids.ServiceSource(AlbumId, V2IdKind.Album) is { } source ? [source] : null;
}

public sealed record V2Releases(
    [property: JsonPropertyName("releases")] IReadOnlyList<V2Release> Releases,
    [property: JsonPropertyName("followedArtists")] IReadOnlyList<V2ReleaseArtist> FollowedArtists);

public sealed record V2ArtistFollow(
    [property: JsonPropertyName("followed")] bool Followed,
    [property: JsonPropertyName("artist")] V2ReleaseArtist Artist);
