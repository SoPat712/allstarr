using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using allstarr.Models.Settings;
using allstarr.Models.Spotify;
using allstarr.Services.Spotify;
using allstarr.Services.Jellyfin;
using allstarr.Services.Common;
using allstarr.Services;
using allstarr.Filters;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime;

namespace allstarr.Controllers;

/// <summary>
/// Admin API controller for the web dashboard.
/// Provides endpoints for viewing status, playlists, and modifying configuration.
/// Only accessible on internal admin port (5275) - not exposed through reverse proxy.
/// </summary>
[ApiController]
[Route("api/admin")]
[ServiceFilter(typeof(AdminPortFilter))]
public class AdminController : ControllerBase
{
    private readonly ILogger<AdminController> _logger;
    private readonly IConfiguration _configuration;
    private readonly SpotifyApiSettings _spotifyApiSettings;
    private readonly SpotifyImportSettings _spotifyImportSettings;
    private readonly JellyfinSettings _jellyfinSettings;
    private readonly SubsonicSettings _subsonicSettings;
    private readonly DeezerSettings _deezerSettings;
    private readonly QobuzSettings _qobuzSettings;
    private readonly SquidWTFSettings _squidWtfSettings;
    private readonly MusicBrainzSettings _musicBrainzSettings;
    private readonly SpotifyApiClient _spotifyClient;
    private readonly SpotifyPlaylistFetcher _playlistFetcher;
    private readonly SpotifyTrackMatchingService? _matchingService;
    private readonly RedisCacheService _cache;
    private readonly HttpClient _jellyfinHttpClient;
    private readonly IWebHostEnvironment _environment;
    private readonly IServiceProvider _serviceProvider;
    private readonly string _envFilePath;
    private readonly List<string> _squidWtfApiUrls;
    private static int _urlIndex = 0;
    private static readonly object _urlIndexLock = new();
    private const string CacheDirectory = "/app/cache/spotify";
    
    public AdminController(
        ILogger<AdminController> logger,
        IConfiguration configuration,
        IWebHostEnvironment environment,
        IOptions<SpotifyApiSettings> spotifyApiSettings,
        IOptions<SpotifyImportSettings> spotifyImportSettings,
        IOptions<JellyfinSettings> jellyfinSettings,
        IOptions<SubsonicSettings> subsonicSettings,
        IOptions<DeezerSettings> deezerSettings,
        IOptions<QobuzSettings> qobuzSettings,
        IOptions<SquidWTFSettings> squidWtfSettings,
        IOptions<MusicBrainzSettings> musicBrainzSettings,
        SpotifyApiClient spotifyClient,
        SpotifyPlaylistFetcher playlistFetcher,
        RedisCacheService cache,
        IHttpClientFactory httpClientFactory,
        IServiceProvider serviceProvider,
        SpotifyTrackMatchingService? matchingService = null)
    {
        _logger = logger;
        _configuration = configuration;
        _environment = environment;
        _spotifyApiSettings = spotifyApiSettings.Value;
        _spotifyImportSettings = spotifyImportSettings.Value;
        _jellyfinSettings = jellyfinSettings.Value;
        _subsonicSettings = subsonicSettings.Value;
        _deezerSettings = deezerSettings.Value;
        _qobuzSettings = qobuzSettings.Value;
        _squidWtfSettings = squidWtfSettings.Value;
        _musicBrainzSettings = musicBrainzSettings.Value;
        _spotifyClient = spotifyClient;
        _playlistFetcher = playlistFetcher;
        _matchingService = matchingService;
        _cache = cache;
        _jellyfinHttpClient = httpClientFactory.CreateClient();
        _serviceProvider = serviceProvider;
        
        // Decode SquidWTF base URLs
        _squidWtfApiUrls = DecodeSquidWtfUrls();
        
        // .env file path is always /app/.env in Docker (mounted from host)
        // In development, it's in the parent directory of ContentRootPath
        _envFilePath = _environment.IsDevelopment() 
            ? Path.Combine(_environment.ContentRootPath, "..", ".env")
            : "/app/.env";
    }
    
