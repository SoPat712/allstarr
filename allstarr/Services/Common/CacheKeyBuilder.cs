namespace allstarr.Services.Common;

/// <summary>
/// Utility class for building consistent cache keys across the application.
/// Centralizes cache key generation to ensure consistency and prevent typos.
/// </summary>
public static class CacheKeyBuilder
{
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

    public static string BuildSpotifyManualMappingKey(string playlist, string spotifyId)
    {
        return $"spotify:manual-map:{playlist}:{spotifyId}";
    }

    public static string BuildSpotifyExternalMappingKey(string playlist, string spotifyId)
    {
        return $"spotify:external-map:{playlist}:{spotifyId}";
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

    public static string BuildLyricsManualMappingKey(string artist, string title)
    {
        return $"lyrics:manual-map:{artist}:{title}";
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

    /// <summary>
    /// Builds a cache key for external album/song/artist cover art images.
    /// Images are cached as byte[] in Redis with ProxyImagesTTL (default 14 days).
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
        return $"odesli:url-to-spotify:{musicUrl}";
    }

    #endregion
}
