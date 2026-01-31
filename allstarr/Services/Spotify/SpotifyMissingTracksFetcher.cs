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
    private readonly IOptions<JellyfinSettings> _jellyfinSettings;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly RedisCacheService _cache;
    private readonly ILogger<SpotifyMissingTracksFetcher> _logger;
    private readonly JellyfinProxyService _proxyService;
    private bool _hasRunOnce = false;
    private Dictionary<string, string> _playlistIdToName = new();

    public SpotifyMissingTracksFetcher(
        IOptions<SpotifyImportSettings> spotifySettings,
        IOptions<JellyfinSettings> jellyfinSettings,
        IHttpClientFactory httpClientFactory,
        RedisCacheService cache,
        JellyfinProxyService proxyService,
        ILogger<SpotifyMissingTracksFetcher> logger)
    {
        _spotifySettings = spotifySettings;
        _jellyfinSettings = jellyfinSettings;
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _proxyService = proxyService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("========================================");
        _logger.LogInformation("SpotifyMissingTracksFetcher: Starting up...");
        
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
        _logger.LogInformation("Configured Playlist IDs: {Count}", _spotifySettings.Value.PlaylistIds.Count);
        
        // Fetch playlist names from Jellyfin
        await LoadPlaylistNamesAsync();
        
        foreach (var kvp in _playlistIdToName)
        {
            _logger.LogInformation("  - {Name} (ID: {Id})", kvp.Value, kvp.Key);
        }
        _logger.LogInformation("========================================");

        // Run once on startup if we haven't run in the last 24 hours
        if (!_hasRunOnce)
        {
            var shouldRunOnStartup = await ShouldRunOnStartupAsync();
            if (shouldRunOnStartup)
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
                _logger.LogInformation("Skipping startup fetch - already ran within last 24 hours");
                _hasRunOnce = true;
            }
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await FetchMissingTracksAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching Spotify missing tracks");
            }

            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }

    private async Task LoadPlaylistNamesAsync()
    {
        _playlistIdToName.Clear();
        
        foreach (var playlistId in _spotifySettings.Value.PlaylistIds)
        {
            try
            {
                var playlistInfo = await _proxyService.GetJsonAsync($"Items/{playlistId}", null, null);
                if (playlistInfo != null && playlistInfo.RootElement.TryGetProperty("Name", out var nameElement))
                {
                    var name = nameElement.GetString() ?? "";
                    if (!string.IsNullOrEmpty(name))
                    {
                        _playlistIdToName[playlistId] = name;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get name for playlist {PlaylistId}", playlistId);
            }
        }
    }

    private async Task<bool> ShouldRunOnStartupAsync()
    {
        // Check if any playlist has cached data from the last 24 hours
        foreach (var playlistName in _playlistIdToName.Values)
        {
            var cacheKey = $"spotify:missing:{playlistName}";
            if (await _cache.ExistsAsync(cacheKey))
            {
                return false; // Already have recent data
            }
        }
        return true; // No recent data, should fetch
    }

    private async Task FetchMissingTracksAsync(CancellationToken cancellationToken)
    {
        var settings = _spotifySettings.Value;
        var now = DateTime.UtcNow;
        var syncStart = now.Date
            .AddHours(settings.SyncStartHour)
            .AddMinutes(settings.SyncStartMinute);
        var syncEnd = syncStart.AddHours(settings.SyncWindowHours);

        if (now < syncStart || now > syncEnd)
        {
            return;
        }

        _logger.LogInformation("Within sync window, fetching missing tracks...");

        foreach (var kvp in _playlistIdToName)
        {
            await FetchPlaylistMissingTracksAsync(kvp.Value, cancellationToken);
        }
    }

    private async Task FetchPlaylistMissingTracksAsync(
        string playlistName,
        CancellationToken cancellationToken)
    {
        var cacheKey = $"spotify:missing:{playlistName}";
        
        if (await _cache.ExistsAsync(cacheKey))
        {
            _logger.LogDebug("Cache already exists for {Playlist}", playlistName);
            return;
        }

        var settings = _spotifySettings.Value;
        var jellyfinUrl = _jellyfinSettings.Value.Url;
        var apiKey = _jellyfinSettings.Value.ApiKey;
        var httpClient = _httpClientFactory.CreateClient();
        var today = DateTime.UtcNow.Date;
        var syncStart = today
            .AddHours(settings.SyncStartHour)
            .AddMinutes(settings.SyncStartMinute);
        var syncEnd = syncStart.AddHours(settings.SyncWindowHours);

        _logger.LogInformation("Searching for missing tracks file for {Playlist}", playlistName);

        for (var time = syncStart; time <= syncEnd; time = time.AddMinutes(5))
        {
            if (cancellationToken.IsCancellationRequested) break;

            var filename = $"{playlistName}_missing_{time:yyyy-MM-dd_HH-mm}.json";
            var url = $"{jellyfinUrl}/Viperinius.Plugin.SpotifyImport/MissingTracksFile" +
                     $"?name={Uri.EscapeDataString(filename)}&api_key={apiKey}";

            try
            {
                _logger.LogDebug("Trying {Filename}", filename);
                var response = await httpClient.GetAsync(url, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync(cancellationToken);
                    var tracks = ParseMissingTracks(json);
                    
                    if (tracks.Count > 0)
                    {
                        await _cache.SetAsync(cacheKey, tracks, TimeSpan.FromHours(24));
                        _logger.LogInformation(
                            "✓ Cached {Count} missing tracks for {Playlist} from {Filename}",
                            tracks.Count, playlistName, filename);
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to fetch {Filename}", filename);
            }
        }
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
