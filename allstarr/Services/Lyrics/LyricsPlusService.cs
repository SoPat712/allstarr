using System.Text.Json;
using System.Text.Json.Serialization;
using allstarr.Models.Lyrics;
using allstarr.Services.Common;

namespace allstarr.Services.Lyrics;

/// <summary>
/// Service for fetching lyrics from LyricsPlus API (https://lyricsplus.prjktla.workers.dev)
/// Supports multiple sources: Apple Music, Spotify, Musixmatch, and more
/// </summary>
public class LyricsPlusService
{
    private readonly HttpClient _httpClient;
    private readonly RedisCacheService _cache;
    private readonly ILogger<LyricsPlusService> _logger;
    private const string BaseUrl = "https://lyricsplus.prjktla.workers.dev/v2/lyrics/get";

    public LyricsPlusService(
        IHttpClientFactory httpClientFactory,
        RedisCacheService cache,
        ILogger<LyricsPlusService> logger)
    {
        _httpClient = httpClientFactory.CreateClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Allstarr/1.0.0 (https://github.com/SoPat712/allstarr)");
        _cache = cache;
        _logger = logger;
    }

    public async Task<LyricsInfo?> GetLyricsAsync(string trackName, string artistName, string? albumName, int durationSeconds)
    {
        return await GetLyricsAsync(trackName, new[] { artistName }, albumName, durationSeconds);
    }

    public async Task<LyricsInfo?> GetLyricsAsync(string trackName, string[] artistNames, string? albumName, int durationSeconds)
    {
        // Validate input parameters
        if (string.IsNullOrWhiteSpace(trackName) || artistNames == null || artistNames.Length == 0)
        {
            _logger.LogDebug("Invalid parameters for LyricsPlus search: trackName={TrackName}, artistCount={ArtistCount}", 
                trackName, artistNames?.Length ?? 0);
            return null;
        }
        
        var artistName = string.Join(", ", artistNames);
        var cacheKey = $"lyricsplus:{artistName}:{trackName}:{albumName}:{durationSeconds}";
        
        // Check cache
        var cached = await _cache.GetStringAsync(cacheKey);
        if (!string.IsNullOrEmpty(cached))
        {
            try
            {
                return JsonSerializer.Deserialize<LyricsInfo>(cached, JsonOptions);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to deserialize cached LyricsPlus lyrics");
            }
        }

        try
        {
            // Build URL with query parameters
            var url = $"{BaseUrl}?title={Uri.EscapeDataString(trackName)}&artist={Uri.EscapeDataString(artistName)}";
            
            if (!string.IsNullOrEmpty(albumName))
            {
                url += $"&album={Uri.EscapeDataString(albumName)}";
            }
            
            if (durationSeconds > 0)
            {
                url += $"&duration={durationSeconds}";
            }
            
            // Add sources: apple, lyricsplus, musixmatch, spotify, musixmatch-word
            url += "&source=apple,lyricsplus,musixmatch,spotify,musixmatch-word";

            _logger.LogDebug("Fetching lyrics from LyricsPlus: {Url}", url);

            var response = await _httpClient.GetAsync(url);
            
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogDebug("Lyrics not found on LyricsPlus for {Artist} - {Track}", artistName, trackName);
                return null;
            }

            response.EnsureSuccessStatusCode();
            
            var json = await response.Content.ReadAsStringAsync();
            var lyricsResponse = JsonSerializer.Deserialize<LyricsPlusResponse>(json, JsonOptions);

            if (lyricsResponse == null || lyricsResponse.Lyrics == null || lyricsResponse.Lyrics.Count == 0)
            {
                _logger.LogDebug("Empty lyrics response from LyricsPlus for {Artist} - {Track}", artistName, trackName);
                return null;
            }

            // Convert to LyricsInfo format
            var result = ConvertToLyricsInfo(lyricsResponse, trackName, artistName, albumName, durationSeconds);
            
            if (result != null)
            {
                await _cache.SetStringAsync(cacheKey, JsonSerializer.Serialize(result, JsonOptions), TimeSpan.FromDays(30));
                _logger.LogInformation("✓ Retrieved lyrics from LyricsPlus for {Artist} - {Track} (type: {Type}, source: {Source})", 
                    artistName, trackName, lyricsResponse.Type, lyricsResponse.Metadata?.Source);
            }
            
            return result;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Failed to fetch lyrics from LyricsPlus for {Artist} - {Track}", artistName, trackName);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching lyrics from LyricsPlus for {Artist} - {Track}", artistName, trackName);
            return null;
        }
    }

    private LyricsInfo? ConvertToLyricsInfo(LyricsPlusResponse response, string trackName, string artistName, string? albumName, int durationSeconds)
    {
        if (response.Lyrics == null || response.Lyrics.Count == 0)
        {
            return null;
        }

        string? syncedLyrics = null;
        string? plainLyrics = null;

        // Convert based on type
        if (response.Type == "Word")
        {
            // Word-level timing - convert to line-level LRC
            syncedLyrics = ConvertWordTimingToLrc(response.Lyrics);
            plainLyrics = string.Join("\n", response.Lyrics.Select(l => l.Text));
        }
        else if (response.Type == "Line")
        {
            // Line-level timing - convert to LRC
            syncedLyrics = ConvertLineTimingToLrc(response.Lyrics);
            plainLyrics = string.Join("\n", response.Lyrics.Select(l => l.Text));
        }
        else
        {
            // Static or unknown type - just plain text
            plainLyrics = string.Join("\n", response.Lyrics.Select(l => l.Text));
        }

        return new LyricsInfo
        {
            TrackName = trackName,
            ArtistName = artistName,
            AlbumName = albumName ?? string.Empty,
            Duration = durationSeconds,
            Instrumental = false,
            PlainLyrics = plainLyrics,
            SyncedLyrics = syncedLyrics
        };
    }

    private string ConvertLineTimingToLrc(List<LyricsPlusLine> lines)
    {
        var lrcLines = new List<string>();
        
        foreach (var line in lines)
        {
            if (line.Time.HasValue)
            {
                var timestamp = TimeSpan.FromMilliseconds(line.Time.Value);
                var mm = (int)timestamp.TotalMinutes;
                var ss = timestamp.Seconds;
                var cs = timestamp.Milliseconds / 10; // Convert to centiseconds
                
                lrcLines.Add($"[{mm:D2}:{ss:D2}.{cs:D2}]{line.Text}");
            }
            else
            {
                // No timing, just add the text
                lrcLines.Add(line.Text);
            }
        }
        
        return string.Join("\n", lrcLines);
    }

    private string ConvertWordTimingToLrc(List<LyricsPlusLine> lines)
    {
        // For word-level timing, we use the line start time
        // (word-level detail is in syllabus array but we simplify to line-level for LRC)
        return ConvertLineTimingToLrc(lines);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private class LyricsPlusResponse
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty; // "Word", "Line", or "Static"
        
        [JsonPropertyName("metadata")]
        public LyricsPlusMetadata? Metadata { get; set; }
        
        [JsonPropertyName("lyrics")]
        public List<LyricsPlusLine> Lyrics { get; set; } = new();
    }

    private class LyricsPlusMetadata
    {
        [JsonPropertyName("source")]
        public string? Source { get; set; }
        
        [JsonPropertyName("title")]
        public string? Title { get; set; }
        
        [JsonPropertyName("language")]
        public string? Language { get; set; }
    }

    private class LyricsPlusLine
    {
        [JsonPropertyName("time")]
        public long? Time { get; set; } // Milliseconds
        
        [JsonPropertyName("duration")]
        public long? Duration { get; set; }
        
        [JsonPropertyName("text")]
        public string Text { get; set; } = string.Empty;
        
        [JsonPropertyName("syllabus")]
        public List<LyricsPlusSyllable>? Syllabus { get; set; }
    }

    private class LyricsPlusSyllable
    {
        [JsonPropertyName("time")]
        public long Time { get; set; }
        
        [JsonPropertyName("duration")]
        public long Duration { get; set; }
        
        [JsonPropertyName("text")]
        public string Text { get; set; } = string.Empty;
    }
}
