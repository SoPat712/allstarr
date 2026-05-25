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
public class MappingController : ControllerBase
{
    private readonly ILogger<MappingController> _logger;
    private readonly RedisCacheService _cache;
    private readonly AdminHelperService _adminHelper;
    private readonly SpotifyMappingService _mappingService;

    public MappingController(
        ILogger<MappingController> logger,
        RedisCacheService cache,
        AdminHelperService adminHelper,
        SpotifyMappingService mappingService)
    {
        _logger = logger;
        _cache = cache;
        _adminHelper = adminHelper;
        _mappingService = mappingService;
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
                            var targets = await BuildExternalTargetsForManualMappingAsync(mapping);
                            allMappings.Add(new
                            {
                                playlist = playlistName,
                                spotifyId = mapping.SpotifyId,
                                type = !string.IsNullOrEmpty(mapping.JellyfinId) ? "jellyfin" : "external",
                                jellyfinId = mapping.JellyfinId,
                                externalProvider = mapping.ExternalProvider,
                                externalId = mapping.ExternalId,
                                externalTargets = targets,
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
    public async Task<IActionResult> DeleteTrackMapping(
        [FromQuery] string playlist,
        [FromQuery] string spotifyId,
        [FromQuery] string? provider = null)
    {
        if (string.IsNullOrEmpty(playlist) || string.IsNullOrEmpty(spotifyId))
        {
            return BadRequest(new { error = "playlist and spotifyId parameters are required" });
        }
        
        try
        {
            var removedPlaylistManual = false;
            var removedGlobal = false;

            if (!string.IsNullOrWhiteSpace(provider))
            {
                removedGlobal = await _mappingService.RemoveExternalProviderAsync(spotifyId, provider);
                removedPlaylistManual = await TryRemovePlaylistManualProviderAsync(
                    playlist,
                    spotifyId,
                    provider);
            }
            else
            {
                removedPlaylistManual = await TryRemovePlaylistManualMappingAsync(playlist, spotifyId);
                if (removedPlaylistManual)
                {
                    var cacheKey = $"manual:mapping:{playlist}:{spotifyId}";
                    await _cache.DeleteAsync(cacheKey);
                }

                removedGlobal = await _mappingService.DeleteMappingAsync(spotifyId);
            }

            if (!removedPlaylistManual && !removedGlobal)
            {
                return NotFound(new { error = "Mapping not found" });
            }

            return Ok(new { success = true, message = "Mapping deleted successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete track mapping for {Playlist} - {SpotifyId}", playlist, spotifyId);
            return StatusCode(500, new { error = "Failed to delete track mapping" });
        }
    }

    private async Task<List<object>> BuildExternalTargetsForManualMappingAsync(ManualMappingEntry mapping)
    {
        var targets = new List<object>();
        var seenProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddTarget(string? provider, string? externalId, string source)
        {
            if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(externalId))
            {
                return;
            }

            var key = provider.Trim().ToLowerInvariant();
            if (!seenProviders.Add(key))
            {
                return;
            }

            targets.Add(new
            {
                provider,
                externalId,
                source
            });
        }

        var global = await _mappingService.GetMappingAsync(mapping.SpotifyId);
        if (global != null)
        {
            foreach (var external in global.ExternalMappings)
            {
                AddTarget(external.Provider, external.ExternalId, external.Source);
            }

            AddTarget(global.ExternalProvider, global.ExternalId, global.Source);
        }

        AddTarget(mapping.ExternalProvider, mapping.ExternalId, "manual");

        return targets;
    }

    private async Task<bool> TryRemovePlaylistManualProviderAsync(
        string playlist,
        string spotifyId,
        string provider)
    {
        var mappingsDir = "/app/cache/mappings";
        var safeName = AdminHelperService.SanitizeFileName(playlist);
        var filePath = Path.Combine(mappingsDir, $"{safeName}_mappings.json");

        if (!System.IO.File.Exists(filePath))
        {
            return false;
        }

        var json = await System.IO.File.ReadAllTextAsync(filePath);
        var mappings = JsonSerializer.Deserialize<Dictionary<string, ManualMappingEntry>>(json);
        if (mappings == null || !mappings.TryGetValue(spotifyId, out var entry))
        {
            return false;
        }

        if (!string.Equals(entry.ExternalProvider, provider, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        mappings.Remove(spotifyId);
        await SavePlaylistMappingsFileAsync(filePath, mappings, playlist, spotifyId);
        return true;
    }

    private async Task<bool> TryRemovePlaylistManualMappingAsync(string playlist, string spotifyId)
    {
        var mappingsDir = "/app/cache/mappings";
        var safeName = AdminHelperService.SanitizeFileName(playlist);
        var filePath = Path.Combine(mappingsDir, $"{safeName}_mappings.json");

        if (!System.IO.File.Exists(filePath))
        {
            return false;
        }

        var json = await System.IO.File.ReadAllTextAsync(filePath);
        var mappings = JsonSerializer.Deserialize<Dictionary<string, ManualMappingEntry>>(json);
        if (mappings == null || !mappings.ContainsKey(spotifyId))
        {
            return false;
        }

        mappings.Remove(spotifyId);
        await SavePlaylistMappingsFileAsync(filePath, mappings, playlist, spotifyId);
        return true;
    }

    private async Task SavePlaylistMappingsFileAsync(
        string filePath,
        Dictionary<string, ManualMappingEntry> mappings,
        string playlist,
        string spotifyId)
    {
        if (mappings.Count == 0)
        {
            System.IO.File.Delete(filePath);
            _logger.LogInformation("🗑️ Deleted empty mapping file for playlist {Playlist}", playlist);
            return;
        }

        var updatedJson = JsonSerializer.Serialize(mappings, new JsonSerializerOptions { WriteIndented = true });
        await System.IO.File.WriteAllTextAsync(filePath, updatedJson);
        _logger.LogInformation("🗑️ Deleted mapping: {Playlist} - {SpotifyId}", playlist, spotifyId);
    }
    
    /// <summary>
    /// Test Spotify lyrics API by fetching lyrics for a specific Spotify track ID
    /// Example: GET /api/admin/lyrics/spotify/test?trackId=3yII7UwgLF6K5zW3xad3MP
    /// </summary>
}
