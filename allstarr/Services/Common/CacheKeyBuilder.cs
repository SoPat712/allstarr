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

    public static string BuildPlaybackArtworkKey(string provider, string itemId) =>
        $"artwork:playback:{Normalize(provider)}:{Normalize(itemId)}";

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

    public static string BuildSongKey(string provider, string externalId)
    {
        return $"{provider}:song:{externalId}";
    }

    #endregion

    #region Spotify Keys

    public static string BuildSpotifyPlaylistKey(string playlistName)
    {
        return $"spotify:playlist:{playlistName}";
    }

    public static string BuildSpotifyPlaylistItemsKey(string playlistName)
    {
        return $"spotify:playlist:items:{playlistName}";
    }

    public static string BuildSpotifyPlaylistOrderedKey(string playlistName)
    {
        return $"spotify:playlist:ordered:{playlistName}";
    }

    public static string BuildSpotifyMatchedTracksKey(string playlistName)
    {
        return $"spotify:matched:ordered:{playlistName}";
    }

    public static string BuildSpotifyLegacyMatchedTracksKey(string playlistName)
    {
        return $"spotify:matched:{playlistName}";
    }

    public static string BuildSpotifyPlaylistStatsKey(string playlistName)
    {
        return $"spotify:playlist:stats:{playlistName}";
    }

    public static string BuildSpotifyPlaylistLastSuccessfulSyncKey(string playlistName)
    {
        return $"spotify:playlist:last-successful-sync:{playlistName}";
    }

    public static string BuildSpotifyPlaylistStatsPattern()
    {
        return "spotify:playlist:stats:*";
    }

    public static string BuildSpotifyMissingTracksKey(string playlistName)
    {
        return $"spotify:missing:{playlistName}";
    }

    public static string BuildSpotifyGlobalMappingKey(string spotifyId)
    {
        return $"spotify:global-map:{spotifyId}";
    }

    public static string BuildSpotifyGlobalMappingsIndexKey()
    {
        return "spotify:global-map:all-ids";
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

    public static string BuildPlaylistImageKey(string playlistId)
    {
        return $"playlist:image:{playlistId}";
    }

    public static string BuildPlaylistTrackContextKey(string trackId)
    {
        return $"playlist:track-context:{trackId}";
    }

    public static string BuildJellyfinImageKey(
        string itemId,
        string imageType,
        int? maxWidth,
        int? maxHeight,
        string? imageTag)
    {
        return $"image:{itemId}:{imageType}:{maxWidth}:{maxHeight}:{imageTag}";
    }

    public static string BuildJellyfinImagePattern(string itemId)
    {
        return $"image:{itemId}:*";
    }

    public static string BuildImagePattern() => "image:*";

    /// <summary>
    /// Builds a cache key for external album/song/artist cover art images.
    /// Images are cached in the bounded disk-backed media tier.
    /// </summary>
    public static string BuildExternalImageKey(string provider, string type, string externalId)
    {
        return $"image:{provider}:{type}:{externalId}";
    }

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

    public static string BuildGenreKey(string genre)
    {
        return $"genre:{genre.ToLowerInvariant()}";
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

    public static string BuildSpotifyPlaylistJellyfinSignatureKey(string playlistName)
    {
        return $"spotify:playlist:jellyfin-signature:{playlistName}";
    }

    private static string HashOdesliUrl(string value) =>
        Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(value)));

    #endregion
}
