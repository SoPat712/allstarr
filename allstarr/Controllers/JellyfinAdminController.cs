using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using allstarr.Models.Settings;
using allstarr.Models.Admin;
using allstarr.Services.Admin;
using allstarr.Services.Common;
using allstarr.Filters;
using System.Text.Json;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin")]
[ServiceFilter(typeof(AdminPortFilter))]
public class JellyfinAdminController : ControllerBase
{
    private readonly ILogger<JellyfinAdminController> _logger;
    private readonly JellyfinSettings _jellyfinSettings;
    private readonly HttpClient _jellyfinHttpClient;
    private readonly AdminHelperService _helperService;
    private readonly RedisCacheService _cache;
    private readonly IConfiguration _configuration;
    private readonly SpotifyImportSettings _spotifyImportSettings;

    public JellyfinAdminController(
        ILogger<JellyfinAdminController> logger,
        IOptions<JellyfinSettings> jellyfinSettings,
        IHttpClientFactory httpClientFactory,
        AdminHelperService helperService,
        RedisCacheService cache,
        IConfiguration configuration,
        IOptions<SpotifyImportSettings> spotifyImportSettings)
    {
        _logger = logger;
        _jellyfinSettings = jellyfinSettings.Value;
        _jellyfinHttpClient = httpClientFactory.CreateClient();
        _helperService = helperService;
        _cache = cache;
        _configuration = configuration;
        _spotifyImportSettings = spotifyImportSettings.Value;
    }

