using allstarr.Models.Domain;
using allstarr.Models.Settings;
using allstarr.Models.Spotify;
using allstarr.Services.Common;
using allstarr.Services.Jellyfin;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
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
/// </summary>
public class SpotifyTrackMatchingService : BackgroundService
{
    private readonly SpotifyImportSettings _spotifySettings;
    private readonly SpotifyApiSettings _spotifyApiSettings;
    private readonly RedisCacheService _cache;
    private readonly ILogger<SpotifyTrackMatchingService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private const int DelayBetweenSearchesMs = 150; // 150ms = ~6.6 searches/second to avoid rate limiting
    private const int BatchSize = 11; // Number of parallel searches (matches SquidWTF provider count)
    private DateTime _lastMatchingRun = DateTime.MinValue;
    private readonly TimeSpan _minimumMatchingInterval = TimeSpan.FromMinutes(5); // Don't run more than once per 5 minutes

    public SpotifyTrackMatchingService(
        IOptions<SpotifyImportSettings> spotifySettings,
        IOptions<SpotifyApiSettings> spotifyApiSettings,
        RedisCacheService cache,
        IServiceProvider serviceProvider,
        ILogger<SpotifyTrackMatchingService> logger)
    {
        _spotifySettings = spotifySettings.Value;
        _spotifyApiSettings = spotifyApiSettings.Value;
        _cache = cache;
        _serviceProvider = serviceProvider;
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
        _logger.LogInformation("SpotifyTrackMatchingService: Starting up...");
        
        if (!_spotifySettings.Enabled)
        {
            _logger.LogInformation("Spotify playlist injection is DISABLED, matching service will not run");
            return;
        }
        
        var matchMode = _spotifyApiSettings.Enabled && _spotifyApiSettings.PreferIsrcMatching 
            ? "ISRC-preferred" : "fuzzy";
        _logger.LogInformation("Matching mode: {Mode}", matchMode);

        // Wait a bit for the fetcher to run first
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);

        // Run once on startup to match any existing missing tracks
        try
        {
            _logger.LogInformation("Running initial track matching on startup");
            await MatchAllPlaylistsAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during startup track matching");
        }

