using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using allstarr.Core.Music;

namespace allstarr.Core.ClientApi;

public sealed record ClientLyricsSource(
    [property: JsonPropertyName("sourceId")] string SourceId,
    [property: JsonPropertyName("format")] string? Format,
    [property: JsonPropertyName("preview")] string? Preview,
    [property: JsonPropertyName("providerId")] string ProviderId,
    [property: JsonPropertyName("providerName")] string ProviderName,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("artist")] string? Artist,
    [property: JsonPropertyName("album")] string? Album,
    [property: JsonPropertyName("durationMs"), JsonNumberHandling(JsonNumberHandling.Strict)] long? DurationMs)
{
    [JsonPropertyName("state")] public string State { get; init; } = "ready";
}

public sealed record ClientLyricsChosen(
    [property: JsonPropertyName("sourceId")] string SourceId,
    [property: JsonPropertyName("layer")] string Layer);

public sealed record ClientLyricsStamp(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("revision"), JsonNumberHandling(JsonNumberHandling.Strict)] long Revision);

public sealed record ClientLyricsRevision(
    [property: JsonPropertyName("personal")] ClientLyricsStamp? Personal = null,
    [property: JsonPropertyName("household")] ClientLyricsStamp? Household = null)
{
    public LyricsChoiceRevision ToExpected() => new(
        Personal == null ? null : new(Personal.Id, Personal.Revision),
        Household == null ? null : new(Household.Id, Household.Revision));

    public static ClientLyricsRevision From(LyricsChoiceRevision revision) => new(
        revision.Personal == null ? null : new(revision.Personal.Id, revision.Personal.Revision),
        revision.Household == null ? null : new(revision.Household.Id, revision.Household.Revision));
}

public sealed record ClientLyricsSources(
    [property: JsonPropertyName("sources")] IReadOnlyList<ClientLyricsSource> Sources,
    [property: JsonPropertyName("chosen")] ClientLyricsChosen? Chosen,
    [property: JsonPropertyName("revision")] ClientLyricsRevision Revision);

public sealed record ClientLyricsSelection(
    [property: JsonPropertyName("chosen")] ClientLyricsChosen? Chosen,
    [property: JsonPropertyName("revision")] ClientLyricsRevision Revision);

public sealed record SetClientLyricsSourceRequest
{
    [Required, JsonPropertyName("sourceId")]
    public required string SourceId { get; init; }

    [Required, JsonPropertyName("expectedRevision")]
    public required ClientLyricsRevision ExpectedRevision { get; init; }
}

public sealed record ClearClientLyricsSourceRequest
{
    [Required, JsonPropertyName("expectedRevision")]
    public required ClientLyricsRevision ExpectedRevision { get; init; }
}
