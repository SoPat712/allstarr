using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace allstarr.Core.ClientApiV2;

public sealed record V2AddLibraryItem
{
    [JsonPropertyName("itemId"), Required, StringLength(400), Description("A track id, or an album id when unit is `album`.")]
    public required string ItemId { get; init; }

    [JsonPropertyName("unit"), Required, AllowedValues("song", "album")]
    public required string Unit { get; init; }
}

public sealed record V2LibraryAddition(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("itemId")] string ItemId,
    [property: JsonPropertyName("unit")] string Unit,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("libraryItemId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LibraryItemId,
    [property: JsonPropertyName("requestedItemId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RequestedItemId);
