using allstarr.Models.Domain;
using allstarr.Models.Settings;
using allstarr.Models.Spotify;
using allstarr.Services.Common;
using allstarr.Services.Jellyfin;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using System.Text.Json;
using Cronos;

namespace allstarr.Services.Spotify;

/// <summary>
/// Background service that pre-matches Spotify tracks with external providers.
///
/// Supports two modes:
/// 1. Legacy mode: Uses MissingTrack from Jellyfin plugin (no ISRC, no ordering)
/// 2. Direct API mode: Uses SpotifyPlaylistTrack (with ISRC and ordering)
///
/// When ISRC is available, exact matching is preferred. Falls back to fuzzy matching.
///
/// CRON SCHEDULING: Each playlist has its own cron schedule.
/// When a playlist schedule is due, we run the same per-playlist rebuild workflow
/// used by the manual per-playlist "Rebuild" button.
/// Manual refresh is always allowed. Cache persists until next cron run.
/// </summary>
public class SpotifyTrackMatchingService : BackgroundService
{
    private const string CachedPlaylistItemFields =
        "Genres,GenreItems,DateCreated,MediaSources,ParentId,People,Tags,SortName,UserData,ProviderIds";

    private readonly SpotifyImportSettings _spotifySettings;
    private readonly SpotifyApiSettings _spotifyApiSettings;
    private readonly RedisCacheService _cache;
    private readonly SpotifyMappingService _mappingService;
    private readonly SpotifyMappingValidationService _validationService;
    private readonly ILogger<SpotifyTrackMatchingService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private const int DelayBetweenSearchesMs = 150; // 150ms = ~6.6 searches/second to avoid rate limiting
    private const int BatchSize = 11; // Number of parallel searches (matches SquidWTF provider count)
    private static readonly TimeSpan ExternalProviderSearchTimeout = TimeSpan.FromSeconds(30);

    // Track last run time per playlist to prevent duplicate runs
    private readonly Dictionary<string, DateTime> _lastRunTimes = new();
    private readonly TimeSpan _minimumRunInterval = TimeSpan.FromMinutes(5); // Cooldown between runs

