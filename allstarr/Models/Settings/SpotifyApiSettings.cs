namespace allstarr.Models.Settings;

/// <summary>
/// Configuration for direct Spotify API access.
/// This enables fetching playlist data directly from Spotify rather than relying on the Jellyfin plugin.
/// 
/// Benefits over Jellyfin plugin approach:
/// - Track ordering is preserved (critical for playlists like Release Radar)
/// - ISRC codes available for exact matching
/// - Real-time data without waiting for plugin sync
/// - Full track metadata (duration, release date, etc.)
/// </summary>
public class SpotifyApiSettings
{
    /// <summary>
    /// Enable direct Spotify API integration.
    /// When enabled, playlists will be fetched directly from Spotify instead of the Jellyfin plugin.
    /// </summary>
    public bool Enabled { get; set; }
    
    /// <summary>
    /// Spotify session cookie (sp_dc).
    /// Required for accessing editorial/personalized playlists like Release Radar and Discover Weekly.
    /// These playlists are not available via the official API.
    /// 
    /// To get this cookie:
    /// 1. Log into open.spotify.com in your browser
    /// 2. Open DevTools (F12) > Application > Cookies > https://open.spotify.com
    /// 3. Copy the value of the "sp_dc" cookie
    /// 
    /// Note: This cookie expires periodically and will need to be refreshed.
    /// </summary>
    public string SessionCookie { get; set; } = string.Empty;
    
    /// <summary>
    /// Cache duration in minutes for playlist data.
    /// Playlists like Release Radar only update weekly, so caching is beneficial.
    /// Default: 60 minutes
    /// </summary>
    public int CacheDurationMinutes { get; set; } = 60;
    
    /// <summary>
    /// Rate limit delay between Spotify API requests in milliseconds.
    /// Default: 100ms (Spotify allows ~100 requests per minute)
    /// </summary>
    public int RateLimitDelayMs { get; set; } = 100;
    
    /// <summary>
    /// Whether to prefer ISRC matching over fuzzy title/artist matching when ISRC is available.
    /// ISRC provides exact track identification across services.
    /// Default: true
    /// </summary>
    public bool PreferIsrcMatching { get; set; } = true;
    
    /// <summary>
    /// ISO date string of when the session cookie was last set/updated.
    /// Used to track cookie age and warn when it's approaching expiration (~1 year).
    /// </summary>
    public string? SessionCookieSetDate { get; set; }
    
    /// <summary>
    /// URL of the Spotify Lyrics API sidecar service.
    /// Default: http://spotify-lyrics:8080 (docker-compose service name)
    /// This service wraps Spotify's color-lyrics API for easier access.
    /// </summary>
    public string LyricsApiUrl { get; set; } = "http://spotify-lyrics:8080";
}
