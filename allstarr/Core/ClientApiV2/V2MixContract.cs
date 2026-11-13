using System.Text.Json.Serialization;
using allstarr.Core.ClientApi;

namespace allstarr.Core.ClientApiV2;

public sealed record V2Mixes([property: JsonPropertyName("mixes")] IReadOnlyList<V2Mix> Mixes);

public sealed record V2Mix(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("state")] ClientMixState State,
    [property: JsonPropertyName("itemCount")] int ItemCount,
    [property: JsonPropertyName("durationMs")] long DurationMs,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset? UpdatedAt,
    [property: JsonPropertyName("coverUrl")] string CoverUrl,
    [property: JsonPropertyName("inApps")] bool InApps,
    [property: JsonPropertyName("playlistId")] string? PlaylistId,
    [property: JsonPropertyName("section")] string Section,
    [property: JsonPropertyName("source")] string? Source,
    [property: JsonPropertyName("sourcePlaylistId")] string? SourcePlaylistId,
    [property: JsonPropertyName("appsUnavailableReason")] string? AppsUnavailableReason);

public sealed record V2MixTracks(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset? UpdatedAt,
    [property: JsonPropertyName("tracks")] IReadOnlyList<V2Track> Tracks,
    [property: JsonPropertyName("missing")] IReadOnlyList<string> Missing);
