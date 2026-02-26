using allstarr.Models.Settings;
using allstarr.Models.Spotify;
using allstarr.Services.Common;
using allstarr.Services.Jellyfin;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace allstarr.Services.Spotify;

public class SpotifyMissingTracksFetcher : BackgroundService
{
    private readonly IOptions<SpotifyImportSettings> _spotifySettings;
    private readonly IOptions<SpotifyApiSettings> _spotifyApiSettings;
    private readonly IOptions<JellyfinSettings> _jellyfinSettings;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly RedisCacheService _cache;
    private readonly ILogger<SpotifyMissingTracksFetcher> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly SpotifySessionCookieService _spotifySessionCookieService;
    private bool _hasRunOnce = false;
    private Dictionary<string, string> _playlistIdToName = new();
    private const string CacheDirectory = "/app/cache/spotify";

    public SpotifyMissingTracksFetcher(
        IOptions<SpotifyImportSettings> spotifySettings,
        IOptions<SpotifyApiSettings> spotifyApiSettings,
        IOptions<JellyfinSettings> jellyfinSettings,
        IHttpClientFactory httpClientFactory,
        RedisCacheService cache,
        IServiceProvider serviceProvider,
        SpotifySessionCookieService spotifySessionCookieService,
        ILogger<SpotifyMissingTracksFetcher> logger)
    {
        _spotifySettings = spotifySettings;
        _spotifyApiSettings = spotifyApiSettings;
        _jellyfinSettings = jellyfinSettings;
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _serviceProvider = serviceProvider;
        _spotifySessionCookieService = spotifySessionCookieService;
        _logger = logger;
    }

