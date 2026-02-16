using Microsoft.AspNetCore.Mvc;
using allstarr.Models.Admin;
using allstarr.Services.Common;
using allstarr.Services.Admin;
using allstarr.Filters;
using System.Text.Json;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin")]
[ServiceFilter(typeof(AdminPortFilter))]
public class MappingController : ControllerBase
{
    private readonly ILogger<MappingController> _logger;
    private readonly RedisCacheService _cache;
    private readonly AdminHelperService _adminHelper;

    public MappingController(
        ILogger<MappingController> logger,
        RedisCacheService cache,
        AdminHelperService adminHelper)
    {
        _logger = logger;
        _cache = cache;
        _adminHelper = adminHelper;
    }

    
    /// <summary>
    /// Save lyrics mapping to file for persistence across restarts.
    /// Lyrics mappings NEVER expire - they are permanent user decisions.
    /// </summary>
    [HttpGet("mappings/tracks")]
    public async Task<IActionResult> GetAllTrackMappings()
    {
        try
        {
            var mappingsDir = "/app/cache/mappings";
            var allMappings = new List<object>();
            
            if (!Directory.Exists(mappingsDir))
            {
                return Ok(new { mappings = allMappings, totalCount = 0 });
            }
            
            var files = Directory.GetFiles(mappingsDir, "*_mappings.json");
            
            foreach (var file in files)
            {
                try
                {
                    var json = await System.IO.File.ReadAllTextAsync(file);
                    var playlistMappings = JsonSerializer.Deserialize<Dictionary<string, ManualMappingEntry>>(json);
                    
                    if (playlistMappings != null)
                    {
                        var fileName = Path.GetFileNameWithoutExtension(file);
                        var playlistName = fileName.Replace("_mappings", "").Replace("_", " ");
                        
                        foreach (var mapping in playlistMappings.Values)
                        {
                            allMappings.Add(new
                            {
                                playlist = playlistName,
                                spotifyId = mapping.SpotifyId,
                                type = !string.IsNullOrEmpty(mapping.JellyfinId) ? "jellyfin" : "external",
                                jellyfinId = mapping.JellyfinId,
                                externalProvider = mapping.ExternalProvider,
                                externalId = mapping.ExternalId,
                                createdAt = mapping.CreatedAt
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to read mapping file {File}", file);
                }
            }
            
            return Ok(new 
            { 
                mappings = allMappings.OrderBy(m => ((dynamic)m).playlist).ThenBy(m => ((dynamic)m).createdAt),
                totalCount = allMappings.Count,
                jellyfinCount = allMappings.Count(m => ((dynamic)m).type == "jellyfin"),
                externalCount = allMappings.Count(m => ((dynamic)m).type == "external")
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get track mappings");
            return StatusCode(500, new { error = "Failed to get track mappings" });
        }
    }
    
    /// <summary>
    /// Delete a manual track mapping
    /// </summary>
    [HttpDelete("mappings/tracks")]
    public async Task<IActionResult> DeleteTrackMapping([FromQuery] string playlist, [FromQuery] string spotifyId)
    {
        if (string.IsNullOrEmpty(playlist) || string.IsNullOrEmpty(spotifyId))
        {
            return BadRequest(new { error = "playlist and spotifyId parameters are required" });
        }
        
        try
        {
            var mappingsDir = "/app/cache/mappings";
            var safeName = AdminHelperService.SanitizeFileName(playlist);
            var filePath = Path.Combine(mappingsDir, $"{safeName}_mappings.json");
            
            if (!System.IO.File.Exists(filePath))
            {
                return NotFound(new { error = "Mapping file not found for playlist" });
            }
            
            // Load existing mappings
            var json = await System.IO.File.ReadAllTextAsync(filePath);
            var mappings = JsonSerializer.Deserialize<Dictionary<string, ManualMappingEntry>>(json);
            
            if (mappings == null || !mappings.ContainsKey(spotifyId))
            {
                return NotFound(new { error = "Mapping not found" });
            }
            
            // Remove the mapping
            mappings.Remove(spotifyId);
            
            // Save back to file (or delete file if empty)
            if (mappings.Count == 0)
            {
                System.IO.File.Delete(filePath);
                _logger.LogInformation("🗑️ Deleted empty mapping file for playlist {Playlist}", playlist);
            }
            else
            {
                var updatedJson = JsonSerializer.Serialize(mappings, new JsonSerializerOptions { WriteIndented = true });
                await System.IO.File.WriteAllTextAsync(filePath, updatedJson);
                _logger.LogInformation("🗑️ Deleted mapping: {Playlist} - {SpotifyId}", playlist, spotifyId);
            }
            
            // Also remove from Redis cache
            var cacheKey = $"manual:mapping:{playlist}:{spotifyId}";
            await _cache.DeleteAsync(cacheKey);
            
            return Ok(new { success = true, message = "Mapping deleted successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete track mapping for {Playlist} - {SpotifyId}", playlist, spotifyId);
            return StatusCode(500, new { error = "Failed to delete track mapping" });
        }
    }
    
    /// <summary>
    /// Test Spotify lyrics API by fetching lyrics for a specific Spotify track ID
    /// Example: GET /api/admin/lyrics/spotify/test?trackId=3yII7UwgLF6K5zW3xad3MP
    /// </summary>
}
