using allstarr.Models.Domain;
using allstarr.Models.Settings;
using allstarr.Models.Spotify;
using allstarr.Services.Common;
using allstarr.Services.Jellyfin;
using allstarr.Core.Matching;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Cronos;
using System.Text.Json;

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
public sealed class SpotifyPlaylistMatchingAdapter : IPlaylistMatchingAdapter
{
    public string ProviderId => "spotify";
    public bool Enabled => _spotifySettings.Enabled;
    public IReadOnlyList<PlaylistMatchingSchedule> Schedules =>
        _spotifySettings.Playlists.Select(playlist => new PlaylistMatchingSchedule(
            playlist.Name,
            string.IsNullOrWhiteSpace(playlist.SyncSchedule)
                ? "0 8 * * *"
                : playlist.SyncSchedule)).ToArray();
    private const string CachedPlaylistItemFields =
        "Genres,GenreItems,DateCreated,MediaSources,ParentId,People,Tags,SortName,UserData,ProviderIds";

    private readonly SpotifyImportSettings _spotifySettings;
    private readonly SpotifyApiSettings _spotifyApiSettings;
    private readonly IApplicationCache _cache;
    private readonly ITrackMatchRepository _trackMatchCommands;
    private readonly ILogger<SpotifyPlaylistMatchingAdapter> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly PlaylistPlayableSearchService _playableSearch;
    private readonly IConfiguration _configuration;
    private const int DelayBetweenSearchesMs = 150; // 150ms = ~6.6 searches/second to avoid rate limiting
    private const int BatchSize = 11; // Number of parallel searches (matches SquidWTF provider count)
    private const int MatchingSearchLimit = 24;
    private static readonly TimeSpan ExternalProviderSearchTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PlaylistMatchingTimeout = TimeSpan.FromMinutes(30);

    // Track last run time per playlist to prevent duplicate runs
    private readonly Dictionary<string, DateTime> _lastRunTimes = new();
    private readonly TimeSpan _minimumRunInterval = TimeSpan.FromMinutes(5); // Cooldown between runs

