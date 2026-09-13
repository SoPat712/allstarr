namespace allstarr.Models.Settings;

public class CacheSettings
{
    public int SearchResultsMinutes { get; set; } = 1;
    public int PlaylistImagesHours { get; set; } = 168;
    public int LyricsDays { get; set; } = 14;
    public int GenreDays { get; set; } = 30;
    public int MetadataDays { get; set; } = 7;
    public int OdesliLookupDays { get; set; } = 60;
    public int ProxyImagesDays { get; set; } = 14;
    public int TranscodeCacheMinutes { get; set; } = 60;

    public string MediaDirectory { get; set; } = "/app/cache/media";
    public int MediaMaximumMegabytes { get; set; } = 512;
    public int MediaMaximumEntryMegabytes { get; set; } = 16;
    public int MediaCleanupFileLimit { get; set; } = 10_000;
    public int MediaCleanupMinutes { get; set; } = 15;
    public Dictionary<string, int> CategoryMaximumEntries { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> CategoryMaximumMegabytes { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, bool> CategoryEnabled { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public TimeSpan SearchResultsTTL => TimeSpan.FromMinutes(Math.Clamp(SearchResultsMinutes, 1, 1440));
    public TimeSpan PlaylistImagesTTL => TimeSpan.FromHours(Math.Clamp(PlaylistImagesHours, 1, 8760));
    public TimeSpan LyricsTTL => TimeSpan.FromDays(Math.Clamp(LyricsDays, 1, 3650));
    public TimeSpan GenreTTL => TimeSpan.FromDays(Math.Clamp(GenreDays, 1, 3650));
    public TimeSpan MetadataTTL => TimeSpan.FromDays(Math.Clamp(MetadataDays, 1, 3650));
    public TimeSpan OdesliLookupTTL => TimeSpan.FromDays(Math.Clamp(OdesliLookupDays, 1, 3650));
    public TimeSpan ProxyImagesTTL => TimeSpan.FromDays(Math.Clamp(ProxyImagesDays, 1, 3650));
    public TimeSpan TranscodeCacheTTL => TimeSpan.FromMinutes(Math.Clamp(TranscodeCacheMinutes, 1, 10080));
}
