using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace allstarr.Core.ClientApiV2;

public sealed record V2PlaybackRequest
{
    [Required, StringLength(1024), JsonPropertyName("trackId")]
    public required string TrackId { get; init; }

    [Required, RegularExpression("lossless|high|normal|low|original"), JsonPropertyName("tier")]
    public required string Tier { get; init; }

    [JsonPropertyName("client")]
    public V2PlaybackClient? Client { get; init; }

    [RegularExpression("play|prefetch|download|cast"), JsonPropertyName("purpose")]
    public string? Purpose { get; init; }
}

public sealed record V2PlaybackClient
{
    [Required, MaxLength(16), JsonPropertyName("codecs")]
    public required string[] Codecs { get; init; }
    [Range(8000, 384000), JsonPropertyName("maxSampleRate")]
    public int? MaxSampleRate { get; init; }
    [Range(8, 32), JsonPropertyName("maxBitDepth")]
    public int? MaxBitDepth { get; init; }
    [Range(1, 8), JsonPropertyName("channels")]
    public int? Channels { get; init; }
}

public sealed record V2PlaybackResponse(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("playbackId")] string PlaybackId,
    [property: JsonPropertyName("cacheKey")] string CacheKey,
    [property: JsonPropertyName("source")] V2PlaybackFormat Source,
    [property: JsonPropertyName("delivered")] V2PlaybackFormat Delivered,
    [property: JsonPropertyName("transcoded")] bool Transcoded,
    [property: JsonPropertyName("contentLength")] long? ContentLength,
    [property: JsonPropertyName("seek")] string Seek,
    [property: JsonPropertyName("trackGainDb")] double? TrackGainDb,
    [property: JsonPropertyName("albumGainDb")] double? AlbumGainDb,
    [property: JsonPropertyName("provider")] V2PlaybackProvider Provider,
    [property: JsonPropertyName("routeReason")] string RouteReason,
    [property: JsonPropertyName("loweredBy")] string? LoweredBy)
{
    [JsonPropertyName("serving")]
    public V2ServingFacts? Serving { get; init; }
}

/// <summary>The caller's active stream as delivered; null when no stream of this track is active.</summary>
public sealed record V2ServingFacts(
    [property: JsonPropertyName("codec")] string? Codec,
    [property: JsonPropertyName("bitrate")] int? Bitrate,
    [property: JsonPropertyName("bitDepth")] int? BitDepth,
    [property: JsonPropertyName("sampleRate")] int? SampleRate,
    [property: JsonPropertyName("source")] string Source);

public sealed record V2PlaybackProvider(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name);

public sealed record V2PlaybackFormat(
    [property: JsonPropertyName("container")] string Container,
    [property: JsonPropertyName("codec")] string Codec,
    [property: JsonPropertyName("bitrate")] int? Bitrate,
    [property: JsonPropertyName("sampleRate")] int? SampleRate,
    [property: JsonPropertyName("bitDepth")] int? BitDepth,
    [property: JsonPropertyName("isLossless")] bool IsLossless);
