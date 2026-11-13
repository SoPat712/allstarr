using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using allstarr.Core.ClientApi;

namespace allstarr.Core.ClientApiV2;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record V2CreateReport
{
    [JsonPropertyName("kind"), Required, Description("`wrong-song`, `wrong-lyrics`, `wont-play`, `bad-quality`, `wrong-info` or `other`.")]
    public required string Kind { get; init; }

    [JsonPropertyName("note"), Required, StringLength(2000, MinimumLength = 1)]
    public required string Note { get; init; }

    [JsonPropertyName("itemId"), StringLength(400), Description("Optional track id in any accepted spelling.")]
    public string? ItemId { get; init; }

    [JsonPropertyName("playbackRequestId"), JsonConverter(typeof(ClientReportPlaybackRequestIdJsonConverter))]
    public Guid? PlaybackRequestId { get; init; }
}

public sealed record V2ReportReceipt([property: JsonPropertyName("id")] Guid Id);
