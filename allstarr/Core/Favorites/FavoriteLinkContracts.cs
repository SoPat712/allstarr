using System.Text.Json.Serialization;

namespace allstarr.Core.Favorites;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FavoriteLinkPair(Guid JellyfinUserId, string JellyfinFingerprint, Guid SubsonicUserId, string SubsonicFingerprint);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateFavoriteLinkRequest(Guid RequestId, Guid JellyfinUserId, string JellyfinFingerprint,
    Guid SubsonicUserId, string SubsonicFingerprint, bool ConfirmedSamePerson, int ConfirmationVersion);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RetryFavoriteLinkRequest(Guid RequestId, long ExpectedRevision);
public sealed record FavoriteLinkMember(
    [property: JsonPropertyName("userId")] Guid UserId, [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("protocol")] string Protocol, [property: JsonPropertyName("instanceLabel")] string InstanceLabel,
    [property: JsonPropertyName("profileCode")] string ProfileCode, [property: JsonPropertyName("identityFingerprint")] string IdentityFingerprint,
    [property: JsonPropertyName("lastSeenAt")] DateTimeOffset? LastSeenAt, [property: JsonPropertyName("participation")] string Participation,
    [property: JsonPropertyName("linkId")] Guid? LinkId);
public sealed record FavoriteLinkUnion(
    [property: JsonPropertyName("jobId")] Guid JobId, [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("processedRows")] long ProcessedRows, [property: JsonPropertyName("seededItems")] long SeededItems,
    [property: JsonPropertyName("completedAt")] DateTimeOffset? CompletedAt, [property: JsonPropertyName("errorCode")] string? ErrorCode,
    [property: JsonPropertyName("canRetry")] bool CanRetry);
public sealed record FavoriteLinkDetail(
    [property: JsonPropertyName("id")] Guid Id, [property: JsonPropertyName("jellyfin")] FavoriteLinkMember Jellyfin,
    [property: JsonPropertyName("subsonic")] FavoriteLinkMember Subsonic, [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt, [property: JsonPropertyName("revision")] long Revision,
    [property: JsonPropertyName("union")] FavoriteLinkUnion Union);
public sealed record FavoriteLinkPage<T>([property: JsonPropertyName("activeProtocol")] string ActiveProtocol,
    [property: JsonPropertyName("items")] IReadOnlyList<T> Items, [property: JsonPropertyName("nextCursor")] string? NextCursor);
public sealed record FavoriteLinkPreview([property: JsonPropertyName("jellyfin")] FavoriteLinkMember Jellyfin,
    [property: JsonPropertyName("subsonic")] FavoriteLinkMember Subsonic, [property: JsonPropertyName("confirmationVersion")] int ConfirmationVersion,
    [property: JsonPropertyName("existingLinkId")] Guid? ExistingLinkId);
public sealed record FavoriteLinkReceipt([property: JsonPropertyName("linkId")] Guid LinkId,
    [property: JsonPropertyName("jobId")] Guid JobId, [property: JsonPropertyName("created")] bool Created,
    [property: JsonPropertyName("replayed")] bool Replayed);
public sealed class FavoriteLinkException(int statusCode, string code, string message) : InvalidOperationException(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
    public static FavoriteLinkException Invalid() => new(400, "favorite_link_invalid", "The shared provider hearts request is invalid.");
    public static FavoriteLinkException Changed() => new(409, "favorite_link_changed", "The shared provider hearts changed. Refresh before trying again.");
}
