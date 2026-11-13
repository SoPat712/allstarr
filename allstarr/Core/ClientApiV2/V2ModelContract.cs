using System.Text.Json.Serialization;

namespace allstarr.Core.ClientApiV2;

public sealed record V2KaraokeModel(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("sizeBytes")] long SizeBytes,
    [property: JsonPropertyName("computeUnits")] string ComputeUnits,
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("license")] string License);
