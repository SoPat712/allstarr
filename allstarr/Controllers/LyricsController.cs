using Microsoft.AspNetCore.Mvc;
using allstarr.Models.Admin;
using allstarr.Services.Common;
using allstarr.Services.Admin;
using allstarr.Services.Spotify;
using allstarr.Filters;
using System.Text.Json;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin")]
[ServiceFilter(typeof(AdminPortFilter))]
public class LyricsController : ControllerBase
{
    private readonly ILogger<LyricsController> _logger;
    private readonly RedisCacheService _cache;
    private readonly AdminHelperService _adminHelper;
    private readonly SpotifyPlaylistFetcher _playlistFetcher;
    private readonly IServiceProvider _serviceProvider;

    public LyricsController(
        ILogger<LyricsController> logger,
        RedisCacheService cache,
        AdminHelperService adminHelper,
        SpotifyPlaylistFetcher playlistFetcher,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _cache = cache;
        _adminHelper = adminHelper;
        _playlistFetcher = playlistFetcher;
        _serviceProvider = serviceProvider;
    }

    
    /// <summary>
    /// Save manual lyrics ID mapping for a track
    /// </summary>
    [HttpPost("lyrics/map")]
    public async Task<IActionResult> SaveLyricsMapping([FromBody] LyricsMappingRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Artist) || string.IsNullOrWhiteSpace(request.Title))
        {
            return BadRequest(new { error = "Artist and Title are required" });
        }
        
        if (request.LyricsId <= 0)
        {
            return BadRequest(new { error = "Valid LyricsId is required" });
        }
        
        try
        {
            // Store lyrics mapping in cache (NO EXPIRATION - manual mappings are permanent)
            var mappingKey = $"lyrics:manual-map:{request.Artist}:{request.Title}";
            await _cache.SetStringAsync(mappingKey, request.LyricsId.ToString());
            
            // Also save to file for persistence across restarts
            await _adminHelper.SaveLyricsMappingToFileAsync(request.Artist, request.Title, request.Album ?? "", request.DurationSeconds, request.LyricsId);
            
            _logger.LogInformation("Manual lyrics mapping saved: {Artist} - {Title} → Lyrics ID {LyricsId}", 
                request.Artist, request.Title, request.LyricsId);
            
            // Optionally fetch and cache the lyrics immediately
            try
            {
                var lyricsService = _serviceProvider.GetService<allstarr.Services.Lyrics.LrclibService>();
                if (lyricsService != null)
                {
                    var lyricsInfo = await lyricsService.GetLyricsByIdAsync(request.LyricsId);
                    if (lyricsInfo != null && !string.IsNullOrEmpty(lyricsInfo.PlainLyrics))
                    {
                        // Cache the lyrics using the standard cache key
                        var lyricsCacheKey = $"lyrics:{request.Artist}:{request.Title}:{request.Album ?? ""}:{request.DurationSeconds}";
                        await _cache.SetAsync(lyricsCacheKey, lyricsInfo.PlainLyrics);
                        _logger.LogDebug("✓ Fetched and cached lyrics for {Artist} - {Title}", request.Artist, request.Title);
                        
                        return Ok(new 
                        { 
                            message = "Lyrics mapping saved and lyrics cached successfully",
                            lyricsId = request.LyricsId,
                            cached = true,
                            lyrics = new
                            {
                                id = lyricsInfo.Id,
                                trackName = lyricsInfo.TrackName,
                                artistName = lyricsInfo.ArtistName,
                                albumName = lyricsInfo.AlbumName,
                                duration = lyricsInfo.Duration,
                                instrumental = lyricsInfo.Instrumental
                            }
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch lyrics after mapping, but mapping was saved");
            }
            
            return Ok(new 
            { 
                message = "Lyrics mapping saved successfully",
                lyricsId = request.LyricsId,
                cached = false
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save lyrics mapping");
            return StatusCode(500, new { error = "Failed to save lyrics mapping" });
        }
    }
    
    /// <summary>
    /// Get manual lyrics mappings
    /// </summary>
    [HttpGet("lyrics/mappings")]
    public async Task<IActionResult> GetLyricsMappings()
    {
        try
        {
            var mappingsFile = "/app/cache/lyrics_mappings.json";
            
            if (!System.IO.File.Exists(mappingsFile))
            {
                return Ok(new { mappings = new List<object>() });
            }
            
            var json = await System.IO.File.ReadAllTextAsync(mappingsFile);
            var mappings = JsonSerializer.Deserialize<List<LyricsMappingEntry>>(json) ?? new List<LyricsMappingEntry>();
            
            return Ok(new { mappings });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get lyrics mappings");
            return StatusCode(500, new { error = "Failed to get lyrics mappings" });
        }
    }
    
    
    
    /// <summary>
    /// Test Spotify lyrics API by fetching lyrics for a specific Spotify track ID
    /// Example: GET /api/admin/lyrics/spotify/test?trackId=3yII7UwgLF6K5zW3xad3MP
    /// </summary>
    [HttpGet("lyrics/spotify/test")]
    public async Task<IActionResult> TestSpotifyLyrics([FromQuery] string trackId)
    {
        if (string.IsNullOrEmpty(trackId))
        {
            return BadRequest(new { error = "trackId parameter is required" });
        }
        
        try
        {
            var spotifyLyricsService = _serviceProvider.GetService<allstarr.Services.Lyrics.SpotifyLyricsService>();
            
            if (spotifyLyricsService == null)
            {
                return StatusCode(500, new { error = "Spotify lyrics service not available" });
            }
            
            _logger.LogInformation("Testing Spotify lyrics for track ID: {TrackId}", trackId);
            
            var result = await spotifyLyricsService.GetLyricsByTrackIdAsync(trackId);
            
            if (result == null)
            {
                return NotFound(new 
                { 
                    error = "No lyrics found",
                    trackId,
                    message = "Lyrics may not be available for this track, or the Spotify API is not configured correctly"
                });
            }
            
            return Ok(new
            {
                success = true,
                trackId = result.SpotifyTrackId,
                syncType = result.SyncType,
                lineCount = result.Lines.Count,
                language = result.Language,
                provider = result.Provider,
                providerDisplayName = result.ProviderDisplayName,
                lines = result.Lines.Select(l => new
                {
                    startTimeMs = l.StartTimeMs,
                    endTimeMs = l.EndTimeMs,
                    words = l.Words
                }).ToList(),
                // Also show LRC format
                lrcFormat = string.Join("\n", result.Lines.Select(l =>
                {
                    var timestamp = TimeSpan.FromMilliseconds(l.StartTimeMs);
                    var mm = (int)timestamp.TotalMinutes;
                    var ss = timestamp.Seconds;
                    var ms = timestamp.Milliseconds / 10;
                    return $"[{mm:D2}:{ss:D2}.{ms:D2}]{l.Words}";
                }))
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to test Spotify lyrics for track {TrackId}", trackId);
            return StatusCode(500, new { error = $"Failed to fetch lyrics: {ex.Message}" });
        }
    }
    
    /// <summary>
    /// Prefetch lyrics for a specific playlist
    /// </summary>
    [HttpPost("playlists/{name}/prefetch-lyrics")]
    public async Task<IActionResult> PrefetchPlaylistLyrics(string name)
    {
        var decodedName = Uri.UnescapeDataString(name);
        
        try
        {
            var lyricsPrefetchService = _serviceProvider.GetService<allstarr.Services.Lyrics.LyricsPrefetchService>();
            
            if (lyricsPrefetchService == null)
            {
                return StatusCode(500, new { error = "Lyrics prefetch service not available" });
            }
            
            _logger.LogInformation("Starting lyrics prefetch for playlist: {Playlist}", decodedName);
            
            var (fetched, cached, missing) = await lyricsPrefetchService.PrefetchPlaylistLyricsAsync(
                decodedName, 
                HttpContext.RequestAborted);
            
            return Ok(new
            {
                message = "Lyrics prefetch complete",
                playlist = decodedName,
                fetched,
                cached,
                missing,
                total = fetched + cached + missing
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to prefetch lyrics for playlist {Playlist}", decodedName);
            return StatusCode(500, new { error = $"Failed to prefetch lyrics: {ex.Message}" });
        }
    }
    
    
    
    /// <summary>
    /// Invalidates the cached playlist summary so it will be regenerated on next request
    /// </summary>
}
