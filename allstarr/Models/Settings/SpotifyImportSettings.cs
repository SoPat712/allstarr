<<<<<<< HEAD
namespace allstarr.Models.Settings;

/// <summary>
/// Where to position local tracks relative to external matched tracks in Spotify playlists.
/// </summary>
public enum LocalTracksPosition
{
    /// <summary>
    /// Local tracks appear first, external tracks appended at the end (default)
    /// </summary>
    First,
    
    /// <summary>
    /// External tracks appear first, local tracks appended at the end
    /// </summary>
    Last
}

/// <summary>
/// Configuration for a single Spotify Import playlist.
/// </summary>
public class SpotifyPlaylistConfig
{
    /// <summary>
    /// Playlist name as it appears in Jellyfin/Spotify Import plugin
    /// Example: "Discover Weekly", "Release Radar"
    /// </summary>
    public string Name { get; set; } = string.Empty;
    
    /// <summary>
    /// Spotify playlist ID (get from Spotify playlist URL)
    /// Example: "37i9dQZF1DXcBWIGoYBM5M" (from open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M)
    /// Required for personalized playlists like Discover Weekly, Release Radar, etc.
    /// </summary>
    public string Id { get; set; } = string.Empty;
    
    /// <summary>
    /// Jellyfin playlist ID (internal Jellyfin GUID)
    /// Example: "4383a46d8bcac3be2ef9385053ea18df"
    /// This is the ID Jellyfin uses when requesting playlist tracks
    /// </summary>
    public string JellyfinId { get; set; } = string.Empty;
    
    /// <summary>
    /// Where to position local tracks: "first" or "last"
    /// </summary>
    public LocalTracksPosition LocalTracksPosition { get; set; } = LocalTracksPosition.First;
}

/// <summary>
/// Configuration for Spotify playlist injection feature.
/// Requires Jellyfin Spotify Import Plugin: https://github.com/Viperinius/jellyfin-plugin-spotify-import
/// Uses JellyfinSettings.Url and JellyfinSettings.ApiKey for API access.
/// </summary>
public class SpotifyImportSettings
{
    /// <summary>
    /// Enable Spotify playlist injection feature
    /// </summary>
    public bool Enabled { get; set; }
    
    /// <summary>
    /// How often to run track matching in hours.
    /// Spotify playlists like Discover Weekly update once per week, Release Radar updates weekly.
    /// Most playlists don't change frequently, so running every 24 hours is reasonable.
    /// Set to 0 to only run once on startup (manual trigger via admin UI still works).
    /// Default: 24 hours
    /// </summary>
    public int MatchingIntervalHours { get; set; } = 24;
    
    /// <summary>
    /// Combined playlist configuration as JSON array.
    /// Format: [["Name","Id","first|last"],...]
    /// Example: [["Discover Weekly","abc123","first"],["Release Radar","def456","last"]]
    /// </summary>
    public List<SpotifyPlaylistConfig> Playlists { get; set; } = new();
    
    /// <summary>
    /// Legacy: Comma-separated list of Jellyfin playlist IDs to inject
    /// Deprecated: Use Playlists instead
    /// </summary>
    [Obsolete("Use Playlists instead")]
    public List<string> PlaylistIds { get; set; } = new();
    
    /// <summary>
    /// Legacy: Comma-separated list of playlist names
    /// Deprecated: Use Playlists instead
    /// </summary>
    [Obsolete("Use Playlists instead")]
    public List<string> PlaylistNames { get; set; } = new();
    
    /// <summary>
    /// Legacy: Comma-separated list of local track positions ("first" or "last")
    /// Deprecated: Use Playlists instead
    /// Example: "first,last,first,first" (one per playlist)
    /// </summary>
    [Obsolete("Use Playlists instead")]
    public List<string> PlaylistLocalTracksPositions { get; set; } = new();
    
    /// <summary>
    /// Gets the playlist configuration by Jellyfin playlist ID.
    /// </summary>
    public SpotifyPlaylistConfig? GetPlaylistById(string playlistId) =>
        Playlists.FirstOrDefault(p => p.Id.Equals(playlistId, StringComparison.OrdinalIgnoreCase));
    
    /// <summary>
    /// Gets the playlist configuration by Jellyfin playlist ID.
    /// </summary>
    public SpotifyPlaylistConfig? GetPlaylistByJellyfinId(string jellyfinPlaylistId) =>
        Playlists.FirstOrDefault(p => p.JellyfinId.Equals(jellyfinPlaylistId, StringComparison.OrdinalIgnoreCase));
    
    /// <summary>
    /// Gets the playlist configuration by name.
    /// </summary>
    public SpotifyPlaylistConfig? GetPlaylistByName(string name) =>
        Playlists.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    
    /// <summary>
    /// Checks if a Jellyfin playlist ID is configured for Spotify import.
    /// </summary>
    public bool IsSpotifyPlaylist(string jellyfinPlaylistId) =>
        Playlists.Any(p => p.JellyfinId.Equals(jellyfinPlaylistId, StringComparison.OrdinalIgnoreCase));
}
||||||| bc4e5d9
=======
namespace allstarr.Models.Settings;

