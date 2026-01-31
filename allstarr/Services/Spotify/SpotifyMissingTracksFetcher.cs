using allstarr.Models.Settings;
using allstarr.Models.Spotify;
using allstarr.Services.Common;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace allstarr.Services.Spotify;

public class SpotifyMissingTracksFetcher : BackgroundService
{
    private readonly IOptions<SpotifyImportSettings> _settings;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly RedisCacheService _cache;
    private readonly ILogger<SpotifyMissingTracksFetcher> _logger;

    public SpotifyMissingTracksFetcher(
        IOptions<SpotifyImportSettings> settings,
        IHttpClientFactory httpClientFactory,
        RedisCacheService cache,
        ILogger<SpotifyMissingTracksFetcher> logger)
    {
        _settings = settings;
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_settings.Value.Enabled)
        {
            _logger.LogInformation("Spotify playlist injection is disabled");
            return;
        }

        _logger.LogInformation("Spotify missing tracks fetcher started");

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

    private async Task FetchMissingTracksAsync(CancellationToken cancellationToken)
    {
        var settings = _settings.Value;
        
        if (string.IsNullOrEmpty(settings.ApiKey))
        {
            _logger.LogWarning("Spotify import API key not configured");
            return;
        }

        var now = DateTime.UtcNow;
        var syncStart = now.Date
            .AddHours(settings.SyncStartHour)
            .AddMinutes(settings.SyncStartMinute);
        var syncEnd = syncStart.AddHours(settings.SyncWindowHours);

        if (now < syncStart || now > syncEnd)
        {
            return;
        }

        foreach (var playlist in settings.Playlists.Where(p => p.Enabled))
        {
            await FetchPlaylistMissingTracksAsync(playlist, cancellationToken);
        }
    }

    private async Task FetchPlaylistMissingTracksAsync(
        SpotifyPlaylistConfig playlist,
        CancellationToken cancellationToken)
    {
        var cacheKey = $"spotify:missing:{playlist.SpotifyName}";
        
        if (await _cache.ExistsAsync(cacheKey))
        {
            return;
        }

        var settings = _settings.Value;
        var httpClient = _httpClientFactory.CreateClient();
        var today = DateTime.UtcNow.Date;
        var syncStart = today
            .AddHours(settings.SyncStartHour)
            .AddMinutes(settings.SyncStartMinute);
        var syncEnd = syncStart.AddHours(settings.SyncWindowHours);

        for (var time = syncStart; time <= syncEnd; time = time.AddMinutes(5))
        {
            if (cancellationToken.IsCancellationRequested) break;

            var filename = $"{playlist.SpotifyName}_missing_{time:yyyy-MM-dd_HH-mm}.json";
            var url = $"{settings.JellyfinUrl}/Viperinius.Plugin.SpotifyImport/MissingTracksFile" +
                     $"?name={Uri.EscapeDataString(filename)}&api_key={settings.ApiKey}";

            try
            {
                var response = await httpClient.GetAsync(url, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync(cancellationToken);
                    var tracks = ParseMissingTracks(json);
                    
                    if (tracks.Count > 0)
                    {
                        await _cache.SetAsync(cacheKey, tracks, TimeSpan.FromHours(24));
                        _logger.LogInformation(
                            "Cached {Count} missing tracks for {Playlist} from {Filename}",
                            tracks.Count, playlist.Name, filename);
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
