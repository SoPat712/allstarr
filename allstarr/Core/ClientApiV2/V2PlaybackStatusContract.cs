using System.Text.Json.Serialization;

namespace allstarr.Core.ClientApiV2;

public sealed record V2PlaybackStatus(
    [property: JsonPropertyName("playbackId")] string PlaybackId,
    [property: JsonPropertyName("delivered")] V2PlaybackFormat? Delivered,
    [property: JsonPropertyName("serving")] V2ServingFacts? Serving)
{
    [JsonPropertyName("provider"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull),
     System.ComponentModel.Description("The service that served a media-server stream; set when read by its X-Allstarr-Playback-Request id.")]
    public V2Provider? Provider { get; init; }

    [JsonPropertyName("cached"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Cached { get; init; }

    [JsonPropertyName("routeReason"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RouteReason { get; init; }

    [JsonPropertyName("trackId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull),
     System.ComponentModel.Description("The id the stream was requested by.")]
    public string? TrackId { get; init; }

    [JsonPropertyName("startedAt"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? StartedAt { get; init; }
}
