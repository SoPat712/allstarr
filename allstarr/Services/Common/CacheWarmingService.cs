using System.Text.Json;
using allstarr.Models.Domain;
using allstarr.Models.Admin;
using allstarr.Models.Spotify;
using allstarr.Services.Spotify;

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
                    var redisKey = CacheKeyBuilder.BuildGenreEnrichmentKey(cacheEntry.CacheKey);
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
                var fileName = Path.GetFileNameWithoutExtension(file);
                var playlistName = fileName.Replace("_items", "");
                var redisKey = CacheKeyBuilder.BuildSpotifyPlaylistItemsKey(playlistName);

                // Check if cache is expired (24 hours)
                var fileInfo = new FileInfo(file);
                if (DateTime.UtcNow - fileInfo.LastWriteTimeUtc > TimeSpan.FromHours(24))
                {
                    await _cache.DeleteAsync(redisKey);
                    continue; // Don't warm stale in-memory data from an earlier process
                }

                var json = await File.ReadAllTextAsync(file, cancellationToken);
                var items = JsonSerializer.Deserialize<List<Dictionary<string, object?>>>(json);

                if (items != null && items.Count > 0)
                {
                    var playableItems = InjectedPlaylistItemHelper.RemoveUnavailableExternalItems(items);
                    var removedCount = items.Count - playableItems.Count;
                    if (removedCount > 0)
                    {
                        _logger.LogWarning(
                            "Removed {Count} unavailable persisted playlist items from {Playlist}",
                            removedCount,
                            playlistName);
                        if (playableItems.Count == 0)
                        {
                            File.Delete(file);
                        }
                        else
                        {
                            await File.WriteAllTextAsync(
                                file,
                                JsonSerializer.Serialize(playableItems, new JsonSerializerOptions { WriteIndented = true }),
                                cancellationToken);
                        }
                    }

                    if (playableItems.Count == 0)
                    {
                        await _cache.DeleteAsync(redisKey);
                        continue;
                    }

                    await _cache.SetAsync(redisKey, playableItems, CacheExtensions.SpotifyPlaylistItemsTTL);
                    warmedCount++;

                    _logger.LogDebug("🔥 Warmed playlist items cache for {Playlist} ({Count} items)",
                        playlistName, playableItems.Count);
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
                var fileName = Path.GetFileNameWithoutExtension(file);
                var playlistName = fileName.Replace("_matched", "");
                var redisKey = CacheKeyBuilder.BuildSpotifyMatchedTracksKey(playlistName);
                var legacyRedisKey = CacheKeyBuilder.BuildSpotifyLegacyMatchedTracksKey(playlistName);

                // Check if cache is expired (1 hour)
                var fileInfo = new FileInfo(file);
                if (DateTime.UtcNow - fileInfo.LastWriteTimeUtc > TimeSpan.FromHours(1))
                {
                    await _cache.DeleteAsync(redisKey);
                    await _cache.DeleteAsync(legacyRedisKey);
                    continue; // Don't retain stale matches from an earlier process
                }

                var json = await File.ReadAllTextAsync(file, cancellationToken);
                var matchedTracks = JsonSerializer.Deserialize<List<MatchedTrack>>(json);

                if (matchedTracks != null && matchedTracks.Count > 0)
                {
                    var playableTracks = matchedTracks
                        .Where(track => ExternalTrackPlaybackPolicy.CanUseForPlayback(track.MatchedSong))
                        .ToList();
                    var removedCount = matchedTracks.Count - playableTracks.Count;
                    if (removedCount > 0)
                    {
                        _logger.LogWarning(
                            "Removed {Count} unavailable persisted matches from {Playlist}",
                            removedCount,
                            playlistName);
                        if (playableTracks.Count == 0)
                        {
                            File.Delete(file);
                        }
                        else
                        {
                            await File.WriteAllTextAsync(
                                file,
                                JsonSerializer.Serialize(playableTracks, new JsonSerializerOptions { WriteIndented = true }),
                                cancellationToken);
                        }
                    }

                    if (playableTracks.Count == 0)
                    {
                        await _cache.DeleteAsync(redisKey);
                        await _cache.DeleteAsync(legacyRedisKey);
                        continue;
                    }

                    // Ordered matches are authoritative. Never leave the legacy key pointing
                    // at a different, potentially unplayable list after a restart.
                    await _cache.DeleteAsync(legacyRedisKey);
                    await _cache.SetAsync(redisKey, playableTracks, CacheExtensions.SpotifyMatchedTracksTTL);
                    warmedCount++;

                    _logger.LogInformation("🔥 Warmed matched tracks cache for {Playlist} ({Count} tracks)",
                        playlistName, playableTracks.Count);
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
        var compatibleMappings = new Dictionary<string, ManualMappingEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            try
            {
                var existingJson = await File.ReadAllTextAsync(file, cancellationToken);
                var existingMappings = JsonSerializer.Deserialize<Dictionary<string, ManualMappingEntry>>(existingJson);
                if (existingMappings == null)
                {
                    continue;
                }

                foreach (var mapping in existingMappings.Values)
                {
                    if (!string.IsNullOrWhiteSpace(mapping.SpotifyId) &&
                        (!string.IsNullOrWhiteSpace(mapping.JellyfinId) ||
                         (!string.IsNullOrWhiteSpace(mapping.ExternalId) &&
                          ExternalTrackPlaybackPolicy.CanUseForPlayback(mapping.ExternalProvider))))
                    {
                        compatibleMappings.TryAdd(mapping.SpotifyId, mapping);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not inspect manual mappings for compatible legacy targets: {File}", file);
            }
        }

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

                    var changed = false;
                    foreach (var pair in mappings.ToList())
                    {
                        var mapping = pair.Value;
                        if (!string.IsNullOrWhiteSpace(mapping.ExternalId) &&
                            !ExternalTrackPlaybackPolicy.CanUseForPlayback(mapping.ExternalProvider))
                        {
                            compatibleMappings.TryGetValue(mapping.SpotifyId, out var compatiblePeer);
                            var canonical = await _cache.GetAsync<SpotifyTrackMapping>(
                                CacheKeyBuilder.BuildSpotifyGlobalMappingKey(mapping.SpotifyId));
                            if (LegacyManualMappingRecovery.TryCreateReplacement(
                                    mapping,
                                    compatiblePeer,
                                    canonical,
                                    out var recovered))
                            {
                                mapping = recovered;
                                mappings[pair.Key] = recovered;
                                compatibleMappings[mapping.SpotifyId] = recovered;
                                changed = true;
                                _logger.LogWarning(
                                    "Recovered legacy manual mapping for Spotify {SpotifyId} in {Playlist} using an exact playable identity",
                                    mapping.SpotifyId,
                                    playlistName);
                            }
                        }

                        if (!string.IsNullOrEmpty(mapping.JellyfinId))
                        {
                            // Jellyfin mapping
                            var redisKey = CacheKeyBuilder.BuildSpotifyManualMappingKey(playlistName, mapping.SpotifyId);
                            await _cache.SetAsync(redisKey, mapping.JellyfinId);
                            warmedCount++;
                        }
                        else if (!string.IsNullOrEmpty(mapping.ExternalProvider) && !string.IsNullOrEmpty(mapping.ExternalId))
                        {
                            if (!ExternalTrackPlaybackPolicy.CanUseForPlayback(mapping.ExternalProvider))
                            {
                                _logger.LogInformation(
                                    "Skipped metadata-only manual mapping for Spotify {SpotifyId} in {Playlist}",
                                    mapping.SpotifyId,
                                    playlistName);
                                continue;
                            }

                            // External mapping
                            var redisKey = CacheKeyBuilder.BuildSpotifyExternalMappingKey(playlistName, mapping.SpotifyId);
                            var externalMapping = new { provider = mapping.ExternalProvider, id = mapping.ExternalId };
                            await _cache.SetAsync(redisKey, externalMapping);
                            warmedCount++;
                        }
                    }

                    if (changed)
                    {
                        await WriteJsonAtomicallyAsync(file, mappings, cancellationToken);
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

    private static async Task WriteJsonAtomicallyAsync<T>(
        string destination,
        T value,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("Mapping cache path has no parent directory.");
        var temporary = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(
                temporary,
                JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
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
                    var redisKey = CacheKeyBuilder.BuildLyricsManualMappingKey(mapping.Artist, mapping.Title);
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
