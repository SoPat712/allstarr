using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace allstarr.Core.ClientApi;

public sealed record ItemDetailsRequest
{
    [Required, MinLength(1), MaxLength(100), JsonPropertyName("ids")]
    public required string[] Ids { get; init; }
}

public sealed record ItemDetailsResponse(
    [property: JsonPropertyName("items")] IReadOnlyList<ItemDetails> Items,
    [property: JsonPropertyName("missing")] IReadOnlyList<string> Missing);

public sealed record ItemDetails(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("isExternal")] bool IsExternal,
    [property: JsonPropertyName("canonicalRecordingId")] string? CanonicalRecordingId,
    [property: JsonPropertyName("isrc")] string? Isrc,
    [property: JsonPropertyName("provider")] ItemDetailsProvider? Provider,
    [property: JsonPropertyName("audio")] ItemDetailsAudio? Audio,
    [property: JsonPropertyName("explicit")] bool? Explicit,
    [property: JsonPropertyName("saved")] bool Saved,
    [property: JsonPropertyName("keep")] ItemDetailsKeep Keep,
    [property: JsonPropertyName("match")] ItemDetailsMatch? Match,
    [property: JsonPropertyName("lyrics")] ItemDetailsLyrics Lyrics)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public long? PlayCount { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public DateTimeOffset? LastPlayedAt { get; init; }
}

public sealed record ItemDetailsProvider(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("iconUrl")] string? IconUrl,
    [property: JsonPropertyName("glyphUrl")] string? GlyphUrl = null);

public sealed record ItemDetailsAudio(
    [property: JsonPropertyName("codec")] string? Codec,
    [property: JsonPropertyName("bitDepth")] int? BitDepth,
    [property: JsonPropertyName("sampleRate")] int? SampleRate,
    [property: JsonPropertyName("bitrate")] int? Bitrate);

public sealed record ItemDetailsKeep(
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("progress")] double? Progress);

public sealed record ItemDetailsMatch(
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("confidence")] double? Confidence,
    [property: JsonPropertyName("authority")] string Authority);

public sealed record ItemDetailsLyrics(
    [property: JsonPropertyName("available")] bool Available,
    [property: JsonPropertyName("format")] string? Format,
    [property: JsonPropertyName("source")] string? Source);
