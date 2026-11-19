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

    [JsonPropertyName("alsoOn"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull),
     Description("Song and album hits that stand for several proven copies list each, this hit's own first, one per source. Absent otherwise; artists keep theirs in artist.alsoOn.")]
    public IReadOnlyList<V2SearchSource>? AlsoOn { get; init; }
}

public sealed record V2SearchArtist(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("isExternal")] bool IsExternal,
    [property: JsonPropertyName("artwork")] V2Artwork? Artwork,
    [property: JsonPropertyName("alsoOn")] IReadOnlyList<V2SearchSource> AlsoOn,
    [property: JsonPropertyName("namesakes")] IReadOnlyList<V2Ref> Namesakes)
{
    [JsonPropertyName("libraryId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), Description("The media server artist when it is in the library.")]
    public string? LibraryId => V2Ids.LibrarySource(Id, V2IdKind.Artist);

    [JsonPropertyName("aliases"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), Description("The older service id of an artist that is not in the library, which the media-server routes use.")]
    public IReadOnlyList<string>? Aliases => V2Ids.ServiceSource(Id, V2IdKind.Artist) is { } source ? [source] : null;
}

/// <summary>One copy of a song, album or artist a hit stands for.</summary>
public sealed record V2SearchSource(
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("sourceName")] string SourceName,
    [property: JsonPropertyName("id")] string Id)
{
    [JsonPropertyName("libraryId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), Description("The media server item when this copy is the library's.")]
    public string? LibraryId => V2Ids.LibrarySource(Id);

    [JsonPropertyName("aliases"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), Description("The older service id of a service's copy, which the media-server routes use.")]
    public IReadOnlyList<string>? Aliases => V2Ids.ServiceSource(Id) is { } source ? [source] : null;
}

public sealed record V2SearchPlaylist(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("curator")] string? Curator,
    [property: JsonPropertyName("trackCount")] int? TrackCount,
    [property: JsonPropertyName("isExternal")] bool IsExternal,
    [property: JsonPropertyName("artwork")] V2Artwork? Artwork)
{
    [JsonPropertyName("libraryId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), Description("The media server playlist when it is the listener's own.")]
    public string? LibraryId => V2Ids.LibrarySource(Id, V2IdKind.Playlist);

    [JsonPropertyName("aliases"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), Description("The older service id of a service's playlist, which the media-server routes use.")]
    public IReadOnlyList<string>? Aliases => V2Ids.ServiceSource(Id, V2IdKind.Playlist) is { } source ? [source] : null;
}

public sealed record V2LyricsSearchResponse(
    [property: JsonPropertyName("results")] IReadOnlyList<V2LyricsSearchHit> Results,
    [property: JsonPropertyName("truncated")] bool Truncated);

/// <summary>Songs that lyrics services found for a lyric line; each is playable when Allstarr could match it.</summary>
public sealed record V2ServiceLyricsSearchResponse(
    [property: JsonPropertyName("results")] IReadOnlyList<V2ServiceLyricsHit> Results,
    [property: JsonPropertyName("providers"), Description("Each service asked: state ok, unavailable (failed or too slow) or off (turned off in Sources).")]
    IReadOnlyList<V2ServiceLyricsProvider> Providers)
{
    [JsonPropertyName("resolved"), Description("False when asked with resolve=false: every track is null and the same request without it returns them.")]
    public bool Resolved { get; init; } = true;
}

public sealed record V2ServiceLyricsHit(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("artist")] string Artist,
    [property: JsonPropertyName("line")] V2ServiceLyricsLine Line,
    [property: JsonPropertyName("highlights"), Description("Matched text as UTF-16 code-unit offsets within line.text, as in /search/lyrics.")]
    IReadOnlyList<LyricsSearchHighlight> Highlights,
    [property: JsonPropertyName("provider")] V2Provider Provider,
    [property: JsonPropertyName("track"), Description("The playable song, preferring a library copy, or null when none was found; then only title and artist are known.")]
    V2Track? Track)
{
    [JsonPropertyName("matchState"), Description("matched (track set), none (looked up, no playable song) or pending (not looked up yet, or cut off by the time limit; ask again).")]
    public string MatchState { get; init; } = "pending";
}

public sealed record V2ServiceLyricsLine([property: JsonPropertyName("text")] string Text);

public sealed record V2ServiceLyricsProvider(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("state")] string State);

public sealed record V2LyricsSearchHit(
    [property: JsonPropertyName("track")] V2Track Track,
    [property: JsonPropertyName("line")] LyricsSearchLine Line,
    [property: JsonPropertyName("highlights")] IReadOnlyList<LyricsSearchHighlight> Highlights,
    [property: JsonPropertyName("score")] double Score,
    [property: JsonPropertyName("source")] RichLyricsSource Source);