    private static List<string> DecodeSquidWtfUrls()
    {
        var encodedUrls = new[]
        {
            "aHR0cHM6Ly90cml0b24uc3F1aWQud3Rm",                      // triton
            "aHR0cHM6Ly90aWRhbC1hcGkuYmluaW11bS5vcmc=",              // binimum
            "aHR0cHM6Ly90aWRhbC5raW5vcGx1cy5vbmxpbmU=",              // kinoplus
            "aHR0cHM6Ly9oaWZpLXR3by5zcG90aXNhdmVyLm5ldA==",          // spoti-2
            "aHR0cHM6Ly9oaWZpLW9uZS5zcG90aXNhdmVyLm5ldA==",          // spoti-1
            "aHR0cHM6Ly93b2xmLnFxZGwuc2l0ZQ==",                      // wolf
            "aHR0cDovL2h1bmQucXFkbC5zaXRl",                          // hund
            "aHR0cHM6Ly9rYXR6ZS5xcWRsLnNpdGU=",                      // katze
            "aHR0cHM6Ly92b2dlbC5xcWRsLnNpdGU=",                      // vogel
            "aHR0cHM6Ly9tYXVzLnFxZGwuc2l0ZQ=="                       // maus
        };
        
        return encodedUrls
            .Select(encoded => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded)))
            .ToList();
    }
    
    /// <summary>
    /// Helper method to safely check if a dynamic cache result has a value
    /// Handles the case where JsonElement cannot be compared to null directly
    /// </summary>
    private static bool HasValue(object? obj)
    {
        if (obj == null) return false;
        if (obj is JsonElement jsonEl) return jsonEl.ValueKind != JsonValueKind.Null && jsonEl.ValueKind != JsonValueKind.Undefined;
        return true;
    }
    
    /// <summary>
    /// Get current system status and configuration
    /// </summary>
    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        // Determine Spotify auth status based on configuration only
        // DO NOT call Spotify API here - this endpoint is polled frequently
        var spotifyAuthStatus = "not_configured";
        string? spotifyUser = null;
        
        if (_spotifyApiSettings.Enabled && !string.IsNullOrEmpty(_spotifyApiSettings.SessionCookie))
        {
            // If cookie is set, assume it's working until proven otherwise
            // Actual validation happens when playlists are fetched
            spotifyAuthStatus = "configured";
            spotifyUser = "(cookie set)";
        }
        else if (_spotifyApiSettings.Enabled)
        {
            spotifyAuthStatus = "missing_cookie";
        }
        
        return Ok(new
        {
            version = "1.0.1",
            backendType = _configuration.GetValue<string>("Backend:Type") ?? "Jellyfin",
            jellyfinUrl = _jellyfinSettings.Url,
            spotify = new
            {
                apiEnabled = _spotifyApiSettings.Enabled,
                authStatus = spotifyAuthStatus,
                user = spotifyUser,
                hasCookie = !string.IsNullOrEmpty(_spotifyApiSettings.SessionCookie),
                cookieSetDate = _spotifyApiSettings.SessionCookieSetDate,
                cacheDurationMinutes = _spotifyApiSettings.CacheDurationMinutes,
                preferIsrcMatching = _spotifyApiSettings.PreferIsrcMatching
            },
            spotifyImport = new
            {
                enabled = _spotifyImportSettings.Enabled,
                matchingIntervalHours = _spotifyImportSettings.MatchingIntervalHours,
                playlistCount = _spotifyImportSettings.Playlists.Count
            },
            deezer = new
            {
                hasArl = !string.IsNullOrEmpty(_deezerSettings.Arl),
                quality = _deezerSettings.Quality ?? "FLAC"
            },
            qobuz = new
            {
                hasToken = !string.IsNullOrEmpty(_qobuzSettings.UserAuthToken),
                quality = _qobuzSettings.Quality ?? "FLAC"
            },
            squidWtf = new
            {
                quality = _squidWtfSettings.Quality ?? "LOSSLESS"
            }
        });
    }
    
    /// <summary>
    /// Get a random SquidWTF base URL for searching (round-robin)
    /// </summary>
    [HttpGet("squidwtf-base-url")]
    public IActionResult GetSquidWtfBaseUrl()
    {
        if (_squidWtfApiUrls.Count == 0)
        {
            return NotFound(new { error = "No SquidWTF base URLs configured" });
        }
        
        string baseUrl;
        lock (_urlIndexLock)
        {
            baseUrl = _squidWtfApiUrls[_urlIndex];
            _urlIndex = (_urlIndex + 1) % _squidWtfApiUrls.Count;
        }
        
        return Ok(new { baseUrl });
    }
    
    /// <summary>
    /// Get current configuration including cache settings
    /// </summary>
    
    /// <summary>
    /// Get list of configured playlists with their current data
    /// </summary>
    [HttpGet("playlists")]
    public async Task<IActionResult> GetPlaylists([FromQuery] bool refresh = false)
    {
        var playlistCacheFile = "/app/cache/admin_playlists_summary.json";
        
        // Check file cache first (5 minute TTL) unless refresh is requested
        if (!refresh && System.IO.File.Exists(playlistCacheFile))
        {
            try
            {
                var fileInfo = new FileInfo(playlistCacheFile);
                var age = DateTime.UtcNow - fileInfo.LastWriteTimeUtc;
                
                if (age.TotalMinutes < 5)
                {
                    var cachedJson = await System.IO.File.ReadAllTextAsync(playlistCacheFile);
                    var cachedData = JsonSerializer.Deserialize<Dictionary<string, object>>(cachedJson);
                    _logger.LogDebug("📦 Returning cached playlist summary (age: {Age:F1}m)", age.TotalMinutes);
                    return Ok(cachedData);
                }
                else
                {
                    _logger.LogWarning("🔄 Cache expired (age: {Age:F1}m), refreshing...", age.TotalMinutes);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to read cached playlist summary");
            }
        }
        else if (refresh)
        {
            _logger.LogDebug("🔄 Force refresh requested for playlist summary");
        }
        
        var playlists = new List<object>();
        
        // Read playlists directly from .env file to get the latest configuration
        // (IOptions is cached and doesn't reload after .env changes)
        var configuredPlaylists = await ReadPlaylistsFromEnvFile();
        
        foreach (var config in configuredPlaylists)
        {
            var playlistInfo = new Dictionary<string, object?>
            {
                ["name"] = config.Name,
                ["id"] = config.Id,
                ["jellyfinId"] = config.JellyfinId,
                ["localTracksPosition"] = config.LocalTracksPosition.ToString(),
                ["syncSchedule"] = config.SyncSchedule ?? "0 8 * * 1",
                ["trackCount"] = 0,
                ["localTracks"] = 0,
                ["externalTracks"] = 0,
                ["lastFetched"] = null as DateTime?,
                ["cacheAge"] = null as string
            };
            
            // Get Spotify playlist track count from cache
            var cacheFilePath = Path.Combine(CacheDirectory, $"{SanitizeFileName(config.Name)}_spotify.json");
            int spotifyTrackCount = 0;
            
            if (System.IO.File.Exists(cacheFilePath))
            {
                try
                {
                    var json = await System.IO.File.ReadAllTextAsync(cacheFilePath);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    
                    if (root.TryGetProperty("tracks", out var tracks))
                    {
                        spotifyTrackCount = tracks.GetArrayLength();
                        playlistInfo["trackCount"] = spotifyTrackCount;
                    }
                    
                    if (root.TryGetProperty("fetchedAt", out var fetchedAt))
                    {
                        var fetchedTime = fetchedAt.GetDateTime();
                        playlistInfo["lastFetched"] = fetchedTime;
                        var age = DateTime.UtcNow - fetchedTime;
                        playlistInfo["cacheAge"] = age.TotalHours < 1 
                            ? $"{age.TotalMinutes:F0}m" 
                            : $"{age.TotalHours:F1}h";
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to read cache for playlist {Name}", config.Name);
                }
            }
            
            // Get current Jellyfin playlist track count
            if (!string.IsNullOrEmpty(config.JellyfinId))
            {
                try
                {
                    // Jellyfin requires UserId parameter to fetch playlist items
                    var userId = _jellyfinSettings.UserId;
                    
                    // If no user configured, try to get the first user
                    if (string.IsNullOrEmpty(userId))
                    {
                        var usersResponse = await _jellyfinHttpClient.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"{_jellyfinSettings.Url}/Users")
                        {
                            Headers = { { "X-Emby-Authorization", GetJellyfinAuthHeader() } }
                        });
                        
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
                        _logger.LogWarning("No user ID available to fetch playlist items for {Name}", config.Name);
                    }
                    else
                    {
                        var url = $"{_jellyfinSettings.Url}/Playlists/{config.JellyfinId}/Items?UserId={userId}&Fields=Path";
                        var request = new HttpRequestMessage(HttpMethod.Get, url);
                        request.Headers.Add("X-Emby-Authorization", GetJellyfinAuthHeader());
                        
                        _logger.LogDebug("Fetching Jellyfin playlist items for {Name} from {Url}", config.Name, url);
                        
                        var response = await _jellyfinHttpClient.SendAsync(request);
                        if (response.IsSuccessStatusCode)
                        {
                            var jellyfinJson = await response.Content.ReadAsStringAsync();
                            using var jellyfinDoc = JsonDocument.Parse(jellyfinJson);
                            
                            if (jellyfinDoc.RootElement.TryGetProperty("Items", out var items))
                            {
                                // Get Spotify tracks to match against
                                var spotifyTracks = await _playlistFetcher.GetPlaylistTracksAsync(config.Name);
                                
                                // Try to use the pre-built playlist cache first (includes manual mappings!)
                                var playlistItemsCacheKey = $"spotify:playlist:items:{config.Name}";
                                
                                List<Dictionary<string, object?>>? cachedPlaylistItems = null;
                                try
                                {
                                    cachedPlaylistItems = await _cache.GetAsync<List<Dictionary<string, object?>>>(playlistItemsCacheKey);
                                }
                                catch (Exception cacheEx)
                                {
                                    _logger.LogWarning(cacheEx, "Failed to deserialize playlist cache for {Playlist}", config.Name);
                                }
                                
                                _logger.LogDebug("Checking cache for {Playlist}: {CacheKey}, Found: {Found}, Count: {Count}", 
                                    config.Name, playlistItemsCacheKey, cachedPlaylistItems != null, cachedPlaylistItems?.Count ?? 0);
                                
                                if (cachedPlaylistItems != null && cachedPlaylistItems.Count > 0)
                                {
                                    // Use the pre-built cache which respects manual mappings
                                    var localCount = 0;
                                    var externalCount = 0;
                                    
                                    foreach (var item in cachedPlaylistItems)
                                    {
                                        // Check if it's external by looking for external provider in ProviderIds
                                        // External providers: SquidWTF, Deezer, Qobuz, Tidal
                                        // Local tracks: Have Jellyfin ID OR no external provider keys
                                        var isExternal = false;
                                        
                                        if (item.TryGetValue("ProviderIds", out var providerIdsObj) && providerIdsObj != null)
                                        {
                                            // Handle both Dictionary<string, string> and JsonElement
                                            Dictionary<string, string>? providerIds = null;
                                            
                                            if (providerIdsObj is Dictionary<string, string> dict)
                                            {
                                                providerIds = dict;
                                            }
                                            else if (providerIdsObj is JsonElement jsonEl && jsonEl.ValueKind == JsonValueKind.Object)
                                            {
                                                providerIds = new Dictionary<string, string>();
                                                foreach (var prop in jsonEl.EnumerateObject())
                                                {
                                                    providerIds[prop.Name] = prop.Value.GetString() ?? "";
                                                }
                                            }
                                            
                                            if (providerIds != null)
                                            {
                                                // Check for external provider keys (not MusicBrainz, ISRC, Spotify, Jellyfin, etc)
                                                isExternal = providerIds.Keys.Any(k => 
                                                    k.Equals("SquidWTF", StringComparison.OrdinalIgnoreCase) ||
                                                    k.Equals("Deezer", StringComparison.OrdinalIgnoreCase) ||
                                                    k.Equals("Qobuz", StringComparison.OrdinalIgnoreCase) ||
                                                    k.Equals("Tidal", StringComparison.OrdinalIgnoreCase));
                                            }
                                        }
                                        
                                        if (isExternal)
                                        {
                                            externalCount++;
                                        }
                                        else
                                        {
                                            // Local track (has Jellyfin ID or no external provider)
                                            localCount++;
                                        }
                                    }
                                    
                                    var externalMissingCount = spotifyTracks.Count - cachedPlaylistItems.Count;
                                    if (externalMissingCount < 0) externalMissingCount = 0;
                                    
                                    playlistInfo["localTracks"] = localCount;
                                    playlistInfo["externalMatched"] = externalCount;
                                    playlistInfo["externalMissing"] = externalMissingCount;
                                    playlistInfo["externalTotal"] = externalCount + externalMissingCount;
                                    playlistInfo["totalInJellyfin"] = cachedPlaylistItems.Count;
                                    playlistInfo["totalPlayable"] = localCount + externalCount; // Total tracks that will be served
                                    
                                    _logger.LogDebug("Playlist {Name} (from cache): {Total} Spotify tracks, {Local} local, {ExtMatched} external matched, {ExtMissing} external missing, {Playable} total playable", 
                                        config.Name, spotifyTracks.Count, localCount, externalCount, externalMissingCount, localCount + externalCount);
                                }
                                else
                                {
                                    // Fallback: Build list of local tracks from Jellyfin (match by name only)
                                    var localTracks = new List<(string Title, string Artist)>();
                                    foreach (var item in items.EnumerateArray())
                                    {
                                        var title = item.TryGetProperty("Name", out var nameEl) ? nameEl.GetString() ?? "" : "";
                                        var artist = "";
                                        
                                        if (item.TryGetProperty("Artists", out var artistsEl) && artistsEl.GetArrayLength() > 0)
                                        {
                                            artist = artistsEl[0].GetString() ?? "";
                                        }
                                        else if (item.TryGetProperty("AlbumArtist", out var albumArtistEl))
                                        {
                                            artist = albumArtistEl.GetString() ?? "";
                                        }
                                        
                                        if (!string.IsNullOrEmpty(title))
                                        {
                                            localTracks.Add((title, artist));
                                        }
                                    }
                                    
                                    // Get matched external tracks cache once
                                    var matchedTracksKey = $"spotify:matched:ordered:{config.Name}";
                                    var matchedTracks = await _cache.GetAsync<List<MatchedTrack>>(matchedTracksKey);
                                    var matchedSpotifyIds = new HashSet<string>(
                                        matchedTracks?.Select(m => m.SpotifyId) ?? Enumerable.Empty<string>()
                                    );
                                    
                                    var localCount = 0;
                                    var externalMatchedCount = 0;
                                    var externalMissingCount = 0;
                                    
                                    // Match each Spotify track to determine if it's local, external, or missing
                                    foreach (var track in spotifyTracks)
                                    {
                                        var isLocal = false;
                                        var hasExternalMapping = false;
                                        
                                        // FIRST: Check for manual Jellyfin mapping
                                        var manualMappingKey = $"spotify:manual-map:{config.Name}:{track.SpotifyId}";
                                        var manualJellyfinId = await _cache.GetAsync<string>(manualMappingKey);
                                        
                                        if (!string.IsNullOrEmpty(manualJellyfinId))
                                        {
                                            // Manual Jellyfin mapping exists - this track is definitely local
                                            isLocal = true;
                                        }
                                        else
                                        {
                                            // Check for external manual mapping
                                            var externalMappingKey = $"spotify:external-map:{config.Name}:{track.SpotifyId}";
                                            var externalMappingJson = await _cache.GetStringAsync(externalMappingKey);
                                            
                                            if (!string.IsNullOrEmpty(externalMappingJson))
                                            {
                                                // External manual mapping exists
                                                hasExternalMapping = true;
                                            }
                                            else if (localTracks.Count > 0)
                                            {
                                                // SECOND: No manual mapping, try fuzzy matching with local tracks
                                                var bestMatch = localTracks
                                                    .Select(local => new
                                                    {
                                                        Local = local,
                                                        TitleScore = FuzzyMatcher.CalculateSimilarity(track.Title, local.Title),
                                                        ArtistScore = FuzzyMatcher.CalculateSimilarity(track.PrimaryArtist, local.Artist)
                                                    })
                                                    .Select(x => new
                                                    {
                                                        x.Local,
                                                        x.TitleScore,
                                                        x.ArtistScore,
                                                        TotalScore = (x.TitleScore * 0.7) + (x.ArtistScore * 0.3)
                                                    })
                                                    .OrderByDescending(x => x.TotalScore)
                                                    .FirstOrDefault();
                                                
                                                // Use 70% threshold (same as playback matching)
                                                if (bestMatch != null && bestMatch.TotalScore >= 70)
                                                {
                                                    isLocal = true;
                                                }
                                            }
                                        }
                                        
                                        if (isLocal)
                                        {
                                            localCount++;
                                        }
                                        else
                                        {
                                            // Check if external track is matched (either manual mapping or auto-matched)
                                            if (hasExternalMapping || matchedSpotifyIds.Contains(track.SpotifyId))
                                            {
                                                externalMatchedCount++;
                                            }
                                            else
                                            {
                                                externalMissingCount++;
                                            }
                                        }
                                    }
                                    
                                    playlistInfo["localTracks"] = localCount;
                                    playlistInfo["externalMatched"] = externalMatchedCount;
                                    playlistInfo["externalMissing"] = externalMissingCount;
                                    playlistInfo["externalTotal"] = externalMatchedCount + externalMissingCount;
                                    playlistInfo["totalInJellyfin"] = localCount + externalMatchedCount;
                                    playlistInfo["totalPlayable"] = localCount + externalMatchedCount; // Total tracks that will be served
                                    
                                    _logger.LogWarning("Playlist {Name} (fallback): {Total} Spotify tracks, {Local} local, {ExtMatched} external matched, {ExtMissing} external missing, {Playable} total playable", 
                                        config.Name, spotifyTracks.Count, localCount, externalMatchedCount, externalMissingCount, localCount + externalMatchedCount);
                                }
                            }
                            else
                            {
                                _logger.LogWarning("No Items property in Jellyfin response for {Name}", config.Name);
                            }
                        }
                        else
                        {
                            _logger.LogError("Failed to get Jellyfin playlist {Name}: {StatusCode}", 
                                config.Name, response.StatusCode);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to get Jellyfin playlist tracks for {Name}", config.Name);
                }
            }
            else
            {
                _logger.LogInformation("Playlist {Name} has no JellyfinId configured", config.Name);
            }
            
            playlists.Add(playlistInfo);
        }
        
        // Save to file cache
        try
        {
            var cacheDir = "/app/cache";
            Directory.CreateDirectory(cacheDir);
            var cacheFile = Path.Combine(cacheDir, "admin_playlists_summary.json");
            
            var response = new { playlists };
            var json = JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = false });
            await System.IO.File.WriteAllTextAsync(cacheFile, json);
            
            _logger.LogDebug("💾 Saved playlist summary to cache");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save playlist summary cache");
        }
        
        return Ok(new { playlists });
    }
    
    /// <summary>
    /// Get tracks for a specific playlist with local/external status
    /// </summary>
    [HttpGet("playlists/{name}/tracks")]
    public async Task<IActionResult> GetPlaylistTracks(string name)
    {
        var decodedName = Uri.UnescapeDataString(name);
        
        // Get Spotify tracks
        var spotifyTracks = await _playlistFetcher.GetPlaylistTracksAsync(decodedName);
        
        var tracksWithStatus = new List<object>();
        
        // Use the pre-built playlist cache (same as GetPlaylists endpoint)
        // This cache includes all matched tracks with proper provider IDs
        var playlistItemsCacheKey = $"spotify:playlist:items:{decodedName}";
        
        List<Dictionary<string, object?>>? cachedPlaylistItems = null;
        try
        {
            cachedPlaylistItems = await _cache.GetAsync<List<Dictionary<string, object?>>>(playlistItemsCacheKey);
        }
        catch (Exception cacheEx)
        {
            _logger.LogWarning(cacheEx, "Failed to deserialize playlist cache for {Playlist}", decodedName);
        }
        
        _logger.LogDebug("GetPlaylistTracks for {Playlist}: Cache found: {Found}, Count: {Count}", 
            decodedName, cachedPlaylistItems != null, cachedPlaylistItems?.Count ?? 0);
        
        if (cachedPlaylistItems != null && cachedPlaylistItems.Count > 0)
        {
            // Build a map of Spotify ID -> cached item for quick lookup
            var spotifyIdToItem = new Dictionary<string, Dictionary<string, object?>>();
            
            // Also track items by position for fallback matching
            var itemsByPosition = new Dictionary<int, Dictionary<string, object?>>();
            
            for (int i = 0; i < cachedPlaylistItems.Count; i++)
            {
                var item = cachedPlaylistItems[i];
                
                // Try to get Spotify ID from ProviderIds (works for both local and external)
                bool hasSpotifyId = false;
                if (item.TryGetValue("ProviderIds", out var providerIdsObj) && providerIdsObj != null)
                {
                    Dictionary<string, string>? providerIds = null;
                    
                    if (providerIdsObj is Dictionary<string, string> dict)
                    {
                        providerIds = dict;
                    }
                    else if (providerIdsObj is JsonElement jsonEl && jsonEl.ValueKind == JsonValueKind.Object)
                    {
                        providerIds = new Dictionary<string, string>();
                        foreach (var prop in jsonEl.EnumerateObject())
                        {
                            providerIds[prop.Name] = prop.Value.GetString() ?? "";
                        }
                    }
                    
                    if (providerIds != null && providerIds.TryGetValue("Spotify", out var spotifyId) && !string.IsNullOrEmpty(spotifyId))
                    {
                        spotifyIdToItem[spotifyId] = item;
                        hasSpotifyId = true;
                    }
                }
                
                // If no Spotify ID found, use position-based matching as fallback
                if (!hasSpotifyId)
                {
                    itemsByPosition[i] = item;
                }
            }
            
            // Match each Spotify track to its cached item
            foreach (var track in spotifyTracks)
            {
                bool? isLocal = null;
                string? externalProvider = null;
                bool isManualMapping = false;
                string? manualMappingType = null;
                string? manualMappingId = null;
                
                Dictionary<string, object?>? cachedItem = null;
                
                // First try to match by Spotify ID
                if (spotifyIdToItem.TryGetValue(track.SpotifyId, out cachedItem))
                {
                    _logger.LogDebug("Matched track {Title} by Spotify ID", track.Title);
                }
                // Fallback: Try position-based matching for items without Spotify ID
                else if (itemsByPosition.TryGetValue(track.Position, out cachedItem))
                {
                    _logger.LogDebug("Matched track {Title} by position {Position}", track.Title, track.Position);
                }
                
                if (cachedItem != null)
                {
                    // Track is in the cache - determine if it's local or external
                    if (cachedItem.TryGetValue("ProviderIds", out var providerIdsObj) && providerIdsObj != null)
                    {
                        Dictionary<string, string>? providerIds = null;
                        
                        if (providerIdsObj is Dictionary<string, string> dict)
                        {
                            providerIds = dict;
                        }
                        else if (providerIdsObj is JsonElement jsonEl && jsonEl.ValueKind == JsonValueKind.Object)
                        {
                            providerIds = new Dictionary<string, string>();
                            foreach (var prop in jsonEl.EnumerateObject())
                            {
                                providerIds[prop.Name] = prop.Value.GetString() ?? "";
                            }
                        }
                        
                        if (providerIds != null)
                        {
                            _logger.LogDebug("Track {Title} has ProviderIds: {Keys}", track.Title, string.Join(", ", providerIds.Keys));
                            
                            // Check for external provider keys (SquidWTF, Deezer, Qobuz, Tidal)
                            // If found, it's an external track
                            if (providerIds.Keys.Any(k => 
                                k.Equals("squidwtf", StringComparison.OrdinalIgnoreCase) ||
                                k.Equals("SquidWTF", StringComparison.OrdinalIgnoreCase)))
                            {
                                isLocal = false;
                                externalProvider = "SquidWTF";
                                _logger.LogDebug("✓ Track {Title} identified as SquidWTF", track.Title);
                            }
                            else if (providerIds.Keys.Any(k => k.Equals("deezer", StringComparison.OrdinalIgnoreCase)))
                            {
                                isLocal = false;
                                externalProvider = "Deezer";
                                _logger.LogDebug("✓ Track {Title} identified as Deezer", track.Title);
                            }
                            else if (providerIds.Keys.Any(k => k.Equals("qobuz", StringComparison.OrdinalIgnoreCase)))
                            {
                                isLocal = false;
                                externalProvider = "Qobuz";
                                _logger.LogDebug("✓ Track {Title} identified as Qobuz", track.Title);
                            }
                            else if (providerIds.Keys.Any(k => k.Equals("tidal", StringComparison.OrdinalIgnoreCase)))
                            {
                                isLocal = false;
                                externalProvider = "Tidal";
                                _logger.LogDebug("✓ Track {Title} identified as Tidal", track.Title);
                            }
                            else
                            {
                                // No external provider key found - it's a local Jellyfin track
                                // Local tracks may have: Jellyfin ID, MusicBrainz IDs, ISRC, etc.
                                isLocal = true;
                                _logger.LogDebug("✓ Track {Title} identified as LOCAL (has ProviderIds but no external provider)", track.Title);
                            }
                        }
                        else
                        {
                            // ProviderIds exists but is null after parsing - treat as local
                            isLocal = true;
                            _logger.LogDebug("✓ Track {Title} identified as LOCAL (ProviderIds null)", track.Title);
                        }
                    }
                    else
                    {
                        // Track is in cache but has NO ProviderIds property at all
                        // This is typical for local Jellyfin tracks - treat as local
                        isLocal = true;
                        _logger.LogDebug("✓ Track {Title} identified as LOCAL (in cache, no ProviderIds)", track.Title);
                    }
                    
                    // Check if this is a manual mapping
                    var manualJellyfinKey = $"spotify:manual-map:{decodedName}:{track.SpotifyId}";
                    var manualJellyfinId = await _cache.GetAsync<string>(manualJellyfinKey);
                    
                    if (!string.IsNullOrEmpty(manualJellyfinId))
                    {
                        isManualMapping = true;
                        manualMappingType = "jellyfin";
                        manualMappingId = manualJellyfinId;
                    }
                    else
                    {
                        var externalMappingKey = $"spotify:external-map:{decodedName}:{track.SpotifyId}";
                        var externalMappingJson = await _cache.GetStringAsync(externalMappingKey);
                        
                        if (!string.IsNullOrEmpty(externalMappingJson))
                        {
                            try
                            {
                                using var extDoc = JsonDocument.Parse(externalMappingJson);
                                var extRoot = extDoc.RootElement;
                                
                                if (extRoot.TryGetProperty("id", out var idEl))
                                {
                                    isManualMapping = true;
                                    manualMappingType = "external";
                                    manualMappingId = idEl.GetString();
                                }
                            }
                            catch { }
                        }
                    }
                }
                else
                {
                    // Track not in cache - it's missing
                    isLocal = null;
                    externalProvider = null;
                }
                
                // Check lyrics status
                var cacheKey = $"lyrics:{track.PrimaryArtist}:{track.Title}:{track.Album}:{track.DurationMs / 1000}";
                var existingLyrics = await _cache.GetStringAsync(cacheKey);
                var hasLyrics = !string.IsNullOrEmpty(existingLyrics);
                
                tracksWithStatus.Add(new
                {
                    position = track.Position,
                    title = track.Title,
                    artists = track.Artists,
                    album = track.Album,
                    isrc = track.Isrc,
                    spotifyId = track.SpotifyId,
                    durationMs = track.DurationMs,
                    albumArtUrl = track.AlbumArtUrl,
                    isLocal = isLocal,
                    externalProvider = externalProvider,
                    searchQuery = isLocal != true ? $"{track.Title} {track.PrimaryArtist}" : null,
                    isManualMapping = isManualMapping,
                    manualMappingType = manualMappingType,
                    manualMappingId = manualMappingId,
                    hasLyrics = hasLyrics
                });
            }
            
            return Ok(new
            {
                name = decodedName,
                trackCount = spotifyTracks.Count,
                tracks = tracksWithStatus
            });
        }
        
        // Fallback: Cache not available, use matched tracks cache
        _logger.LogWarning("Playlist cache not available for {Playlist}, using fallback", decodedName);
        
        var fallbackMatchedTracksKey = $"spotify:matched:ordered:{decodedName}";
        var fallbackMatchedTracks = await _cache.GetAsync<List<MatchedTrack>>(fallbackMatchedTracksKey);
        var fallbackMatchedSpotifyIds = new HashSet<string>(
            fallbackMatchedTracks?.Select(m => m.SpotifyId) ?? Enumerable.Empty<string>()
        );
        
        foreach (var track in spotifyTracks)
        {
            bool? isLocal = null;
            string? externalProvider = null;
            
            // Check for manual Jellyfin mapping
            var manualMappingKey = $"spotify:manual-map:{decodedName}:{track.SpotifyId}";
            var manualJellyfinId = await _cache.GetAsync<string>(manualMappingKey);
            
            if (!string.IsNullOrEmpty(manualJellyfinId))
            {
                isLocal = true;
            }
            else
            {
                // Check for external manual mapping
                var externalMappingKey = $"spotify:external-map:{decodedName}:{track.SpotifyId}";
                var externalMappingJson = await _cache.GetStringAsync(externalMappingKey);
                
                if (!string.IsNullOrEmpty(externalMappingJson))
                {
                    try
                    {
                        using var extDoc = JsonDocument.Parse(externalMappingJson);
                        var extRoot = extDoc.RootElement;
                        
                        string? provider = null;
                        
                        if (extRoot.TryGetProperty("provider", out var providerEl))
                        {
                            provider = providerEl.GetString();
                        }
                        
                        if (!string.IsNullOrEmpty(provider))
                        {
                            isLocal = false;
                            externalProvider = provider;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to process external manual mapping for {Title}", track.Title);
                    }
                }
                else if (fallbackMatchedSpotifyIds.Contains(track.SpotifyId))
                {
                    isLocal = false;
                    externalProvider = "SquidWTF";
                }
                else
                {
                    isLocal = null;
                    externalProvider = null;
                }
            }
            
            tracksWithStatus.Add(new
            {
                position = track.Position,
                title = track.Title,
                artists = track.Artists,
                album = track.Album,
                isrc = track.Isrc,
                spotifyId = track.SpotifyId,
                durationMs = track.DurationMs,
                albumArtUrl = track.AlbumArtUrl,
                isLocal = isLocal,
                externalProvider = externalProvider,
                searchQuery = isLocal != true ? $"{track.Title} {track.PrimaryArtist}" : null
            });
        }
        
        return Ok(new
        {
            name = decodedName,
            trackCount = spotifyTracks.Count,
            tracks = tracksWithStatus
        });
    }
    
    /// <summary>
    /// Trigger a manual refresh of all playlists
    /// </summary>
    [HttpPost("playlists/refresh")]
    public async Task<IActionResult> RefreshPlaylists()
    {
        _logger.LogInformation("Manual playlist refresh triggered from admin UI");
        await _playlistFetcher.TriggerFetchAsync();
        
        // Invalidate playlist summary cache
        InvalidatePlaylistSummaryCache();
        
        return Ok(new { message = "Playlist refresh triggered", timestamp = DateTime.UtcNow });
    }
    
    /// <summary>
    /// Re-match tracks when LOCAL library has changed (checks if Jellyfin playlist changed).
    /// This is a lightweight operation that reuses cached Spotify data.
    /// </summary>
    [HttpPost("playlists/{name}/match")]
    public async Task<IActionResult> MatchPlaylistTracks(string name)
    {
        var decodedName = Uri.UnescapeDataString(name);
        _logger.LogInformation("Re-match tracks triggered for playlist: {Name} (checking for local changes)", decodedName);
        
        if (_matchingService == null)
        {
            return BadRequest(new { error = "Track matching service is not available" });
        }
        
        try
        {
            // Clear the Jellyfin playlist signature cache to force re-checking if local tracks changed
            var jellyfinSignatureCacheKey = $"spotify:playlist:jellyfin-signature:{decodedName}";
            await _cache.DeleteAsync(jellyfinSignatureCacheKey);
            _logger.LogDebug("Cleared Jellyfin signature cache to force change detection");
            
            // Clear the matched results cache to force re-matching
            var matchedTracksKey = $"spotify:matched:ordered:{decodedName}";
            await _cache.DeleteAsync(matchedTracksKey);
            _logger.LogDebug("Cleared matched tracks cache");
            
            // Clear the playlist items cache
            var playlistItemsCacheKey = $"spotify:playlist:items:{decodedName}";
            await _cache.DeleteAsync(playlistItemsCacheKey);
            _logger.LogDebug("Cleared playlist items cache");
            
            // Trigger matching (will use cached Spotify data if still valid)
            await _matchingService.TriggerMatchingForPlaylistAsync(decodedName);
            
            // Invalidate playlist summary cache
            InvalidatePlaylistSummaryCache();
            
            return Ok(new { 
                message = $"Re-matching tracks for {decodedName} (checking local changes)", 
                timestamp = DateTime.UtcNow 
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to trigger track matching for {Name}", decodedName);
            return StatusCode(500, new { error = "Failed to trigger track matching", details = ex.Message });
        }
    }
    
    /// <summary>
    /// Rebuild playlist from scratch when REMOTE (Spotify) playlist has changed.
    /// Clears all caches including Spotify data and forces fresh fetch.
    /// </summary>
    [HttpPost("playlists/{name}/clear-cache")]
    public async Task<IActionResult> ClearPlaylistCache(string name)
    {
        var decodedName = Uri.UnescapeDataString(name);
        _logger.LogInformation("Rebuild from scratch triggered for playlist: {Name} (clearing Spotify cache)", decodedName);
        
        if (_matchingService == null)
        {
            return BadRequest(new { error = "Track matching service is not available" });
        }
        
        try
        {
            // Clear ALL cache keys for this playlist (including Spotify data)
            var cacheKeys = new[]
            {
                $"spotify:playlist:items:{decodedName}",           // Pre-built items cache
                $"spotify:matched:ordered:{decodedName}",          // Ordered matched tracks
                $"spotify:matched:{decodedName}",                  // Legacy matched tracks
                $"spotify:missing:{decodedName}",                  // Missing tracks
                $"spotify:playlist:jellyfin-signature:{decodedName}", // Jellyfin signature
                $"spotify:playlist:{decodedName}"                  // Spotify playlist data
            };
            
            foreach (var key in cacheKeys)
            {
                await _cache.DeleteAsync(key);
                _logger.LogDebug("Cleared cache key: {Key}", key);
            }
            
            // Delete file caches
            var safeName = string.Join("_", decodedName.Split(Path.GetInvalidFileNameChars()));
            var filesToDelete = new[]
            {
                Path.Combine(CacheDirectory, $"{safeName}_items.json"),
                Path.Combine(CacheDirectory, $"{safeName}_matched.json")
            };
            
            foreach (var file in filesToDelete)
            {
                if (System.IO.File.Exists(file))
                {
                    System.IO.File.Delete(file);
                    _logger.LogDebug("Deleted cache file: {File}", file);
                }
            }
            
            _logger.LogInformation("✓ Cleared all caches for playlist: {Name} (including Spotify data)", decodedName);
            
            // Trigger rebuild (will fetch fresh Spotify data)
            await _matchingService.TriggerMatchingForPlaylistAsync(decodedName);
            
            // Invalidate playlist summary cache
            InvalidatePlaylistSummaryCache();
            
            return Ok(new 
            { 
                message = $"Rebuilding {decodedName} from scratch (fetching fresh Spotify data)", 
                timestamp = DateTime.UtcNow,
                clearedKeys = cacheKeys.Length,
                clearedFiles = filesToDelete.Count(f => System.IO.File.Exists(f))
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear cache for {Name}", decodedName);
            return StatusCode(500, new { error = "Failed to clear cache", details = ex.Message });
        }
    }
    
    /// <summary>
    /// Search Jellyfin library for tracks (for manual mapping)
    /// </summary>
    [HttpGet("jellyfin/search")]
    public async Task<IActionResult> SearchJellyfinTracks([FromQuery] string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return BadRequest(new { error = "Query is required" });
        }
        
        try
        {
            var userId = _jellyfinSettings.UserId;
            
            // Build URL with UserId if available
            var url = $"{_jellyfinSettings.Url}/Items?searchTerm={Uri.EscapeDataString(query)}&includeItemTypes=Audio&recursive=true&limit=20";
            if (!string.IsNullOrEmpty(userId))
            {
                url += $"&UserId={userId}";
            }
            
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("X-Emby-Authorization", GetJellyfinAuthHeader());
            
            _logger.LogDebug("Searching Jellyfin: {Url}", url);
            
            var response = await _jellyfinHttpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                _logger.LogError("Jellyfin search failed: {StatusCode} - {Error}", response.StatusCode, errorBody);
                return StatusCode((int)response.StatusCode, new { error = "Failed to search Jellyfin" });
            }
            
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            
            var tracks = new List<object>();
            if (doc.RootElement.TryGetProperty("Items", out var items))
            {
                foreach (var item in items.EnumerateArray())
                {
                    // Verify it's actually an Audio item
                    var type = item.TryGetProperty("Type", out var typeEl) ? typeEl.GetString() : "";
                    if (type != "Audio")
                    {
                        _logger.LogWarning("Skipping non-audio item: {Type}", type);
                        continue;
                    }
                    
                    var id = item.TryGetProperty("Id", out var idEl) ? idEl.GetString() : "";
                    var title = item.TryGetProperty("Name", out var nameEl) ? nameEl.GetString() : "";
                    var album = item.TryGetProperty("Album", out var albumEl) ? albumEl.GetString() : "";
                    var artist = "";
                    
                    if (item.TryGetProperty("Artists", out var artistsEl) && artistsEl.GetArrayLength() > 0)
                    {
                        artist = artistsEl[0].GetString() ?? "";
                    }
                    else if (item.TryGetProperty("AlbumArtist", out var albumArtistEl))
                    {
                        artist = albumArtistEl.GetString() ?? "";
                    }
                    
                    tracks.Add(new { id, title, artist, album });
                }
            }
            
            return Ok(new { tracks });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to search Jellyfin tracks");
            return StatusCode(500, new { error = "Search failed" });
        }
    }
    
    /// <summary>
    /// Get track details by Jellyfin ID (for URL-based mapping)
    /// </summary>
    [HttpGet("jellyfin/track/{id}")]
    public async Task<IActionResult> GetJellyfinTrack(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return BadRequest(new { error = "Track ID is required" });
        }
        
        try
        {
            var userId = _jellyfinSettings.UserId;
            
            var url = $"{_jellyfinSettings.Url}/Items/{id}";
            if (!string.IsNullOrEmpty(userId))
            {
                url += $"?UserId={userId}";
            }
            
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("X-Emby-Authorization", GetJellyfinAuthHeader());
            
            _logger.LogDebug("Fetching Jellyfin track {Id} from {Url}", id, url);
            
            var response = await _jellyfinHttpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                _logger.LogError("Failed to fetch Jellyfin track {Id}: {StatusCode} - {Error}", 
                    id, response.StatusCode, errorBody);
                return StatusCode((int)response.StatusCode, new { error = "Track not found in Jellyfin" });
            }
            
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            
            var item = doc.RootElement;
            
            // Verify it's an Audio item
            var type = item.TryGetProperty("Type", out var typeEl) ? typeEl.GetString() : "";
            if (type != "Audio")
            {
                _logger.LogWarning("Item {Id} is not an Audio track, it's a {Type}", id, type);
                return BadRequest(new { error = $"Item is not an audio track (it's a {type})" });
            }
            
            var trackId = item.TryGetProperty("Id", out var idEl) ? idEl.GetString() : "";
            var title = item.TryGetProperty("Name", out var nameEl) ? nameEl.GetString() : "";
            var album = item.TryGetProperty("Album", out var albumEl) ? albumEl.GetString() : "";
            var artist = "";
            
            if (item.TryGetProperty("Artists", out var artistsEl) && artistsEl.GetArrayLength() > 0)
            {
                artist = artistsEl[0].GetString() ?? "";
            }
            else if (item.TryGetProperty("AlbumArtist", out var albumArtistEl))
            {
                artist = albumArtistEl.GetString() ?? "";
            }
            
            _logger.LogInformation("Found Jellyfin track: {Title} by {Artist}", title, artist);
            
            return Ok(new { id = trackId, title, artist, album });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get Jellyfin track {Id}", id);
            return StatusCode(500, new { error = "Failed to get track details" });
        }
    }
    
    /// <summary>
    /// Save manual track mapping (local Jellyfin or external provider)
    /// </summary>
    [HttpPost("playlists/{name}/map")]
    public async Task<IActionResult> SaveManualMapping(string name, [FromBody] ManualMappingRequest request)
    {
        var decodedName = Uri.UnescapeDataString(name);
        
        if (string.IsNullOrWhiteSpace(request.SpotifyId))
        {
            return BadRequest(new { error = "SpotifyId is required" });
        }
        
        // Validate that either Jellyfin mapping or external mapping is provided
        var hasJellyfinMapping = !string.IsNullOrWhiteSpace(request.JellyfinId);
        var hasExternalMapping = !string.IsNullOrWhiteSpace(request.ExternalProvider) && !string.IsNullOrWhiteSpace(request.ExternalId);
        
        if (!hasJellyfinMapping && !hasExternalMapping)
        {
            return BadRequest(new { error = "Either JellyfinId or (ExternalProvider + ExternalId) is required" });
        }
        
        if (hasJellyfinMapping && hasExternalMapping)
        {
            return BadRequest(new { error = "Cannot specify both Jellyfin and external mapping for the same track" });
        }
        
        try
        {
            string? normalizedProvider = null;
            
            if (hasJellyfinMapping)
            {
                // Store Jellyfin mapping in cache (NO EXPIRATION - manual mappings are permanent)
                var mappingKey = $"spotify:manual-map:{decodedName}:{request.SpotifyId}";
                await _cache.SetAsync(mappingKey, request.JellyfinId!);
                
                // Also save to file for persistence across restarts
                await SaveManualMappingToFileAsync(decodedName, request.SpotifyId, request.JellyfinId!, null, null);
                
                _logger.LogInformation("Manual Jellyfin mapping saved: {Playlist} - Spotify {SpotifyId} → Jellyfin {JellyfinId}", 
                    decodedName, request.SpotifyId, request.JellyfinId);
            }
            else
            {
                // Store external mapping in cache (NO EXPIRATION - manual mappings are permanent)
                var externalMappingKey = $"spotify:external-map:{decodedName}:{request.SpotifyId}";
                normalizedProvider = request.ExternalProvider!.ToLowerInvariant(); // Normalize to lowercase
                var externalMapping = new { provider = normalizedProvider, id = request.ExternalId };
                await _cache.SetAsync(externalMappingKey, externalMapping);
                
                // Also save to file for persistence across restarts
                await SaveManualMappingToFileAsync(decodedName, request.SpotifyId, null, normalizedProvider, request.ExternalId!);
                
                _logger.LogInformation("Manual external mapping saved: {Playlist} - Spotify {SpotifyId} → {Provider} {ExternalId}", 
                    decodedName, request.SpotifyId, normalizedProvider, request.ExternalId);
            }
            
            // Clear all related caches to force rebuild
            var matchedCacheKey = $"spotify:matched:{decodedName}";
            var orderedCacheKey = $"spotify:matched:ordered:{decodedName}";
            var playlistItemsKey = $"spotify:playlist:items:{decodedName}";
            
            await _cache.DeleteAsync(matchedCacheKey);
            await _cache.DeleteAsync(orderedCacheKey);
            await _cache.DeleteAsync(playlistItemsKey);
            
            // Also delete file caches to force rebuild
            try
            {
                var cacheDir = "/app/cache/spotify";
                var safeName = string.Join("_", decodedName.Split(Path.GetInvalidFileNameChars()));
                var matchedFile = Path.Combine(cacheDir, $"{safeName}_matched.json");
                var itemsFile = Path.Combine(cacheDir, $"{safeName}_items.json");
                
                if (System.IO.File.Exists(matchedFile))
                {
                    System.IO.File.Delete(matchedFile);
                    _logger.LogInformation("Deleted matched tracks file cache for {Playlist}", decodedName);
                }
                
                if (System.IO.File.Exists(itemsFile))
                {
                    System.IO.File.Delete(itemsFile);
                    _logger.LogDebug("Deleted playlist items file cache for {Playlist}", decodedName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete file caches for {Playlist}", decodedName);
            }
            
            _logger.LogInformation("Cleared playlist caches for {Playlist} to force rebuild", decodedName);
            
            // Fetch external provider track details to return to the UI (only for external mappings)
            string? trackTitle = null;
            string? trackArtist = null;
            string? trackAlbum = null;
            
            if (hasExternalMapping && normalizedProvider != null)
            {
                try
                {
                    var metadataService = HttpContext.RequestServices.GetRequiredService<IMusicMetadataService>();
                    var externalSong = await metadataService.GetSongAsync(normalizedProvider, request.ExternalId!);
                    
                    if (externalSong != null)
                    {
                        trackTitle = externalSong.Title;
                        trackArtist = externalSong.Artist;
                        trackAlbum = externalSong.Album;
                        _logger.LogInformation("✓ Fetched external track metadata: {Title} by {Artist}", trackTitle, trackArtist);
                    }
                    else
                    {
                        _logger.LogError("Failed to fetch external track metadata for {Provider} ID {Id}", 
                            normalizedProvider, request.ExternalId);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to fetch external track metadata, but mapping was saved");
                }
            }
            
            // Trigger immediate playlist rebuild with the new mapping
            if (_matchingService != null)
            {
                _logger.LogInformation("Triggering immediate playlist rebuild for {Playlist} with new manual mapping", decodedName);
                
                // Run rebuild in background with timeout to avoid blocking the response
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2)); // 2 minute timeout
                        await _matchingService.TriggerMatchingForPlaylistAsync(decodedName);
                        _logger.LogInformation("✓ Playlist {Playlist} rebuilt successfully with manual mapping", decodedName);
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.LogWarning("Playlist rebuild for {Playlist} timed out after 2 minutes", decodedName);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to rebuild playlist {Playlist} after manual mapping", decodedName);
                    }
                });
            }
            else
            {
                _logger.LogWarning("Matching service not available - playlist will rebuild on next scheduled run");
            }
            
            // Return success with track details if available
            var mappedTrack = new
            {
                id = request.ExternalId,
                title = trackTitle ?? "Unknown",
                artist = trackArtist ?? "Unknown",
                album = trackAlbum ?? "Unknown",
                isLocal = false,
                externalProvider = request.ExternalProvider!.ToLowerInvariant()
            };
            
            return Ok(new 
            { 
                message = "Mapping saved and playlist rebuild triggered", 
                track = mappedTrack,
                rebuildTriggered = _matchingService != null
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save manual mapping");
            return StatusCode(500, new { error = "Failed to save mapping" });
        }
    }
    
    /// <summary>
    /// Trigger track matching for all playlists
    /// </summary>
    [HttpPost("playlists/match-all")]
    public async Task<IActionResult> MatchAllPlaylistTracks()
    {
        _logger.LogInformation("Manual track matching triggered for all playlists");
        
        if (_matchingService == null)
        {
            return BadRequest(new { error = "Track matching service is not available" });
        }
        
        try
        {
            await _matchingService.TriggerMatchingAsync();
            return Ok(new { message = "Track matching triggered for all playlists", timestamp = DateTime.UtcNow });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to trigger track matching for all playlists");
            return StatusCode(500, new { error = "Failed to trigger track matching", details = ex.Message });
        }
    }
    
    /// <summary>
    /// Get current configuration (safe values only)
    /// </summary>
    [HttpGet("config")]
    public IActionResult GetConfig()
    {
        return Ok(new
        {
            backendType = _configuration.GetValue<string>("Backend:Type") ?? "Jellyfin",
            musicService = _configuration.GetValue<string>("MusicService") ?? "SquidWTF",
            explicitFilter = _configuration.GetValue<string>("ExplicitFilter") ?? "All",
            enableExternalPlaylists = _configuration.GetValue<bool>("EnableExternalPlaylists", false),
            playlistsDirectory = _configuration.GetValue<string>("PlaylistsDirectory") ?? "(not set)",
            redisEnabled = _configuration.GetValue<bool>("Redis:Enabled", false),
            spotifyApi = new
            {
                enabled = _spotifyApiSettings.Enabled,
                sessionCookie = MaskValue(_spotifyApiSettings.SessionCookie, showLast: 8),
                sessionCookieSetDate = _spotifyApiSettings.SessionCookieSetDate,
                cacheDurationMinutes = _spotifyApiSettings.CacheDurationMinutes,
                rateLimitDelayMs = _spotifyApiSettings.RateLimitDelayMs,
                preferIsrcMatching = _spotifyApiSettings.PreferIsrcMatching
            },
            spotifyImport = new
            {
                enabled = _spotifyImportSettings.Enabled,
                matchingIntervalHours = _spotifyImportSettings.MatchingIntervalHours,
                playlists = _spotifyImportSettings.Playlists.Select(p => new
                {
                    name = p.Name,
                    id = p.Id,
                    localTracksPosition = p.LocalTracksPosition.ToString()
                })
            },
            jellyfin = new
            {
                url = _jellyfinSettings.Url,
                apiKey = MaskValue(_jellyfinSettings.ApiKey),
                userId = _jellyfinSettings.UserId ?? "(not set)",
                libraryId = _jellyfinSettings.LibraryId
            },
            library = new
            {
                downloadPath = _subsonicSettings.StorageMode == StorageMode.Cache 
                    ? Path.Combine(_configuration["Library:DownloadPath"] ?? "./downloads", "cache")
                    : Path.Combine(_configuration["Library:DownloadPath"] ?? "./downloads", "permanent"),
                keptPath = Path.Combine(_configuration["Library:DownloadPath"] ?? "./downloads", "kept"),
                storageMode = _subsonicSettings.StorageMode.ToString(),
                cacheDurationHours = _subsonicSettings.CacheDurationHours,
                downloadMode = _subsonicSettings.DownloadMode.ToString()
            },
            deezer = new
            {
                arl = MaskValue(_deezerSettings.Arl, showLast: 8),
                arlFallback = MaskValue(_deezerSettings.ArlFallback, showLast: 8),
                quality = _deezerSettings.Quality ?? "FLAC"
            },
            qobuz = new
            {
                userAuthToken = MaskValue(_qobuzSettings.UserAuthToken, showLast: 8),
                userId = _qobuzSettings.UserId,
                quality = _qobuzSettings.Quality ?? "FLAC"
            },
            squidWtf = new
            {
                quality = _squidWtfSettings.Quality ?? "LOSSLESS"
            },
            musicBrainz = new
            {
                enabled = _musicBrainzSettings.Enabled,
                username = _musicBrainzSettings.Username ?? "(not set)",
                password = MaskValue(_musicBrainzSettings.Password),
                baseUrl = _musicBrainzSettings.BaseUrl,
                rateLimitMs = _musicBrainzSettings.RateLimitMs
            }
        });
    }
    
    /// <summary>
    /// Update configuration by modifying .env file
    /// </summary>
    [HttpPost("config")]
    public async Task<IActionResult> UpdateConfig([FromBody] ConfigUpdateRequest request)
    {
        if (request == null || request.Updates == null || request.Updates.Count == 0)
        {
            return BadRequest(new { error = "No updates provided" });
        }
        
        _logger.LogDebug("Config update requested: {Count} changes", request.Updates.Count);
        
        try
        {
            // Check if .env file exists
            if (!System.IO.File.Exists(_envFilePath))
            {
                _logger.LogWarning(".env file not found at {Path}, creating new file", _envFilePath);
            }
            
            // Read current .env file or create new one
            var envContent = new Dictionary<string, string>();
            
            if (System.IO.File.Exists(_envFilePath))
            {
                var lines = await System.IO.File.ReadAllLinesAsync(_envFilePath);
                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
                        continue;
                    
                    var eqIndex = line.IndexOf('=');
                    if (eqIndex > 0)
                    {
                        var key = line[..eqIndex].Trim();
                        var value = line[(eqIndex + 1)..].Trim();
                        envContent[key] = value;
                    }
                }
                _logger.LogDebug("Loaded {Count} existing env vars from {Path}", envContent.Count, _envFilePath);
            }
            
            // Apply updates with validation
            var appliedUpdates = new List<string>();
            foreach (var (key, value) in request.Updates)
            {
                // Validate key format
                if (!IsValidEnvKey(key))
                {
                    _logger.LogWarning("Invalid env key rejected: {Key}", key);
                    return BadRequest(new { error = $"Invalid environment variable key: {key}" });
                }
                
                envContent[key] = value;
                appliedUpdates.Add(key);
                _logger.LogInformation("  Setting {Key} = {Value}", key, 
                    key.Contains("COOKIE") || key.Contains("TOKEN") || key.Contains("KEY") || key.Contains("ARL") 
                        ? "***" + (value.Length > 8 ? value[^8..] : "") 
                        : value);
                
                // Auto-set cookie date when Spotify session cookie is updated
                if (key == "SPOTIFY_API_SESSION_COOKIE" && !string.IsNullOrEmpty(value))
                {
                    var dateKey = "SPOTIFY_API_SESSION_COOKIE_SET_DATE";
                    var dateValue = DateTime.UtcNow.ToString("o"); // ISO 8601 format
                    envContent[dateKey] = dateValue;
                    appliedUpdates.Add(dateKey);
                    _logger.LogInformation("  Auto-setting {Key} to {Value}", dateKey, dateValue);
                }
            }
            
            // Write back to .env file
            var newContent = string.Join("\n", envContent.Select(kv => $"{kv.Key}={kv.Value}"));
            await System.IO.File.WriteAllTextAsync(_envFilePath, newContent + "\n");
            
            _logger.LogDebug("Config file updated successfully at {Path}", _envFilePath);
            
            // Invalidate playlist summary cache if playlists were updated
            if (appliedUpdates.Contains("SPOTIFY_IMPORT_PLAYLISTS"))
            {
                InvalidatePlaylistSummaryCache();
            }
            
            return Ok(new
            {
                message = "Configuration updated. Restart container to apply changes.",
                updatedKeys = appliedUpdates,
                requiresRestart = true,
                envFilePath = _envFilePath
            });
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "Permission denied writing to .env file at {Path}", _envFilePath);
            return StatusCode(500, new { 
                error = "Permission denied", 
                details = "Cannot write to .env file. Check file permissions and volume mount.",
                path = _envFilePath
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update configuration at {Path}", _envFilePath);
            return StatusCode(500, new { 
                error = "Failed to update configuration", 
                details = ex.Message,
                path = _envFilePath
            });
        }
    }
    
    /// <summary>
    /// Add a new playlist to the configuration
    /// </summary>
    [HttpPost("playlists")]
    public async Task<IActionResult> AddPlaylist([FromBody] AddPlaylistRequest request)
    {
        if (string.IsNullOrEmpty(request.Name) || string.IsNullOrEmpty(request.SpotifyId))
        {
            return BadRequest(new { error = "Name and SpotifyId are required" });
        }
        
        _logger.LogInformation("Adding playlist: {Name} ({SpotifyId})", request.Name, request.SpotifyId);
        
        // Get current playlists
        var currentPlaylists = _spotifyImportSettings.Playlists.ToList();
        
        // Check for duplicates
        if (currentPlaylists.Any(p => p.Id == request.SpotifyId || p.Name == request.Name))
        {
            return BadRequest(new { error = "Playlist with this name or ID already exists" });
        }
        
        // Add new playlist
        currentPlaylists.Add(new SpotifyPlaylistConfig
        {
            Name = request.Name,
            Id = request.SpotifyId,
            LocalTracksPosition = request.LocalTracksPosition == "last" 
                ? LocalTracksPosition.Last 
                : LocalTracksPosition.First
        });
        
        // Convert to JSON format for env var
        var playlistsJson = JsonSerializer.Serialize(
            currentPlaylists.Select(p => new[] { p.Name, p.Id, p.LocalTracksPosition.ToString().ToLower() }).ToArray()
        );
        
        // Update .env file
        var updateRequest = new ConfigUpdateRequest
        {
            Updates = new Dictionary<string, string>
            {
                ["SPOTIFY_IMPORT_PLAYLISTS"] = playlistsJson
            }
        };
        
        return await UpdateConfig(updateRequest);
    }
    
    /// <summary>
    /// Remove a playlist from the configuration
    /// </summary>
    [HttpDelete("playlists/{name}")]
    public async Task<IActionResult> RemovePlaylist(string name)
    {
        var decodedName = Uri.UnescapeDataString(name);
        _logger.LogInformation("Removing playlist: {Name}", decodedName);
        
        // Read current playlists from .env file (not stale in-memory config)
        var currentPlaylists = await ReadPlaylistsFromEnvFile();
        var playlist = currentPlaylists.FirstOrDefault(p => p.Name == decodedName);
        
        if (playlist == null)
        {
            return NotFound(new { error = "Playlist not found" });
        }
        
        currentPlaylists.Remove(playlist);
        
        // Convert to JSON format for env var: [["Name","SpotifyId","JellyfinId","first|last"],...]
        var playlistsJson = JsonSerializer.Serialize(
            currentPlaylists.Select(p => new[] { p.Name, p.Id, p.JellyfinId, p.LocalTracksPosition.ToString().ToLower() }).ToArray()
        );
        
        // Update .env file
        var updateRequest = new ConfigUpdateRequest
        {
            Updates = new Dictionary<string, string>
            {
                ["SPOTIFY_IMPORT_PLAYLISTS"] = playlistsJson
            }
        };
        
        return await UpdateConfig(updateRequest);
    }
    
    /// <summary>
    /// Clear all cached data
    /// </summary>
    [HttpPost("cache/clear")]
    public async Task<IActionResult> ClearCache()
    {
        _logger.LogDebug("Cache clear requested from admin UI");
        
        var clearedFiles = 0;
        var clearedRedisKeys = 0;
        
        // Clear file cache
        if (Directory.Exists(CacheDirectory))
        {
            foreach (var file in Directory.GetFiles(CacheDirectory, "*.json"))
            {
                try
                {
                    System.IO.File.Delete(file);
                    clearedFiles++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to delete cache file {File}", file);
                }
            }
        }
        
        // Clear ALL Redis cache keys for Spotify playlists
        // This includes matched tracks, ordered tracks, missing tracks, playlist items, etc.
        foreach (var playlist in _spotifyImportSettings.Playlists)
        {
            var keysToDelete = new[]
            {
                $"spotify:playlist:{playlist.Name}",
                $"spotify:missing:{playlist.Name}",
                $"spotify:matched:{playlist.Name}",
                $"spotify:matched:ordered:{playlist.Name}",
                $"spotify:playlist:items:{playlist.Name}"  // NEW: Clear file-backed playlist items cache
            };
            
            foreach (var key in keysToDelete)
            {
                if (await _cache.DeleteAsync(key))
                {
                    clearedRedisKeys++;
                    _logger.LogInformation("Cleared Redis cache key: {Key}", key);
                }
            }
        }
        
        // Clear all search cache keys (pattern-based deletion)
        var searchKeysDeleted = await _cache.DeleteByPatternAsync("search:*");
        clearedRedisKeys += searchKeysDeleted;
        
        // Clear all image cache keys (pattern-based deletion)
        var imageKeysDeleted = await _cache.DeleteByPatternAsync("image:*");
        clearedRedisKeys += imageKeysDeleted;
        
        _logger.LogInformation("Cache cleared: {Files} files, {RedisKeys} Redis keys (including {SearchKeys} search keys, {ImageKeys} image keys)", 
            clearedFiles, clearedRedisKeys, searchKeysDeleted, imageKeysDeleted);
        
        return Ok(new { 
            message = "Cache cleared successfully", 
            filesDeleted = clearedFiles,
            redisKeysDeleted = clearedRedisKeys
        });
    }
    
    /// <summary>
    /// Restart the allstarr container to apply configuration changes
    /// </summary>
    [HttpPost("restart")]
    public async Task<IActionResult> RestartContainer()
    {
        _logger.LogDebug("Container restart requested from admin UI");
        
        try
        {
            // Use Docker socket to restart the container
            var socketPath = "/var/run/docker.sock";
            
            if (!System.IO.File.Exists(socketPath))
            {
                _logger.LogWarning("Docker socket not available at {Path}", socketPath);
                return StatusCode(503, new { 
                    error = "Docker socket not available", 
                    message = "Please restart manually: docker-compose restart allstarr" 
                });
            }
            
            // Get container ID from hostname (Docker sets hostname to container ID by default)
            // Or use the well-known container name
            var containerId = Environment.MachineName;
            var containerName = "allstarr";
            
            _logger.LogDebug("Attempting to restart container {ContainerId} / {ContainerName}", containerId, containerName);
            
            // Create Unix socket HTTP client
            var handler = new SocketsHttpHandler
            {
                ConnectCallback = async (context, cancellationToken) =>
                {
                    var socket = new System.Net.Sockets.Socket(
                        System.Net.Sockets.AddressFamily.Unix,
                        System.Net.Sockets.SocketType.Stream,
                        System.Net.Sockets.ProtocolType.Unspecified);
                    
                    var endpoint = new System.Net.Sockets.UnixDomainSocketEndPoint(socketPath);
                    await socket.ConnectAsync(endpoint, cancellationToken);
                    
                    return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
                }
            };
            
            using var dockerClient = new HttpClient(handler)
            {
                BaseAddress = new Uri("http://localhost")
            };
            
            // Try to restart by container name first, then by ID
            var response = await dockerClient.PostAsync($"/containers/{containerName}/restart?t=5", null);
            
            if (!response.IsSuccessStatusCode)
            {
                // Try by container ID
                response = await dockerClient.PostAsync($"/containers/{containerId}/restart?t=5", null);
            }
            
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Container restart initiated successfully");
                return Ok(new { message = "Restarting container...", success = true });
            }
            else
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                _logger.LogError("Failed to restart container: {StatusCode} - {Body}", response.StatusCode, errorBody);
                return StatusCode((int)response.StatusCode, new { 
                    error = "Failed to restart container", 
                    message = "Please restart manually: docker-compose restart allstarr" 
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error restarting container");
            return StatusCode(500, new { 
                error = "Failed to restart container", 
                details = ex.Message,
                message = "Please restart manually: docker-compose restart allstarr" 
            });
        }
    }
    
    /// <summary>
    /// Initialize cookie date to current date if cookie exists but date is not set
    /// </summary>
    [HttpPost("config/init-cookie-date")]
    public async Task<IActionResult> InitCookieDate()
    {
        // Only init if cookie exists but date is not set
        if (string.IsNullOrEmpty(_spotifyApiSettings.SessionCookie))
        {
            return BadRequest(new { error = "No cookie set" });
        }
        
        if (!string.IsNullOrEmpty(_spotifyApiSettings.SessionCookieSetDate))
        {
            return Ok(new { message = "Cookie date already set", date = _spotifyApiSettings.SessionCookieSetDate });
        }
        
        _logger.LogInformation("Initializing cookie date to current date (cookie existed without date tracking)");
        
        var updateRequest = new ConfigUpdateRequest
        {
            Updates = new Dictionary<string, string>
            {
                ["SPOTIFY_API_SESSION_COOKIE_SET_DATE"] = DateTime.UtcNow.ToString("o")
            }
        };
        
        return await UpdateConfig(updateRequest);
    }
    
    /// <summary>
    /// Get all Jellyfin users
    /// </summary>
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
            
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("X-Emby-Authorization", GetJellyfinAuthHeader());
            
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
            
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("X-Emby-Authorization", GetJellyfinAuthHeader());
            
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
    [HttpGet("spotify/user-playlists")]
    public async Task<IActionResult> GetSpotifyUserPlaylists()
    {
        if (!_spotifyApiSettings.Enabled || string.IsNullOrEmpty(_spotifyApiSettings.SessionCookie))
        {
            return BadRequest(new { error = "Spotify API not configured. Please set sp_dc session cookie." });
        }
        
        try
        {
            // Get list of already-configured Spotify playlist IDs
            var configuredPlaylists = await ReadPlaylistsFromEnvFile();
            var linkedSpotifyIds = new HashSet<string>(
                configuredPlaylists.Select(p => p.Id),
                StringComparer.OrdinalIgnoreCase
            );
            
            // Use SpotifyApiClient's GraphQL method - much less rate-limited than REST API
            var spotifyPlaylists = await _spotifyClient.GetUserPlaylistsAsync(searchName: null);
            
            if (spotifyPlaylists == null || spotifyPlaylists.Count == 0)
            {
                return Ok(new { playlists = new List<object>() });
            }
            
            var playlists = spotifyPlaylists.Select(p => new
            {
                id = p.SpotifyId,
                name = p.Name,
                trackCount = p.TotalTracks,
                owner = p.OwnerName ?? "",
                isPublic = p.Public,
                isLinked = linkedSpotifyIds.Contains(p.SpotifyId)
            }).ToList();
            
            return Ok(new { playlists });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching Spotify user playlists");
            return StatusCode(500, new { error = "Failed to fetch Spotify playlists", details = ex.Message });
        }
    }
    
    /// <summary>
    /// Get all playlists from Jellyfin
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
            
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("X-Emby-Authorization", GetJellyfinAuthHeader());
            
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
            var configuredPlaylists = await ReadPlaylistsFromEnvFile();
            
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
                var usersResponse = await _jellyfinHttpClient.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"{_jellyfinSettings.Url}/Users")
                {
                    Headers = { { "X-Emby-Authorization", GetJellyfinAuthHeader() } }
                });
                
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
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("X-Emby-Authorization", GetJellyfinAuthHeader());
            
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
        var currentPlaylists = await ReadPlaylistsFromEnvFile();
        
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
            SyncSchedule = request.SyncSchedule ?? "0 8 * * 1" // Default to Monday 8 AM
        });
        
        // Convert to JSON format for env var: [["Name","SpotifyId","JellyfinId","first|last","cronSchedule"],...]
        var playlistsJson = JsonSerializer.Serialize(
            currentPlaylists.Select(p => new[] { 
                p.Name, 
                p.Id, 
                p.JellyfinId, 
                p.LocalTracksPosition.ToString().ToLower(),
                p.SyncSchedule ?? "0 8 * * 1"
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
        
        return await UpdateConfig(updateRequest);
    }
    
    /// <summary>
    /// Unlink a playlist (remove from configuration)
    /// </summary>
    [HttpDelete("jellyfin/playlists/{name}/unlink")]
    public async Task<IActionResult> UnlinkPlaylist(string name)
    {
        var decodedName = Uri.UnescapeDataString(name);
        return await RemovePlaylist(decodedName);
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
        var currentPlaylists = await ReadPlaylistsFromEnvFile();
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
                p.SyncSchedule ?? "0 8 * * 1"
            }).ToArray()
        );
        
        var updateRequest = new ConfigUpdateRequest
        {
            Updates = new Dictionary<string, string>
            {
                ["SPOTIFY_IMPORT_PLAYLISTS"] = playlistsJson
            }
        };
        
        return await UpdateConfig(updateRequest);
    }
    
    private string GetJellyfinAuthHeader()
    {
        return $"MediaBrowser Client=\"Allstarr\", Device=\"Server\", DeviceId=\"allstarr-admin\", Version=\"1.0.1\", Token=\"{_jellyfinSettings.ApiKey}\"";
    }
    
    /// <summary>
    /// Read current playlists from .env file (not stale in-memory config)
    /// </summary>
    private async Task<List<SpotifyPlaylistConfig>> ReadPlaylistsFromEnvFile()
    {
        var playlists = new List<SpotifyPlaylistConfig>();
        
        if (!System.IO.File.Exists(_envFilePath))
        {
            return playlists;
        }
        
        try
        {
            var lines = await System.IO.File.ReadAllLinesAsync(_envFilePath);
            foreach (var line in lines)
            {
                if (line.TrimStart().StartsWith("SPOTIFY_IMPORT_PLAYLISTS="))
                {
                    var value = line.Substring(line.IndexOf('=') + 1).Trim();
                    
                    if (string.IsNullOrWhiteSpace(value) || value == "[]")
                    {
                        return playlists;
                    }
                    
                    // Parse JSON array format: [["Name","SpotifyId","JellyfinId","first|last","cronSchedule"],...]
                    var playlistArrays = JsonSerializer.Deserialize<string[][]>(value);
                    if (playlistArrays != null)
                    {
                        foreach (var arr in playlistArrays)
                        {
                            if (arr.Length >= 2)
                            {
                                playlists.Add(new SpotifyPlaylistConfig
                                {
                                    Name = arr[0].Trim(),
                                    Id = arr[1].Trim(),
                                    JellyfinId = arr.Length >= 3 ? arr[2].Trim() : "",
                                    LocalTracksPosition = arr.Length >= 4 && 
                                        arr[3].Trim().Equals("last", StringComparison.OrdinalIgnoreCase)
                                        ? LocalTracksPosition.Last
                                        : LocalTracksPosition.First,
                                    SyncSchedule = arr.Length >= 5 ? arr[4].Trim() : "0 8 * * 1"
                                });
                            }
                        }
                    }
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read playlists from .env file");
        }
        
        return playlists;
    }
    
    private static string MaskValue(string? value, int showLast = 0)
    {
        if (string.IsNullOrEmpty(value)) return "(not set)";
        if (value.Length <= showLast) return "***";
        return showLast > 0 ? "***" + value[^showLast..] : value[..8] + "...";
    }
    
    private static string SanitizeFileName(string name)
    {
        return string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
    }
    
    private static bool IsValidEnvKey(string key)
    {
        // Only allow alphanumeric, underscore, and must start with letter/underscore
        return Regex.IsMatch(key, @"^[A-Z_][A-Z0-9_]*$", RegexOptions.IgnoreCase);
    }
    
    /// <summary>
    /// Export .env file for backup/transfer
    /// </summary>
    [HttpGet("export-env")]
    public IActionResult ExportEnv()
    {
        try
        {
            if (!System.IO.File.Exists(_envFilePath))
            {
                return NotFound(new { error = ".env file not found" });
            }

            var envContent = System.IO.File.ReadAllText(_envFilePath);
            var bytes = System.Text.Encoding.UTF8.GetBytes(envContent);
            
            return File(bytes, "text/plain", ".env");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export .env file");
            return StatusCode(500, new { error = "Failed to export .env file", details = ex.Message });
        }
    }
    
    /// <summary>
    /// Import .env file from upload
    /// </summary>
    [HttpPost("import-env")]
    public async Task<IActionResult> ImportEnv([FromForm] IFormFile file)
    {
        if (file == null || file.Length == 0)
        {
            return BadRequest(new { error = "No file provided" });
        }

        if (!file.FileName.EndsWith(".env"))
        {
            return BadRequest(new { error = "File must be a .env file" });
        }

        try
        {
            // Read uploaded file
            using var reader = new StreamReader(file.OpenReadStream());
            var content = await reader.ReadToEndAsync();
            
            // Validate it's a valid .env file (basic check)
            if (string.IsNullOrWhiteSpace(content))
            {
                return BadRequest(new { error = ".env file is empty" });
            }

            // Backup existing .env
            if (System.IO.File.Exists(_envFilePath))
            {
                var backupPath = $"{_envFilePath}.backup.{DateTime.UtcNow:yyyyMMddHHmmss}";
                System.IO.File.Copy(_envFilePath, backupPath, true);
                _logger.LogDebug("Backed up existing .env to {BackupPath}", backupPath);
            }

            // Write new .env file
            await System.IO.File.WriteAllTextAsync(_envFilePath, content);
            
            _logger.LogInformation(".env file imported successfully");
            
            return Ok(new 
            { 
                success = true, 
                message = ".env file imported successfully. Restart the application for changes to take effect." 
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to import .env file");
            return StatusCode(500, new { error = "Failed to import .env file", details = ex.Message });
        }
    }
    
    /// <summary>
    /// Gets detailed memory usage statistics for debugging.
    /// </summary>
    [HttpGet("memory-stats")]
    public IActionResult GetMemoryStats()
    {
        try
        {
            // Get memory stats BEFORE GC
            var memoryBeforeGC = GC.GetTotalMemory(false);
            var gen0Before = GC.CollectionCount(0);
            var gen1Before = GC.CollectionCount(1);
            var gen2Before = GC.CollectionCount(2);
            
            // Force garbage collection to get accurate numbers
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var memoryAfterGC = GC.GetTotalMemory(false);
            var gen0After = GC.CollectionCount(0);
            var gen1After = GC.CollectionCount(1);
            var gen2After = GC.CollectionCount(2);
            
            // Get process memory info
            var process = System.Diagnostics.Process.GetCurrentProcess();
            
            return Ok(new {
                Timestamp = DateTime.UtcNow,
                BeforeGC = new {
                    GCMemoryBytes = memoryBeforeGC,
                    GCMemoryMB = Math.Round(memoryBeforeGC / (1024.0 * 1024.0), 2)
                },
                AfterGC = new {
                    GCMemoryBytes = memoryAfterGC,
                    GCMemoryMB = Math.Round(memoryAfterGC / (1024.0 * 1024.0), 2)
                },
                MemoryFreedMB = Math.Round((memoryBeforeGC - memoryAfterGC) / (1024.0 * 1024.0), 2),
                ProcessWorkingSetBytes = process.WorkingSet64,
                ProcessWorkingSetMB = Math.Round(process.WorkingSet64 / (1024.0 * 1024.0), 2),
                ProcessPrivateMemoryBytes = process.PrivateMemorySize64,
                ProcessPrivateMemoryMB = Math.Round(process.PrivateMemorySize64 / (1024.0 * 1024.0), 2),
                ProcessVirtualMemoryBytes = process.VirtualMemorySize64,
                ProcessVirtualMemoryMB = Math.Round(process.VirtualMemorySize64 / (1024.0 * 1024.0), 2),
                GCCollections = new {
                    Gen0Before = gen0Before,
                    Gen0After = gen0After,
                    Gen0Triggered = gen0After - gen0Before,
                    Gen1Before = gen1Before,
                    Gen1After = gen1After,
                    Gen1Triggered = gen1After - gen1Before,
                    Gen2Before = gen2Before,
                    Gen2After = gen2After,
                    Gen2Triggered = gen2After - gen2Before
                },
                GCMode = GCSettings.IsServerGC ? "Server" : "Workstation",
                GCLatencyMode = GCSettings.LatencyMode.ToString()
            });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Forces garbage collection to free up memory (emergency use only).
    /// </summary>
    [HttpPost("force-gc")]
    public IActionResult ForceGarbageCollection()
    {
        try
        {
            var memoryBefore = GC.GetTotalMemory(false);
            var processBefore = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64;
            
            // Force full garbage collection
            GC.Collect(2, GCCollectionMode.Forced);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced);
            
            var memoryAfter = GC.GetTotalMemory(false);
            var processAfter = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64;
            
            return Ok(new {
                Timestamp = DateTime.UtcNow,
                MemoryFreedMB = Math.Round((memoryBefore - memoryAfter) / (1024.0 * 1024.0), 2),
                ProcessMemoryFreedMB = Math.Round((processBefore - processAfter) / (1024.0 * 1024.0), 2),
                BeforeGCMB = Math.Round(memoryBefore / (1024.0 * 1024.0), 2),
                AfterGCMB = Math.Round(memoryAfter / (1024.0 * 1024.0), 2),
                BeforeProcessMB = Math.Round(processBefore / (1024.0 * 1024.0), 2),
                AfterProcessMB = Math.Round(processAfter / (1024.0 * 1024.0), 2)
            });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Gets current active sessions for debugging.
    /// </summary>
    [HttpGet("sessions")]
    public IActionResult GetActiveSessions()
    {
        try
        {
            var sessionManager = HttpContext.RequestServices.GetService<JellyfinSessionManager>();
            if (sessionManager == null)
            {
                return BadRequest(new { error = "Session manager not available" });
            }

            var sessionInfo = sessionManager.GetSessionsInfo();
            return Ok(sessionInfo);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Helper method to trigger GC after large file operations to prevent memory leaks.
    /// </summary>
    private static void TriggerGCAfterLargeOperation(int sizeInBytes)
    {
        // Only trigger GC for files larger than 1MB to avoid performance impact
        if (sizeInBytes > 1024 * 1024)
        {
            // Suggest GC collection for large objects (they go to LOH and aren't collected as frequently)
            GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
        }
    }

    #region Spotify Admin Endpoints

    /// <summary>
    /// Manual trigger endpoint to force fetch Spotify missing tracks.
    /// </summary>
    [HttpGet("spotify/sync")]
    public async Task<IActionResult> TriggerSpotifySync([FromServices] IEnumerable<IHostedService> hostedServices)
    {
        try
        {
            if (!_spotifyImportSettings.Enabled)
            {
                return BadRequest(new { error = "Spotify Import is not enabled" });
            }

            _logger.LogInformation("Manual Spotify sync triggered via admin endpoint");
            
            // Find the SpotifyMissingTracksFetcher service
            var fetcherService = hostedServices
                .OfType<allstarr.Services.Spotify.SpotifyMissingTracksFetcher>()
                .FirstOrDefault();

            if (fetcherService == null)
            {
                return BadRequest(new { error = "SpotifyMissingTracksFetcher service not found" });
            }

            // Trigger the sync in background
            _ = Task.Run(async () =>
            {
                try
                {
                    // Use reflection to call the private ExecuteOnceAsync method
                    var method = fetcherService.GetType().GetMethod("ExecuteOnceAsync", 
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    
                    if (method != null)
                    {
                        await (Task)method.Invoke(fetcherService, new object[] { CancellationToken.None })!;
                        _logger.LogInformation("Manual Spotify sync completed successfully");
                    }
                    else
                    {
                        _logger.LogError("Could not find ExecuteOnceAsync method on SpotifyMissingTracksFetcher");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during manual Spotify sync");
                }
            });

            return Ok(new { 
                message = "Spotify sync started in background",
                timestamp = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error triggering Spotify sync");
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Manual trigger endpoint to force Spotify track matching.
    /// </summary>
    [HttpGet("spotify/match")]
    public async Task<IActionResult> TriggerSpotifyMatch([FromServices] IEnumerable<IHostedService> hostedServices)
    {
        try
        {
            if (!_spotifyApiSettings.Enabled)
            {
                return BadRequest(new { error = "Spotify API is not enabled" });
            }

            _logger.LogInformation("Manual Spotify track matching triggered via admin endpoint");
            
            // Find the SpotifyTrackMatchingService
            var matchingService = hostedServices
                .OfType<allstarr.Services.Spotify.SpotifyTrackMatchingService>()
                .FirstOrDefault();

            if (matchingService == null)
            {
                return BadRequest(new { error = "SpotifyTrackMatchingService not found" });
            }

            // Trigger matching in background
            _ = Task.Run(async () =>
            {
                try
                {
                    // Use reflection to call the private ExecuteOnceAsync method
                    var method = matchingService.GetType().GetMethod("ExecuteOnceAsync", 
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    
                    if (method != null)
                    {
                        await (Task)method.Invoke(matchingService, new object[] { CancellationToken.None })!;
                        _logger.LogInformation("Manual Spotify track matching completed successfully");
                    }
                    else
                    {
                        _logger.LogError("Could not find ExecuteOnceAsync method on SpotifyTrackMatchingService");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during manual Spotify track matching");
                }
            });

            return Ok(new { 
                message = "Spotify track matching started in background",
                timestamp = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error triggering Spotify track matching");
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Clear Spotify playlist cache to force re-matching.
    /// </summary>
    [HttpPost("spotify/clear-cache")]
    public async Task<IActionResult> ClearSpotifyCache()
    {
        try
        {
            var clearedKeys = new List<string>();
            
            // Clear Redis cache for all configured playlists
            foreach (var playlist in _spotifyImportSettings.Playlists)
            {
                var keys = new[]
                {
                    $"spotify:playlist:{playlist.Name}",
                    $"spotify:playlist:items:{playlist.Name}",
                    $"spotify:matched:{playlist.Name}"
                };
                
                foreach (var key in keys)
                {
                    await _cache.DeleteAsync(key);
                    clearedKeys.Add(key);
                }
            }
            
            _logger.LogDebug("Cleared Spotify cache for {Count} keys via admin endpoint", clearedKeys.Count);
            
            return Ok(new { 
                message = "Spotify cache cleared successfully",
                clearedKeys = clearedKeys,
                timestamp = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error clearing Spotify cache");
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    #endregion

    #region Debug Endpoints

    /// <summary>
    /// Gets endpoint usage statistics from the log file.
    /// </summary>
    [HttpGet("debug/endpoint-usage")]
    public async Task<IActionResult> GetEndpointUsage(
        [FromQuery] int top = 100,
        [FromQuery] string? since = null)
    {
        try
        {
            var logFile = "/app/cache/endpoint-usage/endpoints.csv";
            
            if (!System.IO.File.Exists(logFile))
            {
                return Ok(new { 
                    message = "No endpoint usage data available",
                    endpoints = new object[0]
                });
            }
            
            var lines = await System.IO.File.ReadAllLinesAsync(logFile);
            var usage = new Dictionary<string, int>();
            DateTime? sinceDate = null;
            
            if (!string.IsNullOrEmpty(since) && DateTime.TryParse(since, out var parsedDate))
            {
                sinceDate = parsedDate;
            }
            
            foreach (var line in lines.Skip(1)) // Skip header
            {
                var parts = line.Split(',');
                if (parts.Length >= 3)
                {
                    var timestamp = parts[0];
                    var method = parts[1];
                    var endpoint = parts[2];
                    
                    // Combine method and endpoint for better clarity
                    var fullEndpoint = $"{method} {endpoint}";
                    
                    // Filter by date if specified
                    if (sinceDate.HasValue && DateTime.TryParse(timestamp, out var logDate))
                    {
                        if (logDate < sinceDate.Value)
                            continue;
                    }
                    
                    usage[fullEndpoint] = usage.GetValueOrDefault(fullEndpoint, 0) + 1;
                }
            }
            
            var topEndpoints = usage
                .OrderByDescending(kv => kv.Value)
                .Take(top)
                .Select(kv => new { endpoint = kv.Key, count = kv.Value })
                .ToArray();
            
            return Ok(new {
                totalEndpoints = usage.Count,
                totalRequests = usage.Values.Sum(),
                since = since,
                top = top,
                endpoints = topEndpoints
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting endpoint usage");
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Clears the endpoint usage log file.
    /// </summary>
    [HttpDelete("debug/endpoint-usage")]
    public IActionResult ClearEndpointUsage()
    {
        try
        {
            var logFile = "/app/cache/endpoint-usage/endpoints.csv";
            
            if (System.IO.File.Exists(logFile))
            {
                System.IO.File.Delete(logFile);
                _logger.LogDebug("Cleared endpoint usage log via admin endpoint");
                
                return Ok(new { 
                    message = "Endpoint usage log cleared successfully",
                    timestamp = DateTime.UtcNow
                });
            }
            else
            {
                return Ok(new { 
                    message = "No endpoint usage log file found",
                    timestamp = DateTime.UtcNow
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error clearing endpoint usage log");
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    #endregion
    
    #region Private Helper Methods
    
    /// <summary>
    /// Saves a manual mapping to file for persistence across restarts.
    /// Manual mappings NEVER expire - they are permanent user decisions.
    /// </summary>
    private async Task SaveManualMappingToFileAsync(
        string playlistName, 
        string spotifyId, 
        string? jellyfinId, 
        string? externalProvider, 
        string? externalId)
    {
        try
        {
            var mappingsDir = "/app/cache/mappings";
            Directory.CreateDirectory(mappingsDir);
            
            var safeName = string.Join("_", playlistName.Split(Path.GetInvalidFileNameChars()));
            var filePath = Path.Combine(mappingsDir, $"{safeName}_mappings.json");
            
            // Load existing mappings
            var mappings = new Dictionary<string, ManualMappingEntry>();
            if (System.IO.File.Exists(filePath))
            {
                var json = await System.IO.File.ReadAllTextAsync(filePath);
                mappings = JsonSerializer.Deserialize<Dictionary<string, ManualMappingEntry>>(json) 
                    ?? new Dictionary<string, ManualMappingEntry>();
            }
            
            // Add or update mapping
            mappings[spotifyId] = new ManualMappingEntry
            {
                SpotifyId = spotifyId,
                JellyfinId = jellyfinId,
                ExternalProvider = externalProvider,
                ExternalId = externalId,
                CreatedAt = DateTime.UtcNow
            };
            
            // Save back to file
            var updatedJson = JsonSerializer.Serialize(mappings, new JsonSerializerOptions { WriteIndented = true });
            await System.IO.File.WriteAllTextAsync(filePath, updatedJson);
            
            _logger.LogDebug("💾 Saved manual mapping to file: {Playlist} - {SpotifyId}", playlistName, spotifyId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save manual mapping to file for {Playlist}", playlistName);
        }
    }
    
    /// <summary>
    /// Save lyrics mapping to file for persistence across restarts.
    /// Lyrics mappings NEVER expire - they are permanent user decisions.
    /// </summary>
    private async Task SaveLyricsMappingToFileAsync(
        string artist,
        string title,
        string album,
        int durationSeconds,
        int lyricsId)
    {
        try
        {
            var mappingsFile = "/app/cache/lyrics_mappings.json";
            
            // Load existing mappings
            var mappings = new List<LyricsMappingEntry>();
            if (System.IO.File.Exists(mappingsFile))
            {
                var json = await System.IO.File.ReadAllTextAsync(mappingsFile);
                mappings = JsonSerializer.Deserialize<List<LyricsMappingEntry>>(json) 
                    ?? new List<LyricsMappingEntry>();
            }
            
            // Remove any existing mapping for this track
            mappings.RemoveAll(m => 
                m.Artist.Equals(artist, StringComparison.OrdinalIgnoreCase) && 
                m.Title.Equals(title, StringComparison.OrdinalIgnoreCase));
            
            // Add new mapping
            mappings.Add(new LyricsMappingEntry
            {
                Artist = artist,
                Title = title,
                Album = album,
                DurationSeconds = durationSeconds,
                LyricsId = lyricsId,
                CreatedAt = DateTime.UtcNow
            });
            
            // Save back to file
            var updatedJson = JsonSerializer.Serialize(mappings, new JsonSerializerOptions { WriteIndented = true });
            await System.IO.File.WriteAllTextAsync(mappingsFile, updatedJson);
            
            _logger.LogDebug("💾 Saved lyrics mapping to file: {Artist} - {Title} → Lyrics ID {LyricsId}", 
                artist, title, lyricsId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save lyrics mapping to file for {Artist} - {Title}", artist, title);
        }
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
            await SaveLyricsMappingToFileAsync(request.Artist, request.Title, request.Album ?? "", request.DurationSeconds, request.LyricsId);
            
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
    /// Get all manual track mappings (both Jellyfin and external) for all playlists
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
            var safeName = string.Join("_", playlist.Split(Path.GetInvalidFileNameChars()));
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
    
    #endregion
    
    #region Helper Methods
    
    /// <summary>
    /// Invalidates the cached playlist summary so it will be regenerated on next request
    /// </summary>
    private void InvalidatePlaylistSummaryCache()
    {
        try
        {
            var cacheFile = "/app/cache/admin_playlists_summary.json";
            if (System.IO.File.Exists(cacheFile))
            {
                System.IO.File.Delete(cacheFile);
                _logger.LogDebug("🗑️ Invalidated playlist summary cache");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to invalidate playlist summary cache");
        }
    }
    
    #endregion

public class ManualMappingRequest
{
    public string SpotifyId { get; set; } = "";
    public string? JellyfinId { get; set; }
    public string? ExternalProvider { get; set; }
    public string? ExternalId { get; set; }
}

public class LyricsMappingRequest
{
    public string Artist { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Album { get; set; }
    public int DurationSeconds { get; set; }
    public int LyricsId { get; set; }
}

public class ManualMappingEntry
{
    public string SpotifyId { get; set; } = "";
    public string? JellyfinId { get; set; }
    public string? ExternalProvider { get; set; }
    public string? ExternalId { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class LyricsMappingEntry
{
    public string Artist { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Album { get; set; }
    public int DurationSeconds { get; set; }
    public int LyricsId { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class ConfigUpdateRequest
{
    public Dictionary<string, string> Updates { get; set; } = new();
}

public class AddPlaylistRequest
{
    public string Name { get; set; } = string.Empty;
    public string SpotifyId { get; set; } = string.Empty;
    public string LocalTracksPosition { get; set; } = "first";
}

public class LinkPlaylistRequest
{
    public string Name { get; set; } = string.Empty;
    public string SpotifyPlaylistId { get; set; } = string.Empty;
    public string SyncSchedule { get; set; } = "0 8 * * 1"; // Default: 8 AM every Monday
}

public class UpdateScheduleRequest
{
    public string SyncSchedule { get; set; } = string.Empty;
}

    /// <summary>
    /// GET /api/admin/downloads
    /// Lists all downloaded files in the KEPT folder only (favorited tracks)
    /// </summary>
    [HttpGet("downloads")]
    public IActionResult GetDownloads()
    {
        try
        {
            var keptPath = Path.Combine(_configuration["Library:DownloadPath"] ?? "./downloads", "kept");
            
            _logger.LogDebug("📂 Checking kept folder: {Path}", keptPath);
            _logger.LogInformation("📂 Directory exists: {Exists}", Directory.Exists(keptPath));
            
            if (!Directory.Exists(keptPath))
            {
                _logger.LogWarning("Kept folder does not exist: {Path}", keptPath);
                return Ok(new { files = new List<object>(), totalSize = 0, count = 0 });
            }
            
            var files = new List<object>();
            long totalSize = 0;
            
            // Recursively get all audio files from kept folder
            var audioExtensions = new[] { ".flac", ".mp3", ".m4a", ".opus" };
            
            var allFiles = Directory.GetFiles(keptPath, "*.*", SearchOption.AllDirectories)
                .Where(f => audioExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .ToList();
            
            _logger.LogDebug("📂 Found {Count} audio files in kept folder", allFiles.Count);
            
            foreach (var filePath in allFiles)
            {
                _logger.LogDebug("📂 Processing file: {Path}", filePath);
                
                var fileInfo = new FileInfo(filePath);
                var relativePath = Path.GetRelativePath(keptPath, filePath);
                
                // Parse artist/album/track from path structure
                var parts = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var artist = parts.Length > 0 ? parts[0] : "";
                var album = parts.Length > 1 ? parts[1] : "";
                var fileName = parts.Length > 2 ? parts[^1] : Path.GetFileName(filePath);
                
                files.Add(new
                {
                    path = relativePath,
                    fullPath = filePath,
                    artist,
                    album,
                    fileName,
                    size = fileInfo.Length,
                    sizeFormatted = FormatFileSize(fileInfo.Length),
                    lastModified = fileInfo.LastWriteTimeUtc,
                    extension = fileInfo.Extension
                });
                
                totalSize += fileInfo.Length;
            }
            
            _logger.LogDebug("📂 Returning {Count} kept files, total size: {Size}", files.Count, FormatFileSize(totalSize));
            
            return Ok(new
            {
                files = files.OrderBy(f => ((dynamic)f).artist).ThenBy(f => ((dynamic)f).album).ThenBy(f => ((dynamic)f).fileName),
                totalSize,
                totalSizeFormatted = FormatFileSize(totalSize),
                count = files.Count
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list kept downloads");
            return StatusCode(500, new { error = "Failed to list kept downloads" });
        }
    }
    
    /// <summary>
    /// DELETE /api/admin/downloads
    /// Deletes a specific kept file and cleans up empty folders
    /// </summary>
    [HttpDelete("downloads")]
    public IActionResult DeleteDownload([FromQuery] string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path))
            {
                return BadRequest(new { error = "Path is required" });
            }
            
            var keptPath = Path.Combine(_configuration["Library:DownloadPath"] ?? "./downloads", "kept");
            var fullPath = Path.Combine(keptPath, path);
            
            _logger.LogDebug("🗑️ Delete request for: {Path}", fullPath);
            
            // Security: Ensure the path is within the kept directory
            var normalizedFullPath = Path.GetFullPath(fullPath);
            var normalizedKeptPath = Path.GetFullPath(keptPath);
            
            if (!normalizedFullPath.StartsWith(normalizedKeptPath))
            {
                _logger.LogWarning("🗑️ Invalid path (outside kept folder): {Path}", normalizedFullPath);
                return BadRequest(new { error = "Invalid path" });
            }
            
            if (!System.IO.File.Exists(fullPath))
            {
                _logger.LogWarning("🗑️ File not found: {Path}", fullPath);
                return NotFound(new { error = "File not found" });
            }
            
            System.IO.File.Delete(fullPath);
            _logger.LogDebug("🗑️ Deleted file: {Path}", fullPath);
            
            // Clean up empty directories (Album folder, then Artist folder if empty)
            var directory = Path.GetDirectoryName(fullPath);
            while (directory != null && directory != keptPath && directory.StartsWith(keptPath))
            {
                if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                    _logger.LogInformation("🗑️ Deleted empty directory: {Dir}", directory);
                    directory = Path.GetDirectoryName(directory);
                }
                else
                {
                    _logger.LogInformation("🗑️ Directory not empty or doesn't exist, stopping cleanup: {Dir}", directory);
                    break;
                }
            }
            
            return Ok(new { success = true, message = "File deleted successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete file: {Path}", path);
            return StatusCode(500, new { error = "Failed to delete file" });
        }
    }
    
    /// <summary>
    /// GET /api/admin/downloads/file
    /// Downloads a specific file from the kept folder
    /// </summary>
    [HttpGet("downloads/file")]
    public IActionResult DownloadFile([FromQuery] string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path))
            {
                return BadRequest(new { error = "Path is required" });
            }
            
            var keptPath = Path.Combine(_configuration["Library:DownloadPath"] ?? "./downloads", "kept");
            var fullPath = Path.Combine(keptPath, path);
            
            // Security: Ensure the path is within the kept directory
            var normalizedFullPath = Path.GetFullPath(fullPath);
            var normalizedKeptPath = Path.GetFullPath(keptPath);
            
            if (!normalizedFullPath.StartsWith(normalizedKeptPath))
            {
                return BadRequest(new { error = "Invalid path" });
            }
            
            if (!System.IO.File.Exists(fullPath))
            {
                return NotFound(new { error = "File not found" });
            }
            
            var fileName = Path.GetFileName(fullPath);
            var fileStream = System.IO.File.OpenRead(fullPath);
            
            return File(fileStream, "application/octet-stream", fileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download file: {Path}", path);
            return StatusCode(500, new { error = "Failed to download file" });
        }
    }
    
    private static string FormatFileSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len = len / 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }
}

/// <summary>
/// Request model for updating configuration
/// </summary>
public class ConfigUpdateRequest
{
    public Dictionary<string, string> Updates { get; set; } = new();
}
