using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using allstarr.Models.Lyrics;
using allstarr.Models.Settings;
using allstarr.Services.Common;
using allstarr.Services.Spotify;
using Microsoft.Extensions.Options;

namespace allstarr.Services.Lyrics;

/// <summary>
/// Service for fetching synchronized lyrics from Spotify's internal color-lyrics API.
/// 
/// Spotify's lyrics API provides:
/// - Line-by-line synchronized lyrics with precise timestamps
/// - Word-level timing for karaoke-style display (syllable sync)
/// - Background color suggestions based on album art
/// - Support for multiple languages and translations
/// 
/// This requires the sp_dc session cookie for authentication.
/// </summary>
public class SpotifyLyricsService
{
    private readonly ILogger<SpotifyLyricsService> _logger;
    private readonly SpotifyApiSettings _settings;
    private readonly RedisCacheService _cache;
    private readonly HttpClient _httpClient;
    
    public SpotifyLyricsService(
        ILogger<SpotifyLyricsService> logger,
        IOptions<SpotifyApiSettings> settings,
        RedisCacheService cache,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _settings = settings.Value;
        _cache = cache;
        
        _httpClient = httpClientFactory.CreateClient();
        _httpClient.Timeout = TimeSpan.FromSeconds(10);
    }
    
    /// <summary>
    /// Gets synchronized lyrics for a Spotify track by its ID using the sidecar API.
    /// </summary>
    /// <param name="spotifyTrackId">Spotify track ID (e.g., "3a8mo25v74BMUOJ1IDUEBL")</param>
    /// <returns>Lyrics info with synced lyrics in LRC format, or null if not available</returns>
    public async Task<SpotifyLyricsResult?> GetLyricsByTrackIdAsync(string spotifyTrackId)
    {
        if (!_settings.Enabled || string.IsNullOrEmpty(_settings.SessionCookie))
        {
            _logger.LogInformation("Spotify API not enabled or no session cookie configured");
            return null;
        }
        
        if (string.IsNullOrEmpty(_settings.LyricsApiUrl))
        {
            _logger.LogInformation("Spotify lyrics API URL not configured");
            return null;
        }
        
        // Normalize track ID (remove URI prefix if present)
        spotifyTrackId = ExtractTrackId(spotifyTrackId);
        
        // NO CACHING - Spotify lyrics come from local Docker container (fast)
        try
        {
            var url = $"{_settings.LyricsApiUrl}/?trackid={spotifyTrackId}&format=id3";
            
            _logger.LogDebug("Fetching lyrics from sidecar API: {Url}", url);
            
            var response = await _httpClient.GetAsync(url);
            
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Sidecar API returned {StatusCode} for track {TrackId}", 
                    response.StatusCode, spotifyTrackId);
                return null;
            }
            
            var json = await response.Content.ReadAsStringAsync();
            var result = ParseSidecarResponse(json, spotifyTrackId);
            
