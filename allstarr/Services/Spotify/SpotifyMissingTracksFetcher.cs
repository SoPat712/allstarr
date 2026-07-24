using allstarr.Models.Settings;
using allstarr.Models.Spotify;
using allstarr.Services.Common;
using allstarr.Services.Jellyfin;
using Microsoft.Extensions.Options;
using System.Net;
using System.Text.Json;

namespace allstarr.Services.Spotify;

public class SpotifyMissingTracksFetcher : BackgroundService
{
    private const int MaximumCandidateProbes = 256;
    private const int ScheduledRunToleranceMinutes = 30;
    private readonly IOptions<SpotifyImportSettings> _spotifySettings;
    private readonly IOptions<SpotifyApiSettings> _spotifyApiSettings;
    private readonly IOptions<JellyfinSettings> _jellyfinSettings;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IApplicationCache _cache;
    private readonly ILogger<SpotifyMissingTracksFetcher> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly SpotifySessionCookieService _spotifySessionCookieService;
    private readonly MissingTrackExportRetryPolicy _retryPolicy = new();
    private readonly string _cacheDirectory;
    private readonly SemaphoreSlim _fetchGate = new(1, 1);
    private bool _hasRunOnce = false;
    private Dictionary<string, string> _playlistIdToName = new();
    public SpotifyMissingTracksFetcher(
        IOptions<SpotifyImportSettings> spotifySettings,
        IOptions<SpotifyApiSettings> spotifyApiSettings,
        IOptions<JellyfinSettings> jellyfinSettings,
        IHttpClientFactory httpClientFactory,
        IApplicationCache cache,
        IServiceProvider serviceProvider,
        SpotifySessionCookieService spotifySessionCookieService,
        IConfiguration configuration,
        ILogger<SpotifyMissingTracksFetcher> logger)
    {
        _spotifySettings = spotifySettings;
        _spotifyApiSettings = spotifyApiSettings;
        _jellyfinSettings = jellyfinSettings;
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _serviceProvider = serviceProvider;
        _spotifySessionCookieService = spotifySessionCookieService;
        _cacheDirectory = configuration["Cache:SpotifyDirectory"] ?? "/app/cache/spotify";
        _logger = logger;
    }

    /// <summary>
    /// Public method to trigger fetching manually (called from controller).
    /// </summary>
    public async Task TriggerFetchAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Manual fetch triggered");
        await RunFetchAsync(cancellationToken, waitForActiveRun: true, force: true);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("========================================");
        _logger.LogInformation("SpotifyMissingTracksFetcher: Starting up...");

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

        Directory.CreateDirectory(_cacheDirectory);

        _logger.LogInformation("Spotify Import ENABLED");
        _logger.LogInformation("Configured Playlists: {Count}", _spotifySettings.Value.Playlists.Count);
        _logger.LogInformation("Background check interval: 5 minutes");

        // Fetch playlist names from Jellyfin
        await LoadPlaylistNamesAsync();