    public SpotifyPlaylistMatchingAdapter(
        IOptions<SpotifyImportSettings> spotifySettings,
        IOptions<SpotifyApiSettings> spotifyApiSettings,
        IApplicationCache cache,
        ITrackMatchRepository trackMatchCommands,
        IServiceProvider serviceProvider,
        PlaylistPlayableSearchService playableSearch,
        IConfiguration configuration,
        ILogger<SpotifyPlaylistMatchingAdapter> logger)
    {
        _spotifySettings = spotifySettings.Value;
        _spotifyApiSettings = spotifyApiSettings.Value;
        _cache = cache;
        _trackMatchCommands = trackMatchCommands;
        _serviceProvider = serviceProvider;
        _playableSearch = playableSearch;
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

    public async Task TriggerScheduledRebuildAsync(
        string playlistName,
        CancellationToken cancellationToken = default)
    {
        await TryRunSinglePlaylistRebuildWithCooldownAsync(
            playlistName,
            cancellationToken,
            trigger: "cron");
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
        await RecordSuccessfulPlaylistSyncAsync(playlist.Name);
        _logger.LogInformation("✓ Rebuild complete for {Playlist}", playlistName);
    }

    /// <summary>
    /// Matches tracks for a single playlist WITHOUT clearing cache or refreshing from Spotify.
    /// Used for lightweight re-matching when only local library has changed.
    /// </summary>
    private async Task MatchSinglePlaylistAsync(
        string playlistName,
        CancellationToken cancellationToken,
        bool enrichProviderBackups = false,
        Func<PlaylistMatchingProgress, CancellationToken, Task>? progress = null)
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
                    playlist.Name,
                    playlistFetcher,
                    metadataService,
                    cancellationToken,
                    enrichProviderBackups,
                    progress);
            }
            else
            {
                // Fall back to legacy mode
                await MatchPlaylistTracksLegacyAsync(
                    playlist.Name, metadataService, cancellationToken);
            }

            await ClearPlaylistImageCacheAsync(playlist);
            await RecordSuccessfulPlaylistSyncAsync(playlist.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error matching tracks for playlist {Playlist}", playlist.Name);
            throw;
        }
    }

    private async Task RecordSuccessfulPlaylistSyncAsync(string playlistName)
    {
        var completedAt = DateTimeOffset.UtcNow.ToString("O");
        await _cache.SetStringAsync(
            CacheKeyBuilder.BuildSpotifyPlaylistLastSuccessfulSyncKey(playlistName),
            completedAt,
            TimeSpan.FromDays(365));
    }

    private async Task ClearPlaylistImageCacheAsync(SpotifyPlaylistConfig playlist)
    {
        if (string.IsNullOrWhiteSpace(playlist.JellyfinId))
        {
            return;
        }

        var deletedCount = await _cache.DeleteByPatternAsync(
            CacheKeyBuilder.BuildJellyfinImagePattern(playlist.JellyfinId));
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
    public async Task TriggerMatchingAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Manual track matching triggered for all playlists (bypassing cron schedules)");
        await MatchAllPlaylistsAsync(null, cancellationToken);
    }

    public async Task TriggerMatchingAsync(
        Func<PlaylistMatchingProgress, CancellationToken, Task>? progress,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Manual track matching triggered for all playlists (bypassing cron schedules)");
        await MatchAllPlaylistsAsync(progress, cancellationToken);
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
        await MatchSinglePlaylistAsync(
            playlistName,
            CancellationToken.None,
            enrichProviderBackups: true);
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

    private async Task MatchAllPlaylistsAsync(
        Func<PlaylistMatchingProgress, CancellationToken, Task>? progress,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("=== STARTING TRACK MATCHING FOR ALL PLAYLISTS ===");

        var playlists = _spotifySettings.Playlists;
        if (playlists.Count == 0)
        {
            _logger.LogInformation("No playlists configured for matching");
            return;
        }

        var failed = new List<string>();
        var completed = 0;
        foreach (var playlist in playlists)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (progress != null)
            {
                await progress(
                    new PlaylistMatchingProgress(
                        "playlist-started",
                        $"Matching playlist {playlist.Name}.",
                        completed,
                        playlists.Count,
                        ProviderId,
                        playlist.Name),
                    cancellationToken);
            }

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(PlaylistMatchingTimeout);
                await MatchSinglePlaylistAsync(
                        playlist.Name,
                        timeout.Token,
                        enrichProviderBackups: true,
                        progress: progress)
                    .WaitAsync(timeout.Token);
                completed++;
                if (progress != null)
                {
                    await progress(
                        new PlaylistMatchingProgress(
                            "playlist-completed",
                            $"Finished matching playlist {playlist.Name}.",
                            completed,
                            playlists.Count,
                            ProviderId,
                            playlist.Name),
                        cancellationToken);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                failed.Add(playlist.Name);
                _logger.LogError(
                    "Track matching timed out for playlist {Playlist} after {TimeoutMinutes} minutes",
                    playlist.Name,
                    PlaylistMatchingTimeout.TotalMinutes);
                if (progress != null)
                {
                    await progress(
                        new PlaylistMatchingProgress(
                            "playlist-failed",
                            $"Playlist {playlist.Name} timed out.",
                            completed,
                            playlists.Count,
                            ProviderId,
                            playlist.Name),
                        cancellationToken);
                }
            }
            catch (Exception ex)
            {
                failed.Add(playlist.Name);
                _logger.LogError(ex, "Error matching tracks for playlist {Playlist}", playlist.Name);
                if (progress != null)
                {
                    await progress(
                        new PlaylistMatchingProgress(
                            "playlist-failed",
                            $"Playlist {playlist.Name} could not be matched.",
                            completed,
                            playlists.Count,
                            ProviderId,
                            playlist.Name),
                        cancellationToken);
                }
            }
        }

        if (failed.Count > 0)
        {
            throw new InvalidOperationException(
                $"Playlist matching failed for {failed.Count} playlist(s).");
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
        CancellationToken cancellationToken,
        bool enrichProviderBackups = false,
        Func<PlaylistMatchingProgress, CancellationToken, Task>? progress = null)
    {
        var matchedTracksKey = CacheKeyBuilder.BuildSpotifyMatchedTracksKey(playlistName);

        // Get playlist tracks with full metadata including ISRC and position
        var spotifyTracks = await playlistFetcher.GetPlaylistTracksAsync(playlistName);
        if (spotifyTracks.Count == 0)
        {
            _logger.LogWarning("No tracks found for {Playlist}, skipping matching", playlistName);
            return;
        }

        await _trackMatchCommands.EnsureSourceSnapshotsAsync(
            spotifyTracks.Select(track => new SourceTrackSeed(
                "spotify",
                track.SpotifyId,
                track.Title,
                track.PrimaryArtist,
                track.Album,
                track.DurationMs,
                track.Isrc,
                track.AlbumArtUrl,
                "spotify-playlist-v1")).ToArray(),
            cancellationToken);
        if (progress != null)
        {
            await progress(
                new PlaylistMatchingProgress(
                    "local-matching",
                    $"Scoring {spotifyTracks.Count} tracks against the indexed library.",
                    0,
                    spotifyTracks.Count,
                    ProviderId,
                    playlistName),
                cancellationToken);
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

        if (tracksToMatch.Count == 0 && !enrichProviderBackups)
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
        if (!enrichProviderBackups && existingMatched != null && existingMatched.Count > 0)
        {
            var trackIdsToMatch = tracksToMatch
                .Select(track => track.SpotifyId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var cachedIds = existingMatched
                .Select(match => match.SpotifyId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var hasUncachedSourceTracks = trackIdsToMatch.Any(id => !cachedIds.Contains(id));
            var hasLocalMatchesToReverify = existingMatched.Any(match =>
                trackIdsToMatch.Contains(match.SpotifyId) && match.MatchedSong?.IsLocal == true);
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

            if (hasUncachedSourceTracks)
            {
                _logger.LogInformation(
                    "Rebuilding matched track cache for {Playlist}: the source playlist contains tracks not present in the cache",
                    playlistName);
            }

            if (hasLocalMatchesToReverify)
            {
                _logger.LogInformation(
                    "Rebuilding matched track cache for {Playlist}: cached local tracks are no longer present in the target playlist and must be reverified",
                    playlistName);
            }

            if (!hasUncachedSourceTracks && !hasLocalMatchesToReverify &&
                !hasIncompleteLocalSnapshots && !hasPolicyBlockedExternalMatches)
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

        // PHASE 2: Match every source track through the provider-neutral durable
        // decision engine. Automatic, interactive, and rematch decisions now use
        // the same normalization, scoring, thresholds, and persisted candidates.
        var localMatches = new Dictionary<string, (Song JellyfinTrack, SpotifyPlaylistTrack SpotifyTrack, double Score)>();
        var durableLocalMatches = await _trackMatchCommands.MatchSourceTracksAsync(
            spotifyTracks.Select(track => new SourceTrackSeed(
                "spotify",
                track.SpotifyId,
                track.Title,
                track.PrimaryArtist,
                track.Album,
                track.DurationMs,
                track.Isrc,
                track.AlbumArtUrl,
                "spotify-playlist-v1")).ToArray(),
            $"playlist-match-{playlistName}",
            cancellationToken);
        foreach (var result in durableLocalMatches.Where(item =>
                     (item.State is TrackMatchReviewState.Accepted or TrackMatchReviewState.Pinned) &&
                     !string.IsNullOrWhiteSpace(item.LocalBackendItemId)))
        {
            var spotifyTrack = spotifyTracks.First(track =>
                track.SpotifyId.Equals(result.ExternalId, StringComparison.OrdinalIgnoreCase));
            var jellyfinTrack = jellyfinTracks.FirstOrDefault(track =>
                track.Id.Equals(result.LocalBackendItemId, StringComparison.OrdinalIgnoreCase)) ?? new Song
            {
                Id = result.LocalBackendItemId!,
                Title = result.Title ?? spotifyTrack.Title,
                Artist = result.Artist ?? spotifyTrack.PrimaryArtist,
                Album = result.Album ?? spotifyTrack.Album,
                Duration = result.DurationSeconds,
                Isrc = result.Isrc ?? spotifyTrack.Isrc,
                IsLocal = true
            };
            localMatches[result.ExternalId] =
                (jellyfinTrack, spotifyTrack, result.Confidence * 100d);
        }
        var usedSpotifyIds = localMatches.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

        _logger.LogInformation("✅ Matched {LocalCount}/{SpotifyCount} Spotify tracks to local Jellyfin tracks",
            localMatches.Count, spotifyTracks.Count);

        // PHASE 3: For remaining unmatched Spotify tracks, search external providers
        var unmatchedSpotifyTracks = spotifyTracks
            .Where(t => enrichProviderBackups || !usedSpotifyIds.Contains(t.SpotifyId))
            .GroupBy(t => t.SpotifyId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(track => track.Position).First())
            .ToList();

        _logger.LogInformation("🔍 Searching external providers for {Count} unique unmatched tracks",
            unmatchedSpotifyTracks.Count);

        // Snapshot the playback provider order once per phase so each track's
        // per-provider walk uses the same list. This matches the priority the
        // settings page advertises and keeps the walk deterministic.
        var playbackProviderList = _serviceProvider
            .GetRequiredService<ProviderStatusManager>()
            .GetEnabledPlaybackProviders();
        var playbackProviderRanks = playbackProviderList
            .Select((provider, index) => (provider, index))
            .ToDictionary(item => item.provider, item => item.index, StringComparer.OrdinalIgnoreCase);

        // Concrete services for the per-provider walk. Resolved once per
        // phase so each track's walk uses the same set of providers.
        var concreteServices = _serviceProvider
            .GetServices<IConcreteMetadataService>()
            .ToList();

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
            if (progress != null)
            {
                var activeTrack = batch[0];
                await progress(
                    new PlaylistMatchingProgress(
                        "provider-search",
                        $"Searching playback routes for tracks {batchStart}-{batchEnd}.",
                        i,
                        unmatchedSpotifyTracks.Count,
                        ProviderId,
                        playlistName,
                        $"{activeTrack.PrimaryArtist} - {activeTrack.Title}"),
                    cancellationToken);
            }

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

                    // Reuse durable provider identities before issuing another search.
                    var durableProjection = await _trackMatchCommands.GetSpotifyProjectionAsync(
                        spotifyTrack.SpotifyId,
                        trackCancellationToken);
                    var durableRoute = durableProjection.ProviderRoutes.FirstOrDefault();
                    if (durableRoute != null)
                    {
                        var mappedSong = await metadataService.GetSongAsync(
                            durableRoute.ProviderId,
                            durableRoute.ExternalId,
                            trackCancellationToken);

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
                            if (!enrichProviderBackups)
                            {
                                return (spotifyTrack, candidates);
                            }
                        }
                    }

                    // Per-provider walk in configured playback priority order.
                    // Local library is the implicit first stop; the fuzzy multiple
                    // search above already covered the local pass. We now walk the
                    // playback provider list, stop on the first acceptable match,
                    // and only fall back to title-only retries when no provider
                    // crossed the accept threshold.
                    var injectedSource = BuildInjectedSourceTrack(spotifyTrack);
                    var walk = await WalkProvidersForTrackAsync(
                        injectedSource,
                        spotifyTrack,
                        playbackProviderList,
                        concreteServices,
                        metadataService,
                        localFuzzy: null,
                        localFuzzyScore: null,
                        trackCancellationToken,
                        collectAllProviderMatches: enrichProviderBackups);

                    if (walk.AcceptedMatches.Count > 0)
                    {
                        foreach (var accepted in walk.AcceptedMatches)
                        {
                            candidates.Add((accepted.Song, accepted.Score, accepted.MatchType));
                        }
                        _logger.LogDebug(
                            "Per-provider walk accepted {AcceptedCount} route(s) for {Playlist} track #{Position}: {Title} (primary {Provider}, {MatchType}, score {Score:F1}, walked {Steps} step(s))",
                            walk.AcceptedMatches.Count,
                            playlistName,
                            spotifyTrack.Position,
                            spotifyTrack.Title,
                            walk.ProviderUsed,
                            walk.MatchType,
                            walk.Score,
                            walk.Walked.Count);
                    }
                    else if (walk.Walked.Count > 0)
                    {
                        _logger.LogDebug(
                            "Per-provider walk produced no accept for {Playlist} track #{Position}: {Title} (walked {Steps} provider(s): {Reasons})",
                            playlistName,
                            spotifyTrack.Position,
                            spotifyTrack.Title,
                            walk.Walked.Count,
                            string.Join(", ", walk.Walked.Select(step => $"{step.Provider}={step.Outcome}")));
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
            if (progress != null)
            {
                var completedTrack = batch[^1];
                await progress(
                    new PlaylistMatchingProgress(
                        "provider-search",
                        $"Finished route search for tracks {batchStart}-{batchEnd}.",
                        batchEnd,
                        unmatchedSpotifyTracks.Count,
                        ProviderId,
                        playlistName,
                        $"{completedTrack.PrimaryArtist} - {completedTrack.Title}"),
                    cancellationToken);
            }

            if (i + BatchSize < unmatchedSpotifyTracks.Count)
            {
                await Task.Delay(DelayBetweenSearchesMs, cancellationToken);
            }
        }

        // PHASE 4: Prefer the complete local music library, then select the first
        // acceptable external route in configured provider order. Repeated playlist
        // entries may intentionally reuse the same local or provider track.
        var externalAssignments = new Dictionary<string, (Song Song, double Score, string MatchType)>();

        // playbackProviderRanks is computed once at the start of phase 3 above.

        var persistedProviderRoutes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (spotifyTrack, song, score, matchType) in allCandidates
                     .Where(candidate => !candidate.MatchedSong.IsLocal)
                     .OrderBy(candidate => playbackProviderRanks.TryGetValue(
                         candidate.MatchedSong.ExternalProvider ?? string.Empty,
                         out var rank) ? rank : int.MaxValue)
                     .ThenByDescending(candidate => candidate.Score))
        {
            if (!ExternalTrackPlaybackPolicy.CanUseForPlayback(song.ExternalProvider, song.Id)) continue;

            var providerId = song.ExternalProvider ?? "Unknown";
            var routeKey = $"{spotifyTrack.SpotifyId}:{providerId}";
            if (!persistedProviderRoutes.Add(routeKey)) continue;

            var isPrimaryExternal = !localMatches.ContainsKey(spotifyTrack.SpotifyId)
                                    && !externalAssignments.ContainsKey(spotifyTrack.SpotifyId);
            if (!enrichProviderBackups && !isPrimaryExternal) continue;

            if (isPrimaryExternal)
            {
                externalAssignments[spotifyTrack.SpotifyId] = (song, score, matchType);
            }

            await _trackMatchCommands.PersistAutomatedAsync(
                "spotify",
                spotifyTrack.SpotifyId,
                new PersistAutomatedTrackMatchCommand(
                    "provider",
                    song.ExternalId ?? song.Id,
                    providerId,
                    Math.Clamp(score / 100d, 0, 1)),
                $"auto-provider-{spotifyTrack.SpotifyId}",
                cancellationToken);

            if (isPrimaryExternal)
            {
                if (matchType == "isrc") isrcMatches++;
                else fuzzyMatches++;
            }

            _logger.LogInformation(
                "  ✓ External {RouteRole}: {Title} → {Provider}:{ExternalId} (score: {Score:F1})",
                isPrimaryExternal ? "primary" : "backup",
                spotifyTrack.Title,
                providerId,
                song.ExternalId,
                score);
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
                // Jellyfin's SearchTerm tokenization can miss punctuation variants (for
                // example curly vs straight apostrophes) and can become too restrictive
                // when title and artist are combined. Search title variants first and
                // score the union locally; all queries remain Audio-only.
                var localItems = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
                foreach (var searchTerm in new[] { title, titleStripped, query }
                             .Where(term => !string.IsNullOrWhiteSpace(term))
                             .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var searchParams = new Dictionary<string, string>
                    {
                        ["searchTerm"] = searchTerm,
                        ["includeItemTypes"] = "Audio",
                        ["recursive"] = "true",
                        ["limit"] = "25",
                        ["fields"] = CachedPlaylistItemFields
                    };
                    var (searchResponse, _) = await proxyService.GetJsonAsyncInternal("Items", searchParams);
                    if (searchResponse == null ||
                        !searchResponse.RootElement.TryGetProperty("Items", out var searchItems)) continue;
                    foreach (var item in searchItems.EnumerateArray())
                    {
                        if (!item.TryGetProperty("Id", out var idElement)) continue;
                        var itemId = idElement.GetString();
                        if (!string.IsNullOrWhiteSpace(itemId)) localItems[itemId] = item.Clone();
                    }
                }

                if (localItems.Count > 0)
                {
                    var jellyfinModelMapper = scope.ServiceProvider.GetService<JellyfinModelMapper>();
                    var localResults = new List<Song>();
                    foreach (var item in localItems.Values)
                    {
                        if (jellyfinModelMapper != null)
                        {
                            localResults.Add(jellyfinModelMapper.ParseSong(item));
                            continue;
                        }
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
        var externalResults = await SearchPlayableSongsAsync(metadataService, query, MatchingSearchLimit, cancellationToken);
        if (externalResults.Count < MatchingSearchLimit / 2 && !string.Equals(query, title, StringComparison.Ordinal))
        {
            var existingSongKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var existing in externalResults)
            {
                existingSongKeys.Add(GetExternalMatchKey(existing));
            }

            var titleOnlyResults = await SearchPlayableSongsAsync(
                metadataService, title, MatchingSearchLimit, cancellationToken);
            foreach (var song in titleOnlyResults)
            {
                if (existingSongKeys.Add(GetExternalMatchKey(song)))
                {
                    externalResults.Add(song);
                }
            }
        }

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
        return metadataService is MultiProviderMetadataService multiProvider
            ? await multiProvider.FindPlayableSongByIsrcAsync(isrc, cancellationToken)
            : await metadataService.FindSongByIsrcAsync(isrc, cancellationToken);
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

            var results = await SearchPlayableSongsAsync(metadataService, query, MatchingSearchLimit);

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
        var currentSource = await _cache.GetAsync<SpotifyPlaylist>(
            CacheKeyBuilder.BuildSpotifyPlaylistKey(playlistName));

        // Check if we already have matched tracks cached
        var existingMatched = await _cache.GetAsync<List<Song>>(matchedTracksKey);
        if (existingMatched != null && existingMatched.Count > 0)
        {
            var playableMatched = existingMatched
                .Where(ExternalTrackPlaybackPolicy.CanUseForPlayback)
                .ToList();
            var blockedCount = existingMatched.Count - playableMatched.Count;
            var exactRetained = currentSource?.Tracks is { Count: > 0 }
                ? LegacyPlaylistMatchRecovery.ReconstructExact(currentSource.Tracks, playableMatched)
                : [];
            var sourceGenerationChanged = currentSource?.Tracks is { Count: > 0 } &&
                                          exactRetained.Count != playableMatched.Count;

            if (blockedCount == 0 && !sourceGenerationChanged)
            {
                if (exactRetained.Count > 0)
                {
                    await _cache.SetAsync(
                        CacheKeyBuilder.BuildSpotifyMatchedTracksKey(playlistName),
                        exactRetained,
                        CacheExtensions.SpotifyMatchedTracksTTL);
                }
                _logger.LogWarning("Playlist {Playlist} already has {Count} matched tracks cached, skipping",
                    playlistName, existingMatched.Count);
                await EnsureLegacyPlaylistItemsCacheAsync(playlistName, cancellationToken);
                return;
            }

            if (sourceGenerationChanged)
            {
                await _cache.DeleteAsync(matchedTracksKey);
                await _cache.DeleteAsync(CacheKeyBuilder.BuildSpotifyMatchedTracksKey(playlistName));
                _logger.LogInformation(
                    "Discarded {Count} retained matches for {Playlist} because the provider playlist generation changed",
                    playableMatched.Count,
                    playlistName);
            }

            if (!sourceGenerationChanged && playableMatched.Count > 0)
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

            if (blockedCount > 0)
            {
                _logger.LogWarning(
                    "Removed {BlockedCount} unavailable cached tracks from {Playlist}; rebuilding legacy matches",
                    blockedCount,
                    playlistName);
            }
        }

        // Get missing tracks
        var missingTracks = await _cache.GetAsync<List<MissingTrack>>(missingTracksKey);
        if (currentSource?.Tracks is { Count: > 0 } currentTracks)
        {
            var currentIds = currentTracks
                .Select(track => track.SpotifyId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missingSnapshotIsStale = missingTracks is not { Count: > 0 } ||
                                         missingTracks.Any(track => !currentIds.Contains(track.SpotifyId));

            if (missingSnapshotIsStale)
            {
                missingTracks = currentTracks
                    .OrderBy(track => track.Position)
                    .Select(track => track.ToMissingTrack())
                    .ToList();
                await _cache.SetAsync(
                    missingTracksKey,
                    missingTracks,
                    CacheExtensions.SpotifyPlaylistItemsTTL);
                _logger.LogInformation(
                    "Replaced stale missing-track snapshot for {Playlist} with {Count} tracks from the current provider generation",
                    playlistName,
                    missingTracks.Count);
            }
        }

        if (missingTracks == null || missingTracks.Count == 0)
        {
            _logger.LogDebug("No missing tracks found for {Playlist}, skipping matching", playlistName);
            return;
        }

        _logger.LogInformation("Matching {Count} tracks for {Playlist} (with rate limiting)",
            missingTracks.Count, playlistName);

        var matchedSongs = new List<Song>();
        var orderedMatches = new List<MatchedTrack>();
        var playlist = _spotifySettings.Playlists.FirstOrDefault(item =>
            item.Name.Equals(playlistName, StringComparison.OrdinalIgnoreCase));
        var legacySourceTracks = missingTracks.Select((track, position) => new SpotifyPlaylistTrack
        {
            SpotifyId = track.SpotifyId,
            Position = position,
            Title = track.Title,
            Album = track.Album,
            Artists = track.Artists
        }).ToList();
        var matchCount = 0;

        for (var missingPosition = 0; missingPosition < missingTracks.Count; missingPosition++)
        {
            var track = missingTracks[missingPosition];
            if (cancellationToken.IsCancellationRequested) break;

            try
            {
                var query = $"{track.Title} {track.PrimaryArtist}";
                var results = await SearchPlayableSongsAsync(metadataService, query, 5, cancellationToken);

                if (results.Count > 0)
                {
                    // Fuzzy match to find best result
                    // Check that ALL artists match (not just some)
                    var bestMatch = results
                        .Where(ExternalTrackPlaybackPolicy.CanUseForPlayback)
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
                        orderedMatches.Add(new MatchedTrack
                        {
                            Position = missingPosition,
                            SpotifyId = track.SpotifyId,
                            SpotifyTitle = track.Title,
                            SpotifyArtist = track.PrimaryArtist,
                            MatchType = "legacy-provider-search",
                            MatchedSong = bestMatch.Song
                        });
                        matchCount++;

                        if (matchCount % 10 == 0)
                        {
                            _logger.LogInformation("Matched {Count}/{Total} tracks for {Playlist}",
                                matchCount, missingTracks.Count, playlistName);
                            await _cache.SetAsync(
                                CacheKeyBuilder.BuildSpotifyMatchedTracksKey(playlistName),
                                orderedMatches,
                                CacheExtensions.SpotifyMatchedTracksTTL);
                            if (!string.IsNullOrWhiteSpace(playlist?.JellyfinId))
                            {
                                await PreBuildPlaylistItemsCacheAsync(
                                    playlistName,
                                    playlist.JellyfinId,
                                    legacySourceTracks,
                                    orderedMatches,
                                    TimeSpan.FromHours(24),
                                    cancellationToken,
                                    includeUnorderedLocalItems: true);
                            }
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

            if (orderedMatches.Count > 0 && !string.IsNullOrWhiteSpace(playlist?.JellyfinId))
            {
                await _cache.SetAsync(
                    CacheKeyBuilder.BuildSpotifyMatchedTracksKey(playlistName),
                    orderedMatches,
                    CacheExtensions.SpotifyMatchedTracksTTL);
                await PreBuildPlaylistItemsCacheAsync(
                    playlistName,
                    playlist.JellyfinId,
                    legacySourceTracks,
                    orderedMatches,
                    TimeSpan.FromHours(24),
                    cancellationToken,
                    includeUnorderedLocalItems: true);
            }
        }
        else
        {
            _logger.LogInformation("No tracks matched for {Playlist}", playlistName);
        }
    }

    /// <summary>
    /// Adapter that converts a <see cref="SpotifyPlaylistTrack"/> into a
    /// provider-agnostic <see cref="InjectedSourceTrack"/>. The matching
    /// walker only needs the source descriptor; it does not care whether
    /// the source is Spotify, Apple MusicKit, or any other injected
    /// provider.
    /// </summary>
    private static InjectedSourceTrack BuildInjectedSourceTrack(SpotifyPlaylistTrack track) =>
        new(
            SourceId: track.SpotifyId ?? string.Empty,
            SourceProvider: "spotify",
            Title: track.Title ?? string.Empty,
            Artists: track.Artists is { Count: > 0 }
                ? track.Artists
                : new List<string> { track.PrimaryArtist ?? string.Empty },
            Isrc: string.IsNullOrWhiteSpace(track.Isrc) ? null : track.Isrc,
            DurationMs: track.DurationMs,
            Album: track.Album,
            AlbumArtUrl: track.AlbumArtUrl,
            Position: track.Position);

    /// <summary>
    /// Per-track, per-provider walk for an unmatched source track. Wraps
    /// the provider-agnostic <see cref="PerProviderTrackWalker"/> with the
    /// concrete services available in this scope and the snapshot of the
    /// configured playback provider list. The Spotify path uses this
    /// directly; Apple MusicKit (and any future injected source) can use
    /// the same walker.
    /// </summary>
    private async Task<PerProviderMatchResult> WalkProvidersForTrackAsync(
        InjectedSourceTrack source,
        SpotifyPlaylistTrack spotifyTrack,
        IReadOnlyList<string> playbackProviders,
        IReadOnlyList<IConcreteMetadataService> concreteServices,
        IMusicMetadataService metadataService,
        Song? localFuzzy,
        double? localFuzzyScore,
        CancellationToken cancellationToken,
        bool collectAllProviderMatches = false)
    {
        var walker = new PerProviderTrackWalker(
            concreteServices,
            new PerProviderAcceptThresholds(),
            _logger,
            MatchingSearchLimit);

        return await walker.WalkAsync(
            source,
            playbackProviders,
            localFuzzy,
            localFuzzyScore,
            cancellationToken,
            collectAllProviderMatches);
    }

    private async Task<List<Song>> SearchPlayableSongsAsync(
        IMusicMetadataService metadataService,
        string query,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var requestedLimit = Math.Max(1, limit);

        var scoped = await _playableSearch.SearchAsync(query, requestedLimit, cancellationToken);
        if (scoped is null)
        {
            return metadataService is MultiProviderMetadataService multiProvider
                ? await multiProvider.SearchPlayableSongsAsync(query, requestedLimit, cancellationToken)
                : await SearchAndFilterPlayableSongsAsync(metadataService, query, requestedLimit, cancellationToken);
        }

        var scopedResults = scoped.ToList();
        if (scopedResults.Count >= requestedLimit || metadataService is not MultiProviderMetadataService)
        {
            return scopedResults;
        }

        var fallback = await ((MultiProviderMetadataService)metadataService).SearchPlayableSongsAsync(
            query, requestedLimit, cancellationToken);
        if (fallback.Count == 0)
        {
            return scopedResults;
        }

        var merged = MergeUniqueByExternalIdentity(scopedResults, fallback)
            .Take(requestedLimit)
            .ToList();
        if (merged.Count != scopedResults.Count)
        {
            _logger.LogDebug("Playlist search fallback enriched scoped results for '{Query}' with {Added} additional candidates",
                query, merged.Count - scopedResults.Count);
        }
        return merged;

        static List<Song> MergeUniqueByExternalIdentity(List<Song> first, List<Song> second)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var merged = new List<Song>(first.Count + second.Count);
            foreach (var song in first.Concat(second))
            {
                var key = GetExternalMatchKey(song);
                if (seen.Add(key))
                {
                    merged.Add(song);
                }
            }
            return merged;
        }
    }

    private static string GetExternalMatchKey(Song song)
    {
        var provider = (song.ExternalProvider ?? "unknown").Trim().ToLowerInvariant();
        var externalId = song.ExternalId ?? string.Empty;
        var fallbackId = song.Id ?? string.Empty;
        return $"{provider}:{(!string.IsNullOrWhiteSpace(externalId) ? externalId : fallbackId)}";
    }

    private static async Task<List<Song>> SearchAndFilterPlayableSongsAsync(
        IMusicMetadataService metadataService,
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        var results = await metadataService.SearchSongsAsync(query, limit, cancellationToken);
        return results.Where(ExternalTrackPlaybackPolicy.CanUseForPlayback).ToList();
    }

    private async Task EnsureLegacyPlaylistItemsCacheAsync(
        string playlistName,
        CancellationToken cancellationToken)
    {
        var source = await _cache.GetAsync<SpotifyPlaylist>(
            CacheKeyBuilder.BuildSpotifyPlaylistKey(playlistName));
        var retainedMatches = await _cache.GetAsync<List<MatchedTrack>>(
            CacheKeyBuilder.BuildSpotifyMatchedTracksKey(playlistName));
        if (retainedMatches is not { Count: > 0 } && source?.Tracks is { Count: > 0 })
        {
            var legacySongs = await _cache.GetAsync<List<Song>>(
                CacheKeyBuilder.BuildSpotifyLegacyMatchedTracksKey(playlistName));
            if (legacySongs is { Count: > 0 })
            {
                retainedMatches = LegacyPlaylistMatchRecovery.ReconstructExact(
                    source.Tracks,
                    legacySongs);
                if (retainedMatches.Count > 0)
                {
                    await _cache.SetAsync(
                        CacheKeyBuilder.BuildSpotifyMatchedTracksKey(playlistName),
                        retainedMatches,
                        CacheExtensions.SpotifyMatchedTracksTTL);
                    _logger.LogInformation(
                        "Recovered {Count} exact ordered matches for legacy playlist {Playlist}",
                        retainedMatches.Count,
                        playlistName);
                }
            }
        }
        var playlist = _spotifySettings.Playlists.FirstOrDefault(item =>
            item.Name.Equals(playlistName, StringComparison.OrdinalIgnoreCase));

        if (source?.Tracks is not { Count: > 0 } ||
            retainedMatches is not { Count: > 0 } ||
            string.IsNullOrWhiteSpace(playlist?.JellyfinId))
        {
            _logger.LogWarning(
                "Could not rebuild missing player playlist cache for {Playlist}: retained source, ordered matches, or Jellyfin playlist identity is unavailable",
                playlistName);
            return;
        }

        await EnsurePlaylistItemsCacheAsync(
            playlistName,
            playlist.JellyfinId,
            source.Tracks,
            retainedMatches,
            cancellationToken);
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
        CancellationToken cancellationToken,
        bool includeUnorderedLocalItems = false)
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
                    var syntheticItem = item.TryGetProperty("Id", out var itemIdElement) &&
                                        itemIdElement.ValueKind == JsonValueKind.String &&
                                        itemIdElement.GetString()?.StartsWith(
                                            "ext-", StringComparison.OrdinalIgnoreCase) == true;
                    var legacySyntheticItem = item.TryGetProperty("ServerId", out var serverIdEl) &&
                                              serverIdEl.ValueKind == JsonValueKind.String &&
                                              string.Equals(serverIdEl.GetString(), "allstarr", StringComparison.OrdinalIgnoreCase);
                    if (syntheticItem || legacySyntheticItem)
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

            if (includeUnorderedLocalItems)
            {
                foreach (var (key, item) in jellyfinItemsByName)
                {
                    var itemDictionary = JsonSerializer.Deserialize<Dictionary<string, object?>>(item.GetRawText());
                    if (itemDictionary == null) continue;
                    finalItems.Add(itemDictionary);
                    usedJellyfinItems.Add(key);
                    localUsedCount++;
                }
            }

            foreach (var spotifyTrack in spotifyTracks.OrderBy(t => t.Position))
            {
                if (cancellationToken.IsCancellationRequested) break;

                JsonElement? matchedJellyfinItem = null;
                string? matchedKey = null;
                var durableProjection = await _trackMatchCommands.GetSpotifyProjectionAsync(
                    spotifyTrack.SpotifyId,
                    cancellationToken);

                // PostgreSQL owns manual decisions. Playlist-scoped compatibility keys are no
                // longer consulted once the source snapshot has been seeded.
                var manualJellyfinId = durableProjection.LocalIsManual
                    ? durableProjection.LocalBackendItemId
                    : null;

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

                // SECOND: PostgreSQL is the sole authority for manual provider routes.
                var manualRoute = durableProjection.ProviderRoutes
                    .FirstOrDefault(route => route.IsManual);
                var externalMappingJson = manualRoute is null
                    ? null
                    : JsonSerializer.Serialize(new
                    {
                        provider = manualRoute.ProviderId,
                        id = manualRoute.ExternalId
                    });

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

                // Save to the shared cache with the matched-track expiry.
                var cacheKey = CacheKeyBuilder.BuildSpotifyPlaylistItemsKey(playlistName);
                await _cache.SetAsync(cacheKey, finalItems, cacheExpiration);

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
