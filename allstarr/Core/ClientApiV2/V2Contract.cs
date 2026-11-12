using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using allstarr.Core.Capabilities;
using allstarr.Core.ClientApi;

namespace allstarr.Core.ClientApiV2;

public static class V2Contract
{
    public const string GroupName = "allstarr-client-v2";
    public const int MaximumIdsPerRequest = 100;
    public const int MaximumBatchRequests = 20;
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
    [property: JsonPropertyName("featuring"), Description("Track artists after the first who are not album artists.")] IReadOnlyList<V2Ref> Featuring,
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
    [property: JsonPropertyName("lyrics")] V2LyricsAvailability Lyrics)
{
    internal string? SourceVersion { get; init; }
}

/// <summary>A named reference; <c>id</c> is null when the source names something it can't identify.</summary>
public sealed record V2Ref(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("name")] string Name);

public sealed record V2Provider(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("iconUrl")] string? IconUrl,
    [property: JsonPropertyName("glyphUrl")] string? GlyphUrl);

/// <summary>
/// Artwork that the server resizes; append <c>?w=</c> with the pixel width you draw at. The url changes when the picture does.
/// It works without a token, like the media server's own image urls, so lock screens, cast receivers and widgets can load it.
/// </summary>
public sealed record V2Artwork([property: JsonPropertyName("url")] string Url);

/// <summary>
/// The library file for library songs, or the service's file as last measured for other songs; null until it has been
/// measured. It never describes a stream after conversion.
/// </summary>
public sealed record V2Format(
    [property: JsonPropertyName("container")] string? Container,
    [property: JsonPropertyName("codec")] string? Codec,
    [property: JsonPropertyName("bitrate"), Description("Bits per second.")] int? Bitrate,
    [property: JsonPropertyName("sampleRate"), Description("Hz.")] int? SampleRate,
    [property: JsonPropertyName("bitDepth"), Description("Bits per sample; null for lossy codecs.")] int? BitDepth,
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

public sealed record V2LyricsTranslationResponse(
    [property: JsonPropertyName("trackId")] string TrackId,
    [property: JsonPropertyName("to")] string To,
    [property: JsonPropertyName("provider"), AllowedValues("google", "mymemory", "libretranslate", null)] string? Provider,
    [property: JsonPropertyName("lines"), Description("One entry per lyrics view line, in the same order, including blank and untranslated lines.")] IReadOnlyList<V2LyricsTranslationLine> Lines);

public sealed record V2LyricsTranslationLine(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("text"), Description("Null when no translation is needed or the result was dropped.")] string? Text,
    [property: JsonPropertyName("from")] string? From);

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

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("views")]
    public V2TrackViews? Views { get; init; }

    /// <summary>For each requested view: <c>ok</c>, <c>none</c> when empty, <c>partial</c> when autoplay needs more time, or <c>unavailable</c> to retry later.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("viewStatus")]
    public IReadOnlyDictionary<string, string>? ViewStatus { get; init; }
}

/// <summary>Server-computed facts about one track. Only requested views that have data are present.</summary>
public sealed record V2TrackViews
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("playback")]
    public V2PlaybackResponse? Playback { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("lyrics")]
    public RichLyrics? Lyrics { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("lyricsSources")]
    public ClientLyricsSources? LyricsSources { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("match")]
    public ClientItemMatch? Match { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("autoplay")]
    public V2AutoplayResponse? Autoplay { get; init; }
}

public sealed record V2AutoplayResponse(
    [property: JsonPropertyName("tracks")] IReadOnlyList<V2Track> Tracks,
    [property: JsonPropertyName("next"), Description("Opaque continuation cursor; null when exhausted. A partial page can have no tracks and still have a next cursor.")] string? Next);

public sealed record V2BatchRequest
{
    [Required, MinLength(1), MaxLength(V2Contract.MaximumBatchRequests), JsonPropertyName("requests")]
    public required V2BatchItem[] Requests { get; init; }
}

public sealed record V2BatchItem
{
    [Required, StringLength(64, MinimumLength = 1), JsonPropertyName("id")]
    public required string Id { get; init; }

    [Required, StringLength(8192, MinimumLength = 1), JsonPropertyName("path"), Description("A v2 GET path with its query, such as `/allstarr/v2/tracks/trk_…?views=lyrics`.")]
    public required string Path { get; init; }
}

public sealed record V2BatchResponse(
    [property: JsonPropertyName("responses")] IReadOnlyDictionary<string, V2BatchResult> Responses);

/// <summary>One sub-request's answer: the body that route returns on its own, or its <c>{code,error}</c>.</summary>
public sealed record V2BatchResult(
    [property: JsonPropertyName("status")] int Status,
    [property: JsonPropertyName("body")] object Body);
