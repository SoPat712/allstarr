namespace allstarr.Models.Settings;

public sealed class MusicBrainzSettings
{
    // Source policy controls availability; accept older binders without retaining a second switch.
    public bool Enabled { get => true; set { } }
    public string SourceId { get; set; } = "musicbrainz";
    public string BaseUrl { get; set; } = "https://musicbrainz.org/ws/2";
    public int RateLimitMs { get; set; } = 1000;
    public string? AuthorizedUserAgentOverride { get; set; }
}
