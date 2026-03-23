namespace allstarr.Models.Settings;

/// <summary>
/// Cache TTL (Time To Live) settings for various data types.
/// All values are configurable via Web UI and require restart to apply.
/// </summary>
public class CacheSettings
{
    /// <summary>
    /// Search results cache duration in minutes.
    /// Default: 1 minute (60 seconds)
    /// </summary>
    public int SearchResultsMinutes { get; set; } = 1;

    /// <summary>
    /// Playlist cover images cache duration in hours.
    /// Default: 168 hours (1 week)
    /// </summary>
    public int PlaylistImagesHours { get; set; } = 168;

    /// <summary>
    /// Spotify playlist items cache duration in hours.
    /// Default: 168 hours (1 week, until next cron job)
    /// </summary>
    public int SpotifyPlaylistItemsHours { get; set; } = 168;

    /// <summary>
    /// Spotify matched tracks cache duration in days.
    /// This is the mapping of Spotify IDs to local/external tracks.
    /// Default: 30 days
    /// </summary>
    public int SpotifyMatchedTracksDays { get; set; } = 30;

    /// <summary>
    /// Lyrics cache duration in days.
    /// Default: 14 days (2 weeks)
    /// </summary>
    public int LyricsDays { get; set; } = 14;

    /// <summary>
    /// Genre data cache duration in days.
    /// Default: 30 days
    /// </summary>
    public int GenreDays { get; set; } = 30;

    /// <summary>
    /// External metadata (SquidWTF albums/artists) cache duration in days.
    /// Default: 7 days
    /// </summary>
    public int MetadataDays { get; set; } = 7;

    /// <summary>
    /// Odesli Spotify ID lookup cache duration in days.
    /// Default: 60 days
    /// </summary>
    public int OdesliLookupDays { get; set; } = 60;

    /// <summary>
    /// Jellyfin proxy images cache duration in days.
    /// Default: 14 days (2 weeks)
    /// </summary>
    public int ProxyImagesDays { get; set; } = 14;

    /// <summary>
    /// Transcoded audio cache duration in minutes.
    /// Quality-override files (downloaded at lower quality for cellular streaming)
    /// are cached in {downloads}/transcoded/ and cleaned up after this duration.
    /// Default: 60 minutes (1 hour)
    /// </summary>
    public int TranscodeCacheMinutes { get; set; } = 60;

    // Helper methods to get TimeSpan values
    public TimeSpan SearchResultsTTL => TimeSpan.FromMinutes(SearchResultsMinutes);
    public TimeSpan PlaylistImagesTTL => TimeSpan.FromHours(PlaylistImagesHours);
    public TimeSpan SpotifyPlaylistItemsTTL => TimeSpan.FromHours(SpotifyPlaylistItemsHours);
    public TimeSpan SpotifyMatchedTracksTTL => TimeSpan.FromDays(SpotifyMatchedTracksDays);
    public TimeSpan LyricsTTL => TimeSpan.FromDays(LyricsDays);
    public TimeSpan GenreTTL => TimeSpan.FromDays(GenreDays);
    public TimeSpan MetadataTTL => TimeSpan.FromDays(MetadataDays);
    public TimeSpan OdesliLookupTTL => TimeSpan.FromDays(OdesliLookupDays);
    public TimeSpan ProxyImagesTTL => TimeSpan.FromDays(ProxyImagesDays);
    public TimeSpan TranscodeCacheTTL => TimeSpan.FromMinutes(TranscodeCacheMinutes);
}
