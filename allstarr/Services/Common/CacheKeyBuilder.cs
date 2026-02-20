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
    
    public static string BuildSpotifyMatchedTracksKey(string playlistName)
    {
        return $"spotify:matched:ordered:{playlistName}";
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
    
    #endregion
    
    #region Playlist Keys
    
    public static string BuildPlaylistImageKey(string playlistId)
    {
        return $"playlist:image:{playlistId}";
    }
    
    #endregion
    
    #region Genre Keys
    
    public static string BuildGenreKey(string genre)
    {
        return $"genre:{genre.ToLowerInvariant()}";
    }
    
    #endregion
}