            if (result != null)
            {
                _logger.LogDebug("Got Spotify lyrics from sidecar for track {TrackId} ({LineCount} lines)", 
                    spotifyTrackId, result.Lines.Count);
            }
            
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching lyrics from sidecar API for track {TrackId}", spotifyTrackId);
            return null;
        }
    }
    
    /// <summary>
    /// Searches for a track on Spotify and returns its lyrics using the sidecar API.
    /// Useful when you have track metadata but not a Spotify ID.
    /// Note: This requires the sidecar to handle search, or we skip it.
    /// </summary>
    public async Task<SpotifyLyricsResult?> SearchAndGetLyricsAsync(
        string trackName, 
        string artistName, 
        string? albumName = null,
        int? durationMs = null)
    {
        if (!_settings.Enabled || string.IsNullOrEmpty(_settings.SessionCookie))
        {
            _logger.LogInformation("Spotify lyrics search skipped: API not enabled or no session cookie");
            return null;
        }
        
        // The sidecar API only supports track ID, not search
        // So we skip Spotify lyrics for search-based requests
        // LRCLib will be used as fallback
        _logger.LogWarning("Spotify lyrics search by metadata not supported with sidecar API, skipping");
        return null;
    }
    
    /// <summary>
    /// Converts Spotify lyrics to LRCLIB-compatible LyricsInfo format.
    /// </summary>
    public LyricsInfo? ToLyricsInfo(SpotifyLyricsResult spotifyLyrics)
    {
        if (spotifyLyrics.Lines.Count == 0)
        {
            return null;
        }
        
        // Build synced lyrics in LRC format
        var lrcLines = new List<string>();
        foreach (var line in spotifyLyrics.Lines)
        {
            var timestamp = TimeSpan.FromMilliseconds(line.StartTimeMs);
            var mm = (int)timestamp.TotalMinutes;
            var ss = timestamp.Seconds;
            var ms = timestamp.Milliseconds / 10; // LRC uses centiseconds
            
            lrcLines.Add($"[{mm:D2}:{ss:D2}.{ms:D2}]{line.Words}");
        }
        
        return new LyricsInfo
        {
            TrackName = spotifyLyrics.TrackName ?? "",
            ArtistName = spotifyLyrics.ArtistName ?? "",
            AlbumName = spotifyLyrics.AlbumName ?? "",
            Duration = (int)(spotifyLyrics.DurationMs / 1000),
            Instrumental = spotifyLyrics.Lines.Count == 0,
            SyncedLyrics = string.Join("\n", lrcLines),
            PlainLyrics = string.Join("\n", spotifyLyrics.Lines.Select(l => l.Words))
        };
    }
    
    /// <summary>
    /// Parses the response from the sidecar spotify-lyrics-api service.
    /// Format: {"error": false, "syncType": "LINE_SYNCED", "lines": [...]}
    /// </summary>
    private SpotifyLyricsResult? ParseSidecarResponse(string json, string trackId)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            
            // Check for error
            if (root.TryGetProperty("error", out var error) && error.GetBoolean())
            {
                _logger.LogError("Sidecar API returned error for track {TrackId}", trackId);
                return null;
            }
            
            var result = new SpotifyLyricsResult
            {
                SpotifyTrackId = trackId
            };
            
            // Get sync type
            if (root.TryGetProperty("syncType", out var syncType))
            {
                result.SyncType = syncType.GetString() ?? "LINE_SYNCED";
            }
            
            // Parse lines
            if (root.TryGetProperty("lines", out var lines))
            {
                foreach (var line in lines.EnumerateArray())
                {
                    var lyricsLine = new SpotifyLyricsLine
                    {
                        StartTimeMs = line.TryGetProperty("startTimeMs", out var start) 
                            ? long.Parse(start.GetString() ?? "0") : 0,
                        Words = line.TryGetProperty("words", out var words) 
                            ? words.GetString() ?? "" : "",
                        EndTimeMs = line.TryGetProperty("endTimeMs", out var end)
                            ? long.Parse(end.GetString() ?? "0") : 0
                    };
                    
                    result.Lines.Add(lyricsLine);
                }
            }
            
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing sidecar API response");
            return null;
        }
    }
    
    private static string ExtractTrackId(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        
        // Handle spotify:track:xxxxx format
        if (input.StartsWith("spotify:track:"))
        {
            return input.Substring("spotify:track:".Length);
        }
        
        // Handle https://open.spotify.com/track/xxxxx format
        if (input.Contains("open.spotify.com/track/"))
        {
            var start = input.IndexOf("/track/") + "/track/".Length;
            var end = input.IndexOf('?', start);
            return end > 0 ? input.Substring(start, end - start) : input.Substring(start);
        }
        
        return input;
    }
}

/// <summary>
/// Result from Spotify's color-lyrics API.
/// </summary>
public class SpotifyLyricsResult
{
    public string SpotifyTrackId { get; set; } = string.Empty;
    public string? TrackName { get; set; }
    public string? ArtistName { get; set; }
    public string? AlbumName { get; set; }
    public long DurationMs { get; set; }
    
    /// <summary>
    /// Sync type: "LINE_SYNCED", "SYLLABLE_SYNCED", or "UNSYNCED"
    /// </summary>
    public string SyncType { get; set; } = "LINE_SYNCED";
    
    /// <summary>
    /// Language code (e.g., "en", "es", "ja")
    /// </summary>
    public string? Language { get; set; }
    
    /// <summary>
    /// Lyrics provider (e.g., "MusixMatch", "Spotify")
    /// </summary>
    public string? Provider { get; set; }
    
    public string? ProviderDisplayName { get; set; }
    
    /// <summary>
    /// Lyrics lines in order
    /// </summary>
    public List<SpotifyLyricsLine> Lines { get; set; } = new();
    
    /// <summary>
    /// Color suggestions based on album art
    /// </summary>
    public SpotifyLyricsColors? Colors { get; set; }
}

public class SpotifyLyricsLine
{
    /// <summary>
    /// Start time in milliseconds
    /// </summary>
    public long StartTimeMs { get; set; }
    
    /// <summary>
    /// End time in milliseconds
    /// </summary>
    public long EndTimeMs { get; set; }
    
    /// <summary>
    /// The lyrics text for this line
    /// </summary>
    public string Words { get; set; } = string.Empty;
    
    /// <summary>
    /// Syllable-level timing for karaoke display (if available)
    /// </summary>
    public List<SpotifyLyricsSyllable> Syllables { get; set; } = new();
}

public class SpotifyLyricsSyllable
{
    public long StartTimeMs { get; set; }
    public string Text { get; set; } = string.Empty;
}

public class SpotifyLyricsColors
{
    /// <summary>
    /// Suggested background color (ARGB integer)
    /// </summary>
    public int? Background { get; set; }
    
    /// <summary>
    /// Suggested text color (ARGB integer)
    /// </summary>
    public int? Text { get; set; }
    
    /// <summary>
    /// Suggested highlight/active text color (ARGB integer)
    /// </summary>
    public int? HighlightText { get; set; }
}
