using System.Text.Json;
using System.Text.Json.Serialization;
using allstarr.Models.Lyrics;
using allstarr.Services.Common;

namespace allstarr.Services.Lyrics;

public class LrclibService
{
    private readonly HttpClient _httpClient;
    private readonly RedisCacheService _cache;
    private readonly ILogger<LrclibService> _logger;
    private const string BaseUrl = "https://lrclib.net/api";

    public LrclibService(
        IHttpClientFactory httpClientFactory,
        RedisCacheService cache,
        ILogger<LrclibService> logger)
    {
        _httpClient = httpClientFactory.CreateClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Allstarr/1.0.0 (https://github.com/SoPat712/allstarr)");
        _cache = cache;
        _logger = logger;
    }

    public async Task<LyricsInfo?> GetLyricsAsync(string trackName, string artistName, string albumName, int durationSeconds)
    {
        var cacheKey = $"lyrics:{artistName}:{trackName}:{albumName}:{durationSeconds}";
        
        var cached = await _cache.GetStringAsync(cacheKey);
        if (!string.IsNullOrEmpty(cached))
        {
            try
            {
                return JsonSerializer.Deserialize<LyricsInfo>(cached, JsonOptions);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to deserialize cached lyrics");
            }
        }

        try
        {
            var url = $"{BaseUrl}/get?" +
                     $"track_name={Uri.EscapeDataString(trackName)}&" +
                     $"artist_name={Uri.EscapeDataString(artistName)}&" +
                     $"album_name={Uri.EscapeDataString(albumName)}&" +
                     $"duration={durationSeconds}";

            _logger.LogDebug("Fetching lyrics from LRCLIB: {Url}", url);

            var response = await _httpClient.GetAsync(url);
            
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogDebug("Lyrics not found for {Artist} - {Track}", artistName, trackName);
                return null;
            }

            response.EnsureSuccessStatusCode();
            
            var json = await response.Content.ReadAsStringAsync();
            var lyrics = JsonSerializer.Deserialize<LrclibResponse>(json, JsonOptions);

            if (lyrics == null)
            {
                return null;
            }

            var result = new LyricsInfo
            {
                Id = lyrics.Id,
                TrackName = lyrics.TrackName ?? trackName,
                ArtistName = lyrics.ArtistName ?? artistName,
                AlbumName = lyrics.AlbumName ?? albumName,
                Duration = lyrics.Duration,
                Instrumental = lyrics.Instrumental,
                PlainLyrics = lyrics.PlainLyrics,
                SyncedLyrics = lyrics.SyncedLyrics
            };

            await _cache.SetStringAsync(cacheKey, JsonSerializer.Serialize(result, JsonOptions), TimeSpan.FromDays(30));

            _logger.LogInformation("Retrieved lyrics for {Artist} - {Track} (ID: {Id})", artistName, trackName, lyrics.Id);
            
            return result;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Failed to fetch lyrics from LRCLIB for {Artist} - {Track}", artistName, trackName);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching lyrics for {Artist} - {Track}", artistName, trackName);
            return null;
        }
    }

    public async Task<LyricsInfo?> GetLyricsCachedAsync(string trackName, string artistName, string albumName, int durationSeconds)
    {
        try
        {
            var url = $"{BaseUrl}/get-cached?" +
                     $"track_name={Uri.EscapeDataString(trackName)}&" +
                     $"artist_name={Uri.EscapeDataString(artistName)}&" +
                     $"album_name={Uri.EscapeDataString(albumName)}&" +
                     $"duration={durationSeconds}";

            var response = await _httpClient.GetAsync(url);
            
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }

            response.EnsureSuccessStatusCode();
            
            var json = await response.Content.ReadAsStringAsync();
            var lyrics = JsonSerializer.Deserialize<LrclibResponse>(json, JsonOptions);

            if (lyrics == null)
            {
                return null;
            }

            return new LyricsInfo
            {
                Id = lyrics.Id,
                TrackName = lyrics.TrackName ?? trackName,
                ArtistName = lyrics.ArtistName ?? artistName,
                AlbumName = lyrics.AlbumName ?? albumName,
                Duration = lyrics.Duration,
                Instrumental = lyrics.Instrumental,
                PlainLyrics = lyrics.PlainLyrics,
                SyncedLyrics = lyrics.SyncedLyrics
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch cached lyrics for {Artist} - {Track}", artistName, trackName);
            return null;
        }
    }

    public async Task<LyricsInfo?> GetLyricsByIdAsync(int id)
    {
        var cacheKey = $"lyrics:id:{id}";
        
        var cached = await _cache.GetStringAsync(cacheKey);
        if (!string.IsNullOrEmpty(cached))
        {
            try
            {
                return JsonSerializer.Deserialize<LyricsInfo>(cached, JsonOptions);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to deserialize cached lyrics");
            }
        }

        try
        {
            var url = $"{BaseUrl}/get/{id}";
            var response = await _httpClient.GetAsync(url);
            
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }

            response.EnsureSuccessStatusCode();
            
            var json = await response.Content.ReadAsStringAsync();
            var lyrics = JsonSerializer.Deserialize<LrclibResponse>(json, JsonOptions);

            if (lyrics == null)
            {
                return null;
            }

            var result = new LyricsInfo
            {
                Id = lyrics.Id,
                TrackName = lyrics.TrackName ?? string.Empty,
                ArtistName = lyrics.ArtistName ?? string.Empty,
                AlbumName = lyrics.AlbumName ?? string.Empty,
                Duration = lyrics.Duration,
                Instrumental = lyrics.Instrumental,
                PlainLyrics = lyrics.PlainLyrics,
                SyncedLyrics = lyrics.SyncedLyrics
            };

            await _cache.SetStringAsync(cacheKey, JsonSerializer.Serialize(result, JsonOptions), TimeSpan.FromDays(30));
            
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching lyrics by ID {Id}", id);
            return null;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private class LrclibResponse
    {
        public int Id { get; set; }
        public string? TrackName { get; set; }
        public string? ArtistName { get; set; }
        public string? AlbumName { get; set; }
        public int Duration { get; set; }
        public bool Instrumental { get; set; }
        public string? PlainLyrics { get; set; }
        public string? SyncedLyrics { get; set; }
    }
}
