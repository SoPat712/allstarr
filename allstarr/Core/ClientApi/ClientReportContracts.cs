using System.Text.Json.Serialization;

namespace allstarr.Core.ClientApi;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateClientReportRequest(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("note")] string Note,
    [property: JsonPropertyName("itemId")] string? ItemId = null,
    [property: JsonPropertyName("playbackRequestId")] Guid? PlaybackRequestId = null);

public sealed record ClientReportReceipt([property: JsonPropertyName("id")] Guid Id);
