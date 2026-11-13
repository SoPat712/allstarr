using System.Text.Json.Serialization;

namespace allstarr.Core.ClientApiV2;

public sealed record V2PlaybackStatus(
    [property: JsonPropertyName("playbackId")] string PlaybackId,
    [property: JsonPropertyName("delivered")] V2PlaybackFormat? Delivered,
    [property: JsonPropertyName("serving")] V2ServingFacts? Serving);
