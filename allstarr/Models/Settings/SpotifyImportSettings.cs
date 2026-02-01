namespace allstarr.Models.Settings;

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
    /// Comma-separated list of Jellyfin playlist IDs to inject
    /// Example: "4383a46d8bcac3be2ef9385053ea18df,ba50e26c867ec9d57ab2f7bf24cfd6b0"
    /// Get IDs from Jellyfin playlist URLs
    /// </summary>
    public List<string> PlaylistIds { get; set; } = new();
    
    /// <summary>
    /// Comma-separated list of playlist names (must match Spotify Import plugin format)
    /// Example: "Discover_Weekly,Release_Radar"
    /// Must be in same order as PlaylistIds
    /// Plugin replaces spaces with underscores in filenames
    /// </summary>
    public List<string> PlaylistNames { get; set; } = new();
}