    public SpotifyTrackMatchingService(
        IOptions<SpotifyImportSettings> spotifySettings,
        IOptions<SpotifyApiSettings> spotifyApiSettings,
        RedisCacheService cache,
        SpotifyMappingService mappingService,
        SpotifyMappingValidationService validationService,
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        ILogger<SpotifyTrackMatchingService> logger)
    {
        _spotifySettings = spotifySettings.Value;
        _spotifyApiSettings = spotifyApiSettings.Value;
        _cache = cache;
        _mappingService = mappingService;
        _validationService = validationService;
        _serviceProvider = serviceProvider;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Helper method to safely check if a dynamic cache result has a value
    /// Handles the case where JsonElement cannot be compared to null directly
    /// </summary>
    private static bool HasValue(object? obj)
    {
        if (obj == null) return false;
        if (obj is JsonElement jsonEl) return jsonEl.ValueKind != JsonValueKind.Null && jsonEl.ValueKind != JsonValueKind.Undefined;
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("========================================");
        _logger.LogInformation("SpotifyTrackMatchingService: Starting up...");

        if (!_spotifySettings.Enabled)
        {
            _logger.LogInformation("Spotify playlist injection is DISABLED, matching service will not run");
            _logger.LogInformation("========================================");
            return;
        }

        var matchMode = _spotifyApiSettings.Enabled && _spotifyApiSettings.PreferIsrcMatching
            ? "ISRC-preferred" : "fuzzy";
        _logger.LogInformation("Matching mode: {Mode}", matchMode);
        _logger.LogInformation("Cron-based scheduling: each playlist runs independently");

        // Log all playlist schedules
        foreach (var playlist in _spotifySettings.Playlists)
        {
            var schedule = string.IsNullOrEmpty(playlist.SyncSchedule) ? "0 8 * * 1" : playlist.SyncSchedule;
            _logger.LogInformation("  - {Name}: {Schedule}", playlist.Name, schedule);
        }

        _logger.LogInformation("========================================");

        // Wait a bit for the fetcher to run first
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);

        // Run once on startup to match any existing missing tracks
        try
        {
            _logger.LogInformation("Running initial track matching on startup (one-time)");
            await MatchAllPlaylistsAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during startup track matching");
        }

        // Now start the cron-based scheduling loop
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Calculate next run time for each playlist.
                // Use a small grace window so we don't miss exact-minute cron runs when waking slightly late.
                var now = DateTime.UtcNow;
                var schedulerReference = now.AddMinutes(-1);
                var nextRuns = new List<(string PlaylistName, DateTime NextRun, CronExpression Cron)>();

                foreach (var playlist in _spotifySettings.Playlists)
                {
                    var schedule = string.IsNullOrEmpty(playlist.SyncSchedule) ? "0 8 * * *" : playlist.SyncSchedule;

                    try
                    {
                        var cron = CronExpression.Parse(schedule);
                        var nextRun = cron.GetNextOccurrence(schedulerReference, TimeZoneInfo.Utc);

                        if (nextRun.HasValue)
                        {
                            nextRuns.Add((playlist.Name, nextRun.Value, cron));
                        }
                        else
                        {
                            _logger.LogWarning("Could not calculate next run for playlist {Name} with schedule {Schedule}",
                                playlist.Name, schedule);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Invalid cron schedule for playlist {Name}: {Schedule}",
                            playlist.Name, schedule);
                    }
                }

                if (nextRuns.Count == 0)
                {
                    _logger.LogWarning("No valid cron schedules found, sleeping for 1 hour");
                    await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
                    continue;
                }

                // Run all playlists that are currently due.
                var duePlaylists = nextRuns
                    .Where(x => x.NextRun <= now)
                    .OrderBy(x => x.NextRun)
                    .ToList();

                if (duePlaylists.Count == 0)
                {
                    // No playlist due yet: wait until the next scheduled run (or max 1 hour to re-check schedules)
                    var nextPlaylist = nextRuns.OrderBy(x => x.NextRun).First();
                    var waitTime = nextPlaylist.NextRun - now;

                    _logger.LogInformation("Next scheduled run: {Playlist} at {Time} UTC (in {Minutes:F1} minutes)",
                        nextPlaylist.PlaylistName, nextPlaylist.NextRun, waitTime.TotalMinutes);

                    var maxWait = TimeSpan.FromHours(1);
                    var actualWait = waitTime > maxWait ? maxWait : waitTime;
                    await Task.Delay(actualWait, stoppingToken);
                    continue;
                }

                _logger.LogInformation(
                    "=== CRON TRIGGER: Running scheduled rebuild for {Count} due playlists ===",
                    duePlaylists.Count);

                var anySkippedForCooldown = false;

                foreach (var due in duePlaylists)
                {
                    if (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }

                    _logger.LogInformation("→ Running scheduled rebuild for {Playlist}", due.PlaylistName);

                    var rebuilt = await TryRunSinglePlaylistRebuildWithCooldownAsync(
                        due.PlaylistName,
                        stoppingToken,
                        trigger: "cron");

                    if (!rebuilt)
                    {
                        anySkippedForCooldown = true;
                        continue;
                    }

                    _logger.LogInformation("✓ Finished scheduled rebuild for {Playlist} - Next run at {NextRun} UTC",
                        due.PlaylistName, due.Cron.GetNextOccurrence(DateTime.UtcNow, TimeZoneInfo.Utc));
                }

                // Avoid a tight loop if one or more due playlists were skipped by cooldown.
                if (anySkippedForCooldown)
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in cron scheduling loop");
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }
    }

    /// <summary>
    /// Rebuilds a single playlist from scratch (clears cache, fetches fresh data, re-matches).
    /// Used by individual per-playlist rebuild actions.
    /// </summary>
    private async Task RebuildSinglePlaylistAsync(string playlistName, CancellationToken cancellationToken)
    {
        var playlist = _spotifySettings.Playlists
            .FirstOrDefault(p => p.Name.Equals(playlistName, StringComparison.OrdinalIgnoreCase));

        if (playlist == null)
        {
            _logger.LogInformation("Playlist {Playlist} not found in configuration", playlistName);
            return;
        }

        _logger.LogInformation("Step 1/3: Clearing cache for {Playlist}", playlistName);

        // Clear cache for this playlist (same as "Rebuild All Remote" button)
        var keysToDelete = new[]
        {
            CacheKeyBuilder.BuildSpotifyPlaylistKey(playlist.Name),
            CacheKeyBuilder.BuildSpotifyMissingTracksKey(playlist.Name),
            CacheKeyBuilder.BuildSpotifyLegacyMatchedTracksKey(playlist.Name), // Legacy key
            CacheKeyBuilder.BuildSpotifyMatchedTracksKey(playlist.Name),
            CacheKeyBuilder.BuildSpotifyPlaylistItemsKey(playlist.Name),
            CacheKeyBuilder.BuildSpotifyPlaylistOrderedKey(playlist.Name),
            CacheKeyBuilder.BuildSpotifyPlaylistStatsKey(playlist.Name)
        };

        foreach (var key in keysToDelete)
        {
            await _cache.DeleteAsync(key);
        }

        _logger.LogInformation("Step 2/3: Fetching fresh data from Spotify for {Playlist}", playlistName);

        using var scope = _serviceProvider.CreateScope();
        var metadataService = scope.ServiceProvider.GetRequiredService<IMusicMetadataService>();

        // Trigger fresh fetch from Spotify
        SpotifyPlaylistFetcher? playlistFetcher = null;
        if (_spotifyApiSettings.Enabled)
        {
            playlistFetcher = scope.ServiceProvider.GetService<SpotifyPlaylistFetcher>();
            if (playlistFetcher != null)
            {
                // Force refresh from Spotify (clears cache and re-fetches)
                await playlistFetcher.RefreshPlaylistAsync(playlist.Name);
            }
        }

        _logger.LogInformation("Step 3/3: Matching tracks for {Playlist}", playlistName);

        try
        {
            if (playlistFetcher != null)
            {
                // Use new direct API mode with ISRC support
                await MatchPlaylistTracksWithIsrcAsync(
                    playlist.Name, playlistFetcher, metadataService, cancellationToken);
            }
            else
            {
                // Fall back to legacy mode
                await MatchPlaylistTracksLegacyAsync(
                    playlist.Name, metadataService, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error matching tracks for playlist {Playlist}", playlist.Name);
            throw;
        }

        await ClearPlaylistImageCacheAsync(playlist);
        _logger.LogInformation("✓ Rebuild complete for {Playlist}", playlistName);
    }

    /// <summary>
    /// Matches tracks for a single playlist WITHOUT clearing cache or refreshing from Spotify.
    /// Used for lightweight re-matching when only local library has changed.
    /// </summary>
    private async Task MatchSinglePlaylistAsync(string playlistName, CancellationToken cancellationToken)
    {
        var playlist = _spotifySettings.Playlists
            .FirstOrDefault(p => p.Name.Equals(playlistName, StringComparison.OrdinalIgnoreCase));

        if (playlist == null)
        {
            _logger.LogInformation("Playlist {Playlist} not found in configuration", playlistName);
            return;
        }

        using var scope = _serviceProvider.CreateScope();
        var metadataService = scope.ServiceProvider.GetRequiredService<IMusicMetadataService>();

        // Check if we should use the new SpotifyPlaylistFetcher
        SpotifyPlaylistFetcher? playlistFetcher = null;
        if (_spotifyApiSettings.Enabled)
        {
            playlistFetcher = scope.ServiceProvider.GetService<SpotifyPlaylistFetcher>();
        }

        try
        {
            if (playlistFetcher != null)
            {
                // Use new direct API mode with ISRC support
                await MatchPlaylistTracksWithIsrcAsync(
                    playlist.Name, playlistFetcher, metadataService, cancellationToken);
            }
            else
            {
                // Fall back to legacy mode
                await MatchPlaylistTracksLegacyAsync(
                    playlist.Name, metadataService, cancellationToken);
            }

            await ClearPlaylistImageCacheAsync(playlist);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error matching tracks for playlist {Playlist}", playlist.Name);
            throw;
        }
    }

    private async Task ClearPlaylistImageCacheAsync(SpotifyPlaylistConfig playlist)
    {
        if (string.IsNullOrWhiteSpace(playlist.JellyfinId))
        {
            return;
        }

        var deletedCount = await _cache.DeleteByPatternAsync($"image:{playlist.JellyfinId}:*");
        _logger.LogDebug("Cleared {Count} cached local image entries for playlist {Playlist}",
            deletedCount,
            playlist.Name);
    }

    /// <summary>
    /// Public method to trigger full rebuild for all playlists (called from "Rebuild All Remote" button).
    /// This clears caches, fetches fresh data, and re-matches everything immediately.
    /// </summary>
    public async Task TriggerRebuildAllAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Full rebuild triggered for all playlists");
        await RebuildAllPlaylistsAsync(cancellationToken);
    }

    /// <summary>
    /// Public method to trigger full rebuild for a single playlist (called from individual "Rebuild Remote" button).
    /// This clears cache, fetches fresh data, and re-matches - same workflow as scheduled cron rebuilds for a playlist.
    /// </summary>
    public async Task TriggerRebuildForPlaylistAsync(string playlistName)
    {
        _logger.LogInformation("Manual full rebuild triggered for playlist: {Playlist}", playlistName);
        var rebuilt = await TryRunSinglePlaylistRebuildWithCooldownAsync(
            playlistName,
            CancellationToken.None,
            trigger: "manual");

        if (!rebuilt)
        {
            if (_lastRunTimes.TryGetValue(playlistName, out var lastRun))
            {
                var timeSinceLastRun = DateTime.UtcNow - lastRun;
                var remaining = _minimumRunInterval - timeSinceLastRun;
                var remainingSeconds = Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
                throw new InvalidOperationException(
                    $"Please wait {remainingSeconds} more seconds before rebuilding again");
            }

            throw new InvalidOperationException("Playlist rebuild skipped due to cooldown");
        }
    }

    private async Task<bool> TryRunSinglePlaylistRebuildWithCooldownAsync(
        string playlistName,
        CancellationToken cancellationToken,
        string trigger)
    {
        if (_lastRunTimes.TryGetValue(playlistName, out var lastRun))
        {
            var timeSinceLastRun = DateTime.UtcNow - lastRun;
            if (timeSinceLastRun < _minimumRunInterval)
            {
                _logger.LogWarning(
                    "Skipping {Trigger} rebuild for {Playlist} - last run was {Seconds}s ago (cooldown: {Cooldown}s)",
                    trigger,
                    playlistName,
                    (int)timeSinceLastRun.TotalSeconds,
                    (int)_minimumRunInterval.TotalSeconds);
                return false;
            }
        }

        await RebuildSinglePlaylistAsync(playlistName, cancellationToken);
        _lastRunTimes[playlistName] = DateTime.UtcNow;
        return true;
    }

    /// <summary>
    /// Public method to trigger lightweight matching for all playlists (called from controller).
    /// This bypasses cron schedules and runs immediately WITHOUT clearing cache or refreshing from Spotify.
    /// Use this when only the local library has changed.
    /// </summary>
    public async Task TriggerMatchingAsync()
    {
        _logger.LogInformation("Manual track matching triggered for all playlists (bypassing cron schedules)");
        await MatchAllPlaylistsAsync(CancellationToken.None);
    }

    /// <summary>
    /// Public method to trigger lightweight matching for a single playlist (called from "Re-match Local" button).
    /// This bypasses cron schedules and runs immediately WITHOUT clearing cache or refreshing from Spotify.
    /// Use this when only the local library has changed, not when Spotify playlist changed.
    /// </summary>
    public async Task TriggerMatchingForPlaylistAsync(string playlistName)
    {
        _logger.LogInformation("Manual track matching triggered for playlist: {Playlist} (lightweight, no cache clear)", playlistName);

        // Intentionally no cooldown here: this path should react immediately to
        // local library changes and manual mapping updates without waiting for
        // Spotify API cooldown windows.
        await MatchSinglePlaylistAsync(playlistName, CancellationToken.None);
    }

    private async Task RebuildAllPlaylistsAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("=== STARTING FULL REBUILD FOR ALL PLAYLISTS ===");

        var playlists = _spotifySettings.Playlists;
        if (playlists.Count == 0)
        {
            _logger.LogInformation("No playlists configured for rebuild");
            return;
        }

        foreach (var playlist in playlists)
        {
            if (cancellationToken.IsCancellationRequested) break;

            try
            {
                await RebuildSinglePlaylistAsync(playlist.Name, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error rebuilding playlist {Playlist}", playlist.Name);
            }
        }

        _logger.LogInformation("=== FINISHED FULL REBUILD FOR ALL PLAYLISTS ===");
    }

    private async Task MatchAllPlaylistsAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("=== STARTING TRACK MATCHING FOR ALL PLAYLISTS ===");

        var playlists = _spotifySettings.Playlists;
        if (playlists.Count == 0)
        {
            _logger.LogInformation("No playlists configured for matching");
            return;
        }

        foreach (var playlist in playlists)
        {
            if (cancellationToken.IsCancellationRequested) break;

            try
            {
                await MatchSinglePlaylistAsync(playlist.Name, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error matching tracks for playlist {Playlist}", playlist.Name);
            }
        }

        _logger.LogInformation("=== FINISHED TRACK MATCHING FOR ALL PLAYLISTS ===");
    }

    /// <summary>
    /// New matching mode that uses ISRC when available for exact matches.
    /// Preserves track position for correct playlist ordering.
    /// Only matches tracks that aren't already in the Jellyfin playlist.
    /// Uses GREEDY ASSIGNMENT to maximize total matches.
    /// </summary>
    private async Task MatchPlaylistTracksWithIsrcAsync(
        string playlistName,
        SpotifyPlaylistFetcher playlistFetcher,
        IMusicMetadataService metadataService,
        CancellationToken cancellationToken)
    {
        var matchedTracksKey = CacheKeyBuilder.BuildSpotifyMatchedTracksKey(playlistName);

        // Get playlist tracks with full metadata including ISRC and position
        var spotifyTracks = await playlistFetcher.GetPlaylistTracksAsync(playlistName);
        if (spotifyTracks.Count == 0)
        {
            _logger.LogWarning("No tracks found for {Playlist}, skipping matching", playlistName);
            return;
        }

        // Get the Jellyfin playlist ID to check which tracks already exist
        var playlistConfig = _spotifySettings.Playlists
            .FirstOrDefault(p => p.Name.Equals(playlistName, StringComparison.OrdinalIgnoreCase));

        HashSet<string> existingSpotifyIds = new();

        if (!string.IsNullOrEmpty(playlistConfig?.JellyfinId))
        {
            // Get existing tracks from Jellyfin playlist to avoid re-matching
            using var scope = _serviceProvider.CreateScope();
            var proxyService = scope.ServiceProvider.GetService<JellyfinProxyService>();
            var jellyfinSettings = scope.ServiceProvider.GetService<IOptions<JellyfinSettings>>()?.Value;

            if (proxyService != null && jellyfinSettings != null)
            {
                try
                {
                    // CRITICAL: Must include UserId parameter or Jellyfin returns empty results
                    var userId = jellyfinSettings.UserId;
                    var playlistItemsUrl = $"Playlists/{playlistConfig.JellyfinId}/Items";
                    var queryParams = new Dictionary<string, string>();
                    if (!string.IsNullOrEmpty(userId))
                    {
                        queryParams["UserId"] = userId;
                    }
                    else
                    {
                        _logger.LogInformation("No UserId configured - may not be able to fetch existing playlist tracks for {Playlist}", playlistName);
                    }

                    var (existingTracksResponse, _) = await proxyService.GetJsonAsyncInternal(
                        playlistItemsUrl,
                        queryParams);

                    if (existingTracksResponse != null &&
                        existingTracksResponse.RootElement.TryGetProperty("Items", out var items))
                    {
                        foreach (var item in items.EnumerateArray())
                        {
                            if (item.TryGetProperty("ProviderIds", out var providerIds) &&
                                providerIds.TryGetProperty("Spotify", out var spotifyId))
                            {
                                var id = spotifyId.GetString();
                                if (!string.IsNullOrEmpty(id))
                                {
                                    existingSpotifyIds.Add(id);
                                }
                            }
                        }
                        _logger.LogInformation("Found {Count} tracks already in Jellyfin playlist {Playlist}, will skip matching these",
                            existingSpotifyIds.Count, playlistName);
                    }
                    else
                    {
                        _logger.LogWarning("No Items found in Jellyfin playlist response for {Playlist} - may need UserId parameter", playlistName);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Could not fetch existing Jellyfin tracks for {Playlist}, will match all tracks", playlistName);
                }
            }
        }

        // Filter to only tracks not already in Jellyfin
        var tracksToMatch = spotifyTracks
            .Where(t => !existingSpotifyIds.Contains(t.SpotifyId))
            .ToList();
        var existingMatched = await _cache.GetAsync<List<MatchedTrack>>(matchedTracksKey);

        if (tracksToMatch.Count == 0)
        {
            _logger.LogWarning("All {Count} tracks for {Playlist} already exist in Jellyfin, skipping matching",
                spotifyTracks.Count, playlistName);
            await EnsurePlaylistItemsCacheAsync(
                playlistName,
                playlistConfig?.JellyfinId,
                spotifyTracks,
                existingMatched ?? [],
                cancellationToken);
            return;
        }

        _logger.LogWarning("Matching {ToMatch}/{Total} tracks for {Playlist} (skipping {Existing} already in Jellyfin, ISRC: {IsrcEnabled}, AGGRESSIVE MODE)",
            tracksToMatch.Count, spotifyTracks.Count, playlistName, existingSpotifyIds.Count, _spotifyApiSettings.PreferIsrcMatching);

        // CRITICAL: Skip matching if cache exists and is valid
        // Only re-match if cache is missing OR if we detect manual mappings that need to be applied
        if (existingMatched != null && existingMatched.Count > 0)
        {
            var hasIncompleteLocalSnapshots = existingMatched.Any(m =>
                m.MatchedSong?.IsLocal == true && !JellyfinItemSnapshotHelper.HasRawItemSnapshot(m.MatchedSong));
            var hasPolicyBlockedExternalMatches = existingMatched.Any(m =>
                m.MatchedSong is { IsLocal: false } song &&
                !ExternalTrackPlaybackPolicy.CanUseForPlayback(song.ExternalProvider, song.Id));

            if (hasIncompleteLocalSnapshots)
            {
                _logger.LogInformation(
                    "Rebuilding matched track cache for {Playlist}: cached local matches are missing full Jellyfin item snapshots",
                    playlistName);
            }

            if (hasPolicyBlockedExternalMatches)
            {
                _logger.LogInformation(
                    "Rebuilding matched track cache for {Playlist}: a cached provider can no longer supply playback audio",
                    playlistName);
            }

            // Check if we have NEW manual mappings that aren't in the cache
            var hasNewManualMappings = false;
            foreach (var track in tracksToMatch)
            {
                // Check if this track has a manual mapping but isn't in the cached results
                var manualMappingKey = CacheKeyBuilder.BuildSpotifyManualMappingKey(playlistName, track.SpotifyId);
                var manualMapping = await _cache.GetAsync<string>(manualMappingKey);

                var externalMappingKey = CacheKeyBuilder.BuildSpotifyExternalMappingKey(playlistName, track.SpotifyId);
                var externalMappingJson = await _cache.GetStringAsync(externalMappingKey);

                var hasManualMapping = !string.IsNullOrEmpty(manualMapping) || !string.IsNullOrEmpty(externalMappingJson);
                var isInCache = existingMatched.Any(m => m.SpotifyId == track.SpotifyId);

                // If track has manual mapping but isn't in cache, we need to rebuild
                if (hasManualMapping && !isInCache)
                {
                    hasNewManualMappings = true;
                    break;
                }
            }

            if (!hasNewManualMappings && !hasIncompleteLocalSnapshots && !hasPolicyBlockedExternalMatches)
            {
                _logger.LogWarning("✓ Playlist {Playlist} already has {Count} matched tracks cached (skipping {ToMatch} new tracks), no re-matching needed",
                    playlistName, existingMatched.Count, tracksToMatch.Count);
                await EnsurePlaylistItemsCacheAsync(
                    playlistName,
                    playlistConfig?.JellyfinId,
                    spotifyTracks,
                    existingMatched,
                    cancellationToken);
                return;
            }

            _logger.LogInformation(
                "Rebuilding matched track cache for {Playlist} to apply updated mappings or snapshot completeness",
                playlistName);
        }

        // PHASE 1: Get ALL Jellyfin tracks from the playlist (already injected by plugin)
        var jellyfinTracks = new List<Song>();
        if (!string.IsNullOrEmpty(playlistConfig?.JellyfinId))
        {
            using var scope = _serviceProvider.CreateScope();
            var proxyService = scope.ServiceProvider.GetService<JellyfinProxyService>();
            var jellyfinSettings = scope.ServiceProvider.GetService<IOptions<JellyfinSettings>>()?.Value;
            var jellyfinModelMapper = scope.ServiceProvider.GetService<JellyfinModelMapper>();

            if (proxyService != null && jellyfinSettings != null)
            {
                try
                {
                    var userId = jellyfinSettings.UserId;
                    var playlistItemsUrl = $"Playlists/{playlistConfig.JellyfinId}/Items";
                    var queryParams = new Dictionary<string, string> { ["Fields"] = CachedPlaylistItemFields };
                    if (!string.IsNullOrEmpty(userId))
                    {
                        queryParams["UserId"] = userId;
                    }

                    var (response, _) = await proxyService.GetJsonAsyncInternal(playlistItemsUrl, queryParams);

                    if (response != null && response.RootElement.TryGetProperty("Items", out var items))
                    {
                        foreach (var item in items.EnumerateArray())
                        {
                            var song = jellyfinModelMapper?.ParseSong(item) ?? CreateLocalSongSnapshot(item);
                            jellyfinTracks.Add(song);
                        }
                        _logger.LogInformation("📚 Loaded {Count} tracks from Jellyfin playlist {Playlist}",
                            jellyfinTracks.Count, playlistName);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to load Jellyfin tracks for {Playlist}", playlistName);
                }
            }
        }

        // PHASE 2: Match Jellyfin tracks → Spotify tracks using fuzzy matching
        _logger.LogInformation("🔍 Matching {JellyfinCount} Jellyfin tracks to {SpotifyCount} Spotify tracks",
            jellyfinTracks.Count, spotifyTracks.Count);

        var localMatches = new Dictionary<string, (Song JellyfinTrack, SpotifyPlaylistTrack SpotifyTrack, double Score)>();
        var usedJellyfinIds = new HashSet<string>();
        var usedSpotifyIds = new HashSet<string>();

        // Build all possible matches with scores
        var allLocalCandidates = new List<(Song JellyfinTrack, SpotifyPlaylistTrack SpotifyTrack, double Score)>();

        foreach (var jellyfinTrack in jellyfinTracks)
        {
            foreach (var spotifyTrack in spotifyTracks)
            {
                var score = CalculateMatchScore(jellyfinTrack.Title, jellyfinTrack.Artist,
                    spotifyTrack.Title, spotifyTrack.PrimaryArtist);

                if (score >= 70) // Only consider good matches
                {
                    allLocalCandidates.Add((jellyfinTrack, spotifyTrack, score));
                }
            }
        }

        // Greedy assignment: best matches first
        foreach (var (jellyfinTrack, spotifyTrack, score) in allLocalCandidates.OrderByDescending(c => c.Score))
        {
            if (usedJellyfinIds.Contains(jellyfinTrack.Id)) continue;
            if (usedSpotifyIds.Contains(spotifyTrack.SpotifyId)) continue;

            localMatches[spotifyTrack.SpotifyId] = (jellyfinTrack, spotifyTrack, score);
            usedJellyfinIds.Add(jellyfinTrack.Id);
            usedSpotifyIds.Add(spotifyTrack.SpotifyId);

            // Save local mapping
            var metadata = new TrackMetadata
            {
                Title = spotifyTrack.Title,
                Artist = spotifyTrack.PrimaryArtist,
                Album = spotifyTrack.Album,
                ArtworkUrl = spotifyTrack.AlbumArtUrl,
                DurationMs = spotifyTrack.DurationMs
            };

            await _mappingService.SaveLocalMappingAsync(spotifyTrack.SpotifyId, jellyfinTrack.Id, metadata);

            _logger.LogInformation("  ✓ Local: {SpotifyTitle} → {JellyfinTitle} (score: {Score:F1})",
                spotifyTrack.Title, jellyfinTrack.Title, score);
        }

        _logger.LogInformation("✅ Matched {LocalCount}/{SpotifyCount} Spotify tracks to local Jellyfin tracks",
            localMatches.Count, spotifyTracks.Count);

        // PHASE 3: For remaining unmatched Spotify tracks, search external providers
        var unmatchedSpotifyTracks = spotifyTracks
            .Where(t => !usedSpotifyIds.Contains(t.SpotifyId))
            .ToList();

        _logger.LogInformation("🔍 Searching external providers for {Count} unmatched tracks",
            unmatchedSpotifyTracks.Count);

        var matchedTracks = new List<MatchedTrack>();
        var isrcMatches = 0;
        var fuzzyMatches = 0;
        var noMatch = 0;

        var allCandidates = new List<(SpotifyPlaylistTrack SpotifyTrack, Song MatchedSong, double Score, string MatchType)>();

        // Process unmatched tracks in batches
        for (int i = 0; i < unmatchedSpotifyTracks.Count; i += BatchSize)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var batch = unmatchedSpotifyTracks.Skip(i).Take(BatchSize).ToList();
            var batchStart = i + 1;
            var batchEnd = i + batch.Count;
            var batchStopwatch = System.Diagnostics.Stopwatch.StartNew();

            _logger.LogInformation(
                "Starting external matching batch for {Playlist}: tracks {Start}-{End}/{Total}",
                playlistName,
                batchStart,
                batchEnd,
                unmatchedSpotifyTracks.Count);

            var batchTasks = batch.Select(async spotifyTrack =>
            {
                var primaryArtist = spotifyTrack.PrimaryArtist;
                var trackStopwatch = System.Diagnostics.Stopwatch.StartNew();

                try
                {
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeoutCts.CancelAfter(ExternalProviderSearchTimeout);
                    var trackCancellationToken = timeoutCts.Token;

                    var candidates = new List<(Song Song, double Score, string MatchType)>();

                    // Check global external mapping first
                    var globalMapping = await _mappingService.GetMappingAsync(spotifyTrack.SpotifyId);
                    if (globalMapping != null && globalMapping.TargetType == "external")
                    {
                        Song? mappedSong = null;
                        if (globalMapping.TryGetExternalTarget(null, out var mappedProvider, out var mappedExternalId))
                        {
                            mappedSong = await metadataService.GetSongAsync(
                                mappedProvider,
                                mappedExternalId,
                                trackCancellationToken);
                        }

                        if (mappedSong != null &&
                            ExternalTrackPlaybackPolicy.CanUseForPlayback(mappedSong.ExternalProvider, mappedSong.Id))
                        {
                            candidates.Add((mappedSong, 100.0, "global-mapping-external"));
                            trackStopwatch.Stop();
                            _logger.LogDebug(
                                "External candidate search finished for {Playlist} track #{Position}: {Title} by {Artist} in {ElapsedMs}ms using global mapping",
                                playlistName,
                                spotifyTrack.Position,
                                spotifyTrack.Title,
                                primaryArtist,
                                trackStopwatch.ElapsedMilliseconds);
                            return (spotifyTrack, candidates);
                        }
                    }

                    // Try ISRC match
                    if (_spotifyApiSettings.PreferIsrcMatching && !string.IsNullOrEmpty(spotifyTrack.Isrc))
                    {
                        try
                        {
                            var isrcSong = await TryMatchByIsrcAsync(
                                spotifyTrack.Isrc,
                                metadataService,
                                trackCancellationToken);

                            if (isrcSong != null &&
                                ExternalTrackPlaybackPolicy.CanUseForPlayback(isrcSong.ExternalProvider, isrcSong.Id))
                            {
                                candidates.Add((isrcSong, 100.0, "isrc"));
                            }
                        }
                        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(
                                ex,
                                "ISRC lookup failed for {Playlist} track #{Position}: {Title} by {Artist}",
                                playlistName,
                                spotifyTrack.Position,
                                spotifyTrack.Title,
                                primaryArtist);
                        }
                    }

                    // Fuzzy search external providers
                    var fuzzySongs = await TryMatchByFuzzyMultipleAsync(
                        spotifyTrack.Title,
                        spotifyTrack.Artists,
                        metadataService,
                        trackCancellationToken);

                    foreach (var (song, score) in fuzzySongs)
                    {
                        if (!song.IsLocal &&
                            ExternalTrackPlaybackPolicy.CanUseForPlayback(song.ExternalProvider, song.Id))
                        {
                            candidates.Add((song, score, "fuzzy-external"));
                        }
                    }

                    trackStopwatch.Stop();
                    _logger.LogDebug(
                        "External candidate search finished for {Playlist} track #{Position}: {Title} by {Artist} in {ElapsedMs}ms with {CandidateCount} candidates",
                        playlistName,
                        spotifyTrack.Position,
                        spotifyTrack.Title,
                        primaryArtist,
                        trackStopwatch.ElapsedMilliseconds,
                        candidates.Count);

                    return (spotifyTrack, candidates);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return (spotifyTrack, new List<(Song, double, string)>());
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning(
                        "External candidate search timed out for {Playlist} track #{Position}: {Title} by {Artist} after {TimeoutSeconds}s",
                        playlistName,
                        spotifyTrack.Position,
                        spotifyTrack.Title,
                        primaryArtist,
                        ExternalProviderSearchTimeout.TotalSeconds);
                    return (spotifyTrack, new List<(Song, double, string)>());
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Failed to match track for {Playlist} track #{Position}: {Title} by {Artist}",
                        playlistName,
                        spotifyTrack.Position,
                        spotifyTrack.Title,
                        primaryArtist);
                    return (spotifyTrack, new List<(Song, double, string)>());
                }
            }).ToList();

            var batchResults = await Task.WhenAll(batchTasks);
            batchStopwatch.Stop();

            foreach (var result in batchResults)
            {
                foreach (var candidate in result.Item2)
                {
                    allCandidates.Add((result.Item1, candidate.Item1, candidate.Item2, candidate.Item3));
                }
            }

            var batchCandidateCount = batchResults.Sum(result => result.Item2.Count);
            _logger.LogInformation(
                "Finished external matching batch for {Playlist}: tracks {Start}-{End}/{Total} in {ElapsedMs}ms ({CandidateCount} candidates)",
                playlistName,
                batchStart,
                batchEnd,
                unmatchedSpotifyTracks.Count,
                batchStopwatch.ElapsedMilliseconds,
                batchCandidateCount);

            if (i + BatchSize < unmatchedSpotifyTracks.Count)
            {
                await Task.Delay(DelayBetweenSearchesMs, cancellationToken);
            }
        }

        // PHASE 4: Greedy assignment for external matches
        var usedSongIds = new HashSet<string>();
        var externalAssignments = new Dictionary<string, (Song Song, double Score, string MatchType)>();

        foreach (var (spotifyTrack, song, score, matchType) in allCandidates.OrderByDescending(c => c.Score))
        {
            if (externalAssignments.ContainsKey(spotifyTrack.SpotifyId)) continue;
            if (usedSongIds.Contains(song.Id)) continue;
            if (!ExternalTrackPlaybackPolicy.CanUseForPlayback(song.ExternalProvider, song.Id)) continue;

            externalAssignments[spotifyTrack.SpotifyId] = (song, score, matchType);
            usedSongIds.Add(song.Id);

            // Save external mapping
            var metadata = new TrackMetadata
            {
                Title = spotifyTrack.Title,
                Artist = spotifyTrack.PrimaryArtist,
                Album = spotifyTrack.Album,
                ArtworkUrl = spotifyTrack.AlbumArtUrl,
                DurationMs = spotifyTrack.DurationMs
            };

            await _mappingService.SaveExternalMappingAsync(
                spotifyTrack.SpotifyId,
                song.ExternalProvider ?? "Unknown",
                song.ExternalId ?? song.Id,
                metadata);

            if (matchType == "isrc") isrcMatches++;
            else fuzzyMatches++;

            _logger.LogInformation("  ✓ External: {Title} → {Provider}:{ExternalId} (score: {Score:F1})",
                spotifyTrack.Title, song.ExternalProvider, song.ExternalId, score);
        }

        // PHASE 5: Build final matched tracks list (local + external)
        foreach (var spotifyTrack in spotifyTracks.OrderBy(t => t.Position))
        {
            MatchedTrack? matched = null;

            // Check local matches first
            if (localMatches.TryGetValue(spotifyTrack.SpotifyId, out var localMatch))
            {
                matched = new MatchedTrack
                {
                    Position = spotifyTrack.Position,
                    SpotifyId = spotifyTrack.SpotifyId,
                    SpotifyTitle = spotifyTrack.Title,
                    SpotifyArtist = spotifyTrack.PrimaryArtist,
                    Isrc = spotifyTrack.Isrc,
                    MatchType = "fuzzy-local",
                    MatchedSong = localMatch.JellyfinTrack
                };
            }
            // Check external matches
            else if (externalAssignments.TryGetValue(spotifyTrack.SpotifyId, out var externalMatch))
            {
                matched = new MatchedTrack
                {
                    Position = spotifyTrack.Position,
                    SpotifyId = spotifyTrack.SpotifyId,
                    SpotifyTitle = spotifyTrack.Title,
                    SpotifyArtist = spotifyTrack.PrimaryArtist,
                    Isrc = spotifyTrack.Isrc,
                    MatchType = externalMatch.MatchType,
                    MatchedSong = externalMatch.Song
                };
            }
            else
            {
                noMatch++;
                _logger.LogDebug("  #{Position} {Title} → no match", spotifyTrack.Position, spotifyTrack.Title);
            }

            if (matched != null)
            {
                matchedTracks.Add(matched);
            }
        }

        if (matchedTracks.Count > 0)
        {
            // UPDATE STATS CACHE: Calculate and cache stats immediately after matching
            var statsLocalCount = localMatches.Count;
            var statsExternalCount = externalAssignments.Count;
            var statsMissingCount = spotifyTracks.Count - statsLocalCount - statsExternalCount;

            var stats = new Dictionary<string, int>
            {
                ["local"] = statsLocalCount,
                ["external"] = statsExternalCount,
                ["missing"] = statsMissingCount
            };

            var statsCacheKey = CacheKeyBuilder.BuildSpotifyPlaylistStatsKey(playlistName);
            await _cache.SetAsync(statsCacheKey, stats, TimeSpan.FromMinutes(30));

            _logger.LogInformation("📊 Updated stats cache for {Playlist}: {Local} local, {External} external, {Missing} missing",
                playlistName, statsLocalCount, statsExternalCount, statsMissingCount);

            // Calculate cache expiration: until next cron run (not just cache duration from settings)
            var playlist = _spotifySettings.Playlists
                .FirstOrDefault(p => p.Name.Equals(playlistName, StringComparison.OrdinalIgnoreCase));

            var cacheExpiration = TimeSpan.FromHours(24); // Default 24 hours

            if (playlist != null && !string.IsNullOrEmpty(playlist.SyncSchedule))
            {
                try
                {
                    var cron = CronExpression.Parse(playlist.SyncSchedule);
                    var nextRun = cron.GetNextOccurrence(DateTime.UtcNow, TimeZoneInfo.Utc);

                    if (nextRun.HasValue)
                    {
                        var timeUntilNextRun = nextRun.Value - DateTime.UtcNow;
                        // Add 5 minutes buffer to ensure cache doesn't expire before next run
                        cacheExpiration = timeUntilNextRun + TimeSpan.FromMinutes(5);

                        _logger.LogInformation("Cache will persist until next cron run: {NextRun} UTC (in {Hours:F1} hours)",
                            nextRun.Value, timeUntilNextRun.TotalHours);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Could not calculate next cron run for {Playlist}, using default cache duration", playlistName);
                }
            }

            // Cache matched tracks with position data until next cron run
            await _cache.SetAsync(matchedTracksKey, matchedTracks, cacheExpiration);

            // Save matched tracks to file for persistence across restarts
            await SaveMatchedTracksToFileAsync(playlistName, matchedTracks);

            // Also update legacy cache for backward compatibility
            var legacyKey = CacheKeyBuilder.BuildSpotifyLegacyMatchedTracksKey(playlistName);
            var legacySongs = matchedTracks.OrderBy(t => t.Position).Select(t => t.MatchedSong).ToList();
            await _cache.SetAsync(legacyKey, legacySongs, cacheExpiration);

            _logger.LogInformation(
                "✓ Cached {Matched}/{Total} tracks for {Playlist} via GREEDY ASSIGNMENT (ISRC: {Isrc}, Fuzzy: {Fuzzy}, No match: {NoMatch}) - cache expires in {Hours:F1}h",
                matchedTracks.Count, tracksToMatch.Count, playlistName, isrcMatches, fuzzyMatches, noMatch, cacheExpiration.TotalHours);

            // Pre-build playlist items cache for instant serving
            // This is what makes the UI show all matched tracks at once
            await PreBuildPlaylistItemsCacheAsync(playlistName, playlistConfig?.JellyfinId, spotifyTracks, matchedTracks, cacheExpiration, cancellationToken);
        }
        else
        {
            _logger.LogInformation("No tracks matched for {Playlist}", playlistName);
        }
    }

    /// <summary>
    /// Attempts to match a track by title and artist and returns scored candidates.
    /// </summary>
    private async Task<List<(Song Song, double Score)>> TryMatchByFuzzyMultipleAsync(
        string title,
        List<string> artists,
        IMusicMetadataService metadataService,
        CancellationToken cancellationToken)
    {
        var primaryArtist = artists.FirstOrDefault() ?? "";
        var titleStripped = FuzzyMatcher.StripDecorators(title);
        var query = $"{titleStripped} {primaryArtist}";

        var allCandidates = new List<(Song Song, double Score)>();

        // STEP 1: Search LOCAL Jellyfin library FIRST
        using var scope = _serviceProvider.CreateScope();
        var proxyService = scope.ServiceProvider.GetService<JellyfinProxyService>();
        if (proxyService != null)
        {
            try
            {
                // Search Jellyfin for local tracks
                var searchParams = new Dictionary<string, string>
                {
                    ["searchTerm"] = query,
                    ["includeItemTypes"] = "Audio",
                    ["recursive"] = "true",
                    ["limit"] = "10"
                };

                var (searchResponse, _) = await proxyService.GetJsonAsyncInternal("Items", searchParams);

                if (searchResponse != null && searchResponse.RootElement.TryGetProperty("Items", out var items))
                {
                    var localResults = new List<Song>();
                    foreach (var item in items.EnumerateArray())
                    {
                        var id = item.TryGetProperty("Id", out var idEl) ? idEl.GetString() ?? "" : "";
                        var songTitle = item.TryGetProperty("Name", out var nameEl) ? nameEl.GetString() ?? "" : "";
                        var artist = "";

                        if (item.TryGetProperty("Artists", out var artistsEl) && artistsEl.GetArrayLength() > 0)
                        {
                            artist = artistsEl[0].GetString() ?? "";
                        }
                        else if (item.TryGetProperty("AlbumArtist", out var albumArtistEl))
                        {
                            artist = albumArtistEl.GetString() ?? "";
                        }

                        localResults.Add(new Song
                        {
                            Id = id,
                            Title = songTitle,
                            Artist = artist,
                            IsLocal = true
                        });
                    }

                    if (localResults.Count > 0)
                    {
                        // Score local results
                        var scoredLocal = localResults
                            .Select(song => new
                            {
                                Song = song,
                                TitleScore = FuzzyMatcher.CalculateSimilarityAggressive(title, song.Title),
                                ArtistScore = FuzzyMatcher.CalculateArtistMatchScore(artists, song.Artist, song.Contributors)
                            })
                            .Select(x => new
                            {
                                x.Song,
                                x.TitleScore,
                                x.ArtistScore,
                                TotalScore = (x.TitleScore * 0.7) + (x.ArtistScore * 0.3)
                            })
                            .Where(x =>
                                x.TotalScore >= 40 ||
                                (x.ArtistScore >= 70 && x.TitleScore >= 30) ||
                                x.TitleScore >= 85)
                            .OrderByDescending(x => x.TotalScore)
                            .Select(x => (x.Song, x.TotalScore))
                            .ToList();

                        allCandidates.AddRange(scoredLocal);

                        // If we found good local matches, return them (don't search external)
                        if (scoredLocal.Any(x => x.TotalScore >= 70))
                        {
                            _logger.LogDebug("Found {Count} local matches for '{Title}', skipping external search",
                                scoredLocal.Count, title);
                            return allCandidates;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to search local library for '{Title}'", title);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        // STEP 2: Only search EXTERNAL if no good local match found
        var externalResults = await metadataService.SearchSongsAsync(query, limit: 10, cancellationToken);

        if (externalResults.Count > 0)
        {
            var scoredExternal = externalResults
                .Select(song => new
                {
                    Song = song,
                    TitleScore = FuzzyMatcher.CalculateSimilarityAggressive(title, song.Title),
                    ArtistScore = FuzzyMatcher.CalculateArtistMatchScore(artists, song.Artist, song.Contributors)
                })
                .Select(x => new
                {
                    x.Song,
                    x.TitleScore,
                    x.ArtistScore,
                    TotalScore = (x.TitleScore * 0.7) + (x.ArtistScore * 0.3)
                })
                .Where(x =>
                    x.TotalScore >= 40 ||
                    (x.ArtistScore >= 70 && x.TitleScore >= 30) ||
                    x.TitleScore >= 85)
                .OrderByDescending(x => x.TotalScore)
                .Select(x => (x.Song, x.TotalScore))
                .ToList();

            allCandidates.AddRange(scoredExternal);
        }

        return allCandidates;
    }

    private double CalculateMatchScore(string jellyfinTitle, string jellyfinArtist, string spotifyTitle, string spotifyArtist)
    {
        var titleScore = FuzzyMatcher.CalculateSimilarityAggressive(spotifyTitle, jellyfinTitle);
        var artistScore = FuzzyMatcher.CalculateSimilarity(spotifyArtist, jellyfinArtist);
        return (titleScore * 0.7) + (artistScore * 0.3);
    }

    /// <summary>
    /// Attempts to match a track by ISRC.
    /// SEARCHES LOCAL FIRST, then external if no local match found.
    /// </summary>
    private async Task<Song?> TryMatchByIsrcAsync(
        string isrc,
        IMusicMetadataService metadataService,
        CancellationToken cancellationToken)
    {
        // STEP 1: Search LOCAL Jellyfin library FIRST by ISRC
        // Note: Jellyfin doesn't have ISRC search, so we skip local ISRC search
        // Local tracks will be found via fuzzy matching instead

        cancellationToken.ThrowIfCancellationRequested();

        // STEP 2: Search EXTERNAL by ISRC
        return await metadataService.FindSongByIsrcAsync(isrc, cancellationToken);
    }

    /// <summary>
    /// Attempts to match a track by title and artist using AGGRESSIVE fuzzy matching.
    /// FOLLOWS OPTIMAL ORDER:
    /// 1. Strip decorators FIRST (before searching)
    /// 2. Substring matching (in FuzzyMatcher)
    /// 3. Levenshtein distance (in FuzzyMatcher)
    /// PRIORITY: Match as many tracks as possible, even with lower confidence.
    /// </summary>
    private async Task<Song?> TryMatchByFuzzyAsync(
        string title,
        List<string> artists,
        IMusicMetadataService metadataService)
    {
        try
        {
            var primaryArtist = artists.FirstOrDefault() ?? "";

            // STEP 1: Strip decorators FIRST (before searching)
            var titleStripped = FuzzyMatcher.StripDecorators(title);
            var query = $"{titleStripped} {primaryArtist}";

            var results = await metadataService.SearchSongsAsync(query, limit: 10);

            if (results.Count == 0) return null;

            // STEP 2-3: Score all results (substring + Levenshtein in CalculateSimilarityAggressive)
            var scoredResults = results
                .Select(song => new
                {
                    Song = song,
                    // Use aggressive matching which follows optimal order internally
                    TitleScore = FuzzyMatcher.CalculateSimilarityAggressive(title, song.Title),
                    ArtistScore = FuzzyMatcher.CalculateArtistMatchScore(artists, song.Artist, song.Contributors)
                })
                .Select(x => new
                {
                    x.Song,
                    x.TitleScore,
                    x.ArtistScore,
                    // Weight: 70% title, 30% artist (prioritize title matching)
                    TotalScore = (x.TitleScore * 0.7) + (x.ArtistScore * 0.3)
                })
                .OrderByDescending(x => x.TotalScore)
                .ToList();

            var bestMatch = scoredResults.FirstOrDefault();

            if (bestMatch == null) return null;

            // AGGRESSIVE: Accept matches with score >= 40 (was 50)
            if (bestMatch.TotalScore >= 40)
            {
                _logger.LogDebug("✓ Matched (score: {Score:F1}, title: {TitleScore}, artist: {ArtistScore}): {SpotifyTitle} → {MatchedTitle}",
                    bestMatch.TotalScore, bestMatch.TitleScore, bestMatch.ArtistScore, title, bestMatch.Song.Title);
                return bestMatch.Song;
            }

            // SUPER AGGRESSIVE: If artist matches well (70+), accept even lower title scores
            // This handles cases like "a" → "a-blah" where artist is the same
            if (bestMatch.ArtistScore >= 70 && bestMatch.TitleScore >= 30)
            {
                _logger.LogDebug("✓ Matched via artist priority (artist: {ArtistScore}, title: {TitleScore}): {SpotifyTitle} → {MatchedTitle}",
                    bestMatch.ArtistScore, bestMatch.TitleScore, title, bestMatch.Song.Title);
                return bestMatch.Song;
            }

            // ULTRA AGGRESSIVE: If title has high substring match (85+), accept it
            // This handles "luther" → "luther (feat. sza)"
            if (bestMatch.TitleScore >= 85)
            {
                _logger.LogDebug("✓ Matched via substring (title: {TitleScore}): {SpotifyTitle} → {MatchedTitle}",
                    bestMatch.TitleScore, title, bestMatch.Song.Title);
                return bestMatch.Song;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Legacy matching mode using MissingTrack from Jellyfin plugin.
    /// </summary>
    private async Task MatchPlaylistTracksLegacyAsync(
        string playlistName,
        IMusicMetadataService metadataService,
        CancellationToken cancellationToken)
    {
        var missingTracksKey = CacheKeyBuilder.BuildSpotifyMissingTracksKey(playlistName);
        var matchedTracksKey = CacheKeyBuilder.BuildSpotifyLegacyMatchedTracksKey(playlistName);

        // Check if we already have matched tracks cached
        var existingMatched = await _cache.GetAsync<List<Song>>(matchedTracksKey);
        if (existingMatched != null && existingMatched.Count > 0)
        {
            var playableMatched = existingMatched
                .Where(ExternalTrackPlaybackPolicy.CanUseForPlayback)
                .ToList();
            var blockedCount = existingMatched.Count - playableMatched.Count;

            if (blockedCount == 0)
            {
                _logger.LogWarning("Playlist {Playlist} already has {Count} matched tracks cached, skipping",
                    playlistName, existingMatched.Count);
                return;
            }

            if (playableMatched.Count > 0)
            {
                await _cache.SetAsync(
                    matchedTracksKey,
                    playableMatched,
                    CacheExtensions.SpotifyMatchedTracksTTL);
            }
            else
            {
                await _cache.DeleteAsync(matchedTracksKey);
            }

            _logger.LogWarning(
                "Removed {BlockedCount} unavailable cached tracks from {Playlist}; rebuilding legacy matches",
                blockedCount,
                playlistName);
        }

        // Get missing tracks
        var missingTracks = await _cache.GetAsync<List<MissingTrack>>(missingTracksKey);
        if (missingTracks == null || missingTracks.Count == 0)
        {
            _logger.LogWarning("No missing tracks found for {Playlist}, skipping matching", playlistName);
            return;
        }

        _logger.LogWarning("Matching {Count} tracks for {Playlist} (with rate limiting)",
            missingTracks.Count, playlistName);

        var matchedSongs = new List<Song>();
        var matchCount = 0;

        foreach (var track in missingTracks)
        {
            if (cancellationToken.IsCancellationRequested) break;

            try
            {
                var query = $"{track.Title} {track.PrimaryArtist}";
                var results = await metadataService.SearchSongsAsync(query, limit: 5);

                if (results.Count > 0)
                {
                    // Fuzzy match to find best result
                    // Check that ALL artists match (not just some)
                    var bestMatch = results
                        .Select(song => new
                        {
                            Song = song,
                            TitleScore = FuzzyMatcher.CalculateSimilarity(track.Title, song.Title),
                            // Calculate artist score by checking ALL artists match
                            ArtistScore = FuzzyMatcher.CalculateArtistMatchScore(track.Artists, song.Artist, song.Contributors)
                        })
                        .Select(x => new
                        {
                            x.Song,
                            x.TitleScore,
                            x.ArtistScore,
                            TotalScore = (x.TitleScore * 0.6) + (x.ArtistScore * 0.4)
                        })
                        .OrderByDescending(x => x.TotalScore)
                        .FirstOrDefault();

                    if (bestMatch != null && bestMatch.TotalScore >= 60)
                    {
                        matchedSongs.Add(bestMatch.Song);
                        matchCount++;

                        if (matchCount % 10 == 0)
                        {
                            _logger.LogInformation("Matched {Count}/{Total} tracks for {Playlist}",
                                matchCount, missingTracks.Count, playlistName);
                        }
                    }
                }

                // Rate limiting: delay between searches
                await Task.Delay(DelayBetweenSearchesMs, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to match track: {Title} - {Artist}",
                    track.Title, track.PrimaryArtist);
            }
        }

        if (matchedSongs.Count > 0)
        {
            // Cache matched tracks for configurable duration
            await _cache.SetAsync(matchedTracksKey, matchedSongs, CacheExtensions.SpotifyMatchedTracksTTL);
            _logger.LogInformation("✓ Cached {Matched}/{Total} matched tracks for {Playlist}",
                matchedSongs.Count, missingTracks.Count, playlistName);
        }
        else
        {
            _logger.LogInformation("No tracks matched for {Playlist}", playlistName);
        }
    }

    /// <summary>
    /// Pre-builds the playlist items cache for instant serving.
    /// This combines local Jellyfin tracks with external matched tracks in the correct Spotify order.
    /// PRIORITY: Local Jellyfin tracks FIRST, then external providers for unmatched tracks only.
    /// </summary>
    private async Task PreBuildPlaylistItemsCacheAsync(
        string playlistName,
        string? jellyfinPlaylistId,
        List<SpotifyPlaylistTrack> spotifyTracks,
        List<MatchedTrack> externalMatchedTracks,
        TimeSpan cacheExpiration,
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug("🔨 Pre-building playlist items cache for {Playlist}...", playlistName);

            if (string.IsNullOrEmpty(jellyfinPlaylistId))
            {
                _logger.LogError("No Jellyfin playlist ID configured for {Playlist}, cannot pre-build cache", playlistName);
                return;
            }

            // Get existing tracks from Jellyfin playlist
            using var scope = _serviceProvider.CreateScope();
            var proxyService = scope.ServiceProvider.GetService<JellyfinProxyService>();
            var responseBuilder = scope.ServiceProvider.GetService<JellyfinResponseBuilder>();
            var jellyfinSettings = scope.ServiceProvider.GetService<IOptions<JellyfinSettings>>()?.Value;

            if (proxyService == null || responseBuilder == null || jellyfinSettings == null)
            {
                _logger.LogWarning("Required services not available for pre-building cache");
                return;
            }

            var userId = jellyfinSettings.UserId;
            if (string.IsNullOrEmpty(userId))
            {
                _logger.LogError("No UserId configured, cannot pre-build playlist cache for {Playlist}", playlistName);
                return;
            }

            // Create authentication headers for background service call
            var headers = new HeaderDictionary();
            if (!string.IsNullOrEmpty(jellyfinSettings.ApiKey))
            {
                headers["X-Emby-Authorization"] = $"MediaBrowser Token=\"{jellyfinSettings.ApiKey}\"";
            }

            // Request all fields that clients typically need (not just MediaSources)
            var playlistItemsUrl = $"Playlists/{jellyfinPlaylistId}/Items?UserId={userId}&Fields={CachedPlaylistItemFields}";
            var (existingTracksResponse, statusCode) = await proxyService.GetJsonAsync(playlistItemsUrl, null, headers);

            if (statusCode != 200 || existingTracksResponse == null)
            {
                _logger.LogError("Failed to fetch Jellyfin playlist items for {Playlist}: HTTP {StatusCode}", playlistName, statusCode);
                return;
            }

            // Index Jellyfin items by title+artist for matching
            var jellyfinItemsByName = new Dictionary<string, JsonElement>();

            if (existingTracksResponse.RootElement.TryGetProperty("Items", out var items))
            {
                foreach (var item in items.EnumerateArray())
                {
                    // Ignore synthetic external stubs when building local match candidates.
                    // They belong to allstarr and should not be treated as local Jellyfin tracks.
                    if (item.TryGetProperty("ServerId", out var serverIdEl) &&
                        serverIdEl.ValueKind == JsonValueKind.String &&
                        string.Equals(serverIdEl.GetString(), "allstarr", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var title = item.TryGetProperty("Name", out var nameEl) ? nameEl.GetString() ?? "" : "";
                    var artist = "";
                    if (item.TryGetProperty("Artists", out var artistsEl) && artistsEl.GetArrayLength() > 0)
                    {
                        artist = artistsEl[0].GetString() ?? "";
                    }
                    else if (item.TryGetProperty("AlbumArtist", out var albumArtistEl))
                    {
                        artist = albumArtistEl.GetString() ?? "";
                    }

                    var key = $"{title}|{artist}".ToLowerInvariant();
                    if (!jellyfinItemsByName.ContainsKey(key))
                    {
                        jellyfinItemsByName[key] = item;
                    }
                }
            }

            // Build the final track list in correct Spotify order
            // PRIORITY: Local Jellyfin tracks FIRST, then external for unmatched only
            var finalItems = new List<Dictionary<string, object?>>();
            var usedJellyfinItems = new HashSet<string>();
            var matchedSpotifyIds = new HashSet<string>(); // Track which Spotify tracks got local matches
            var localUsedCount = 0;
            var externalUsedCount = 0;
            var manualExternalCount = 0;

            foreach (var spotifyTrack in spotifyTracks.OrderBy(t => t.Position))
            {
                if (cancellationToken.IsCancellationRequested) break;

                JsonElement? matchedJellyfinItem = null;
                string? matchedKey = null;

                // FIRST: Check for manual Jellyfin mapping
                var manualMappingKey = CacheKeyBuilder.BuildSpotifyManualMappingKey(playlistName, spotifyTrack.SpotifyId);
                var manualJellyfinId = await _cache.GetAsync<string>(manualMappingKey);

                if (!string.IsNullOrEmpty(manualJellyfinId))
                {
                    // Find the Jellyfin item by ID
                    foreach (var kvp in jellyfinItemsByName)
                    {
                        var item = kvp.Value;
                        if (item.TryGetProperty("Id", out var idEl) && idEl.GetString() == manualJellyfinId)
                        {
                            matchedJellyfinItem = item;
                            matchedKey = kvp.Key;
                            _logger.LogInformation("✓ Using manual Jellyfin mapping for {Title}: Jellyfin ID {Id}",
                                spotifyTrack.Title, manualJellyfinId);
                            break;
                        }
                    }

                    if (matchedJellyfinItem.HasValue)
                    {
                        // Use the raw Jellyfin item (preserves ALL metadata)
                        var itemDict = JsonSerializer.Deserialize<Dictionary<string, object?>>(matchedJellyfinItem.Value.GetRawText());
                        if (itemDict != null)
                        {
                            // Add Jellyfin ID to ProviderIds for easy identification
                            if (itemDict.TryGetValue("Id", out var jellyfinIdObj) && jellyfinIdObj != null)
                            {
                                var jellyfinId = jellyfinIdObj.ToString();
                                if (!string.IsNullOrEmpty(jellyfinId))
                                {
                                    if (!itemDict.ContainsKey("ProviderIds"))
                                    {
                                        itemDict["ProviderIds"] = new Dictionary<string, string>();
                                    }

                                    // Handle ProviderIds which might be a JsonElement or Dictionary
                                    Dictionary<string, string>? providerIds = null;

                                    if (itemDict["ProviderIds"] is Dictionary<string, string> dict)
                                    {
                                        providerIds = dict;
                                    }
                                    else if (itemDict["ProviderIds"] is JsonElement jsonEl && jsonEl.ValueKind == JsonValueKind.Object)
                                    {
                                        // Convert JsonElement to Dictionary
                                        providerIds = new Dictionary<string, string>();
                                        foreach (var prop in jsonEl.EnumerateObject())
                                        {
                                            providerIds[prop.Name] = prop.Value.GetString() ?? "";
                                        }
                                        // Replace the JsonElement with the Dictionary
                                        itemDict["ProviderIds"] = providerIds;
                                    }

                                    if (providerIds != null && !providerIds.ContainsKey("Jellyfin"))
                                    {
                                        providerIds["Jellyfin"] = jellyfinId;
                                        _logger.LogDebug("Added Jellyfin ID {JellyfinId} to manual mapped local track {Title}",
                                            jellyfinId, spotifyTrack.Title);
                                    }
                                }
                            }

                            ProviderIdsEnricher.EnsureSpotifyProviderIds(itemDict, spotifyTrack.SpotifyId,
                                spotifyTrack.AlbumId);

                            finalItems.Add(itemDict);
                            if (matchedKey != null)
                            {
                                usedJellyfinItems.Add(matchedKey);
                            }
                            matchedSpotifyIds.Add(spotifyTrack.SpotifyId); // Mark as locally matched
                            localUsedCount++;
                        }
                        continue; // Skip to next track
                    }
                }

                // SECOND: Check for external manual mapping
                var externalMappingKey = CacheKeyBuilder.BuildSpotifyExternalMappingKey(playlistName, spotifyTrack.SpotifyId);
                var externalMappingJson = await _cache.GetStringAsync(externalMappingKey);

                if (!string.IsNullOrEmpty(externalMappingJson))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(externalMappingJson);
                        var root = doc.RootElement;

                        string? provider = null;
                        string? externalId = null;

                        if (root.TryGetProperty("provider", out var providerEl))
                        {
                            provider = providerEl.GetString();
                        }

                        if (root.TryGetProperty("id", out var idEl))
                        {
                            externalId = idEl.GetString();
                        }

                        if (!string.IsNullOrEmpty(provider) &&
                            !string.IsNullOrEmpty(externalId) &&
                            ExternalTrackPlaybackPolicy.CanUseForPlayback(provider))
                        {
                            // Fetch full metadata from the provider instead of using minimal Spotify data
                            Song? externalSong = null;

                            try
                            {
                                using var metadataScope = _serviceProvider.CreateScope();
                                var metadataServiceForFetch = metadataScope.ServiceProvider.GetRequiredService<IMusicMetadataService>();
                                externalSong = await metadataServiceForFetch.GetSongAsync(provider, externalId);

                                if (externalSong != null)
                                {
                                    _logger.LogInformation("✓ Fetched full metadata for manual external mapping: {Title} by {Artist}",
                                        externalSong.Title, externalSong.Artist);
                                }
                                else
                                {
                                    _logger.LogError("Failed to fetch metadata for {Provider} ID {ExternalId}, using fallback",
                                        provider, externalId);
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Error fetching metadata for {Provider} ID {ExternalId}, using fallback",
                                    provider, externalId);
                            }

                            // Fallback to minimal metadata if fetch failed
                            if (externalSong == null)
                            {
                                externalSong = new Song
                                {
                                    Id = $"ext-{provider}-song-{externalId}",
                                    Title = spotifyTrack.Title,
                                    Artist = spotifyTrack.PrimaryArtist,
                                    Album = spotifyTrack.Album,
                                    Duration = spotifyTrack.DurationMs / 1000,
                                    Isrc = spotifyTrack.Isrc,
                                    IsLocal = false,
                                    ExternalProvider = provider,
                                    ExternalId = externalId
                                };
                            }

                            // Convert external song to Jellyfin item format and add to finalItems
                            var externalItem = responseBuilder.ConvertSongToJellyfinItem(externalSong);
                            ProviderIdsEnricher.EnsureSpotifyProviderIds(externalItem, spotifyTrack.SpotifyId,
                                spotifyTrack.AlbumId);

                            finalItems.Add(externalItem);
                            externalUsedCount++;
                            manualExternalCount++;
                            matchedSpotifyIds.Add(spotifyTrack.SpotifyId); // Mark as matched (external)

                            _logger.LogInformation("✓ Using manual external mapping for {Title}: {Provider} {ExternalId}",
                                spotifyTrack.Title, provider, externalId);
                            continue; // Skip to next track
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to process external manual mapping for {Title}", spotifyTrack.Title);
                    }
                }

                // THIRD: Try AGGRESSIVE fuzzy matching with local Jellyfin tracks (PRIORITY!)
                double bestScore = 0;

                foreach (var kvp in jellyfinItemsByName)
                {
                    if (usedJellyfinItems.Contains(kvp.Key)) continue;

                    var item = kvp.Value;
                    var title = item.TryGetProperty("Name", out var nameEl) ? nameEl.GetString() ?? "" : "";
                    var artist = "";
                    if (item.TryGetProperty("Artists", out var artistsEl) && artistsEl.GetArrayLength() > 0)
                    {
                        artist = artistsEl[0].GetString() ?? "";
                    }

                    // Use AGGRESSIVE matching with decorator stripping
                    var titleScore = FuzzyMatcher.CalculateSimilarityAggressive(spotifyTrack.Title, title);
                    var artistScore = FuzzyMatcher.CalculateSimilarity(spotifyTrack.PrimaryArtist, artist);

                    // Weight: 70% title, 30% artist (prioritize title matching)
                    var totalScore = (titleScore * 0.7) + (artistScore * 0.3);

                    // AGGRESSIVE: Accept score >= 40 (was 70)
                    // Also accept if artist matches well (70+) and title is decent (30+)
                    var isGoodMatch = totalScore >= 40 || (artistScore >= 70 && titleScore >= 30);

                    if (totalScore > bestScore && isGoodMatch)
                    {
                        bestScore = totalScore;
                        matchedJellyfinItem = item;
                        matchedKey = kvp.Key;
                    }
                }

                if (matchedJellyfinItem.HasValue)
                {
                    // Use the raw Jellyfin item (preserves ALL metadata)
                    var itemDict = JsonSerializer.Deserialize<Dictionary<string, object?>>(matchedJellyfinItem.Value.GetRawText());
                    if (itemDict != null)
                    {
                        // Add Jellyfin ID to ProviderIds for easy identification
                        if (itemDict.TryGetValue("Id", out var jellyfinIdObj) && jellyfinIdObj != null)
                        {
                            var jellyfinId = jellyfinIdObj.ToString();
                            if (!string.IsNullOrEmpty(jellyfinId))
                            {
                                if (!itemDict.ContainsKey("ProviderIds"))
                                {
                                    itemDict["ProviderIds"] = new Dictionary<string, string>();
                                }

                                // Handle ProviderIds which might be a JsonElement or Dictionary
                                Dictionary<string, string>? providerIds = null;

                                if (itemDict["ProviderIds"] is Dictionary<string, string> dict)
                                {
                                    providerIds = dict;
                                }
                                else if (itemDict["ProviderIds"] is JsonElement jsonEl && jsonEl.ValueKind == JsonValueKind.Object)
                                {
                                    // Convert JsonElement to Dictionary
                                    providerIds = new Dictionary<string, string>();
                                    foreach (var prop in jsonEl.EnumerateObject())
                                    {
                                        providerIds[prop.Name] = prop.Value.GetString() ?? "";
                                    }
                                    // Replace the JsonElement with the Dictionary
                                    itemDict["ProviderIds"] = providerIds;
                                }

                                if (providerIds != null)
                                {
                                    if (!providerIds.ContainsKey("Jellyfin"))
                                    {
                                        providerIds["Jellyfin"] = jellyfinId;
                                    }

                                    // Add Spotify ID for matching in track details endpoint
                                    if (!providerIds.ContainsKey("Spotify") && !string.IsNullOrEmpty(spotifyTrack.SpotifyId))
                                    {
                                        providerIds["Spotify"] = spotifyTrack.SpotifyId;
                                    }

                                    _logger.LogDebug("Fuzzy matched local track {Title} with Jellyfin ID {Id} (score: {Score:F1})",
                                        spotifyTrack.Title, jellyfinId, bestScore);
                                }
                            }
                        }

                        ProviderIdsEnricher.EnsureSpotifyProviderIds(itemDict, spotifyTrack.SpotifyId,
                            spotifyTrack.AlbumId);

                        finalItems.Add(itemDict);
                        if (matchedKey != null)
                        {
                            usedJellyfinItems.Add(matchedKey);
                        }
                        matchedSpotifyIds.Add(spotifyTrack.SpotifyId); // Mark as locally matched
                        localUsedCount++;
                    }
                }
                else
                {
                    // FOURTH: No local match - try to find external track (ONLY for unmatched tracks)
                    var matched = externalMatchedTracks.FirstOrDefault(t => t.SpotifyId == spotifyTrack.SpotifyId);
                    if (matched != null && matched.MatchedSong != null)
                    {
                        // Convert external song to Jellyfin item format
                        var externalItem = responseBuilder.ConvertSongToJellyfinItem(matched.MatchedSong);
                        ProviderIdsEnricher.EnsureSpotifyProviderIds(externalItem, spotifyTrack.SpotifyId,
                            spotifyTrack.AlbumId);

                        finalItems.Add(externalItem);
                        matchedSpotifyIds.Add(spotifyTrack.SpotifyId); // Mark as matched (external)
                        externalUsedCount++;

                        _logger.LogDebug("Using external match for {Title}: {Provider}",
                            spotifyTrack.Title, matched.MatchedSong.ExternalProvider);
                    }
                    // else: Track remains unmatched (not added to finalItems)
                }
            }

            if (finalItems.Count > 0)
            {
                // Enrich external tracks with genres from MusicBrainz
                if (externalUsedCount > 0)
                {
                    try
                    {
                        var genreEnrichment = _serviceProvider.GetService<GenreEnrichmentService>();
                        if (genreEnrichment != null)
                        {
                            _logger.LogDebug("🎨 Enriching {Count} external tracks with genres from MusicBrainz...", externalUsedCount);

                            // Extract external songs from externalMatchedTracks that were actually used
                            var usedExternalSpotifyIds = finalItems
                                .Where(item => item.TryGetValue("Id", out var idObj) &&
                                              idObj is string id && id.StartsWith("ext-"))
                                .Select(item =>
                                {
                                    // Try to get Spotify ID from ProviderIds
                                    if (item.TryGetValue("ProviderIds", out var providerIdsObj) && providerIdsObj is Dictionary<string, string> providerIds)
                                    {
                                        providerIds.TryGetValue("Spotify", out var spotifyId);
                                        return spotifyId;
                                    }
                                    return null;
                                })
                                .Where(id => !string.IsNullOrEmpty(id))
                                .ToHashSet();

                            var externalSongs = externalMatchedTracks
                                .Where(t => t.MatchedSong != null &&
                                           !t.MatchedSong.IsLocal &&
                                           usedExternalSpotifyIds.Contains(t.SpotifyId))
                                .Select(t => t.MatchedSong!)
                                .ToList();

                            // Enrich genres in parallel
                            await genreEnrichment.EnrichSongsGenresAsync(externalSongs);

                            // Update the genres in finalItems
                            foreach (var item in finalItems)
                            {
                                if (item.TryGetValue("Id", out var idObj) && idObj is string id && id.StartsWith("ext-"))
                                {
                                    // Find the corresponding song
                                    var song = externalSongs.FirstOrDefault(s => s.Id == id);
                                    if (song != null && !string.IsNullOrEmpty(song.Genre))
                                    {
                                        // Update Genres array
                                        item["Genres"] = new[] { song.Genre };

                                        // Update GenreItems array
                                        item["GenreItems"] = new[]
                                        {
                                            new Dictionary<string, object?>
                                            {
                                                ["Name"] = song.Genre,
                                                ["Id"] = $"genre-{song.Genre.ToLowerInvariant()}"
                                            }
                                        };

                                        _logger.LogDebug("✓ Enriched {Title} with genre: {Genre}", song.Title, song.Genre);
                                    }
                                }
                            }

                            _logger.LogInformation("✅ Genre enrichment complete for {Playlist}", playlistName);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to enrich genres for {Playlist}, continuing without genres", playlistName);
                    }
                }

                // Save to Redis cache with same expiration as matched tracks (until next cron run)
                var cacheKey = CacheKeyBuilder.BuildSpotifyPlaylistItemsKey(playlistName);
                await _cache.SetAsync(cacheKey, finalItems, cacheExpiration);

                // Save to file cache for persistence
                await SavePlaylistItemsToFileAsync(playlistName, finalItems);

                var manualMappingInfo = "";
                if (manualExternalCount > 0)
                {
                    manualMappingInfo = $" [Manual external: {manualExternalCount}]";
                }

                _logger.LogDebug("✅ Pre-built playlist cache for {Playlist}: {Total} tracks ({Local} LOCAL + {External} EXTERNAL){ManualInfo} - expires in {Hours:F1}h",
                    playlistName, finalItems.Count, localUsedCount, externalUsedCount, manualMappingInfo, cacheExpiration.TotalHours);
            }
            else
            {
                _logger.LogWarning("No items to cache for {Playlist}", playlistName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to pre-build playlist items cache for {Playlist}", playlistName);
        }
    }

    private async Task EnsurePlaylistItemsCacheAsync(
        string playlistName,
        string? jellyfinPlaylistId,
        List<SpotifyPlaylistTrack> spotifyTracks,
        List<MatchedTrack> matchedTracks,
        CancellationToken cancellationToken)
    {
        var itemsKey = CacheKeyBuilder.BuildSpotifyPlaylistItemsKey(playlistName);
        var existingItems = await _cache.GetAsync<List<Dictionary<string, object?>>>(itemsKey);
        if (existingItems is { Count: > 0 })
            return;

        _logger.LogInformation(
            "Rebuilding missing player playlist cache for {Playlist} from retained matches",
            playlistName);
        await PreBuildPlaylistItemsCacheAsync(
            playlistName,
            jellyfinPlaylistId,
            spotifyTracks,
            matchedTracks,
            TimeSpan.FromHours(24),
            cancellationToken);
    }

    /// <summary>
    /// Saves playlist items to file cache for persistence across restarts.
    /// </summary>
    private async Task SavePlaylistItemsToFileAsync(string playlistName, List<Dictionary<string, object?>> items)
    {
        try
        {
            var cacheDir = "/app/cache/spotify";
            Directory.CreateDirectory(cacheDir);

            var safeName = string.Join("_", playlistName.Split(Path.GetInvalidFileNameChars()));
            var filePath = Path.Combine(cacheDir, $"{safeName}_items.json");

            var json = JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true });
            await System.IO.File.WriteAllTextAsync(filePath, json);

            _logger.LogDebug("💾 Saved {Count} playlist items to file cache: {Path}", items.Count, filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save playlist items to file for {Playlist}", playlistName);
        }
    }

    /// <summary>
    /// Saves matched tracks to file cache for persistence across restarts.
    /// </summary>
    private async Task SaveMatchedTracksToFileAsync(string playlistName, List<MatchedTrack> matchedTracks)
    {
        try
        {
            var cacheDir = "/app/cache/spotify";
            Directory.CreateDirectory(cacheDir);

            var safeName = string.Join("_", playlistName.Split(Path.GetInvalidFileNameChars()));
            var filePath = Path.Combine(cacheDir, $"{safeName}_matched.json");

            var json = JsonSerializer.Serialize(matchedTracks, new JsonSerializerOptions { WriteIndented = true });
            await System.IO.File.WriteAllTextAsync(filePath, json);

            _logger.LogInformation("💾 Saved {Count} matched tracks to file cache: {Path}", matchedTracks.Count, filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save matched tracks to file for {Playlist}", playlistName);
        }
    }

    private static Song CreateLocalSongSnapshot(JsonElement item)
    {
        var runTimeTicks = item.TryGetProperty("RunTimeTicks", out var rtt) ? rtt.GetInt64() : 0;
        var song = new Song
        {
            Id = item.TryGetProperty("Id", out var idEl) ? idEl.GetString() ?? "" : "",
            Title = item.TryGetProperty("Name", out var name) ? name.GetString() ?? "" : "",
            Album = item.TryGetProperty("Album", out var album) ? album.GetString() ?? "" : "",
            AlbumId = item.TryGetProperty("AlbumId", out var albumId) ? albumId.GetString() : null,
            Duration = (int)(runTimeTicks / TimeSpan.TicksPerSecond),
            Track = item.TryGetProperty("IndexNumber", out var track) ? track.GetInt32() : null,
            DiscNumber = item.TryGetProperty("ParentIndexNumber", out var disc) ? disc.GetInt32() : null,
            Year = item.TryGetProperty("ProductionYear", out var year) ? year.GetInt32() : null,
            IsLocal = true
        };

        if (item.TryGetProperty("Artists", out var artists) && artists.GetArrayLength() > 0)
        {
            song.Artist = artists[0].GetString() ?? "";
        }
        else if (item.TryGetProperty("AlbumArtist", out var albumArtist))
        {
            song.Artist = albumArtist.GetString() ?? "";
        }

        JellyfinItemSnapshotHelper.StoreRawItemSnapshot(song, item);
        song.JellyfinMetadata ??= new Dictionary<string, object?>();
        if (item.TryGetProperty("MediaSources", out var mediaSources))
        {
            song.JellyfinMetadata["MediaSources"] = JsonSerializer.Deserialize<object>(mediaSources.GetRawText());
        }

        return song;
    }

}
