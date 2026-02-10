using allstarr.Models.Settings;
using allstarr.Models.Spotify;
using allstarr.Services.Common;
using Microsoft.Extensions.Options;
using System.Text.Json;
using Cronos;

namespace allstarr.Services.Spotify;

/// <summary>
/// Background service that fetches playlist tracks directly from Spotify's API.
/// 
/// This replaces the Jellyfin Spotify Import plugin dependency with key advantages:
/// - Track ordering is preserved (critical for playlists like Release Radar)
/// - ISRC codes available for exact matching
/// - Real-time data without waiting for plugin sync schedules
/// - Full track metadata (duration, release date, etc.)
/// 
/// CRON SCHEDULING: Playlists are fetched based on their cron schedules, not a global interval.
/// Cache persists until next cron run to prevent excess Spotify API calls.
/// </summary>
public class SpotifyPlaylistFetcher : BackgroundService
{
    private readonly ILogger<SpotifyPlaylistFetcher> _logger;
    private readonly SpotifyApiSettings _spotifyApiSettings;
    private readonly SpotifyImportSettings _spotifyImportSettings;
    private readonly SpotifyApiClient _spotifyClient;
    private readonly RedisCacheService _cache;
    
    private const string CacheDirectory = "/app/cache/spotify";
    private const string CacheKeyPrefix = "spotify:playlist:";
    
    // Track Spotify playlist IDs after discovery
    private readonly Dictionary<string, string> _playlistNameToSpotifyId = new();
    
    public SpotifyPlaylistFetcher(
        ILogger<SpotifyPlaylistFetcher> logger,
        IOptions<SpotifyApiSettings> spotifyApiSettings,
        IOptions<SpotifyImportSettings> spotifyImportSettings,
        SpotifyApiClient spotifyClient,
        RedisCacheService cache)
    {
        _logger = logger;
        _spotifyApiSettings = spotifyApiSettings.Value;
        _spotifyImportSettings = spotifyImportSettings.Value;
        _spotifyClient = spotifyClient;
        _cache = cache;
    }
    
