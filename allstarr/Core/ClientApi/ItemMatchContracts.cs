using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace allstarr.Core.ClientApi;

public sealed record ClientMatchCandidate(
    [property: JsonPropertyName("candidateId")] string CandidateId,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("artist")] string? Artist,
    [property: JsonPropertyName("album")] string? Album,
    [property: JsonPropertyName("durationMs")] long? DurationMs,
    [property: JsonPropertyName("isrc")] string? Isrc,
    [property: JsonPropertyName("provider")] ItemDetailsProvider Provider,
    [property: JsonPropertyName("isExternal")] bool IsExternal,
    [property: JsonPropertyName("confidence")] double? Confidence,
    [property: JsonPropertyName("reasons")] IReadOnlyList<string> Reasons);

public sealed record ClientItemMatch(
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("confidence")] double? Confidence,
    [property: JsonPropertyName("authority")] string Authority,
    [property: JsonPropertyName("current")] ClientMatchCandidate? Current,
    [property: JsonPropertyName("candidates")] IReadOnlyList<ClientMatchCandidate> Candidates,
    [property: JsonPropertyName("revision")] string Revision);

public sealed record SetClientMatchRequest
{
    [Required, JsonPropertyName("candidateId")]
    public required string CandidateId { get; init; }
    [Required, JsonPropertyName("expectedRevision")]
    public required string ExpectedRevision { get; init; }
}

public sealed record RejectClientMatchRequest
{
    [Required, JsonPropertyName("expectedRevision")]
    public required string ExpectedRevision { get; init; }
}
