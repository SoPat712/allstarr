using System.Text.Json;
using allstarr.Models.Domain;

namespace allstarr.Services.Common;

/// <summary>
/// Background service that warms up Redis cache from file system on startup.
/// Ensures fast access to cached data after container restarts.
/// </summary>
public class CacheWarmingService : IHostedService
{
    private readonly RedisCacheService _cache;
    private readonly ILogger<CacheWarmingService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private const string GenreCacheDirectory = "/app/cache/genres";
    private const string PlaylistCacheDirectory = "/app/cache/spotify";
    private const string MappingsCacheDirectory = "/app/cache/mappings";
    private const string LyricsCacheDirectory = "/app/cache/lyrics";

    public CacheWarmingService(
        RedisCacheService cache,
        IServiceProvider serviceProvider,
        ILogger<CacheWarmingService> logger)
    {
        _cache = cache;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("🔥 Starting cache warming from file system...");

        var startTime = DateTime.UtcNow;
        var genresWarmed = 0;
        var playlistsWarmed = 0;
        var mappingsWarmed = 0;
        var lyricsWarmed = 0;
        var lyricsMappingsWarmed = 0;

        try
        {
            // Warm genre cache
            genresWarmed = await WarmGenreCacheAsync(cancellationToken);

            // Warm playlist cache
            playlistsWarmed = await WarmPlaylistCacheAsync(cancellationToken);
            
            // Warm manual mappings cache
            mappingsWarmed = await WarmManualMappingsCacheAsync(cancellationToken);
            
            // Warm lyrics mappings cache
            lyricsMappingsWarmed = await WarmLyricsMappingsCacheAsync(cancellationToken);
            
            // Warm lyrics cache
            lyricsWarmed = await WarmLyricsCacheAsync(cancellationToken);

            var duration = DateTime.UtcNow - startTime;
            _logger.LogInformation(
                "✅ Cache warming complete in {Duration:F1}s: {Genres} genres, {Playlists} playlists, {Mappings} manual mappings, {LyricsMappings} lyrics mappings, {Lyrics} lyrics",
                duration.TotalSeconds, genresWarmed, playlistsWarmed, mappingsWarmed, lyricsMappingsWarmed, lyricsWarmed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to warm cache from file system");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Warms genre cache from file system.
    /// </summary>
    private async Task<int> WarmGenreCacheAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(GenreCacheDirectory))
        {
            return 0;
        }

        var files = Directory.GetFiles(GenreCacheDirectory, "*.json");
        var warmedCount = 0;

        foreach (var file in files)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            try
            {
                // Check if cache is expired (30 days)
                var fileInfo = new FileInfo(file);
                if (DateTime.UtcNow - fileInfo.LastWriteTimeUtc > TimeSpan.FromDays(30))
                {
                    File.Delete(file);
                    continue;
                }

                var json = await File.ReadAllTextAsync(file, cancellationToken);
                var cacheEntry = JsonSerializer.Deserialize<GenreCacheEntry>(json);

                if (cacheEntry != null && !string.IsNullOrEmpty(cacheEntry.CacheKey))
                {
                    var redisKey = $"genre:{cacheEntry.CacheKey}";
                    await _cache.SetAsync(redisKey, cacheEntry.Genre, CacheExtensions.GenreTTL);
                    warmedCount++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to warm genre cache from file: {File}", file);
            }
        }

        if (warmedCount > 0)
        {
            _logger.LogDebug("🔥 Warmed {Count} genre entries from file cache", warmedCount);
        }

        return warmedCount;
    }

    /// <summary>
    /// Warms playlist cache from file system.
    /// </summary>
    private async Task<int> WarmPlaylistCacheAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(PlaylistCacheDirectory))
        {
            return 0;
        }

        var itemsFiles = Directory.GetFiles(PlaylistCacheDirectory, "*_items.json");
        var matchedFiles = Directory.GetFiles(PlaylistCacheDirectory, "*_matched.json");
        var warmedCount = 0;