    /// <summary>
    /// Public method to trigger fetching manually (called from controller).
    /// </summary>
    public async Task TriggerFetchAsync()
    {
        _logger.LogInformation("Manual fetch triggered");
        await FetchMissingTracksAsync(CancellationToken.None);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("========================================");
        _logger.LogInformation("SpotifyMissingTracksFetcher: Starting up...");

        // Ensure cache directory exists
        Directory.CreateDirectory(CacheDirectory);

        // If Spotify API has any configured cookie (global or user-scoped),
        // SpotifyPlaylistFetcher handles playlist loading and this legacy scraper can stay dormant.
        if (_spotifyApiSettings.Value.Enabled &&
            await _spotifySessionCookieService.HasAnyConfiguredCookieAsync())
        {
            _logger.LogInformation("SpotifyApi has configured session cookie(s) - using direct Spotify API instead of Jellyfin scraping");
            _logger.LogDebug("This service will remain dormant. SpotifyPlaylistFetcher is handling playlists.");
            _logger.LogInformation("========================================");
            return;
        }

        if (!_spotifySettings.Value.Enabled)
        {
            _logger.LogInformation("Spotify playlist injection is DISABLED");
            _logger.LogInformation("========================================");
            return;
        }

        var jellyfinUrl = _jellyfinSettings.Value.Url;
        var apiKey = _jellyfinSettings.Value.ApiKey;

        if (string.IsNullOrEmpty(jellyfinUrl) || string.IsNullOrEmpty(apiKey))
        {
            _logger.LogInformation("Jellyfin URL or API key not configured, Spotify playlist injection disabled");
            _logger.LogInformation("========================================");
            return;
        }

        _logger.LogInformation("Spotify Import ENABLED");
        _logger.LogInformation("Configured Playlists: {Count}", _spotifySettings.Value.Playlists.Count);
        _logger.LogInformation("Background check interval: 5 minutes");

        // Fetch playlist names from Jellyfin
        await LoadPlaylistNamesAsync();

        _logger.LogInformation("Configured Playlists:");
        foreach (var kvp in _playlistIdToName)
        {
            _logger.LogInformation("  - {Name} (ID: {Id})", kvp.Value, kvp.Key);
        }
        _logger.LogInformation("========================================");

        // Run on startup if we don't have cache
        if (!_hasRunOnce)
        {
            var shouldRun = await ShouldRunOnStartupAsync();
            if (shouldRun)
            {
                _logger.LogInformation("Running initial fetch on startup");
                try
                {
                    await FetchMissingTracksAsync(stoppingToken);
                    _hasRunOnce = true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during startup fetch");
                }
            }
            else
            {
                _logger.LogWarning("Skipping startup fetch - already have cached files");
                _hasRunOnce = true;
            }
        }

        // Background loop - check for new files every 5 minutes
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var shouldFetch = await ShouldFetchNowAsync();
                if (shouldFetch)
                {
                    await FetchMissingTracksAsync(stoppingToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching Spotify missing tracks");
            }

            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }

    private async Task<bool> ShouldFetchNowAsync()
    {
        // Check if we have recent cache files (within last 24 hours)
        var now = DateTime.UtcNow;
        var cacheThreshold = now.AddHours(-24);

        foreach (var playlistName in _playlistIdToName.Values)
        {
            var filePath = GetCacheFilePath(playlistName);

            if (!File.Exists(filePath))
            {
                // Missing cache file for this playlist
                return true;
            }

            var fileTime = File.GetLastWriteTimeUtc(filePath);
            if (fileTime < cacheThreshold)
            {
                // Cache file is older than 24 hours
                return true;
            }
        }

        // All playlists have recent cache files
        return false;
    }

    private async Task LoadPlaylistNamesAsync()
    {
        _playlistIdToName.Clear();

        // Use configured playlists
        foreach (var playlist in _spotifySettings.Value.Playlists)
        {
            _playlistIdToName[playlist.Id] = playlist.Name;
        }
    }

    private async Task<bool> ShouldRunOnStartupAsync()
    {
        _logger.LogInformation("=== STARTUP CACHE CHECK ===");

        var allPlaylistsHaveCache = true;

        foreach (var playlistName in _playlistIdToName.Values)
        {
            var filePath = GetCacheFilePath(playlistName);
            var cacheKey = CacheKeyBuilder.BuildSpotifyMissingTracksKey(playlistName);

            // Check file cache
            if (File.Exists(filePath))
            {
                var fileAge = DateTime.UtcNow - File.GetLastWriteTimeUtc(filePath);
                _logger.LogDebug("  {Playlist}: Found file cache (age: {Age:F1}h)", playlistName, fileAge.TotalHours);

                // Load into Redis if not already there
                if (!await _cache.ExistsAsync(cacheKey))
                {
                    await LoadFromFileCache(playlistName);
                }
                continue;
            }

            // Check Redis cache
            if (await _cache.ExistsAsync(cacheKey))
            {
                _logger.LogDebug("  {Playlist}: Found in Redis cache", playlistName);
                continue;
            }

            // No cache found for this playlist
            _logger.LogInformation("  {Playlist}: No cache found", playlistName);
            allPlaylistsHaveCache = false;
        }

        if (allPlaylistsHaveCache)
        {
            _logger.LogWarning("=== ALL PLAYLISTS HAVE CACHE - SKIPPING STARTUP FETCH ===");
            return false;
        }

        _logger.LogInformation("=== WILL FETCH ON STARTUP ===");
        return true;
    }

    private string GetCacheFilePath(string playlistName)
    {
        var safeName = string.Join("_", playlistName.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(CacheDirectory, $"{safeName}_missing.json");
    }

    private async Task LoadFromFileCache(string playlistName)
    {
        try
        {
            var filePath = GetCacheFilePath(playlistName);
            if (!File.Exists(filePath))
                return;

            var json = await File.ReadAllTextAsync(filePath);
            var tracks = JsonSerializer.Deserialize<List<MissingTrack>>(json);

            if (tracks != null && tracks.Count > 0)
            {
                var cacheKey = CacheKeyBuilder.BuildSpotifyMissingTracksKey(playlistName);
                var fileAge = DateTime.UtcNow - File.GetLastWriteTimeUtc(filePath);

                // No expiration - cache persists until next Jellyfin job generates new file
                await _cache.SetAsync(cacheKey, tracks, TimeSpan.FromDays(365));
                _logger.LogDebug("Loaded {Count} tracks from file cache for {Playlist} (age: {Age:F1}h, no expiration)",
                    tracks.Count, playlistName, fileAge.TotalHours);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load file cache for {Playlist}", playlistName);
        }
    }

    private async Task SaveToFileCache(string playlistName, List<MissingTrack> tracks)
    {
        try
        {
            var filePath = GetCacheFilePath(playlistName);
            var json = JsonSerializer.Serialize(tracks, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(filePath, json);
            _logger.LogDebug("Saved {Count} tracks to file cache for {Playlist}",
                tracks.Count, playlistName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save file cache for {Playlist}", playlistName);
        }
    }

    private async Task FetchMissingTracksAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("=== FETCHING MISSING TRACKS ===");
        _logger.LogDebug("Processing {Count} playlists", _playlistIdToName.Count);

        // Track when we find files to optimize search for other playlists
        DateTime? firstFoundTime = null;
        var foundPlaylists = new HashSet<string>();

        foreach (var kvp in _playlistIdToName)
        {
            _logger.LogInformation("Fetching playlist: {Name}", kvp.Value);
            var foundTime = await FetchPlaylistMissingTracksAsync(kvp.Value, cancellationToken, firstFoundTime);

            if (foundTime.HasValue)
            {
                foundPlaylists.Add(kvp.Value);
                if (!firstFoundTime.HasValue)
                {
                    firstFoundTime = foundTime;
                    _logger.LogInformation("  → Will search within ±1h of {Time:yyyy-MM-dd HH:mm} for remaining playlists", firstFoundTime.Value);
                }
            }
        }

        _logger.LogInformation("=== FINISHED FETCHING MISSING TRACKS ({Found}/{Total} playlists found) ===",
            foundPlaylists.Count, _playlistIdToName.Count);
    }

    private async Task<DateTime?> FetchPlaylistMissingTracksAsync(
        string playlistName,
        CancellationToken cancellationToken,
        DateTime? hintTime = null)
    {
        var cacheKey = CacheKeyBuilder.BuildSpotifyMissingTracksKey(playlistName);

        // Check if we have existing cache
        var existingTracks = await _cache.GetAsync<List<MissingTrack>>(cacheKey);
        var filePath = GetCacheFilePath(playlistName);

        if (File.Exists(filePath))
        {
            var fileAge = DateTime.UtcNow - File.GetLastWriteTimeUtc(filePath);
            _logger.LogInformation("  Existing cache file age: {Age:F1}h", fileAge.TotalHours);
        }

        if (existingTracks != null && existingTracks.Count > 0)
        {
            _logger.LogDebug("  Current cache has {Count} tracks, will search for newer file", existingTracks.Count);
        }
        else
        {
            _logger.LogDebug("  No existing cache, will search for missing tracks file");
        }

        var settings = _spotifySettings.Value;
        var jellyfinUrl = _jellyfinSettings.Value.Url;
        var apiKey = _jellyfinSettings.Value.ApiKey;

        if (string.IsNullOrEmpty(jellyfinUrl) || string.IsNullOrEmpty(apiKey))
        {
            _logger.LogWarning("  Jellyfin URL or API key not configured, skipping fetch");
            return null;
        }

        var httpClient = _httpClientFactory.CreateClient();

        // Search starting from 24 hours ahead, going backwards for 72 hours
        // This handles timezone differences where the plugin may have run "in the future" from our perspective
        var now = DateTime.UtcNow;
        var searchStart = now.AddHours(24); // Start 24 hours from now
        var totalMinutesToSearch = 72 * 60; // 72 hours = 4320 minutes

        _logger.LogInformation("  Current UTC time: {Now:yyyy-MM-dd HH:mm}", now);
        _logger.LogInformation("  Search start: {Start:yyyy-MM-dd HH:mm} (24h ahead)", searchStart);
        _logger.LogInformation("  Searching backwards for 72 hours ({Minutes} minutes)", totalMinutesToSearch);

        var found = false;
        DateTime? foundFileTime = null;

        // If we have a hint time from another playlist, search ±1 hour around it first
        if (hintTime.HasValue)
        {
            _logger.LogInformation("  Hint: Searching ±1h around {Time:yyyy-MM-dd HH:mm} (from another playlist)", hintTime.Value);

            // Search ±60 minutes around the hint time
            for (var minuteOffset = 0; minuteOffset <= 60; minuteOffset++)
            {
                if (cancellationToken.IsCancellationRequested) break;

                // Try both forward and backward from hint
                if (minuteOffset > 0)
                {
                    // Try forward
                    var timeForward = hintTime.Value.AddMinutes(minuteOffset);
                    var resultForward = await TryFetchMissingTracksFile(playlistName, timeForward, jellyfinUrl, apiKey, httpClient, cancellationToken);
                    if (resultForward.found)
                    {
                        found = true;
                        foundFileTime = resultForward.fileTime;
                        _logger.LogInformation("  ✓ Found using hint (+{Minutes}min from hint)", minuteOffset);
                        return foundFileTime;
                    }
                }

                // Try backward
                var timeBackward = hintTime.Value.AddMinutes(-minuteOffset);
                var resultBackward = await TryFetchMissingTracksFile(playlistName, timeBackward, jellyfinUrl, apiKey, httpClient, cancellationToken);
                if (resultBackward.found)
                {
                    found = true;
                    foundFileTime = resultBackward.fileTime;
                    _logger.LogInformation("  ✓ Found using hint (-{Minutes}min from hint)", minuteOffset);
                    return foundFileTime;
                }
            }

            _logger.LogInformation("  Not found within ±1h of hint, doing full search...");
        }

        // Search from 24h ahead, going backwards minute by minute for 72 hours
        _logger.LogInformation("  Searching from {Start:yyyy-MM-dd HH:mm} backwards to {End:yyyy-MM-dd HH:mm}...",
            searchStart, searchStart.AddMinutes(-totalMinutesToSearch));

        for (var minutesBehind = 0; minutesBehind <= totalMinutesToSearch; minutesBehind++)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var time = searchStart.AddMinutes(-minutesBehind);

            var result = await TryFetchMissingTracksFile(playlistName, time, jellyfinUrl, apiKey, httpClient, cancellationToken);
            if (result.found)
            {
                found = true;
                foundFileTime = result.fileTime;
                return foundFileTime;
            }

            // Small delay every 60 requests to avoid rate limiting
            if (minutesBehind > 0 && minutesBehind % 60 == 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
            }
        }

        if (!found)
        {
            _logger.LogWarning("  ✗ Could not find new missing tracks file (searched +24h forward, -48h backward)");

            // Keep the existing cache - don't let it expire
            if (existingTracks != null && existingTracks.Count > 0)
            {
                _logger.LogDebug("  ✓ Keeping existing cache with {Count} tracks (no expiration)", existingTracks.Count);
                // Re-save with no expiration to ensure it persists
                await _cache.SetAsync(cacheKey, existingTracks, TimeSpan.FromDays(365)); // Effectively no expiration
            }
            else if (File.Exists(filePath))
            {
                // Load from file if Redis cache is empty
                _logger.LogInformation("  📦 Loading existing file cache to keep playlist populated");
                try
                {
                    var json = await File.ReadAllTextAsync(filePath, cancellationToken);
                    var tracks = JsonSerializer.Deserialize<List<MissingTrack>>(json);

                    if (tracks != null && tracks.Count > 0)
                    {
                        await _cache.SetAsync(cacheKey, tracks, TimeSpan.FromDays(365)); // No expiration
                        _logger.LogDebug("  ✓ Loaded {Count} tracks from file cache (no expiration)", tracks.Count);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "  Failed to reload cache from file for {Playlist}", playlistName);
                }
            }
            else
            {
                _logger.LogWarning("  No existing cache to keep - playlist will be empty until tracks are found");
            }
        }

        return foundFileTime;
    }

    private async Task<(bool found, DateTime? fileTime)> TryFetchMissingTracksFile(
        string playlistName,
        DateTime time,
        string jellyfinUrl,
        string apiKey,
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        var filename = $"{playlistName}_missing_{time:yyyy-MM-dd_HH-mm}.json";
        var url = $"{jellyfinUrl}/Viperinius.Plugin.SpotifyImport/MissingTracksFile" +
                 $"?name={Uri.EscapeDataString(filename)}&api_key={apiKey}";

        try
        {
            // Log every request with the actual filename
            _logger.LogDebug("Checking: {Playlist} at {DateTime}", playlistName, time.ToString("yyyy-MM-dd HH:mm"));

            var response = await httpClient.GetAsync(url, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var tracks = ParseMissingTracks(json);

                if (tracks.Count > 0)
                {
                    var cacheKey = CacheKeyBuilder.BuildSpotifyMissingTracksKey(playlistName);

                    // Save to both Redis and file with extended TTL until next job runs
                    // Set to 365 days (effectively no expiration) - will be replaced when Jellyfin generates new file
                    await _cache.SetAsync(cacheKey, tracks, TimeSpan.FromDays(365));
                    await SaveToFileCache(playlistName, tracks);

                    _logger.LogInformation(
                        "✓ FOUND! Cached {Count} missing tracks for {Playlist} from {Filename}",
                        tracks.Count, playlistName, filename);
                    return (true, time);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch {Filename}", filename);
        }

        return (false, null);
    }

    private List<MissingTrack> ParseMissingTracks(string json)
    {
        var tracks = new List<MissingTrack>();

        try
        {
            var doc = JsonDocument.Parse(json);

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var track = new MissingTrack
                {
                    SpotifyId = item.GetProperty("Id").GetString() ?? "",
                    Title = item.GetProperty("Name").GetString() ?? "",
                    Album = item.GetProperty("AlbumName").GetString() ?? "",
                    Artists = item.GetProperty("ArtistNames")
                        .EnumerateArray()
                        .Select(a => a.GetString() ?? "")
                        .Where(a => !string.IsNullOrEmpty(a))
                        .ToList()
                };

                if (!string.IsNullOrEmpty(track.Title))
                {
                    tracks.Add(track);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse missing tracks JSON");
        }

        return tracks;
    }
}
