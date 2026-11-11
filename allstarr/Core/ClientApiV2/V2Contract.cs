using System.ComponentModel;
using System.Text.Json.Serialization;

namespace allstarr.Core.ClientApiV2;

public static class V2Contract
{
    public const string GroupName = "allstarr-client-v2";
    public const int MaximumIdsPerRequest = 100;
}

/// <summary>One track shape for every list, queue, history entry and detail in the native client API.</summary>
public sealed record V2Track(
    [property: JsonPropertyName("id"), Description("Stable id. `trk_` plus the recording when the song has one, so it survives keeping, adding to the library and rematching.")] string Id,
    [property: JsonPropertyName("aliases"), Description("Other ids this track answers to, including the ones in the request.")] IReadOnlyList<string> Aliases,
    [property: JsonPropertyName("isExternal")] bool IsExternal,
    [property: JsonPropertyName("libraryItemId"), Description("The media server item when the song is in the library.")] string? LibraryItemId,
    [property: JsonPropertyName("provider")] V2Provider? Provider,
    [property: JsonPropertyName("externalIds"), Description("Known identifiers such as `isrc`, `musicbrainzRecording` and the service's own id under its name.")] IReadOnlyDictionary<string, string> ExternalIds,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("artists")] IReadOnlyList<V2Ref> Artists,
    [property: JsonPropertyName("albumArtists")] IReadOnlyList<V2Ref> AlbumArtists,
    [property: JsonPropertyName("album")] V2Ref? Album,
    [property: JsonPropertyName("disc")] int? Disc,
    [property: JsonPropertyName("track")] int? Track,
    [property: JsonPropertyName("durationMs")] long? DurationMs,
    [property: JsonPropertyName("year")] int? Year,
    [property: JsonPropertyName("explicit")] bool? Explicit,
    [property: JsonPropertyName("artwork")] V2Artwork? Artwork,
    [property: JsonPropertyName("format"), Description("The stored or measured file, not what a stream will deliver.")] V2Format? Format,
    [property: JsonPropertyName("trackGainDb"), Description("ReplayGain track gain in dB.")] double? TrackGainDb,
    [property: JsonPropertyName("albumGainDb")] double? AlbumGainDb,
    [property: JsonPropertyName("saved")] bool Saved,
    [property: JsonPropertyName("playCount")] long? PlayCount,
    [property: JsonPropertyName("lastPlayedAt")] DateTimeOffset? LastPlayedAt,
    [property: JsonPropertyName("keep")] V2Keep Keep,
    [property: JsonPropertyName("match")] V2Match? Match,
    [property: JsonPropertyName("lyrics")] V2LyricsAvailability Lyrics);

/// <summary>A named reference; <c>id</c> is null when the source names something it can't identify.</summary>
public sealed record V2Ref(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("name")] string Name);

public sealed record V2Provider(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("iconUrl")] string? IconUrl,
    [property: JsonPropertyName("glyphUrl")] string? GlyphUrl);

/// <summary>Artwork that the server resizes; append <c>?w=</c> with the pixel width you draw at. The url changes when the picture does.</summary>
public sealed record V2Artwork([property: JsonPropertyName("url")] string Url);

/// <summary>What the stored or best available file is, before any delivery decision.</summary>
public sealed record V2Format(
    [property: JsonPropertyName("container")] string? Container,
    [property: JsonPropertyName("codec")] string? Codec,
    [property: JsonPropertyName("bitrate")] int? Bitrate,
    [property: JsonPropertyName("sampleRate")] int? SampleRate,
    [property: JsonPropertyName("bitDepth")] int? BitDepth,
    [property: JsonPropertyName("channels")] int? Channels,
    [property: JsonPropertyName("isLossless")] bool? IsLossless);

public sealed record V2Keep(
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("progress")] double? Progress);

public sealed record V2Match(
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("confidence")] double? Confidence,
    [property: JsonPropertyName("authority")] string Authority);

public sealed record V2LyricsAvailability(
    [property: JsonPropertyName("available")] bool Available,
    [property: JsonPropertyName("format")] string? Format,
    [property: JsonPropertyName("source")] string? Source);

/// <summary>An album summary returned alongside tracks with <c>include=album</c>.</summary>
public sealed record V2AlbumSummary(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("artists")] IReadOnlyList<V2Ref> Artists,
    [property: JsonPropertyName("year")] int? Year,
    [property: JsonPropertyName("isExternal")] bool IsExternal,
    [property: JsonPropertyName("artwork")] V2Artwork? Artwork);

public sealed record V2TracksResponse(
    [property: JsonPropertyName("tracks")] IReadOnlyList<V2Track> Tracks,
    [property: JsonPropertyName("missing")] IReadOnlyList<string> Missing)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("albums")]
    public IReadOnlyDictionary<string, V2AlbumSummary>? Albums { get; init; }
}

public sealed record V2TrackResponse([property: JsonPropertyName("track")] V2Track Track)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("albums")]
    public IReadOnlyDictionary<string, V2AlbumSummary>? Albums { get; init; }
}
