namespace allstarr.Models.Settings;

/// <summary>
/// Configuration for Spotify playlist injection feature.
/// Requires Jellyfin Spotify Import Plugin: https://github.com/Viperinius/jellyfin-plugin-spotify-import
/// </summary>
public class SpotifyImportSettings
{
    /// <summary>
    /// Enable Spotify playlist injection feature
    /// </summary>
    public bool Enabled { get; set; }
    
    /// <summary>
    /// Jellyfin server URL (for accessing plugin API)
    /// </summary>
    public string JellyfinUrl { get; set; } = string.Empty;
    
    /// <summary>
    /// Jellyfin API key (REQUIRED for accessing missing tracks files)
    /// Get from Jellyfin Dashboard > API Keys
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;
    
    /// <summary>
    /// Hour when Spotify Import plugin runs (24-hour format, 0-23)
    /// Example: 16 for 4:00 PM
    /// </summary>
    public int SyncStartHour { get; set; } = 16;
    
    /// <summary>
    /// Minute when Spotify Import plugin runs (0-59)
    /// Example: 15 for 4:15 PM
    /// </summary>
    public int SyncStartMinute { get; set; } = 15;
    
    /// <summary>
    /// How many hours to search for missing tracks files after sync start time
    /// Example: 2 means search from 4:00 PM to 6:00 PM
    /// </summary>
    public int SyncWindowHours { get; set; } = 2;
    
    /// <summary>
    /// List of playlists to inject
    /// </summary>
    public List<SpotifyPlaylistConfig> Playlists { get; set; } = new();
}

/// <summary>
/// Configuration for a single Spotify playlist
/// </summary>
public class SpotifyPlaylistConfig
{
    /// <summary>
    /// Display name in Jellyfin (e.g., "Release Radar")
    /// </summary>
    public string Name { get; set; } = string.Empty;
    
    /// <summary>
    /// Playlist name in Spotify Import plugin missing tracks file
    /// Must match exactly (e.g., "Release Radar")
    /// </summary>
    public string SpotifyName { get; set; } = string.Empty;
    
    /// <summary>
    /// Enable this playlist
    /// </summary>
    public bool Enabled { get; set; } = true;
}
