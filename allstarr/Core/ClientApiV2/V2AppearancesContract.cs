using System.ComponentModel;
using System.Text.Json.Serialization;

namespace allstarr.Core.ClientApiV2;

public sealed record V2TrackAppearances(
    [property: JsonPropertyName("trackId")] string TrackId,
    [property: JsonPropertyName("songCount")] int SongCount,
    [property: JsonPropertyName("appearances")] IReadOnlyList<V2Appearance> Appearances);

public sealed record V2Appearance(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("subtitle")] string? Subtitle,
    [property: JsonPropertyName("coverUrl")] string CoverUrl,
    [property: JsonPropertyName("matchedSongCount")] int MatchedSongCount,
    [property: JsonPropertyName("playlistId")] string? PlaylistId)
{
    [JsonPropertyName("playlistLibraryId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull),
     Description("The media server's id for the playlist, which /Playlists routes take.")]
    public string? PlaylistLibraryId => V2Ids.LibrarySource(PlaylistId, V2IdKind.Playlist);
}