        // Warm playlist items cache
        foreach (var file in itemsFiles)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            try
            {
                // Check if cache is expired (24 hours)
                var fileInfo = new FileInfo(file);
                if (DateTime.UtcNow - fileInfo.LastWriteTimeUtc > TimeSpan.FromHours(24))
                {
                    continue; // Don't delete, let the normal flow handle it
                }

                var json = await File.ReadAllTextAsync(file, cancellationToken);
                var items = JsonSerializer.Deserialize<List<Dictionary<string, object?>>>(json);

                if (items != null && items.Count > 0)
                {
                    // Extract playlist name from filename
                    var fileName = Path.GetFileNameWithoutExtension(file);
                    var playlistName = fileName.Replace("_items", "");

<<<<<<< HEAD
                    var redisKey = $"spotify:playlist:items:{playlistName}";
                    await _cache.SetAsync(redisKey, items, CacheExtensions.SpotifyPlaylistItemsTTL);
||||||| f68706f
                    var redisKey = $"spotify:playlist:items:{playlistName}";
                    await _cache.SetAsync(redisKey, items, TimeSpan.FromHours(24));
=======
                    var redisKey = CacheKeyBuilder.BuildSpotifyPlaylistItemsKey(playlistName);
                    await _cache.SetAsync(redisKey, items, CacheExtensions.SpotifyPlaylistItemsTTL);
>>>>>>> beta
                    warmedCount++;

                    _logger.LogDebug("🔥 Warmed playlist items cache for {Playlist} ({Count} items)", 
                        playlistName, items.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to warm playlist items cache from file: {File}", file);
            }
        }

        // Warm matched tracks cache
        foreach (var file in matchedFiles)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            try
            {
                // Check if cache is expired (1 hour)
                var fileInfo = new FileInfo(file);
                if (DateTime.UtcNow - fileInfo.LastWriteTimeUtc > TimeSpan.FromHours(1))
                {
                    continue; // Skip expired matched tracks
                }

                var json = await File.ReadAllTextAsync(file, cancellationToken);
                var matchedTracks = JsonSerializer.Deserialize<List<MatchedTrack>>(json);

                if (matchedTracks != null && matchedTracks.Count > 0)
                {
                    // Extract playlist name from filename
                    var fileName = Path.GetFileNameWithoutExtension(file);
                    var playlistName = fileName.Replace("_matched", "");

<<<<<<< HEAD
                    var redisKey = $"spotify:matched:ordered:{playlistName}";
                    await _cache.SetAsync(redisKey, matchedTracks, CacheExtensions.SpotifyMatchedTracksTTL);
||||||| f68706f
                    var redisKey = $"spotify:matched:ordered:{playlistName}";
                    await _cache.SetAsync(redisKey, matchedTracks, TimeSpan.FromHours(1));
=======
                    var redisKey = CacheKeyBuilder.BuildSpotifyMatchedTracksKey(playlistName);
                    await _cache.SetAsync(redisKey, matchedTracks, CacheExtensions.SpotifyMatchedTracksTTL);
>>>>>>> beta
                    warmedCount++;

                    _logger.LogInformation("🔥 Warmed matched tracks cache for {Playlist} ({Count} tracks)", 
                        playlistName, matchedTracks.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to warm matched tracks cache from file: {File}", file);
            }
        }

        if (warmedCount > 0)
        {
            _logger.LogDebug("🔥 Warmed {Count} playlist caches from file system", warmedCount);
        }

        return warmedCount;
    }
    
    /// <summary>
    /// Warms manual mappings cache from file system.
    /// Manual mappings NEVER expire - they are permanent user decisions.
    /// </summary>
    private async Task<int> WarmManualMappingsCacheAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(MappingsCacheDirectory))
        {
            return 0;
        }

        var files = Directory.GetFiles(MappingsCacheDirectory, "*_mappings.json");
        var warmedCount = 0;

