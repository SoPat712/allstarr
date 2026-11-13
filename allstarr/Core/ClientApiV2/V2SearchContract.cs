using System.ComponentModel;
using System.Text.Json.Serialization;
using allstarr.Core.Capabilities;
using allstarr.Core.ClientApi;

namespace allstarr.Core.ClientApiV2;

[Description("A complete ranked search snapshot. Each revision replaces the previous snapshot; late appears only on the final revision.")]
public sealed record V2SearchSnapshot(
    [property: JsonPropertyName("revision")] int Revision,
    [property: JsonPropertyName("done")] bool Done,
    [property: JsonPropertyName("providers")] IReadOnlyList<string> Providers,
    [property: JsonPropertyName("top")] V2SearchHit? Top,
    [property: JsonPropertyName("items")] IReadOnlyList<V2SearchHit> Items,
    [property: JsonPropertyName("sections")] IReadOnlyDictionary<string, V2SearchHit[]> Sections,
    [property: JsonPropertyName("late"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Late);

[Description("One ranked result. Kind selects exactly one of track, album, artist or playlist; id is that resource's v2 id.")]
public sealed record V2SearchHit(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("score")] double Score,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("sourceName")] string SourceName)
{
    [JsonPropertyName("track"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public V2Track? Track { get; init; }
    [JsonPropertyName("album"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public V2AlbumSummary? Album { get; init; }
    [JsonPropertyName("artist"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public V2SearchArtist? Artist { get; init; }
    [JsonPropertyName("playlist"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public V2SearchPlaylist? Playlist { get; init; }
}

public sealed record V2SearchArtist(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("isExternal")] bool IsExternal,
    [property: JsonPropertyName("artwork")] V2Artwork? Artwork,
    [property: JsonPropertyName("alsoOn")] IReadOnlyList<V2SearchSource> AlsoOn,
    [property: JsonPropertyName("namesakes")] IReadOnlyList<V2Ref> Namesakes);

public sealed record V2SearchSource(
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("sourceName")] string SourceName,
    [property: JsonPropertyName("id")] string Id);

public sealed record V2SearchPlaylist(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("curator")] string? Curator,
    [property: JsonPropertyName("trackCount")] int? TrackCount,
    [property: JsonPropertyName("isExternal")] bool IsExternal,
    [property: JsonPropertyName("artwork")] V2Artwork? Artwork);

public sealed record V2LyricsSearchResponse(
    [property: JsonPropertyName("results")] IReadOnlyList<V2LyricsSearchHit> Results,
    [property: JsonPropertyName("truncated")] bool Truncated);

public sealed record V2LyricsSearchHit(
    [property: JsonPropertyName("track")] V2Track Track,
    [property: JsonPropertyName("line")] LyricsSearchLine Line,
    [property: JsonPropertyName("highlights")] IReadOnlyList<LyricsSearchHighlight> Highlights,
    [property: JsonPropertyName("score")] double Score,
    [property: JsonPropertyName("source")] RichLyricsSource Source);
