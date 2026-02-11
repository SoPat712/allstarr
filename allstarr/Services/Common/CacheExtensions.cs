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

    // Convenience methods for getting TTLs
    public static TimeSpan SearchResultsTTL => GetCacheSettings().SearchResultsTTL;
    public static TimeSpan PlaylistImagesTTL => GetCacheSettings().PlaylistImagesTTL;
    public static TimeSpan SpotifyPlaylistItemsTTL => GetCacheSettings().SpotifyPlaylistItemsTTL;
    public static TimeSpan SpotifyMatchedTracksTTL => GetCacheSettings().SpotifyMatchedTracksTTL;
    public static TimeSpan LyricsTTL => GetCacheSettings().LyricsTTL;
    public static TimeSpan GenreTTL => GetCacheSettings().GenreTTL;
    public static TimeSpan MetadataTTL => GetCacheSettings().MetadataTTL;
    public static TimeSpan OdesliLookupTTL => GetCacheSettings().OdesliLookupTTL;
    public static TimeSpan ProxyImagesTTL => GetCacheSettings().ProxyImagesTTL;
}