    [HttpGet("jellyfin/users")]
    public async Task<IActionResult> GetJellyfinUsers()
    {
        if (string.IsNullOrEmpty(_jellyfinSettings.Url) || string.IsNullOrEmpty(_jellyfinSettings.ApiKey))
        {
            return BadRequest(new { error = "Jellyfin URL or API key not configured" });
        }
        
        try
        {
            var url = $"{_jellyfinSettings.Url}/Users";
            
            var request = _helperService.CreateJellyfinRequest(HttpMethod.Get, url);
            
            var response = await _jellyfinHttpClient.SendAsync(request);
            
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                _logger.LogError("Failed to fetch Jellyfin users: {StatusCode} - {Body}", response.StatusCode, errorBody);
                return StatusCode((int)response.StatusCode, new { error = "Failed to fetch users from Jellyfin" });
            }
            
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            
            var users = new List<object>();
            
            foreach (var user in doc.RootElement.EnumerateArray())
            {
                var id = user.GetProperty("Id").GetString();
                var name = user.GetProperty("Name").GetString();
                
                users.Add(new { id, name });
            }
            
            return Ok(new { users });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching Jellyfin users");
            return StatusCode(500, new { error = "Failed to fetch users", details = ex.Message });
        }
    }
    
    /// <summary>
    /// Get all Jellyfin libraries (virtual folders)
    /// </summary>
    [HttpGet("jellyfin/libraries")]
    public async Task<IActionResult> GetJellyfinLibraries()
    {
        if (string.IsNullOrEmpty(_jellyfinSettings.Url) || string.IsNullOrEmpty(_jellyfinSettings.ApiKey))
        {
            return BadRequest(new { error = "Jellyfin URL or API key not configured" });
        }
        
        try
        {
            var url = $"{_jellyfinSettings.Url}/Library/VirtualFolders";
            
            var request = _helperService.CreateJellyfinRequest(HttpMethod.Get, url);
            
            var response = await _jellyfinHttpClient.SendAsync(request);
            
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                _logger.LogError("Failed to fetch Jellyfin libraries: {StatusCode} - {Body}", response.StatusCode, errorBody);
                return StatusCode((int)response.StatusCode, new { error = "Failed to fetch libraries from Jellyfin" });
            }
            
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            
            var libraries = new List<object>();
            
            foreach (var lib in doc.RootElement.EnumerateArray())
            {
                var name = lib.GetProperty("Name").GetString();
                var itemId = lib.TryGetProperty("ItemId", out var id) ? id.GetString() : null;
                var collectionType = lib.TryGetProperty("CollectionType", out var ct) ? ct.GetString() : null;
                
                libraries.Add(new { id = itemId, name, collectionType });
            }
            
            return Ok(new { libraries });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching Jellyfin libraries");
            return StatusCode(500, new { error = "Failed to fetch libraries", details = ex.Message });
        }
    }
    
    /// <summary>
    /// Get all playlists from the user's Spotify account
    /// </summary>
    [HttpGet("jellyfin/playlists")]
    public async Task<IActionResult> GetJellyfinPlaylists([FromQuery] string? userId = null)
    {
        if (string.IsNullOrEmpty(_jellyfinSettings.Url) || string.IsNullOrEmpty(_jellyfinSettings.ApiKey))
        {
            return BadRequest(new { error = "Jellyfin URL or API key not configured" });
        }
        
        try
        {
            // Build URL with optional userId filter
            var url = $"{_jellyfinSettings.Url}/Items?IncludeItemTypes=Playlist&Recursive=true&Fields=ProviderIds,ChildCount,RecursiveItemCount,SongCount";
            
            if (!string.IsNullOrEmpty(userId))
            {
                url += $"&UserId={userId}";
            }
            
            var request = _helperService.CreateJellyfinRequest(HttpMethod.Get, url);
            
            var response = await _jellyfinHttpClient.SendAsync(request);
            
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                _logger.LogError("Failed to fetch Jellyfin playlists: {StatusCode} - {Body}", response.StatusCode, errorBody);
                return StatusCode((int)response.StatusCode, new { error = "Failed to fetch playlists from Jellyfin" });
            }
            
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            
            var playlists = new List<object>();
            
            // Read current playlists from .env file for accurate linked status
            var configuredPlaylists = await _helperService.ReadPlaylistsFromEnvFileAsync();
            
            if (doc.RootElement.TryGetProperty("Items", out var items))
            {
                foreach (var item in items.EnumerateArray())
                {
                    var id = item.GetProperty("Id").GetString();
                    var name = item.GetProperty("Name").GetString();
                    
                    // Try multiple fields for track count - Jellyfin may use different fields
                    var childCount = 0;
                    if (item.TryGetProperty("ChildCount", out var cc) && cc.ValueKind == JsonValueKind.Number)
                        childCount = cc.GetInt32();
                    else if (item.TryGetProperty("SongCount", out var sc) && sc.ValueKind == JsonValueKind.Number)
                        childCount = sc.GetInt32();
                    else if (item.TryGetProperty("RecursiveItemCount", out var ric) && ric.ValueKind == JsonValueKind.Number)
                        childCount = ric.GetInt32();
                    
                    // Check if this playlist is configured in allstarr by Jellyfin ID
                    var configuredPlaylist = configuredPlaylists
                        .FirstOrDefault(p => p.JellyfinId.Equals(id, StringComparison.OrdinalIgnoreCase));
                    var isConfigured = configuredPlaylist != null;
                    var linkedSpotifyId = configuredPlaylist?.Id;
                    
                    // Only fetch detailed track stats for configured Spotify playlists
                    // This avoids expensive queries for large non-Spotify playlists
                    var trackStats = (LocalTracks: 0, ExternalTracks: 0, ExternalAvailable: 0);
                    if (isConfigured)
                    {
                        trackStats = await GetPlaylistTrackStats(id!);
                    }
                    
                    // Use actual track stats for configured playlists, otherwise use Jellyfin's count
                    var actualTrackCount = isConfigured 
                        ? trackStats.LocalTracks + trackStats.ExternalTracks 
                        : childCount;
                    
                    playlists.Add(new
                    {
                        id,
                        name,
                        trackCount = actualTrackCount,
                        linkedSpotifyId,
                        isConfigured,
                        localTracks = trackStats.LocalTracks,
                        externalTracks = trackStats.ExternalTracks,
                        externalAvailable = trackStats.ExternalAvailable
                    });
                }
            }
            
            return Ok(new { playlists });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching Jellyfin playlists");
            return StatusCode(500, new { error = "Failed to fetch playlists", details = ex.Message });
        }
    }
    
    /// <summary>
    /// Get track statistics for a playlist (local vs external)
    /// </summary>
    private async Task<(int LocalTracks, int ExternalTracks, int ExternalAvailable)> GetPlaylistTrackStats(string playlistId)
    {
        try
        {
            // Jellyfin requires a UserId to fetch playlist items
            // We'll use the first available user if not specified
            var userId = _jellyfinSettings.UserId;
            
            // If no user configured, try to get the first user
            if (string.IsNullOrEmpty(userId))
            {
                var usersRequest = _helperService.CreateJellyfinRequest(HttpMethod.Get, $"{_jellyfinSettings.Url}/Users");
                var usersResponse = await _jellyfinHttpClient.SendAsync(usersRequest);
                
                if (usersResponse.IsSuccessStatusCode)
                {
                    var usersJson = await usersResponse.Content.ReadAsStringAsync();
                    using var usersDoc = JsonDocument.Parse(usersJson);
                    if (usersDoc.RootElement.GetArrayLength() > 0)
                    {
                        userId = usersDoc.RootElement[0].GetProperty("Id").GetString();
                    }
                }
            }
            
            if (string.IsNullOrEmpty(userId))
            {
                _logger.LogWarning("No user ID available to fetch playlist items for {PlaylistId}", playlistId);
                return (0, 0, 0);
            }
            
            var url = $"{_jellyfinSettings.Url}/Playlists/{playlistId}/Items?UserId={userId}&Fields=Path";
            var request = _helperService.CreateJellyfinRequest(HttpMethod.Get, url);
            
            var response = await _jellyfinHttpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to fetch playlist items for {PlaylistId}: {StatusCode}", playlistId, response.StatusCode);
                return (0, 0, 0);
            }
            
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            
            var localTracks = 0;
            var externalTracks = 0;
            var externalAvailable = 0;
            
            if (doc.RootElement.TryGetProperty("Items", out var items))
            {
                foreach (var item in items.EnumerateArray())
                {
                    // Simpler detection: Check if Path exists and is not empty
                    // External tracks from allstarr won't have a Path property
                    var hasPath = item.TryGetProperty("Path", out var pathProp) && 
                                  pathProp.ValueKind == JsonValueKind.String &&
                                  !string.IsNullOrEmpty(pathProp.GetString());
                    
                    if (hasPath)
                    {
                        var pathStr = pathProp.GetString()!;
                        // Check if it's a real file path (not a URL)
                        if (pathStr.StartsWith("/") || pathStr.Contains(":\\"))
                        {
                            localTracks++;
                        }
                        else
                        {
                            // It's a URL or external source
                            externalTracks++;
                            externalAvailable++;
                        }
                    }
                    else
                    {
                        // No path means it's external
                        externalTracks++;
                        externalAvailable++;
                    }
                }
                
                _logger.LogDebug("Playlist {PlaylistId} stats: {Local} local, {External} external", 
                    playlistId, localTracks, externalTracks);
            }
            else
            {
                _logger.LogWarning("No Items property in playlist response for {PlaylistId}", playlistId);
            }
            
            return (localTracks, externalTracks, externalAvailable);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get track stats for playlist {PlaylistId}", playlistId);
            return (0, 0, 0);
        }
    }
    
    /// <summary>
    /// Link a Jellyfin playlist to a Spotify playlist
    /// </summary>
    [HttpPost("jellyfin/playlists/{jellyfinPlaylistId}/link")]
    public async Task<IActionResult> LinkPlaylist(string jellyfinPlaylistId, [FromBody] LinkPlaylistRequest request)
    {
        if (string.IsNullOrEmpty(request.SpotifyPlaylistId))
        {
            return BadRequest(new { error = "SpotifyPlaylistId is required" });
        }
        
        if (string.IsNullOrEmpty(request.Name))
        {
            return BadRequest(new { error = "Name is required" });
        }
        
        _logger.LogInformation("Linking Jellyfin playlist {JellyfinId} to Spotify playlist {SpotifyId} with name {Name}", 
            jellyfinPlaylistId, request.SpotifyPlaylistId, request.Name);
        
        // Read current playlists from .env file (not in-memory config which is stale)
        var currentPlaylists = await _helperService.ReadPlaylistsFromEnvFileAsync();
        
        // Check if already configured by Jellyfin ID
        var existingByJellyfinId = currentPlaylists
            .FirstOrDefault(p => p.JellyfinId.Equals(jellyfinPlaylistId, StringComparison.OrdinalIgnoreCase));
        
        if (existingByJellyfinId != null)
        {
            return BadRequest(new { error = $"This Jellyfin playlist is already linked to '{existingByJellyfinId.Name}'" });
        }
        
        // Check if already configured by name
        var existingByName = currentPlaylists
            .FirstOrDefault(p => p.Name.Equals(request.Name, StringComparison.OrdinalIgnoreCase));
        
        if (existingByName != null)
        {
            return BadRequest(new { error = $"Playlist name '{request.Name}' is already configured" });
        }
        
        // Add the playlist to configuration
        currentPlaylists.Add(new SpotifyPlaylistConfig
        {
            Name = request.Name,
            Id = request.SpotifyPlaylistId,
            JellyfinId = jellyfinPlaylistId,
            LocalTracksPosition = LocalTracksPosition.First, // Use Spotify order
            SyncSchedule = request.SyncSchedule ?? "0 8 * * *" // Default to daily 8 AM
        });
        
        // Convert to JSON format for env var: [["Name","SpotifyId","JellyfinId","first|last","cronSchedule"],...]
        var playlistsJson = JsonSerializer.Serialize(
            currentPlaylists.Select(p => new[] { 
                p.Name, 
                p.Id, 
                p.JellyfinId, 
                p.LocalTracksPosition.ToString().ToLower(),
                p.SyncSchedule ?? "0 8 * * *"
            }).ToArray()
        );
        
        // Update .env file
        var updateRequest = new ConfigUpdateRequest
        {
            Updates = new Dictionary<string, string>
            {
                ["SPOTIFY_IMPORT_PLAYLISTS"] = playlistsJson
            }
        };
        
        return await _helperService.UpdateEnvConfigAsync(updateRequest.Updates);
    }
    
    /// <summary>
    /// Unlink a playlist (remove from configuration)
    /// </summary>
    [HttpDelete("jellyfin/playlists/{name}/unlink")]
    public async Task<IActionResult> UnlinkPlaylist(string name)
    {
        var decodedName = Uri.UnescapeDataString(name);
        return await _helperService.RemovePlaylistFromConfigAsync(decodedName);
    }
    
    /// <summary>
    /// Update playlist sync schedule
    /// </summary>
    [HttpPut("playlists/{name}/schedule")]
    public async Task<IActionResult> UpdatePlaylistSchedule(string name, [FromBody] UpdateScheduleRequest request)
    {
        var decodedName = Uri.UnescapeDataString(name);
        
        if (string.IsNullOrWhiteSpace(request.SyncSchedule))
        {
            return BadRequest(new { error = "SyncSchedule is required" });
        }
        
        // Basic cron validation
        var cronParts = request.SyncSchedule.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (cronParts.Length != 5)
        {
            return BadRequest(new { error = "Invalid cron format. Expected: minute hour day month dayofweek" });
        }
        
        // Read current playlists
        var currentPlaylists = await _helperService.ReadPlaylistsFromEnvFileAsync();
        var playlist = currentPlaylists.FirstOrDefault(p => p.Name.Equals(decodedName, StringComparison.OrdinalIgnoreCase));
        
        if (playlist == null)
        {
            return NotFound(new { error = $"Playlist '{decodedName}' not found" });
        }
        
        // Update the schedule
        playlist.SyncSchedule = request.SyncSchedule.Trim();
        
        // Save back to .env
        var playlistsJson = JsonSerializer.Serialize(
            currentPlaylists.Select(p => new[] { 
                p.Name, 
                p.Id, 
                p.JellyfinId, 
                p.LocalTracksPosition.ToString().ToLower(),
                p.SyncSchedule ?? "0 8 * * *"
            }).ToArray()
        );
        
        var updateRequest = new ConfigUpdateRequest
        {
            Updates = new Dictionary<string, string>
            {
                ["SPOTIFY_IMPORT_PLAYLISTS"] = playlistsJson
            }
        };
        
        return await _helperService.UpdateEnvConfigAsync(updateRequest.Updates);
    }
    
}
