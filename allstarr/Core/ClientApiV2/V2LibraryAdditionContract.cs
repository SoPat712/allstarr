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
    [property: JsonPropertyName("requestedItemId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RequestedItemId,
    [property: JsonPropertyName("aliases"), Description("The older ids of the requested item and the added track, as the media-server routes and v1 spell them, so an app holding those can find its addition.")] IReadOnlyList<string> Aliases)
{
    [JsonPropertyName("title"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull),
     Description("The song's name, or the album's when unit is `album`, so an app that did not start the addition can name it.")]
    public string? Title { get; init; }

    [JsonPropertyName("artist"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Artist { get; init; }

    [JsonPropertyName("step"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull),
     Description("What is happening now: `fetching`, `checking`, `lyrics`, `tagging`, `loudness`, `placing` or `scanning`. `scanning` goes with state `done` until the media server shows the item and `libraryItemId` appears.")]
    public string? Step { get; init; }

    [JsonPropertyName("heldAt"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull),
     Description("With state `needsLook`: the step the addition stopped at, in the names of `step`. Absent when it stopped on an error.")]
    public string? HeldAt { get; init; }

    [JsonPropertyName("stepsDone"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull),
     Description("Steps finished out of `stepsTotal`, for a progress bar. An album counts every song's steps.")]
    public int? StepsDone { get; init; }

    [JsonPropertyName("stepsTotal"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? StepsTotal { get; init; }

    [JsonPropertyName("itemsDone"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull),
     Description("For an addition of several songs: how many are in the library, out of `itemsTotal`.")]
    public int? ItemsDone { get; init; }

    [JsonPropertyName("itemsTotal"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ItemsTotal { get; init; }
}
