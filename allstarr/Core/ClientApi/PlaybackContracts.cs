using System.Text.Json.Serialization;

namespace allstarr.Core.ClientApi;

[JsonConverter(typeof(JsonStringEnumConverter<PlaybackAccountScope>))]
public enum PlaybackAccountScope
{
    [JsonStringEnumMemberName("personal")] Personal,
    [JsonStringEnumMemberName("shared")] Shared
}

public sealed record ClientPlayback(
    [property: JsonPropertyName("provider")] ItemDetailsProvider? Provider,
    [property: JsonPropertyName("accountScope")] PlaybackAccountScope? AccountScope,
    [property: JsonPropertyName("cached")] bool Cached,
    [property: JsonPropertyName("delivered")] PlaybackDelivered? Delivered,
    [property: JsonPropertyName("routeReason")] string? RouteReason,
    [property: JsonPropertyName("itemId")] string? ItemId,
    [property: JsonPropertyName("startedAt")] DateTimeOffset StartedAt);

public sealed record PlaybackDelivered(
    [property: JsonPropertyName("format")] string? Format,
    [property: JsonPropertyName("codec")] string? Codec,
    [property: JsonPropertyName("bitDepth")] int? BitDepth,
    [property: JsonPropertyName("sampleRate")] int? SampleRate,
    [property: JsonPropertyName("bitrate")] int? Bitrate);
