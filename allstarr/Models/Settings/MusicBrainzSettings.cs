namespace allstarr.Models.Settings;

/// <summary>
/// Settings for MusicBrainz API integration.
/// </summary>
public class MusicBrainzSettings
{
    public bool Enabled { get; set; } = false;
    public string? Username { get; set; }
    public string? Password { get; set; }
    
    /// <summary>
    /// Base URL for MusicBrainz API.
    /// </summary>
    public string BaseUrl { get; set; } = "https://musicbrainz.org/ws/2";
    
    /// <summary>
    /// Rate limit: 1 request per second for unauthenticated, 1 per second for authenticated.
    /// </summary>
    public int RateLimitMs { get; set; } = 1000;
}
