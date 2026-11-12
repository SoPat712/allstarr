using System.Text.Json.Serialization;

namespace allstarr.Core.ClientApi;

[JsonConverter(typeof(JsonStringEnumConverter<ClientMixState>))]
public enum ClientMixState
{
    [JsonStringEnumMemberName("ready")] Ready,
    [JsonStringEnumMemberName("empty")] Empty,
    [JsonStringEnumMemberName("building")] Building,
    [JsonStringEnumMemberName("needs-listening")] NeedsListening
}

public sealed record ClientMixes(
    [property: JsonPropertyName("mixes")] IReadOnlyList<ClientMix> Mixes);

public sealed record ClientMix(
    [property: JsonPropertyName("mixId")] Guid MixId,
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
    [property: JsonPropertyName("artworkUrls")] IReadOnlyList<string>? ArtworkUrls = null,
    [property: JsonPropertyName("section")] string Section = "madeForYou",
    [property: JsonPropertyName("source")] string? Source = null,
    [property: JsonPropertyName("sourcePlaylistId")] string? SourcePlaylistId = null,
    [property: JsonPropertyName("appsUnavailableReason")] string? AppsUnavailableReason = null);

public sealed record ClientMixItems(
    [property: JsonPropertyName("mixId")] Guid MixId,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset? UpdatedAt,
    [property: JsonPropertyName("items")] IReadOnlyList<ClientMixItem> Items);

public sealed record ClientMixItem(
    [property: JsonPropertyName("itemId")] string ItemId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("artist")] string Artist,
    [property: JsonPropertyName("album")] string Album,
    [property: JsonPropertyName("albumId")] string? AlbumId,
    [property: JsonPropertyName("artworkUrl")] string? ArtworkUrl,
    [property: JsonPropertyName("artistId")] string? ArtistId,
    [property: JsonPropertyName("durationMs")] long? DurationMs,
    [property: JsonPropertyName("isExternal")] bool IsExternal,
    [property: JsonPropertyName("provider")] ItemDetailsProvider? Provider,
    [property: JsonPropertyName("reason")] string Reason);
