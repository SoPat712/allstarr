using System.Security.Cryptography;
using System.Text;

namespace allstarr.Services.Common;

/// <summary>
/// Utility class for building consistent cache keys across the application.
/// Centralizes cache key generation to ensure consistency and prevent typos.
/// </summary>
public static class CacheKeyBuilder
{
    public static string BuildAdminPlaylistSummaryKey() => "admin:playlists:summary:v6";

    public static string BuildPlaybackMetadataKey(string provider, string itemId) =>
        $"playback:metadata:{Normalize(provider)}:{Normalize(itemId)}";

    public static string BuildJellyfinItemTypeKey(string itemId) =>
        $"jellyfin:item-type:{Normalize(itemId)}";

    public static string BuildPlaybackSignalDedupeKey(
        string signalType,
        string deviceId,
        string itemId)
    {
        var identity = $"{Normalize(signalType)}\u001f{Normalize(deviceId)}\u001f{Normalize(itemId)}";
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return $"playback:signal:dedupe:{Convert.ToHexStringLower(digest)}";
    }

    public static string BuildProviderPlaylistArtworkDescriptorKey(
        string provider,
        string playlistId,
        string? revision) =>
        $"playlist:artwork-descriptor:{Normalize(provider)}:{Normalize(playlistId)}:{Normalize(revision)}";

    public static string BuildMediaAssetDescriptorKey(MediaAssetIdentity identity)
    {
        var scope = string.Join('\u001f',
            identity.TenantId?.ToString("N"),
            identity.UserId?.ToString("N"),
            identity.ProviderAccountId?.ToString("N"),
            Normalize(identity.ProviderId),
            Normalize(identity.ResourceKind),
            identity.ResourceId.Trim(),
            identity.Revision?.Trim(),
            identity.Width,
            identity.Height);
        return $"media:descriptor:v1:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(scope)))}";
    }

    public static string BuildMediaAssetPayloadKey(string sha256) =>
        $"artwork:payload:v1:{sha256}";

    #region Search Keys

    public static string BuildSearchKey(string? searchTerm, string? itemTypes, int? limit, int? startIndex)
    {
        return $"search:{searchTerm?.ToLowerInvariant()}:{itemTypes}:{limit}:{startIndex}";
    }

    public static string BuildSearchKey(
        string? searchTerm,
        string? itemTypes,
        int? limit,
        int? startIndex,
        string? parentId,
        string? sortBy,
        string? sortOrder,
        bool? recursive,
        string? userId,
        string? isFavorite = null)
    {
        var normalizedTerm = Normalize(searchTerm);
        var normalizedItemTypes = Normalize(itemTypes);
        var normalizedParentId = Normalize(parentId);
        var normalizedSortBy = Normalize(sortBy);
        var normalizedSortOrder = Normalize(sortOrder);
        var normalizedUserId = Normalize(userId);
        var normalizedIsFavorite = Normalize(isFavorite);
        var normalizedRecursive = recursive.HasValue ? (recursive.Value ? "true" : "false") : string.Empty;

        return $"search:{normalizedTerm}:{normalizedItemTypes}:{limit}:{startIndex}:{normalizedParentId}:{normalizedSortBy}:{normalizedSortOrder}:{normalizedRecursive}:{normalizedUserId}:{normalizedIsFavorite}";
    }

    public static string BuildSearchPattern() => "search:*";

    private static string Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToLowerInvariant();
    }

    #endregion

    #region Metadata Keys

    public static string BuildAlbumKey(string provider, string externalId)
    {
        return $"{provider}:album:{externalId}";
    }

    public static string BuildArtistKey(string provider, string externalId)
    {
        return $"{provider}:artist:{externalId}";
    }

    #endregion

    #region Lyrics Keys

    public static string BuildLyricsKey(string artist, string title, string? album, int? durationSeconds)
    {
        return $"lyrics:{artist}:{title}:{album}:{durationSeconds}";
    }

    public static string BuildLyricsPlusKey(string artist, string title, string? album, int? durationSeconds)
    {
        return $"lyricsplus:{artist}:{title}:{album}:{durationSeconds}";
    }

    public static string BuildLyricsByIdKey(int id)
    {
        return $"lyrics:id:{id}";
    }

    #endregion

    #region Image Keys

    public static string BuildMediaDescriptorPattern() => "media:descriptor:*";

    public static string BuildMediaPayloadPattern() => "artwork:payload:*";

    #endregion

    #region Genre Keys

    public static string BuildGenreEnrichmentKey(string title, string artist)
    {
        return $"genre:{title}:{artist}";
    }

    public static string BuildGenreEnrichmentKey(string compositeCacheKey)
    {
        return $"genre:{compositeCacheKey}";
    }

    #endregion

    #region MusicBrainz Keys

    public static string BuildMusicBrainzIsrcKey(string isrc)
    {
        return $"musicbrainz:isrc:{isrc}";
    }

    public static string BuildMusicBrainzSearchKey(string title, string artist, int limit)
    {
        return $"musicbrainz:search:{title.ToLowerInvariant()}:{artist.ToLowerInvariant()}:{limit}";
    }

    public static string BuildMusicBrainzMbidKey(string mbid)
    {
        return $"musicbrainz:mbid:{mbid}";
    }

    #endregion

    #region Odesli Keys

    public static string BuildOdesliTidalToSpotifyKey(string tidalTrackId)
    {
        return $"odesli:tidal-to-spotify:{tidalTrackId}";
    }

    public static string BuildOdesliUrlToSpotifyKey(string musicUrl)
    {
        return $"odesli:url-to-spotify:v2:{HashOdesliUrl(musicUrl)}";
    }

    public static string BuildOdesliTranslationKey(string sourceUrl, string targetPlatform)
    {
        return $"odesli:translate:v2:{HashOdesliUrl(sourceUrl)}:{targetPlatform.ToLowerInvariant()}";
    }

    private static string HashOdesliUrl(string value) =>
        Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(value)));

    #endregion
}