/// <summary>
/// Where to position local tracks relative to external matched tracks in Spotify playlists.
/// </summary>
public enum LocalTracksPosition
{
    /// <summary>
    /// Local tracks appear first, external tracks appended at the end (default)
    /// </summary>
    First,
    
    /// <summary>
    /// External tracks appear first, local tracks appended at the end
    /// </summary>
    Last
}

/// <summary>
/// Configuration for a single Spotify Import playlist.
/// </summary>
public class SpotifyPlaylistConfig
{
    /// <summary>
    /// Playlist name as it appears in Jellyfin/Spotify Import plugin
    /// Example: "Discover Weekly", "Release Radar"
    /// </summary>
    public string Name { get; set; } = string.Empty;
    
    /// <summary>
    /// Spotify playlist ID (get from Spotify playlist URL)
    /// Example: "37i9dQZF1DXcBWIGoYBM5M" (from open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M)
    /// Required for personalized playlists like Discover Weekly, Release Radar, etc.
    /// </summary>
    public string Id { get; set; } = string.Empty;
    
    /// <summary>
    /// Jellyfin playlist ID (internal Jellyfin GUID)
    /// Example: "4383a46d8bcac3be2ef9385053ea18df"
    /// This is the ID Jellyfin uses when requesting playlist tracks
    /// </summary>
    public string JellyfinId { get; set; } = string.Empty;
    
    /// <summary>
    /// Where to position local tracks: "first" or "last"
    /// </summary>
    public LocalTracksPosition LocalTracksPosition { get; set; } = LocalTracksPosition.First;
    
    /// <summary>
    /// Cron schedule for syncing this playlist with Spotify
    /// Format: minute hour day month dayofweek
    /// Example: "0 8 * * 1" = 8 AM every Monday
    /// Default: "0 8 * * 1" (weekly on Monday at 8 AM)
    /// </summary>
    public string SyncSchedule { get; set; } = "0 8 * * 1";
}

/// <summary>
/// Configuration for Spotify playlist injection feature.
/// Requires Jellyfin Spotify Import Plugin: https://github.com/Viperinius/jellyfin-plugin-spotify-import
/// Uses JellyfinSettings.Url and JellyfinSettings.ApiKey for API access.
/// </summary>
public class SpotifyImportSettings
{
    /// <summary>
    /// Enable Spotify playlist injection feature
    /// </summary>
    public bool Enabled { get; set; }
    
    /// <summary>
    /// How often to run track matching in hours.
    /// Spotify playlists like Discover Weekly update once per week, Release Radar updates weekly.
    /// Most playlists don't change frequently, so running every 24 hours is reasonable.
    /// Set to 0 to only run once on startup (manual trigger via admin UI still works).
    /// Default: 24 hours
    /// </summary>
    public int MatchingIntervalHours { get; set; } = 24;
    
    /// <summary>
    /// Combined playlist configuration as JSON array.
    /// Format: [["Name","Id","first|last"],...]
    /// Example: [["Discover Weekly","abc123","first"],["Release Radar","def456","last"]]
    /// </summary>
    public List<SpotifyPlaylistConfig> Playlists { get; set; } = new();
    
    /// <summary>
    /// Legacy: Comma-separated list of Jellyfin playlist IDs to inject
    /// Deprecated: Use Playlists instead
    /// </summary>
    [Obsolete("Use Playlists instead")]
    public List<string> PlaylistIds { get; set; } = new();
    
    /// <summary>
    /// Legacy: Comma-separated list of playlist names
    /// Deprecated: Use Playlists instead
    /// </summary>
    [Obsolete("Use Playlists instead")]
    public List<string> PlaylistNames { get; set; } = new();
    
    /// <summary>
    /// Legacy: Comma-separated list of local track positions ("first" or "last")
    /// Deprecated: Use Playlists instead
    /// Example: "first,last,first,first" (one per playlist)
    /// </summary>
    [Obsolete("Use Playlists instead")]
    public List<string> PlaylistLocalTracksPositions { get; set; } = new();
    
    /// <summary>
    /// Gets the playlist configuration by Jellyfin playlist ID.
    /// </summary>
    public SpotifyPlaylistConfig? GetPlaylistById(string playlistId) =>
        Playlists.FirstOrDefault(p => p.Id.Equals(playlistId, StringComparison.OrdinalIgnoreCase));
    
    /// <summary>
    /// Gets the playlist configuration by Jellyfin playlist ID.
    /// </summary>
    public SpotifyPlaylistConfig? GetPlaylistByJellyfinId(string jellyfinPlaylistId) =>
        Playlists.FirstOrDefault(p => p.JellyfinId.Equals(jellyfinPlaylistId, StringComparison.OrdinalIgnoreCase));
    
    /// <summary>
    /// Gets the playlist configuration by name.
    /// </summary>
    public SpotifyPlaylistConfig? GetPlaylistByName(string name) =>
        Playlists.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    
    /// <summary>
    /// Checks if a Jellyfin playlist ID is configured for Spotify import.
    /// </summary>
    public bool IsSpotifyPlaylist(string jellyfinPlaylistId) =>
        Playlists.Any(p => p.JellyfinId.Equals(jellyfinPlaylistId, StringComparison.OrdinalIgnoreCase));
}
>>>>>>> dev