        _logger.LogDebug("Configured Playlists:");
        foreach (var kvp in _playlistIdToName)
        {
            _logger.LogDebug("  - {Name} (ID: {Id})", kvp.Value, kvp.Key);
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
                    await RunFetchAsync(stoppingToken, waitForActiveRun: false, force: false);
                    _hasRunOnce = true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during startup fetch");
                }
            }
            else
            {
                _logger.LogDebug("Skipping startup fetch because every configured playlist has a cache file");
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
                    await RunFetchAsync(stoppingToken, waitForActiveRun: false, force: false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching Spotify missing tracks");
            }

            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }

    private Task<bool> ShouldFetchNowAsync()
    {
        var now = DateTimeOffset.UtcNow;

        foreach (var playlistName in _playlistIdToName.Values)
        {
            if (NeedsRefresh(playlistName, now.UtcDateTime) &&
                _retryPolicy.IsDue(playlistName, now))
            {
                return Task.FromResult(true);
            }
        }

        return Task.FromResult(false);
    }

    private bool NeedsRefresh(string playlistName, DateTime now)
    {
        var filePath = GetCacheFilePath(playlistName);
        return !File.Exists(filePath) ||
               File.GetLastWriteTimeUtc(filePath) < now.AddHours(-24);
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

                // Load into the shared application cache if not already there.
                if (!await _cache.ExistsAsync(cacheKey))
                {
                    await LoadFromFileCache(playlistName);
                }
                continue;
            }

            // Check the shared application cache.
            if (await _cache.ExistsAsync(cacheKey))
            {
                _logger.LogDebug("  {Playlist}: Found in shared cache", playlistName);
                continue;
            }

            // No cache found for this playlist
            _logger.LogInformation("  {Playlist}: No cache found", playlistName);
            allPlaylistsHaveCache = false;
        }

        if (allPlaylistsHaveCache)
        {
            _logger.LogDebug("All configured playlists have cache; startup fetch is unnecessary");
            return false;
        }

        _logger.LogInformation("=== WILL FETCH ON STARTUP ===");
        return true;
    }

    private string GetCacheFilePath(string playlistName)
    {
        var safeName = string.Join("_", playlistName.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(_cacheDirectory, $"{safeName}_missing.json");
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

            if (tracks != null)
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

    private async Task FetchMissingTracksAsync(
        CancellationToken cancellationToken,
        bool force)
    {
        Directory.CreateDirectory(_cacheDirectory);
        _logger.LogInformation("=== FETCHING MISSING TRACKS ===");
        _logger.LogDebug("Processing {Count} playlists", _playlistIdToName.Count);

        DateTime? firstFoundTime = null;
        var foundPlaylists = new HashSet<string>();
        var attemptedPlaylists = 0;
        var now = DateTimeOffset.UtcNow;

        foreach (var kvp in _playlistIdToName)
        {
            if (!force &&
                (!NeedsRefresh(kvp.Value, now.UtcDateTime) ||
                 !_retryPolicy.IsDue(kvp.Value, now)))
            {
                continue;
            }

            attemptedPlaylists++;
            _logger.LogDebug("Fetching playlist: {Name}", kvp.Value);
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

        _logger.LogInformation(
            "=== FINISHED FETCHING MISSING TRACKS ({Found}/{Attempted} attempted playlists found) ===",
            foundPlaylists.Count,
            attemptedPlaylists);
    }

    private async Task RunFetchAsync(
        CancellationToken cancellationToken,
        bool waitForActiveRun,
        bool force)
    {
        var entered = await _fetchGate.WaitAsync(
            waitForActiveRun ? Timeout.Infinite : 0,
            cancellationToken);
        if (!entered)
        {
            _logger.LogDebug("Skipping missing-track fetch because another fetch is already active");
            return;
        }

        try
        {
            await FetchMissingTracksAsync(cancellationToken, force);
        }
        finally
        {
            _fetchGate.Release();
        }
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
            _logger.LogDebug("Existing cache file age: {Age:F1}h", fileAge.TotalHours);
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

        var now = DateTime.UtcNow;
        DateTime? foundFileTime = null;
        var candidates = BuildCandidateTimes(playlistName, now, hintTime);
        _logger.LogDebug(
            "Probing {CandidateCount} schedule-centered missing-track filenames for {Playlist}",
            candidates.Count,
            playlistName);

        foreach (var time in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await TryFetchMissingTracksFile(
                playlistName,
                time,
                jellyfinUrl,
                apiKey,
                httpClient,
                cancellationToken);
            if (result.Status == MissingTrackFileProbeStatus.Found)
            {
                _retryPolicy.RecordSuccess(playlistName);
                foundFileTime = result.FileTime;
                return foundFileTime;
            }

            if (result.Status == MissingTrackFileProbeStatus.Unavailable)
            {
                var retryAfter = _retryPolicy.RecordMiss(playlistName, DateTimeOffset.UtcNow);
                if (result.StatusCode.HasValue)
                {
                    _logger.LogWarning(
                        result.Exception,
                        "Missing-track export endpoint for {Playlist} returned HTTP {StatusCode}; stopped the candidate scan and will retry in {RetryAfterMinutes} minutes",
                        playlistName,
                        (int)result.StatusCode.Value,
                        retryAfter.TotalMinutes);
                }
                else
                {
                    _logger.LogWarning(
                        result.Exception,
                        "Missing-track export endpoint for {Playlist} was unavailable; stopped the candidate scan and will retry in {RetryAfterMinutes} minutes",
                        playlistName,
                        retryAfter.TotalMinutes);
                }

                return null;
            }
        }

        var missRetryAfter = _retryPolicy.RecordMiss(playlistName, DateTimeOffset.UtcNow);
        if (existingTracks != null || File.Exists(filePath))
        {
            _logger.LogDebug(
                "No newer missing-track export found for {Playlist}; preserving the existing cache and retrying in {RetryAfterMinutes} minutes",
                playlistName,
                missRetryAfter.TotalMinutes);
            if (existingTracks != null)
            {
                await _cache.SetAsync(cacheKey, existingTracks, TimeSpan.FromDays(365));
            }
            else if (File.Exists(filePath))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(filePath, cancellationToken);
                    var tracks = JsonSerializer.Deserialize<List<MissingTrack>>(json);

                    if (tracks != null)
                    {
                        await _cache.SetAsync(cacheKey, tracks, TimeSpan.FromDays(365));
                        _logger.LogDebug(
                            "Restored {Count} cached missing tracks for {Playlist}",
                            tracks.Count,
                            playlistName);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "  Failed to reload cache from file for {Playlist}", playlistName);
                }
            }
        }
        else
        {
            _logger.LogDebug(
                "No missing-track export is available for {Playlist} in the bounded schedule window; retrying in {RetryAfterMinutes} minutes",
                playlistName,
                missRetryAfter.TotalMinutes);
        }

        return foundFileTime;
    }

    private IReadOnlyList<DateTime> BuildCandidateTimes(
        string playlistName,
        DateTime now,
        DateTime? hintTime)
    {
        var candidates = new HashSet<DateTime>();

        static void AddWindow(HashSet<DateTime> target, DateTime center, int radiusMinutes)
        {
            var normalized = new DateTime(
                center.Year,
                center.Month,
                center.Day,
                center.Hour,
                center.Minute,
                0,
                DateTimeKind.Utc);
            for (var offset = -radiusMinutes; offset <= radiusMinutes; offset++)
            {
                target.Add(normalized.AddMinutes(offset));
            }
        }

        if (hintTime.HasValue)
        {
            AddWindow(candidates, hintTime.Value, ScheduledRunToleranceMinutes);
        }
        else
        {
            var schedule = _spotifySettings.Value.GetPlaylistByName(playlistName)?.SyncSchedule;
            var fields = schedule?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields is { Length: >= 2 } &&
                int.TryParse(fields[0], out var minute) &&
                int.TryParse(fields[1], out var hour) &&
                minute is >= 0 and <= 59 &&
                hour is >= 0 and <= 23)
            {
                for (var dayOffset = -2; dayOffset <= 1; dayOffset++)
                {
                    var day = now.Date.AddDays(dayOffset);
                    AddWindow(
                        candidates,
                        new DateTime(day.Year, day.Month, day.Day, hour, minute, 0, DateTimeKind.Utc),
                        ScheduledRunToleranceMinutes);
                }
            }

            AddWindow(candidates, now, ScheduledRunToleranceMinutes);
        }

        return candidates
            .OrderBy(candidate => Math.Abs((candidate - now).TotalMinutes))
            .Take(MaximumCandidateProbes)
            .ToArray();
    }

    private async Task<MissingTrackFileProbeResult> TryFetchMissingTracksFile(
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
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return MissingTrackFileProbeResult.NotFound;
            }

            if (!response.IsSuccessStatusCode)
            {
                return new MissingTrackFileProbeResult(
                    MissingTrackFileProbeStatus.Unavailable,
                    null,
                    response.StatusCode,
                    null);
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var tracks = ParseMissingTracks(json);

            if (tracks != null)
            {
                var cacheKey = CacheKeyBuilder.BuildSpotifyMissingTracksKey(playlistName);

                await _cache.SetAsync(cacheKey, tracks, TimeSpan.FromDays(365));
                await SaveToFileCache(playlistName, tracks);

                _logger.LogInformation(
                    "Cached {Count} missing tracks for {Playlist} from {Filename}",
                    tracks.Count, playlistName, filename);
                return new MissingTrackFileProbeResult(
                    MissingTrackFileProbeStatus.Found,
                    time,
                    response.StatusCode,
                    null);
            }

            return new MissingTrackFileProbeResult(
                MissingTrackFileProbeStatus.Unavailable,
                null,
                response.StatusCode,
                null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new MissingTrackFileProbeResult(
                MissingTrackFileProbeStatus.Unavailable,
                null,
                null,
                ex);
        }
    }

    private List<MissingTrack>? ParseMissingTracks(string json)
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
            return null;
        }

        return tracks;
    }

    private enum MissingTrackFileProbeStatus
    {
        Found,
        NotFound,
        Unavailable
    }

    private readonly record struct MissingTrackFileProbeResult(
        MissingTrackFileProbeStatus Status,
        DateTime? FileTime,
        HttpStatusCode? StatusCode,
        Exception? Exception)
    {
        public static MissingTrackFileProbeResult NotFound { get; } = new(
            MissingTrackFileProbeStatus.NotFound,
            null,
            HttpStatusCode.NotFound,
            null);
    }
}
