using System.ComponentModel;
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
    [property: JsonPropertyName("provider")] V2Provider? Provider)
{
    [JsonPropertyName("libraryId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), Description("The media server album when it is in the library.")]
    public string? LibraryId => V2Ids.LibrarySource(Id, V2IdKind.Album);

    [JsonPropertyName("aliases"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), Description("The older service id of an album that is not in the library, which the media-server routes accept.")]
    public IReadOnlyList<string>? Aliases => V2Ids.ServiceSource(Id, V2IdKind.Album) is { } source ? [source] : null;
}