        // Now start the periodic matching loop
        while (!stoppingToken.IsCancellationRequested)
        {
            // Wait for configured interval before next run (default 24 hours)
            var intervalHours = _spotifySettings.MatchingIntervalHours;
            if (intervalHours <= 0)
            {
                _logger.LogInformation("Periodic matching disabled (MatchingIntervalHours = {Hours}), only startup run will execute", intervalHours);
                break; // Exit loop - only run once on startup
            }
            
            await Task.Delay(TimeSpan.FromHours(intervalHours), stoppingToken);
            
            try
            {
                await MatchAllPlaylistsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in track matching service");
            }
        }
    }

    /// <summary>
    /// Public method to trigger matching manually for all playlists (called from controller).
    /// </summary>
    public async Task TriggerMatchingAsync()
    {
        _logger.LogInformation("Manual track matching triggered for all playlists");
        await MatchAllPlaylistsAsync(CancellationToken.None);
    }
    
    /// <summary>
    /// Public method to trigger matching for a specific playlist (called from controller).
    /// </summary>
    public async Task TriggerMatchingForPlaylistAsync(string playlistName)
    {
        _logger.LogInformation("Manual track matching triggered for playlist: {Playlist}", playlistName);
        
        var playlist = _spotifySettings.Playlists
            .FirstOrDefault(p => p.Name.Equals(playlistName, StringComparison.OrdinalIgnoreCase));
        
        if (playlist == null)
        {
            _logger.LogWarning("Playlist {Playlist} not found in configuration", playlistName);
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
                    playlist.Name, playlistFetcher, metadataService, CancellationToken.None);
            }
            else
            {
                // Fall back to legacy mode
                await MatchPlaylistTracksLegacyAsync(
                    playlist.Name, metadataService, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error matching tracks for playlist {Playlist}", playlist.Name);
            throw;
        }
    }

    private async Task MatchAllPlaylistsAsync(CancellationToken cancellationToken)
    {
        // Check if we've run too recently (cooldown period)
        var timeSinceLastRun = DateTime.UtcNow - _lastMatchingRun;
        if (timeSinceLastRun < _minimumMatchingInterval)
        {
            _logger.LogInformation("Skipping track matching - last run was {Seconds}s ago (minimum interval: {MinSeconds}s)", 
                (int)timeSinceLastRun.TotalSeconds, (int)_minimumMatchingInterval.TotalSeconds);
            return;
        }
        
        _logger.LogInformation("=== STARTING TRACK MATCHING ===");
        _lastMatchingRun = DateTime.UtcNow;
        
        var playlists = _spotifySettings.Playlists;
        if (playlists.Count == 0)
        {
            _logger.LogInformation("No playlists configured for matching");
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

        foreach (var playlist in playlists)
        {
            if (cancellationToken.IsCancellationRequested) break;
            
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
            }
        }
        
        _logger.LogInformation("=== FINISHED TRACK MATCHING ===");
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
        var matchedTracksKey = $"spotify:matched:ordered:{playlistName}";
        
        // Get playlist tracks with full metadata including ISRC and position
        var spotifyTracks = await playlistFetcher.GetPlaylistTracksAsync(playlistName);
        if (spotifyTracks.Count == 0)
        {
            _logger.LogInformation("No tracks found for {Playlist}, skipping matching", playlistName);
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
                        _logger.LogWarning("No UserId configured - may not be able to fetch existing playlist tracks for {Playlist}", playlistName);
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
                    _logger.LogWarning(ex, "Could not fetch existing Jellyfin tracks for {Playlist}, will match all tracks", playlistName);
                }
            }
        }
        
        // Filter to only tracks not already in Jellyfin
        var tracksToMatch = spotifyTracks
            .Where(t => !existingSpotifyIds.Contains(t.SpotifyId))
            .ToList();
        
        if (tracksToMatch.Count == 0)
        {
            _logger.LogInformation("All {Count} tracks for {Playlist} already exist in Jellyfin, skipping matching", 
                spotifyTracks.Count, playlistName);
            return;
        }
        
        _logger.LogInformation("Matching {ToMatch}/{Total} tracks for {Playlist} (skipping {Existing} already in Jellyfin, ISRC: {IsrcEnabled}, AGGRESSIVE MODE)", 
            tracksToMatch.Count, spotifyTracks.Count, playlistName, existingSpotifyIds.Count, _spotifyApiSettings.PreferIsrcMatching);
        
        // Check cache - use snapshot/timestamp to detect changes
        var existingMatched = await _cache.GetAsync<List<MatchedTrack>>(matchedTracksKey);
        
        // CRITICAL: Skip matching if cache exists and is valid
        // Only re-match if cache is missing OR if we detect manual mappings that need to be applied
        if (existingMatched != null && existingMatched.Count > 0)
        {
            // Check if we have NEW manual mappings that aren't in the cache
            var hasNewManualMappings = false;
            foreach (var track in tracksToMatch)
            {
                // Check if this track has a manual mapping but isn't in the cached results
                var manualMappingKey = $"spotify:manual-map:{playlistName}:{track.SpotifyId}";
                var manualMapping = await _cache.GetAsync<string>(manualMappingKey);
                
                var externalMappingKey = $"spotify:external-map:{playlistName}:{track.SpotifyId}";
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
            
            if (!hasNewManualMappings)
            {
                _logger.LogInformation("✓ Playlist {Playlist} already has {Count} matched tracks cached (skipping {ToMatch} new tracks), no re-matching needed", 
                    playlistName, existingMatched.Count, tracksToMatch.Count);
                return;
            }
            
            _logger.LogInformation("New manual mappings detected for {Playlist}, rebuilding cache to apply them", playlistName);
        }

        var matchedTracks = new List<MatchedTrack>();
        var isrcMatches = 0;
        var fuzzyMatches = 0;
        var noMatch = 0;
        
        // GREEDY ASSIGNMENT: Collect all possible matches first, then assign optimally
        var allCandidates = new List<(SpotifyPlaylistTrack SpotifyTrack, Song MatchedSong, double Score, string MatchType)>();

        // Process tracks in batches for parallel searching
        var orderedTracks = tracksToMatch.OrderBy(t => t.Position).ToList();
        for (int i = 0; i < orderedTracks.Count; i += BatchSize)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var batch = orderedTracks.Skip(i).Take(BatchSize).ToList();
            _logger.LogDebug("Processing batch {Start}-{End} of {Total}", 
                i + 1, Math.Min(i + BatchSize, orderedTracks.Count), orderedTracks.Count);

            // Process all tracks in this batch in parallel
            var batchTasks = batch.Select(async spotifyTrack =>
            {
                try
                {
                    var candidates = new List<(Song Song, double Score, string MatchType)>();
                    
                    // Try ISRC match first if available and enabled
                    if (_spotifyApiSettings.PreferIsrcMatching && !string.IsNullOrEmpty(spotifyTrack.Isrc))
                    {
                        var isrcSong = await TryMatchByIsrcAsync(spotifyTrack.Isrc, metadataService);
                        if (isrcSong != null)
                        {
                            candidates.Add((isrcSong, 100.0, "isrc"));
                        }
                    }
                    
                    // Always try fuzzy matching to get more candidates
                    var fuzzySongs = await TryMatchByFuzzyMultipleAsync(
                        spotifyTrack.Title, 
                        spotifyTrack.Artists, 
                        metadataService);
                    
                    foreach (var (song, score) in fuzzySongs)
                    {
                        candidates.Add((song, score, "fuzzy"));
                    }
                    
                    return (spotifyTrack, candidates);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to match track: {Title} - {Artist}", 
                        spotifyTrack.Title, spotifyTrack.PrimaryArtist);
                    return (spotifyTrack, new List<(Song, double, string)>());
                }
            }).ToList();

            // Wait for all tracks in this batch to complete
            var batchResults = await Task.WhenAll(batchTasks);
            
            // Collect all candidates
            foreach (var (spotifyTrack, candidates) in batchResults)
            {
                foreach (var (song, score, matchType) in candidates)
                {
                    allCandidates.Add((spotifyTrack, song, score, matchType));
                }
            }

            // Rate limiting between batches
            if (i + BatchSize < orderedTracks.Count)
            {
                await Task.Delay(DelayBetweenSearchesMs, cancellationToken);
            }
        }
        
        // GREEDY ASSIGNMENT: Assign each Spotify track to its best unique match
        var usedSongIds = new HashSet<string>();
        var assignments = new Dictionary<string, (Song Song, double Score, string MatchType)>();
        
        // Sort candidates by score (highest first)
        var sortedCandidates = allCandidates
            .OrderByDescending(c => c.Score)
            .ToList();
        
        foreach (var (spotifyTrack, song, score, matchType) in sortedCandidates)
        {
            // Skip if this Spotify track already has a match
            if (assignments.ContainsKey(spotifyTrack.SpotifyId))
                continue;
            
            // Skip if this song is already used
            if (usedSongIds.Contains(song.Id))
                continue;
            
            // Assign this match
            assignments[spotifyTrack.SpotifyId] = (song, score, matchType);
            usedSongIds.Add(song.Id);
        }
        
        // Build final matched tracks list
        foreach (var spotifyTrack in orderedTracks)
        {
            if (assignments.TryGetValue(spotifyTrack.SpotifyId, out var match))
            {
                var matched = new MatchedTrack
                {
                    Position = spotifyTrack.Position,
                    SpotifyId = spotifyTrack.SpotifyId,
                    SpotifyTitle = spotifyTrack.Title,
                    SpotifyArtist = spotifyTrack.PrimaryArtist,
                    Isrc = spotifyTrack.Isrc,
                    MatchType = match.MatchType,
                    MatchedSong = match.Song
                };
                
                matchedTracks.Add(matched);
                
                if (match.MatchType == "isrc") isrcMatches++;
                else if (match.MatchType == "fuzzy") fuzzyMatches++;
                
                _logger.LogDebug("  #{Position} {Title} - {Artist} → {MatchType} match (score: {Score:F1}): {MatchedTitle}",
                    spotifyTrack.Position, spotifyTrack.Title, spotifyTrack.PrimaryArtist,
                    match.MatchType, match.Score, match.Song.Title);
            }
            else
            {
                noMatch++;
                _logger.LogDebug("  #{Position} {Title} - {Artist} → no match",
                    spotifyTrack.Position, spotifyTrack.Title, spotifyTrack.PrimaryArtist);
            }
        }

        if (matchedTracks.Count > 0)
        {
            // Cache matched tracks with position data
            await _cache.SetAsync(matchedTracksKey, matchedTracks, TimeSpan.FromHours(1));
            
            // Save matched tracks to file for persistence across restarts
            await SaveMatchedTracksToFileAsync(playlistName, matchedTracks);
            
            // Also update legacy cache for backward compatibility
            var legacyKey = $"spotify:matched:{playlistName}";
            var legacySongs = matchedTracks.OrderBy(t => t.Position).Select(t => t.MatchedSong).ToList();
            await _cache.SetAsync(legacyKey, legacySongs, TimeSpan.FromHours(1));
            
            _logger.LogInformation(
                "✓ Cached {Matched}/{Total} tracks for {Playlist} via GREEDY ASSIGNMENT (ISRC: {Isrc}, Fuzzy: {Fuzzy}, No match: {NoMatch}) - manual mappings will be applied next", 
                matchedTracks.Count, tracksToMatch.Count, playlistName, isrcMatches, fuzzyMatches, noMatch);
            
            // Pre-build playlist items cache for instant serving
            // This is what makes the UI show all matched tracks at once
            await PreBuildPlaylistItemsCacheAsync(playlistName, playlistConfig?.JellyfinId, spotifyTracks, matchedTracks, cancellationToken);
        }
        else
        {
            _logger.LogInformation("No tracks matched for {Playlist}", playlistName);
        }
    }
    
    /// <summary>
    /// Returns multiple candidate matches with scores for greedy assignment.
    /// FOLLOWS OPTIMAL ORDER:
    /// 1. Strip decorators (done in FuzzyMatcher)
    /// 2. Substring matching (done in FuzzyMatcher)
    /// 3. Levenshtein distance (done in FuzzyMatcher)
    /// This method just collects candidates; greedy assignment happens later.
    /// </summary>
    private async Task<List<(Song Song, double Score)>> TryMatchByFuzzyMultipleAsync(
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
            
            if (results.Count == 0) return new List<(Song, double)>();
            
            // STEP 2-3: Score all results (substring + Levenshtein already in CalculateSimilarityAggressive)
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
                .Where(x => 
                    x.TotalScore >= 40 || 
                    (x.ArtistScore >= 70 && x.TitleScore >= 30) ||
                    x.TitleScore >= 85)
                .OrderByDescending(x => x.TotalScore)
                .Select(x => (x.Song, x.TotalScore))
                .ToList();
            
            return scoredResults;
        }
        catch
        {
            return new List<(Song, double)>();
        }
    }
    
    /// <summary>
    /// Attempts to match a track by ISRC using provider search.
    /// </summary>
    private async Task<Song?> TryMatchByIsrcAsync(string isrc, IMusicMetadataService metadataService)
    {
        try
        {
            // Search by ISRC directly - most providers support this
            var results = await metadataService.SearchSongsAsync($"isrc:{isrc}", limit: 1);
            if (results.Count > 0 && results[0].Isrc == isrc)
            {
                return results[0];
            }
            
            // Some providers may not support isrc: prefix, try without
            results = await metadataService.SearchSongsAsync(isrc, limit: 5);
            var exactMatch = results.FirstOrDefault(r => 
                !string.IsNullOrEmpty(r.Isrc) && 
                r.Isrc.Equals(isrc, StringComparison.OrdinalIgnoreCase));
            
            return exactMatch;
        }
        catch
        {
            return null;
        }
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
        var missingTracksKey = $"spotify:missing:{playlistName}";
        var matchedTracksKey = $"spotify:matched:{playlistName}";
        
        // Check if we already have matched tracks cached
        var existingMatched = await _cache.GetAsync<List<Song>>(matchedTracksKey);
        if (existingMatched != null && existingMatched.Count > 0)
        {
            _logger.LogInformation("Playlist {Playlist} already has {Count} matched tracks cached, skipping", 
                playlistName, existingMatched.Count);
            return;
        }

        // Get missing tracks
        var missingTracks = await _cache.GetAsync<List<MissingTrack>>(missingTracksKey);
        if (missingTracks == null || missingTracks.Count == 0)
        {
            _logger.LogInformation("No missing tracks found for {Playlist}, skipping matching", playlistName);
            return;
        }

        _logger.LogInformation("Matching {Count} tracks for {Playlist} (with rate limiting)", 
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
                _logger.LogDebug(ex, "Failed to match track: {Title} - {Artist}", 
                    track.Title, track.PrimaryArtist);
            }
        }

        if (matchedSongs.Count > 0)
        {
            // Cache matched tracks for 1 hour
            await _cache.SetAsync(matchedTracksKey, matchedSongs, TimeSpan.FromHours(1));
            _logger.LogInformation("✓ Cached {Matched}/{Total} matched tracks for {Playlist}", 
                matchedSongs.Count, missingTracks.Count, playlistName);
        }
        else
        {
            _logger.LogInformation("No tracks matched for {Playlist}", playlistName);
        }
    }
    
    /// <summary>
    /// Calculates artist match score ensuring ALL artists are present.
    /// Penalizes if artist counts don't match or if any artist is missing.
    /// </summary>
    private static double CalculateArtistMatchScore(List<string> spotifyArtists, string songMainArtist, List<string> songContributors)
    {
        if (spotifyArtists.Count == 0 || string.IsNullOrEmpty(songMainArtist))
            return 0;
        
        // Build list of all song artists (main + contributors)
        var allSongArtists = new List<string> { songMainArtist };
        allSongArtists.AddRange(songContributors);
        
        // If artist counts differ significantly, penalize
        var countDiff = Math.Abs(spotifyArtists.Count - allSongArtists.Count);
        if (countDiff > 1) // Allow 1 artist difference (sometimes features are listed differently)
            return 0;
        
        // Check that each Spotify artist has a good match in song artists
        var spotifyScores = new List<double>();
        foreach (var spotifyArtist in spotifyArtists)
        {
            var bestMatch = allSongArtists.Max(songArtist => 
                FuzzyMatcher.CalculateSimilarity(spotifyArtist, songArtist));
            spotifyScores.Add(bestMatch);
        }
        
        // Check that each song artist has a good match in Spotify artists
        var songScores = new List<double>();
        foreach (var songArtist in allSongArtists)
        {
            var bestMatch = spotifyArtists.Max(spotifyArtist => 
                FuzzyMatcher.CalculateSimilarity(songArtist, spotifyArtist));
            songScores.Add(bestMatch);
        }
        
        // Average all scores - this ensures ALL artists must match well
        var allScores = spotifyScores.Concat(songScores);
        var avgScore = allScores.Average();
        
        // Penalize if any individual artist match is poor (< 70)
        var minScore = allScores.Min();
        if (minScore < 70)
            avgScore *= 0.7; // 30% penalty for poor individual match
        
        return avgScore;
    }
    
    /// <summary>
    /// Pre-builds the playlist items cache for instant serving.
    /// This combines local Jellyfin tracks with external matched tracks in the correct Spotify order.
    /// </summary>
    private async Task PreBuildPlaylistItemsCacheAsync(
        string playlistName,
        string? jellyfinPlaylistId,
        List<SpotifyPlaylistTrack> spotifyTracks,
        List<MatchedTrack> matchedTracks,
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("🔨 Pre-building playlist items cache for {Playlist}...", playlistName);
            
            if (string.IsNullOrEmpty(jellyfinPlaylistId))
            {
                _logger.LogWarning("No Jellyfin playlist ID configured for {Playlist}, cannot pre-build cache", playlistName);
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
                _logger.LogWarning("No UserId configured, cannot pre-build playlist cache for {Playlist}", playlistName);
                return;
            }
            
            // Create authentication headers for background service call
            var headers = new HeaderDictionary();
            if (!string.IsNullOrEmpty(jellyfinSettings.ApiKey))
            {
                headers["X-Emby-Authorization"] = $"MediaBrowser Token=\"{jellyfinSettings.ApiKey}\"";
            }
            
            var playlistItemsUrl = $"Playlists/{jellyfinPlaylistId}/Items?UserId={userId}&Fields=MediaSources";
            var (existingTracksResponse, statusCode) = await proxyService.GetJsonAsync(playlistItemsUrl, null, headers);
            
            if (statusCode != 200 || existingTracksResponse == null)
            {
                _logger.LogWarning("Failed to fetch Jellyfin playlist items for {Playlist}: HTTP {StatusCode}", playlistName, statusCode);
                return;
            }
            
            // Index Jellyfin items by title+artist for matching
            var jellyfinItemsByName = new Dictionary<string, JsonElement>();
            
            if (existingTracksResponse.RootElement.TryGetProperty("Items", out var items))
            {
                foreach (var item in items.EnumerateArray())
                {
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
            var finalItems = new List<Dictionary<string, object?>>();
            var usedJellyfinItems = new HashSet<string>();
            var localUsedCount = 0;
            var externalUsedCount = 0;
            var manualExternalCount = 0;
            
            foreach (var spotifyTrack in spotifyTracks.OrderBy(t => t.Position))
            {
                if (cancellationToken.IsCancellationRequested) break;
                
                JsonElement? matchedJellyfinItem = null;
                string? matchedKey = null;
                
                // FIRST: Check for manual Jellyfin mapping
                var manualMappingKey = $"spotify:manual-map:{playlistName}:{spotifyTrack.SpotifyId}";
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
                            // Add Spotify ID to ProviderIds so lyrics can work for local tracks too
                            if (!string.IsNullOrEmpty(spotifyTrack.SpotifyId))
                            {
                                if (!itemDict.ContainsKey("ProviderIds"))
                                {
                                    itemDict["ProviderIds"] = new Dictionary<string, string>();
                                }
                                
                                var providerIds = itemDict["ProviderIds"] as Dictionary<string, string>;
                                if (providerIds != null && !providerIds.ContainsKey("Spotify"))
                                {
                                    providerIds["Spotify"] = spotifyTrack.SpotifyId;
                                    _logger.LogDebug("Added Spotify ID {SpotifyId} to local track for lyrics support", spotifyTrack.SpotifyId);
                                }
                            }
                            
                            finalItems.Add(itemDict);
                            if (matchedKey != null)
                            {
                                usedJellyfinItems.Add(matchedKey);
                            }
                            localUsedCount++;
                        }
                        continue; // Skip to next track
                    }
                }
                
                // SECOND: Check for external manual mapping
                var externalMappingKey = $"spotify:external-map:{playlistName}:{spotifyTrack.SpotifyId}";
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
                        
                        if (!string.IsNullOrEmpty(provider) && !string.IsNullOrEmpty(externalId))
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
                                    _logger.LogWarning("Failed to fetch metadata for {Provider} ID {ExternalId}, using fallback", 
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
                            
                            var matchedTrack = new MatchedTrack
                            {
                                Position = spotifyTrack.Position,
                                SpotifyId = spotifyTrack.SpotifyId,
                                MatchedSong = externalSong
                            };
                            
                            matchedTracks.Add(matchedTrack);
                            
                            // Convert external song to Jellyfin item format and add to finalItems
                            var externalItem = responseBuilder.ConvertSongToJellyfinItem(externalSong);
                            
                            // Add Spotify ID to ProviderIds so lyrics can work
                            if (!string.IsNullOrEmpty(spotifyTrack.SpotifyId))
                            {
                                if (!externalItem.ContainsKey("ProviderIds"))
                                {
                                    externalItem["ProviderIds"] = new Dictionary<string, string>();
                                }
                                
                                var providerIds = externalItem["ProviderIds"] as Dictionary<string, string>;
                                if (providerIds != null && !providerIds.ContainsKey("Spotify"))
                                {
                                    providerIds["Spotify"] = spotifyTrack.SpotifyId;
                                }
                            }
                            
                            finalItems.Add(externalItem);
                            externalUsedCount++;
                            manualExternalCount++;
                            
                            _logger.LogInformation("✓ Using manual external mapping for {Title}: {Provider} {ExternalId}", 
                                spotifyTrack.Title, provider, externalId);
                            continue; // Skip to next track
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to process external manual mapping for {Title}", spotifyTrack.Title);
                    }
                }
                
                // If no manual external mapping, try AGGRESSIVE fuzzy matching with local Jellyfin tracks
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
                        // Add Spotify ID to ProviderIds so lyrics can work for fuzzy-matched local tracks too
                        if (!string.IsNullOrEmpty(spotifyTrack.SpotifyId))
                        {
                            if (!itemDict.ContainsKey("ProviderIds"))
                            {
                                itemDict["ProviderIds"] = new Dictionary<string, string>();
                            }
                            
                            var providerIds = itemDict["ProviderIds"] as Dictionary<string, string>;
                            if (providerIds != null && !providerIds.ContainsKey("Spotify"))
                            {
                                providerIds["Spotify"] = spotifyTrack.SpotifyId;
                                _logger.LogDebug("Added Spotify ID {SpotifyId} to fuzzy-matched local track for lyrics support", spotifyTrack.SpotifyId);
                            }
                        }
                        
                        finalItems.Add(itemDict);
                        if (matchedKey != null)
                        {
                            usedJellyfinItems.Add(matchedKey);
                        }
                        localUsedCount++;
                    }
                }
                else
                {
                    // No local match - try to find external track
                    var matched = matchedTracks.FirstOrDefault(t => t.SpotifyId == spotifyTrack.SpotifyId);
                    if (matched != null && matched.MatchedSong != null)
                    {
                        // Convert external song to Jellyfin item format
                        var externalItem = responseBuilder.ConvertSongToJellyfinItem(matched.MatchedSong);
                        
                        // Add Spotify ID to ProviderIds so lyrics can work
                        if (!string.IsNullOrEmpty(spotifyTrack.SpotifyId))
                        {
                            if (!externalItem.ContainsKey("ProviderIds"))
                            {
                                externalItem["ProviderIds"] = new Dictionary<string, string>();
                            }
                            
                            var providerIds = externalItem["ProviderIds"] as Dictionary<string, string>;
                            if (providerIds != null && !providerIds.ContainsKey("Spotify"))
                            {
                                providerIds["Spotify"] = spotifyTrack.SpotifyId;
                            }
                        }
                        
                        finalItems.Add(externalItem);
                        externalUsedCount++;
                    }
                }
            }
            
            if (finalItems.Count > 0)
            {
                // Save to Redis cache
                var cacheKey = $"spotify:playlist:items:{playlistName}";
                await _cache.SetAsync(cacheKey, finalItems, TimeSpan.FromHours(24));
                
                // Save to file cache for persistence
                await SavePlaylistItemsToFileAsync(playlistName, finalItems);
                
                var manualMappingInfo = "";
                if (manualExternalCount > 0)
                {
                    manualMappingInfo = $" [Manual external: {manualExternalCount}]";
                }
                
                _logger.LogInformation(
                    "✅ Pre-built playlist cache for {Playlist}: {Total} tracks ({Local} LOCAL + {External} EXTERNAL){ManualInfo}", 
                    playlistName, finalItems.Count, localUsedCount, externalUsedCount, manualMappingInfo);
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
            
            _logger.LogDebug("💾 Saved {Count} matched tracks to file cache: {Path}", matchedTracks.Count, filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save matched tracks to file for {Playlist}", playlistName);
        }
    }
}

