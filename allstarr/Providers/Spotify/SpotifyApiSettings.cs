namespace allstarr.Models.Settings;

public sealed class SpotifyApiSettings
{
    public bool Enabled { get; set; }
    public string SessionCookie { get; set; } = string.Empty;
    public int CacheDurationMinutes { get; set; } = 60;
    public int RateLimitDelayMs { get; set; } = 100;
    public bool PreferIsrcMatching { get; set; } = true;
    public string? SessionCookieSetDate { get; set; }
    public string LyricsApiUrl { get; set; } = string.Empty;
}
