using System.Text.Json.Serialization;

namespace allstarr.Core.ClientApiV2;

public sealed record V2SessionResponse(
    [property: JsonPropertyName("listener")] V2Listener Listener,
    [property: JsonPropertyName("serverVersion")] string ServerVersion,
    [property: JsonPropertyName("features")] IReadOnlyDictionary<string, bool> Features,
    [property: JsonPropertyName("dashboard"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] V2Dashboard? Dashboard);

public sealed record V2Listener(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name);

public sealed record V2Dashboard([property: JsonPropertyName("url")] string Url);
