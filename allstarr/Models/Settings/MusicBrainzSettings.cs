namespace allstarr.Models.Settings;

public sealed class MusicBrainzSettings
{
    public bool Enabled { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string SourceId { get; set; } = "musicbrainz";
    public string BaseUrl { get; set; } = "https://musicbrainz.org/ws/2";
    public int RateLimitMs { get; set; } = 1000;
    public string? AuthorizedUserAgentOverride { get; set; }
}
