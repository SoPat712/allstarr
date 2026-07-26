using allstarr.Models.Settings;
using allstarr.Models.Spotify;
using allstarr.Services.Common;
using Microsoft.Extensions.Options;
using Cronos;

namespace allstarr.Services.Spotify;

/// <summary>
/// Background service that fetches playlist tracks directly from Spotify on each playlist's cron schedule.
/// </summary>
public class SpotifyPlaylistFetcher : BackgroundService
{
    private readonly ILogger<SpotifyPlaylistFetcher> _logger;
    private readonly SpotifyApiSettings _spotifyApiSettings;
    private readonly SpotifyImportSettings _spotifyImportSettings;
    private readonly SpotifyApiClient _spotifyClient;
    private readonly SpotifyApiClientFactory _spotifyClientFactory;
    private readonly SpotifySessionCookieService _spotifySessionCookieService;
    private readonly IApplicationCache _cache;

    // Track Spotify playlist IDs after discovery
    private readonly Dictionary<string, string> _playlistNameToSpotifyId = new();

    public SpotifyPlaylistFetcher(
        ILogger<SpotifyPlaylistFetcher> logger,
        IOptions<SpotifyApiSettings> spotifyApiSettings,
        IOptions<SpotifyImportSettings> spotifyImportSettings,
        SpotifyApiClient spotifyClient,
        SpotifyApiClientFactory spotifyClientFactory,
        SpotifySessionCookieService spotifySessionCookieService,
        IApplicationCache cache)
    {
        _logger = logger;
        _spotifyApiSettings = spotifyApiSettings.Value;
        _spotifyImportSettings = spotifyImportSettings.Value;
        _spotifyClient = spotifyClient;
        _spotifyClientFactory = spotifyClientFactory;
        _spotifySessionCookieService = spotifySessionCookieService;
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
        var cacheKey = CacheKeyBuilder.BuildSpotifyPlaylistKey(playlistName);
        var playlistConfig = _spotifyImportSettings.GetPlaylistByName(playlistName);

        // Try the shared application cache first.
        var cached = await _cache.GetAsync<SpotifyPlaylist>(cacheKey);
        if (cached != null && cached.Tracks.Count > 0)
        {
            var age = DateTime.UtcNow - cached.FetchedAt;

            // Calculate if cache should still be valid based on cron schedule
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
                        _logger.LogWarning("Cache expired for '{Name}' - next cron run was at {NextRun} UTC",
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

        // Cache miss or expired - need to fetch fresh from Spotify
        var sessionCookie = await _spotifySessionCookieService.ResolveSessionCookieAsync(playlistConfig?.UserId);
        if (string.IsNullOrWhiteSpace(sessionCookie))
        {
            _logger.LogWarning("No Spotify session cookie configured for playlist '{Name}' (user scope: {UserId})",
                playlistName, playlistConfig?.UserId ?? "(global)");
            return cached?.Tracks ?? new List<SpotifyPlaylistTrack>();
        }

        SpotifyApiClient spotifyClient = _spotifyClient;
        SpotifyApiClient? scopedSpotifyClient = null;

        if (!string.Equals(sessionCookie, _spotifyApiSettings.SessionCookie, StringComparison.Ordinal))
        {
            scopedSpotifyClient = _spotifyClientFactory.Create(sessionCookie);
            spotifyClient = scopedSpotifyClient;
        }

        try
        {
            // Try to use cached or configured Spotify playlist ID
            if (!_playlistNameToSpotifyId.TryGetValue(playlistName, out var spotifyId))
            {
                // Check if we have a configured Spotify ID for this playlist
                if (playlistConfig != null && !string.IsNullOrEmpty(playlistConfig.Id))
                {
                    // Use the configured Spotify playlist ID directly
                    spotifyId = playlistConfig.Id;
                    _playlistNameToSpotifyId[playlistName] = spotifyId;
                    _logger.LogInformation("Using configured Spotify playlist ID for '{Name}': {Id}", playlistName, spotifyId);
                }
                else
                {
                    // No configured ID, try searching by name (works for public/followed playlists)
                    _logger.LogInformation("No configured Spotify ID for '{Name}', searching...", playlistName);
                    var playlists = await spotifyClient.SearchUserPlaylistsAsync(playlistName);

                    var exactMatch = playlists.FirstOrDefault(p =>
                        p.Name.Equals(playlistName, StringComparison.OrdinalIgnoreCase));

                    if (exactMatch == null)
                    {
                        _logger.LogInformation("Could not find Spotify playlist named '{Name}' - try configuring the Spotify playlist ID", playlistName);
                        return cached?.Tracks ?? new List<SpotifyPlaylistTrack>();
                    }

                    spotifyId = exactMatch.SpotifyId;
                    _playlistNameToSpotifyId[playlistName] = spotifyId;
                    _logger.LogInformation("Found Spotify playlist '{Name}' with ID: {Id}", playlistName, spotifyId);
                }
            }

            // Fetch the full playlist
            var playlist = await spotifyClient.GetPlaylistAsync(spotifyId);
            if (playlist == null || playlist.Tracks.Count == 0)
            {
                _logger.LogError("Failed to fetch playlist '{Name}' from Spotify", playlistName);
                return cached?.Tracks ?? new List<SpotifyPlaylistTrack>();
            }

            // Calculate cache expiration based on cron schedule
            var playlistCfg = playlistConfig;
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
                    _logger.LogError(ex, "Could not calculate next cron run for '{Name}', using default cache duration", playlistName);
                }
            }

            // Update the shared cache with cron-based expiration.
            var cacheWriteSucceeded = await _cache.SetAsync(cacheKey, playlist, cacheExpiration);

            if (cacheWriteSucceeded)
            {
                _logger.LogInformation("Fetched and cached playlist '{Name}' with {Count} tracks (expires in {Hours:F1}h)",
                    playlistName, playlist.Tracks.Count, cacheExpiration.TotalHours);
            }
            else
            {
                _logger.LogWarning(
                    "Fetched playlist '{Name}' with {Count} tracks, but shared cache write failed (intended expiry: {Hours:F1}h)",
                    playlistName,
                    playlist.Tracks.Count,
                    cacheExpiration.TotalHours);
            }

            return playlist.Tracks;
        }
        finally
        {
            scopedSpotifyClient?.Dispose();
        }
    }

