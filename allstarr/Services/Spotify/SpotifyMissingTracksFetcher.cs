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
        ILogger<SpotifyMissingTracksFetcher> logger)
    {
        _spotifySettings = spotifySettings;
        _spotifyApiSettings = spotifyApiSettings;
        _jellyfinSettings = jellyfinSettings;
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    /// <summary>
    /// Public method to trigger fetching manually (called from controller).
    /// </summary>
    public async Task TriggerFetchAsync()
    {
        _logger.LogInformation("Manual fetch triggered");
        await FetchMissingTracksAsync(CancellationToken.None, bypassSyncWindowCheck: true);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("========================================");
        _logger.LogInformation("SpotifyMissingTracksFetcher: Starting up...");
        
        // Ensure cache directory exists
        Directory.CreateDirectory(CacheDirectory);
        
        // Check if SpotifyApi is enabled with a valid session cookie
        // If so, SpotifyPlaylistFetcher will handle everything - we don't need to scrape Jellyfin
        if (_spotifyApiSettings.Value.Enabled && !string.IsNullOrEmpty(_spotifyApiSettings.Value.SessionCookie))
        {
            _logger.LogInformation("SpotifyApi is enabled with session cookie - using direct Spotify API instead of Jellyfin scraping");
            _logger.LogInformation("This service will remain dormant. SpotifyPlaylistFetcher is handling playlists.");
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
            _logger.LogWarning("Jellyfin URL or API key not configured, Spotify playlist injection disabled");
            _logger.LogInformation("========================================");
            return;
        }

        _logger.LogInformation("Spotify Import ENABLED");
        _logger.LogInformation("Configured Playlists: {Count}", _spotifySettings.Value.Playlists.Count);
        
        // Log the search schedule
        var settings = _spotifySettings.Value;
        var syncTime = DateTime.Today
            .AddHours(settings.SyncStartHour)
            .AddMinutes(settings.SyncStartMinute);
        var syncEndTime = syncTime.AddHours(settings.SyncWindowHours);
        
        _logger.LogInformation("Search Schedule:");
        _logger.LogInformation("  Plugin sync time: {Time:HH:mm} UTC (configured)", syncTime);
        _logger.LogInformation("  Search window: {Start:HH:mm} - {End:HH:mm} UTC ({Hours}h window)", 
            syncTime, syncEndTime, settings.SyncWindowHours);
        _logger.LogInformation("  Will search for new files once per day after sync window ends");
        _logger.LogInformation("  Background check interval: 5 minutes");
        
        // Fetch playlist names from Jellyfin
        await LoadPlaylistNamesAsync();
        
        _logger.LogInformation("Configured Playlists:");
        foreach (var kvp in _playlistIdToName)
        {
            _logger.LogInformation("  - {Name} (ID: {Id})", kvp.Value, kvp.Key);
        }
        _logger.LogInformation("========================================");

        // Check if we should run on startup
        if (!_hasRunOnce)
        {
            var shouldRun = await ShouldRunOnStartupAsync();
            if (shouldRun)
            {
                _logger.LogInformation("Running initial fetch on startup");
                try
                {
                    await FetchMissingTracksAsync(stoppingToken, bypassSyncWindowCheck: true);
                    _hasRunOnce = true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during startup fetch");
                }
            }
            else
            {
                _logger.LogInformation("Skipping startup fetch - already have current files");
                _hasRunOnce = true;
            }
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Only fetch if we're past today's sync window AND we haven't fetched today yet
                var shouldFetch = await ShouldFetchNowAsync();
                if (shouldFetch)
                {
                    await FetchMissingTracksAsync(stoppingToken);
                    _hasRunOnce = true;
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
        var settings = _spotifySettings.Value;
        var now = DateTime.UtcNow;
        
        // Calculate today's sync window
        var todaySync = now.Date
            .AddHours(settings.SyncStartHour)
            .AddMinutes(settings.SyncStartMinute);
        var todaySyncEnd = todaySync.AddHours(settings.SyncWindowHours);
        
        // Only fetch if we're past today's sync window
        if (now < todaySyncEnd)
        {
            return false;
        }
        
        // Check if we already have today's files
        foreach (var playlistName in _playlistIdToName.Values)
        {
            var filePath = GetCacheFilePath(playlistName);
            
            if (File.Exists(filePath))
            {
                var fileTime = File.GetLastWriteTimeUtc(filePath);
                
                // If file is from today's sync or later, we already have it
                if (fileTime >= todaySync)
                {
                    continue;
                }
            }
            
            // Missing today's file for this playlist
            return true;
        }
        
        // All playlists have today's files
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
        
        var settings = _spotifySettings.Value;
        var now = DateTime.UtcNow;
        
        // Calculate today's sync window
        var todaySync = now.Date
            .AddHours(settings.SyncStartHour)
            .AddMinutes(settings.SyncStartMinute);
        var todaySyncEnd = todaySync.AddHours(settings.SyncWindowHours);
        
        _logger.LogInformation("Today's sync window: {Start:yyyy-MM-dd HH:mm} - {End:yyyy-MM-dd HH:mm} UTC", 
            todaySync, todaySyncEnd);
        _logger.LogInformation("Current time: {Now:yyyy-MM-dd HH:mm} UTC", now);
        
        // If we're still before today's sync window end, we should have yesterday's or today's file
        // Don't search again until after today's sync window ends
        if (now < todaySyncEnd)
        {
            _logger.LogInformation("We're before today's sync window end - checking if we have recent cache...");
            
            var allPlaylistsHaveCache = true;
            
            foreach (var playlistName in _playlistIdToName.Values)
            {
                var filePath = GetCacheFilePath(playlistName);
                var cacheKey = $"spotify:missing:{playlistName}";
                
                // Check file cache
                if (File.Exists(filePath))
                {
                    var fileAge = DateTime.UtcNow - File.GetLastWriteTimeUtc(filePath);
                    _logger.LogInformation("  {Playlist}: Found file cache (age: {Age:F1}h)", playlistName, fileAge.TotalHours);
                    
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
                    _logger.LogInformation("  {Playlist}: Found in Redis cache", playlistName);
                    continue;
                }
                
                // No cache found for this playlist
                _logger.LogInformation("  {Playlist}: No cache found", playlistName);
                allPlaylistsHaveCache = false;
            }
            
            if (allPlaylistsHaveCache)
            {
                _logger.LogInformation("=== ALL PLAYLISTS HAVE CACHE - SKIPPING STARTUP FETCH ===");
                _logger.LogInformation("Will search again after {Time:yyyy-MM-dd HH:mm} UTC", todaySyncEnd);
                return false;
            }
        }
        
        // If we're after today's sync window end, check if we already have today's file
        if (now >= todaySyncEnd)
        {
            _logger.LogInformation("We're after today's sync window end - checking if we already fetched today's files...");
            
            var allPlaylistsHaveTodaysFile = true;
            
            foreach (var playlistName in _playlistIdToName.Values)
            {
                var filePath = GetCacheFilePath(playlistName);
                var cacheKey = $"spotify:missing:{playlistName}";
                
                // Check if file exists and was created today (after sync start)
                if (File.Exists(filePath))
                {
                    var fileTime = File.GetLastWriteTimeUtc(filePath);
                    
                    // File should be from today's sync window or later
                    if (fileTime >= todaySync)
                    {
                        var fileAge = DateTime.UtcNow - fileTime;
                        _logger.LogInformation("  {Playlist}: Have today's file (created {Time:yyyy-MM-dd HH:mm}, age: {Age:F1}h)", 
                            playlistName, fileTime, fileAge.TotalHours);
                        
                        // Load into Redis if not already there
                        if (!await _cache.ExistsAsync(cacheKey))
                        {
                            await LoadFromFileCache(playlistName);
                        }
                        continue;
                    }
                    else
                    {
                        _logger.LogInformation("  {Playlist}: File is old (from {Time:yyyy-MM-dd HH:mm}, before today's sync)", 
                            playlistName, fileTime);
                    }
                }
                else
                {
                    _logger.LogInformation("  {Playlist}: No file found", playlistName);
                }
                
                allPlaylistsHaveTodaysFile = false;
            }
            
            if (allPlaylistsHaveTodaysFile)
            {
                _logger.LogInformation("=== ALL PLAYLISTS HAVE TODAY'S FILES - SKIPPING STARTUP FETCH ===");
                
                // Calculate when to search next (tomorrow after sync window)
                var tomorrowSyncEnd = todaySyncEnd.AddDays(1);
                _logger.LogInformation("Will search again after {Time:yyyy-MM-dd HH:mm} UTC", tomorrowSyncEnd);
                return false;
            }
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
                var cacheKey = $"spotify:missing:{playlistName}";
                var fileAge = DateTime.UtcNow - File.GetLastWriteTimeUtc(filePath);
                
                // No expiration - cache persists until next Jellyfin job generates new file
                await _cache.SetAsync(cacheKey, tracks, TimeSpan.FromDays(365));
                _logger.LogInformation("Loaded {Count} tracks from file cache for {Playlist} (age: {Age:F1}h, no expiration)", 
                    tracks.Count, playlistName, fileAge.TotalHours);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load file cache for {Playlist}", playlistName);
        }
    }
    
    private async Task SaveToFileCache(string playlistName, List<MissingTrack> tracks)
    {
        try
        {
            var filePath = GetCacheFilePath(playlistName);
            var json = JsonSerializer.Serialize(tracks, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(filePath, json);
            _logger.LogInformation("Saved {Count} tracks to file cache for {Playlist}", 
                tracks.Count, playlistName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save file cache for {Playlist}", playlistName);
        }
    }

    private async Task FetchMissingTracksAsync(CancellationToken cancellationToken, bool bypassSyncWindowCheck = false)
    {
        var settings = _spotifySettings.Value;
        var now = DateTime.UtcNow;
        var syncStart = now.Date
            .AddHours(settings.SyncStartHour)
            .AddMinutes(settings.SyncStartMinute);
        var syncEnd = syncStart.AddHours(settings.SyncWindowHours);

        // Only run after the sync window has passed (unless bypassing for startup)
        if (!bypassSyncWindowCheck && now < syncEnd)
        {
            _logger.LogInformation("Skipping fetch - sync window not passed yet (now: {Now}, window ends: {End})", 
                now, syncEnd);
            return;
        }

        if (bypassSyncWindowCheck)
        {
            _logger.LogInformation("=== FETCHING MISSING TRACKS (STARTUP MODE) ===");
        }
        else
        {
            _logger.LogInformation("=== FETCHING MISSING TRACKS (SYNC WINDOW PASSED) ===");
        }

        _logger.LogInformation("Processing {Count} playlists", _playlistIdToName.Count);
        
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
        var cacheKey = $"spotify:missing:{playlistName}";
        
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
            _logger.LogInformation("  Current cache has {Count} tracks, will search for newer file", existingTracks.Count);
        }
        else
        {
            _logger.LogInformation("  No existing cache, will search for missing tracks file");
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
                _logger.LogInformation("  ✓ Keeping existing cache with {Count} tracks (no expiration)", existingTracks.Count);
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
                        _logger.LogInformation("  ✓ Loaded {Count} tracks from file cache (no expiration)", tracks.Count);
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
            _logger.LogInformation("Checking: {Playlist} at {DateTime}", playlistName, time.ToString("yyyy-MM-dd HH:mm"));
            
            var response = await httpClient.GetAsync(url, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var tracks = ParseMissingTracks(json);
                
                if (tracks.Count > 0)
                {
                    var cacheKey = $"spotify:missing:{playlistName}";
                    
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
            _logger.LogDebug(ex, "Failed to fetch {Filename}", filename);
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