        foreach (var file in files)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            try
            {
                var json = await File.ReadAllTextAsync(file, cancellationToken);
                var mappings = JsonSerializer.Deserialize<Dictionary<string, ManualMappingEntry>>(json);

                if (mappings != null && mappings.Count > 0)
                {
                    // Extract playlist name from filename
                    var fileName = Path.GetFileNameWithoutExtension(file);
                    var playlistName = fileName.Replace("_mappings", "");

                    foreach (var mapping in mappings.Values)
                    {
                        if (!string.IsNullOrEmpty(mapping.JellyfinId))
                        {
                            // Jellyfin mapping
                            var redisKey = $"spotify:manual-map:{playlistName}:{mapping.SpotifyId}";
                            await _cache.SetAsync(redisKey, mapping.JellyfinId);
                            warmedCount++;
                        }
                        else if (!string.IsNullOrEmpty(mapping.ExternalProvider) && !string.IsNullOrEmpty(mapping.ExternalId))
                        {
                            // External mapping
                            var redisKey = $"spotify:external-map:{playlistName}:{mapping.SpotifyId}";
                            var externalMapping = new { provider = mapping.ExternalProvider, id = mapping.ExternalId };
                            await _cache.SetAsync(redisKey, externalMapping);
                            warmedCount++;
                        }
                    }

                    _logger.LogDebug("🔥 Warmed {Count} manual mappings for {Playlist}", 
                        mappings.Count, playlistName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to warm manual mappings from file: {File}", file);
            }
        }

        if (warmedCount > 0)
        {
            _logger.LogDebug("🔥 Warmed {Count} manual mappings from file system", warmedCount);
        }

        return warmedCount;
    }
    
    /// <summary>
    /// Warms lyrics mappings cache from file system.
    /// Lyrics mappings NEVER expire - they are permanent user decisions.
    /// </summary>
    private async Task<int> WarmLyricsMappingsCacheAsync(CancellationToken cancellationToken)
    {
        var mappingsFile = "/app/cache/lyrics_mappings.json";
        
        if (!File.Exists(mappingsFile))
        {
            return 0;
        }

        try
        {
            var json = await File.ReadAllTextAsync(mappingsFile, cancellationToken);
            var mappings = JsonSerializer.Deserialize<List<LyricsMappingEntry>>(json);

            if (mappings != null && mappings.Count > 0)
            {
                foreach (var mapping in mappings)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    // Store in Redis with NO EXPIRATION (permanent)
                    var redisKey = $"lyrics:manual-map:{mapping.Artist}:{mapping.Title}";
                    await _cache.SetStringAsync(redisKey, mapping.LyricsId.ToString());
                }

                _logger.LogDebug("🔥 Warmed {Count} lyrics mappings from file system", mappings.Count);
                return mappings.Count;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to warm lyrics mappings from file: {File}", mappingsFile);
        }

        return 0;
    }
    
    /// <summary>
    /// Warms lyrics cache from file system using the LyricsPrefetchService.
    /// </summary>
    private async Task<int> WarmLyricsCacheAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Get the LyricsPrefetchService from DI
            using var scope = _serviceProvider.CreateScope();
            var lyricsPrefetchService = scope.ServiceProvider.GetService<allstarr.Services.Lyrics.LyricsPrefetchService>();
            
            if (lyricsPrefetchService != null)
            {
                await lyricsPrefetchService.WarmCacheFromFilesAsync();
                
                // Count files to return warmed count
                if (Directory.Exists(LyricsCacheDirectory))
                {
                    return Directory.GetFiles(LyricsCacheDirectory, "*.json").Length;
                }
            }
            
            return 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to warm lyrics cache");
            return 0;
        }
    }

    private class GenreCacheEntry
    {
        public string CacheKey { get; set; } = "";
        public string Genre { get; set; } = "";
        public DateTime CachedAt { get; set; }
    }
    
    private class MatchedTrack
    {
        public int Position { get; set; }
        public string SpotifyId { get; set; } = "";
        public string SpotifyTitle { get; set; } = "";
        public string SpotifyArtist { get; set; } = "";
        public string? Isrc { get; set; }
        public string MatchType { get; set; } = "";
        public Song? MatchedSong { get; set; }
    }
    
    private class ManualMappingEntry
    {
        public string SpotifyId { get; set; } = "";
        public string? JellyfinId { get; set; }
        public string? ExternalProvider { get; set; }
        public string? ExternalId { get; set; }
        public DateTime CreatedAt { get; set; }
    }
    
    private class LyricsMappingEntry
    {
        public string Artist { get; set; } = "";
        public string Title { get; set; } = "";
        public string? Album { get; set; }
        public int DurationSeconds { get; set; }
        public int LyricsId { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}
