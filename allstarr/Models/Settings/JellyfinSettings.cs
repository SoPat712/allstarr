namespace allstarr.Models.Settings;

/// <summary>
/// Configuration for Jellyfin media server backend
/// </summary>
public class JellyfinSettings
{
    /// <summary>
    /// URL of the Jellyfin server
    /// Environment variable: JELLYFIN_URL
    /// </summary>
    public string? Url { get; set; }
    
    /// <summary>
    /// API key for authenticating with Jellyfin server
    /// Environment variable: JELLYFIN_API_KEY
    /// </summary>
    public string? ApiKey { get; set; }
    
    /// <summary>
    /// User ID for accessing Jellyfin library
    /// Environment variable: JELLYFIN_USER_ID
    /// </summary>
    public string? UserId { get; set; }
    
    /// <summary>
    /// Username that clients must provide to authenticate
    /// Environment variable: JELLYFIN_CLIENT_USERNAME
    /// </summary>
    public string? ClientUsername { get; set; }
    
    /// <summary>
    /// Music library ID in Jellyfin (optional, auto-detected if not specified)
    /// Environment variable: JELLYFIN_LIBRARY_ID
    /// </summary>
    public string? LibraryId { get; set; }
    
    /// <summary>
    /// Client name reported to Jellyfin
    /// </summary>
    public string ClientName { get; set; } = "Allstarr";
    
    /// <summary>
    /// Client version reported to Jellyfin
    /// </summary>
    public string ClientVersion { get; set; } = "1.0.0";
    
    /// <summary>
    /// Device ID reported to Jellyfin
    /// </summary>
    public string DeviceId { get; set; } = "allstarrrr-proxy";
    
    /// <summary>
    /// Device name reported to Jellyfin
    /// </summary>
    public string DeviceName { get; set; } = "Allstarr Proxy";
    
    // Shared settings (same as SubsonicSettings)
    
    public ExplicitFilter ExplicitFilter { get; set; } = ExplicitFilter.All;
    public DownloadMode DownloadMode { get; set; } = DownloadMode.Track;
    public MusicService MusicService { get; set; } = MusicService.SquidWTF;
    public StorageMode StorageMode { get; set; } = StorageMode.Permanent;
    public int CacheDurationHours { get; set; } = 1;
    public bool EnableExternalPlaylists { get; set; } = true;
    public string PlaylistsDirectory { get; set; } = "playlists";
}