    /// <summary>
    /// Gets the Spotify playlist tracks in order, using cache if available.
    /// Cache persists until next cron run to prevent excess API calls.
    /// </summary>
    /// <param name="playlistName">Playlist name (e.g., "Release Radar", "Discover Weekly")</param>
    /// <returns>List of tracks in playlist order, or empty list if not found</returns>
    public async Task<List<SpotifyPlaylistTrack>> GetPlaylistTracksAsync(string playlistName)
    {
        var cacheKey = $"{CacheKeyPrefix}{playlistName}";
        
        // Try Redis cache first
        var cached = await _cache.GetAsync<SpotifyPlaylist>(cacheKey);
        if (cached != null && cached.Tracks.Count > 0)
        {
            var age = DateTime.UtcNow - cached.FetchedAt;
            
            // Calculate if cache should still be valid based on cron schedule
            var playlistConfig = _spotifyImportSettings.GetPlaylistByName(playlistName);
            var shouldRefresh = false;
            
            if (playlistConfig != null && !string.IsNullOrEmpty(playlistConfig.SyncSchedule))
            {
                try
                {
                    var cron = CronExpression.Parse(playlistConfig.SyncSchedule);
                    var nextRun = cron.GetNextOccurrence(cached.FetchedAt, TimeZoneInfo.Utc);
                    
                    if (nextRun.HasValue && DateTime.UtcNow >= nextRun.Value)
                    {
                        shouldRefresh = true;
                        _logger.LogInformation("Cache expired for '{Name}' - next cron run was at {NextRun} UTC", 
                            playlistName, nextRun.Value);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not parse cron schedule for '{Name}', falling back to cache duration", playlistName);
                    shouldRefresh = age.TotalMinutes >= _spotifyApiSettings.CacheDurationMinutes;
                }
            }
            else
            {
                // No cron schedule, use cache duration from settings
                shouldRefresh = age.TotalMinutes >= _spotifyApiSettings.CacheDurationMinutes;
            }
            
            if (!shouldRefresh)
            {
                _logger.LogDebug("Using cached playlist '{Name}' ({Count} tracks, age: {Age:F1}m)", 
                    playlistName, cached.Tracks.Count, age.TotalMinutes);
                return cached.Tracks;
            }
        }
        
        // Try file cache
        var filePath = GetCacheFilePath(playlistName);
        if (File.Exists(filePath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(filePath);
                var filePlaylist = JsonSerializer.Deserialize<SpotifyPlaylist>(json);
                if (filePlaylist != null && filePlaylist.Tracks.Count > 0)
                {
                    var age = DateTime.UtcNow - filePlaylist.FetchedAt;
                    if (age.TotalMinutes < _spotifyApiSettings.CacheDurationMinutes)
                    {
                        _logger.LogDebug("Using file-cached playlist '{Name}' ({Count} tracks)", 
                            playlistName, filePlaylist.Tracks.Count);
                        return filePlaylist.Tracks;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read file cache for '{Name}'", playlistName);
            }
        }
        
        // Need to fetch fresh - try to use cached or configured Spotify playlist ID
        if (!_playlistNameToSpotifyId.TryGetValue(playlistName, out var spotifyId))
        {
            // Check if we have a configured Spotify ID for this playlist
            var config = _spotifyImportSettings.GetPlaylistByName(playlistName);
            if (config != null && !string.IsNullOrEmpty(config.Id))
            {
                // Use the configured Spotify playlist ID directly
                spotifyId = config.Id;
                _playlistNameToSpotifyId[playlistName] = spotifyId;
                _logger.LogInformation("Using configured Spotify playlist ID for '{Name}': {Id}", playlistName, spotifyId);
            }
            else
            {
                // No configured ID, try searching by name (works for public/followed playlists)
                _logger.LogDebug("No configured Spotify ID for '{Name}', searching...", playlistName);
                var playlists = await _spotifyClient.SearchUserPlaylistsAsync(playlistName);
                
                var exactMatch = playlists.FirstOrDefault(p => 
                    p.Name.Equals(playlistName, StringComparison.OrdinalIgnoreCase));
                
                if (exactMatch == null)
                {
                    _logger.LogWarning("Could not find Spotify playlist named '{Name}' - try configuring the Spotify playlist ID", playlistName);
                    
                    // Return file cache even if expired, as a fallback
                    if (File.Exists(filePath))
                    {
                        var json = await File.ReadAllTextAsync(filePath);
                        var fallback = JsonSerializer.Deserialize<SpotifyPlaylist>(json);
                        if (fallback != null)
                        {
                            _logger.LogWarning("Using expired file cache as fallback for '{Name}'", playlistName);
                            return fallback.Tracks;
                        }
                    }
                    
                    return new List<SpotifyPlaylistTrack>();
                }
                
                spotifyId = exactMatch.SpotifyId;
                _playlistNameToSpotifyId[playlistName] = spotifyId;
                _logger.LogInformation("Found Spotify playlist '{Name}' with ID: {Id}", playlistName, spotifyId);
            }
        }
        
        // Fetch the full playlist
        var playlist = await _spotifyClient.GetPlaylistAsync(spotifyId);
        if (playlist == null || playlist.Tracks.Count == 0)
        {
            _logger.LogWarning("Failed to fetch playlist '{Name}' from Spotify", playlistName);
            return cached?.Tracks ?? new List<SpotifyPlaylistTrack>();
        }
        
        // Calculate cache expiration based on cron schedule
        var playlistCfg = _spotifyImportSettings.GetPlaylistByName(playlistName);
        var cacheExpiration = TimeSpan.FromMinutes(_spotifyApiSettings.CacheDurationMinutes * 2); // Default
        
        if (playlistCfg != null && !string.IsNullOrEmpty(playlistCfg.SyncSchedule))
        {
            try
            {
                var cron = CronExpression.Parse(playlistCfg.SyncSchedule);
                var nextRun = cron.GetNextOccurrence(DateTime.UtcNow, TimeZoneInfo.Utc);
                
                if (nextRun.HasValue)
                {
                    var timeUntilNextRun = nextRun.Value - DateTime.UtcNow;
                    // Add 5 minutes buffer
                    cacheExpiration = timeUntilNextRun + TimeSpan.FromMinutes(5);
                    
                    _logger.LogInformation("Playlist '{Name}' cache will persist until next cron run: {NextRun} UTC (in {Hours:F1}h)", 
                        playlistName, nextRun.Value, timeUntilNextRun.TotalHours);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not calculate next cron run for '{Name}', using default cache duration", playlistName);
            }
        }
        
        // Update cache with cron-based expiration
        await _cache.SetAsync(cacheKey, playlist, cacheExpiration);
        await SaveToFileCacheAsync(playlistName, playlist);
        
        _logger.LogInformation("Fetched and cached playlist '{Name}' with {Count} tracks (expires in {Hours:F1}h)", 
            playlistName, playlist.Tracks.Count, cacheExpiration.TotalHours);
        
        return playlist.Tracks;
    }
    
    /// <summary>
    /// Gets missing tracks for a playlist (tracks not found in Jellyfin library).
    /// This provides compatibility with the existing SpotifyMissingTracksFetcher interface.
    /// </summary>
    /// <param name="playlistName">Playlist name</param>
    /// <param name="jellyfinTrackIds">Set of Spotify IDs that exist in Jellyfin library</param>
    /// <returns>List of missing tracks with position preserved</returns>
    public async Task<List<SpotifyPlaylistTrack>> GetMissingTracksAsync(
        string playlistName, 
        HashSet<string> jellyfinTrackIds)
    {
        var allTracks = await GetPlaylistTracksAsync(playlistName);
        
        // Filter to only tracks not in Jellyfin, preserving order
        return allTracks
            .Where(t => !jellyfinTrackIds.Contains(t.SpotifyId))
            .ToList();
    }
    
    /// <summary>
    /// Manual trigger to refresh a specific playlist.
    /// </summary>
    public async Task RefreshPlaylistAsync(string playlistName)
    {
        _logger.LogInformation("Manual refresh triggered for playlist '{Name}'", playlistName);
        
        // Clear cache to force refresh
        var cacheKey = $"{CacheKeyPrefix}{playlistName}";
        await _cache.DeleteAsync(cacheKey);
        
        // Re-fetch
        await GetPlaylistTracksAsync(playlistName);
    }
    
    /// <summary>
    /// Manual trigger to refresh all configured playlists.
    /// </summary>
    public async Task TriggerFetchAsync()
    {
        _logger.LogInformation("Manual fetch triggered for all playlists");
        
        foreach (var config in _spotifyImportSettings.Playlists)
        {
            await RefreshPlaylistAsync(config.Name);
        }
    }
    
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("========================================");
        _logger.LogInformation("SpotifyPlaylistFetcher: Starting up...");
        
        // Ensure cache directory exists
        Directory.CreateDirectory(CacheDirectory);
        
        if (!_spotifyApiSettings.Enabled)
        {
            _logger.LogInformation("Spotify API integration is DISABLED");
            _logger.LogInformation("========================================");
            return;
        }
        
        if (string.IsNullOrEmpty(_spotifyApiSettings.SessionCookie))
        {
            _logger.LogWarning("Spotify session cookie not configured - cannot access editorial playlists");
            _logger.LogInformation("========================================");
            return;
        }
        
        // Verify we can get an access token (the most reliable auth check)
        _logger.LogInformation("Attempting Spotify authentication...");
        var token = await _spotifyClient.GetWebAccessTokenAsync(stoppingToken);
        if (string.IsNullOrEmpty(token))
        {
            _logger.LogError("Failed to get Spotify access token - check session cookie");
            _logger.LogInformation("========================================");
            return;
        }
        
        _logger.LogInformation("Spotify API ENABLED");
        _logger.LogInformation("Authenticated via sp_dc session cookie");
        _logger.LogInformation("ISRC matching: {Enabled}", _spotifyApiSettings.PreferIsrcMatching ? "enabled" : "disabled");
        _logger.LogInformation("Configured Playlists: {Count}", _spotifyImportSettings.Playlists.Count);
        
        foreach (var playlist in _spotifyImportSettings.Playlists)
        {
            var schedule = string.IsNullOrEmpty(playlist.SyncSchedule) ? "0 8 * * 1" : playlist.SyncSchedule;
            _logger.LogInformation("  - {Name}: {Schedule}", playlist.Name, schedule);
        }
        
        _logger.LogInformation("========================================");
        
        // Initial fetch of all playlists on startup
        await FetchAllPlaylistsAsync(stoppingToken);
        
        // Cron-based refresh loop - only fetch when cron schedule triggers
        // This prevents excess Spotify API calls
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Check each playlist to see if it needs refreshing based on cron schedule
                var now = DateTime.UtcNow;
                var needsRefresh = new List<string>();
                
                foreach (var config in _spotifyImportSettings.Playlists)
                {
                    var schedule = string.IsNullOrEmpty(config.SyncSchedule) ? "0 8 * * 1" : config.SyncSchedule;
                    
                    try
                    {
                        var cron = CronExpression.Parse(schedule);
                        
                        // Check if we have cached data
                        var cacheKey = $"{CacheKeyPrefix}{config.Name}";
                        var cached = await _cache.GetAsync<SpotifyPlaylist>(cacheKey);
                        
                        if (cached != null)
                        {
                            // Calculate when the next run should be after the last fetch
                            var nextRun = cron.GetNextOccurrence(cached.FetchedAt, TimeZoneInfo.Utc);
                            
                            if (nextRun.HasValue && now >= nextRun.Value)
                            {
                                needsRefresh.Add(config.Name);
                                _logger.LogInformation("Playlist '{Name}' needs refresh - last fetched {Age:F1}h ago, next run was {NextRun}", 
                                    config.Name, (now - cached.FetchedAt).TotalHours, nextRun.Value);
                            }
                        }
                        else
                        {
                            // No cache, fetch it
                            needsRefresh.Add(config.Name);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Invalid cron schedule for playlist {Name}: {Schedule}", config.Name, schedule);
                    }
                }
                
                // Fetch playlists that need refreshing
                if (needsRefresh.Count > 0)
                {
                    _logger.LogInformation("=== CRON TRIGGER: Fetching {Count} playlists ===", needsRefresh.Count);
                    
                    foreach (var playlistName in needsRefresh)
                    {
                        if (stoppingToken.IsCancellationRequested) break;
                        
                        try
                        {
                            await GetPlaylistTracksAsync(playlistName);
                            
                            // Rate limiting between playlists
                            if (playlistName != needsRefresh.Last())
                            {
                                _logger.LogDebug("Waiting 3 seconds before next playlist to avoid rate limits...");
                                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error fetching playlist '{Name}'", playlistName);
                        }
                    }
                    
                    _logger.LogInformation("=== FINISHED FETCHING PLAYLISTS ===");
                }
                
                // Sleep for 1 hour before checking again
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in playlist fetcher loop");
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }
    }
    
    private async Task FetchAllPlaylistsAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("=== FETCHING SPOTIFY PLAYLISTS ===");
        
        foreach (var config in _spotifyImportSettings.Playlists)
        {
            if (cancellationToken.IsCancellationRequested) break;
            
            try
            {
                var tracks = await GetPlaylistTracksAsync(config.Name);
                _logger.LogInformation("  {Name}: {Count} tracks", config.Name, tracks.Count);
                
                // Log sample of track order for debugging
                if (tracks.Count > 0)
                {
                    _logger.LogDebug("    First track: #{Position} {Title} - {Artist}", 
                        tracks[0].Position, tracks[0].Title, tracks[0].PrimaryArtist);
                    
                    if (tracks.Count > 1)
                    {
                        var last = tracks[^1];
                        _logger.LogDebug("    Last track: #{Position} {Title} - {Artist}", 
                            last.Position, last.Title, last.PrimaryArtist);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching playlist '{Name}'", config.Name);
            }
            
            // Rate limiting between playlists - Spotify is VERY aggressive with rate limiting
            // Wait 3 seconds between each playlist to avoid 429 TooManyRequests errors
            if (config != _spotifyImportSettings.Playlists.Last())
            {
                _logger.LogDebug("Waiting 3 seconds before next playlist to avoid rate limits...");
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
            }
        }
        
        _logger.LogInformation("=== FINISHED FETCHING SPOTIFY PLAYLISTS ===");
    }
    
    private string GetCacheFilePath(string playlistName)
    {
        var safeName = string.Join("_", playlistName.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(CacheDirectory, $"{safeName}_spotify.json");
    }
    
    private async Task SaveToFileCacheAsync(string playlistName, SpotifyPlaylist playlist)
    {
        try
        {
            var filePath = GetCacheFilePath(playlistName);
            var json = JsonSerializer.Serialize(playlist, new JsonSerializerOptions 
            { 
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            await File.WriteAllTextAsync(filePath, json);
            _logger.LogDebug("Saved playlist '{Name}' to file cache", playlistName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save file cache for '{Name}'", playlistName);
        }
    }
}