    /// <summary>
    /// Returns the cached Spotify playlist metadata, including Spotify's playlist-level cover.
    /// Track artwork is deliberately not substituted here so callers can distinguish a real
    /// playlist image from an album-cover fallback.
    /// </summary>
    public Task<SpotifyPlaylist?> GetPlaylistMetadataAsync(string playlistName) =>
        _cache.GetAsync<SpotifyPlaylist>(CacheKeyBuilder.BuildSpotifyPlaylistKey(playlistName));

    /// <summary>
    /// Gets source tracks not present in the supplied local-library identity set.
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
        var cacheKey = CacheKeyBuilder.BuildSpotifyPlaylistKey(playlistName);
        await _cache.DeleteAsync(cacheKey);

        // Re-fetch
        await GetPlaylistTracksAsync(playlistName);
        await ClearPlaylistImageCacheAsync(playlistName);
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

    private async Task ClearPlaylistImageCacheAsync(string playlistName)
    {
        var playlistConfig = _spotifyImportSettings.GetPlaylistByName(playlistName);
        if (playlistConfig == null || string.IsNullOrWhiteSpace(playlistConfig.JellyfinId))
        {
            return;
        }

        var deletedCount = await _cache.DeleteByPatternAsync(
            CacheKeyBuilder.BuildJellyfinImagePattern(playlistConfig.JellyfinId));
        _logger.LogDebug("Cleared {Count} cached local image entries for playlist {Playlist}",
            deletedCount,
            playlistName);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("========================================");
        _logger.LogInformation("SpotifyPlaylistFetcher: Starting up...");

        if (!_spotifyApiSettings.Enabled)
        {
            _logger.LogInformation("Spotify API integration is DISABLED");
            _logger.LogInformation("========================================");
            return;
        }

        if (!await _spotifySessionCookieService.HasAnyConfiguredCookieAsync())
        {
            _logger.LogError("Spotify session cookie not configured (global or user-scoped) - cannot access editorial playlists");
            _logger.LogInformation("========================================");
            return;
        }

        // Validate global fallback cookie if configured; user-scoped cookies are validated per playlist fetch.
        if (!string.IsNullOrWhiteSpace(_spotifyApiSettings.SessionCookie))
        {
            _logger.LogDebug("Attempting Spotify authentication using global fallback cookie...");
            var token = await _spotifyClient.GetWebAccessTokenAsync(stoppingToken);
            if (string.IsNullOrEmpty(token))
            {
                _logger.LogWarning("Global fallback Spotify cookie failed validation. User-scoped cookies may still succeed.");
            }
        }

        _logger.LogInformation("Spotify API ENABLED");
        _logger.LogInformation("Session cookie mode: {Mode}",
            string.IsNullOrWhiteSpace(_spotifyApiSettings.SessionCookie)
                ? "user-scoped only"
                : "global fallback + optional user-scoped overrides");
        _logger.LogInformation("ISRC matching: {Enabled}", _spotifyApiSettings.PreferIsrcMatching ? "enabled" : "disabled");
        _logger.LogInformation("Configured Playlists: {Count}", _spotifyImportSettings.Playlists.Count);

        foreach (var playlist in _spotifyImportSettings.Playlists)
        {
            var schedule = string.IsNullOrEmpty(playlist.SyncSchedule) ? "0 8 * * *" : playlist.SyncSchedule;
            _logger.LogInformation("  - {Name}: {Schedule}", playlist.Name, schedule);
        }

        _logger.LogInformation("========================================");

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
                    var schedule = string.IsNullOrEmpty(config.SyncSchedule) ? "0 8 * * *" : config.SyncSchedule;

                    try
                    {
                        var cron = CronExpression.Parse(schedule);

                        // Check if we have cached data
                        var cacheKey = CacheKeyBuilder.BuildSpotifyPlaylistKey(config.Name);
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
                                _logger.LogWarning("Finished fetching '{Name}' - waiting 3 seconds before next playlist to avoid rate limits...", playlistName);
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
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
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
                _logger.LogDebug("  {Name}: {Count} tracks", config.Name, tracks.Count);

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
                _logger.LogWarning("Finished fetching '{Name}' - waiting 3 seconds before next playlist to avoid rate limits...", config.Name);
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
            }
        }

        _logger.LogInformation("=== FINISHED FETCHING SPOTIFY PLAYLISTS ===");
    }
}
