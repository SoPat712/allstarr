using Microsoft.Extensions.Options;
using allstarr.Models.Settings;

namespace allstarr.Services.Common;

/// <summary>
/// Extension methods for cache TTL management.
/// Provides centralized access to configurable cache durations.
/// </summary>
public static class CacheExtensions
{
    private static CacheSettings? _cacheSettings;
    private static readonly object _lock = new();

    /// <summary>
    /// Initialize cache settings (called once at startup).
    /// </summary>
    public static void InitializeCacheSettings(IServiceProvider serviceProvider)
    {
        lock (_lock)
        {
            if (_cacheSettings == null)
            {
                var options = serviceProvider.GetService<IOptions<CacheSettings>>();
                _cacheSettings = options?.Value ?? new CacheSettings();
            }
        }
    }

    /// <summary>
    /// Get the current cache settings.
    /// </summary>
    public static CacheSettings GetCacheSettings()
    {
        if (_cacheSettings == null)
        {
            throw new InvalidOperationException("Cache settings not initialized. Call InitializeCacheSettings first.");
        }
        return _cacheSettings;
    }

    public static ApplicationCacheCategoryPolicy Policy(ApplicationCacheCategory category) =>
        ApplicationCachePolicyRegistry.Resolve(category, GetCacheSettings());

    private static ApplicationCacheCategoryPolicy Policy(
        ApplicationCacheCategory category,
        TimeSpan freshFor) =>
        Policy(category) with { FreshFor = freshFor };

    public static TimeSpan SearchResultsTTL =>
        Policy(ApplicationCacheCategory.SearchResults).FreshFor;
    public static TimeSpan PlaylistImagesTTL =>
        Policy(ApplicationCacheCategory.Artwork, GetCacheSettings().PlaylistImagesTTL).FreshFor;
    public static TimeSpan LyricsTTL =>
        Policy(ApplicationCacheCategory.Lyrics).FreshFor;
    public static TimeSpan GenreTTL =>
        Policy(ApplicationCacheCategory.CanonicalMetadata, GetCacheSettings().GenreTTL).FreshFor;
    public static TimeSpan MetadataTTL =>
        Policy(ApplicationCacheCategory.CanonicalMetadata).FreshFor;
    public static TimeSpan OdesliLookupTTL =>
        Policy(ApplicationCacheCategory.ProviderResponse, GetCacheSettings().OdesliLookupTTL).FreshFor;
    public static TimeSpan ProxyImagesTTL =>
        Policy(ApplicationCacheCategory.Artwork).FreshFor;
    public static TimeSpan TranscodeCacheTTL =>
        Policy(ApplicationCacheCategory.TemporaryAudio).FreshFor;
}
