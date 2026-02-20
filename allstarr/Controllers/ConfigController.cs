using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using allstarr.Models.Settings;
using allstarr.Models.Admin;
using allstarr.Filters;
using allstarr.Services.Admin;
using allstarr.Services.Common;
using System.Text.Json;
using System.Net.Sockets;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin")]
[ServiceFilter(typeof(AdminPortFilter))]
public class ConfigController : ControllerBase
{
    private readonly ILogger<ConfigController> _logger;
    private readonly IConfiguration _configuration;
    private readonly SpotifyApiSettings _spotifyApiSettings;
    private readonly JellyfinSettings _jellyfinSettings;
    private readonly SubsonicSettings _subsonicSettings;
    private readonly DeezerSettings _deezerSettings;
    private readonly QobuzSettings _qobuzSettings;
    private readonly SquidWTFSettings _squidWtfSettings;
    private readonly MusicBrainzSettings _musicBrainzSettings;
    private readonly SpotifyImportSettings _spotifyImportSettings;
    private readonly ScrobblingSettings _scrobblingSettings;
    private readonly AdminHelperService _helperService;
    private readonly RedisCacheService _cache;
    private const string CacheDirectory = "/app/cache/spotify";

    public ConfigController(
        ILogger<ConfigController> logger,
        IConfiguration configuration,
        IOptions<SpotifyApiSettings> spotifyApiSettings,
        IOptions<JellyfinSettings> jellyfinSettings,
        IOptions<SubsonicSettings> subsonicSettings,
        IOptions<DeezerSettings> deezerSettings,
        IOptions<QobuzSettings> qobuzSettings,
        IOptions<SquidWTFSettings> squidWtfSettings,
        IOptions<MusicBrainzSettings> musicBrainzSettings,
        IOptions<SpotifyImportSettings> spotifyImportSettings,
        IOptions<ScrobblingSettings> scrobblingSettings,
        AdminHelperService helperService,
        RedisCacheService cache)
    {
        _logger = logger;
        _configuration = configuration;
        _spotifyApiSettings = spotifyApiSettings.Value;
        _jellyfinSettings = jellyfinSettings.Value;
        _subsonicSettings = subsonicSettings.Value;
        _deezerSettings = deezerSettings.Value;
        _qobuzSettings = qobuzSettings.Value;
        _squidWtfSettings = squidWtfSettings.Value;
        _musicBrainzSettings = musicBrainzSettings.Value;
        _spotifyImportSettings = spotifyImportSettings.Value;
        _scrobblingSettings = scrobblingSettings.Value;
        _helperService = helperService;
        _cache = cache;
    }

    [HttpGet("config")]
    public async Task<IActionResult> GetConfig()
    {
        return Ok(new
        {
            backendType = _configuration.GetValue<string>("Backend:Type") ?? "Jellyfin",
            musicService = _configuration.GetValue<string>("MusicService") ?? "SquidWTF",
            explicitFilter = _configuration.GetValue<string>("ExplicitFilter") ?? "All",
            enableExternalPlaylists = _configuration.GetValue<bool>("EnableExternalPlaylists", false),
            playlistsDirectory = _configuration.GetValue<string>("PlaylistsDirectory") ?? "(not set)",
            redisEnabled = _configuration.GetValue<bool>("Redis:Enabled", false),
            debug = new
            {
                logAllRequests = _configuration.GetValue<bool>("Debug:LogAllRequests", false)
            },
            spotifyApi = new
            {
                enabled = _spotifyApiSettings.Enabled,
                sessionCookie = AdminHelperService.MaskValue(_spotifyApiSettings.SessionCookie, showLast: 8),
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
                apiKey = AdminHelperService.MaskValue(_jellyfinSettings.ApiKey),
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
                arl = AdminHelperService.MaskValue(_deezerSettings.Arl, showLast: 8),
                arlFallback = AdminHelperService.MaskValue(_deezerSettings.ArlFallback, showLast: 8),
                quality = _deezerSettings.Quality ?? "FLAC"
            },
            qobuz = new
            {
                userAuthToken = AdminHelperService.MaskValue(_qobuzSettings.UserAuthToken, showLast: 8),
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
                password = AdminHelperService.MaskValue(_musicBrainzSettings.Password),
                baseUrl = _musicBrainzSettings.BaseUrl,
                rateLimitMs = _musicBrainzSettings.RateLimitMs
            },
            scrobbling = await GetScrobblingSettingsFromEnvAsync()
        });
    }
    
    /// <summary>
    /// Read scrobbling settings directly from .env file for real-time updates
    /// </summary>
    private async Task<object> GetScrobblingSettingsFromEnvAsync()
    {
        try
        {
            var envPath = _helperService.GetEnvFilePath();
            if (!System.IO.File.Exists(envPath))
            {
                // Fallback to IOptions if .env doesn't exist
                return new
                {
                    enabled = _scrobblingSettings.Enabled,
                    lastFm = new
                    {
                        enabled = _scrobblingSettings.LastFm.Enabled,
                        apiKey = AdminHelperService.MaskValue(_scrobblingSettings.LastFm.ApiKey, showLast: 8),
                        sharedSecret = AdminHelperService.MaskValue(_scrobblingSettings.LastFm.SharedSecret, showLast: 8),
                        sessionKey = AdminHelperService.MaskValue(_scrobblingSettings.LastFm.SessionKey, showLast: 8),
                        username = _scrobblingSettings.LastFm.Username ?? "(not set)",
                        password = AdminHelperService.MaskValue(_scrobblingSettings.LastFm.Password, showLast: 0)
                    },
                    listenBrainz = new
                    {
                        enabled = _scrobblingSettings.ListenBrainz.Enabled,
                        userToken = AdminHelperService.MaskValue(_scrobblingSettings.ListenBrainz.UserToken, showLast: 8)
                    }
                };
            }
            
            var lines = await System.IO.File.ReadAllLinesAsync(envPath);
            var envVars = new Dictionary<string, string>();
            
            foreach (var line in lines)
            {
                if (AdminHelperService.ShouldSkipEnvLine(line))
                    continue;
                    
                var (key, value) = AdminHelperService.ParseEnvLine(line);
                if (!string.IsNullOrEmpty(key))
                {
                    envVars[key] = value;
                }
            }
            
            return new
            {
                enabled = envVars.TryGetValue("SCROBBLING_ENABLED", out var scrobblingEnabled) 
                    ? scrobblingEnabled.Equals("true", StringComparison.OrdinalIgnoreCase) 
                    : _scrobblingSettings.Enabled,
                lastFm = new
                {
                    enabled = envVars.TryGetValue("SCROBBLING_LASTFM_ENABLED", out var lastFmEnabled)
                        ? lastFmEnabled.Equals("true", StringComparison.OrdinalIgnoreCase)
                        : _scrobblingSettings.LastFm.Enabled,
                    apiKey = envVars.TryGetValue("SCROBBLING_LASTFM_API_KEY", out var apiKey)
                        ? AdminHelperService.MaskValue(apiKey, showLast: 8)
                        : AdminHelperService.MaskValue(_scrobblingSettings.LastFm.ApiKey, showLast: 8),
                    sharedSecret = envVars.TryGetValue("SCROBBLING_LASTFM_SHARED_SECRET", out var sharedSecret)
                        ? AdminHelperService.MaskValue(sharedSecret, showLast: 8)
                        : AdminHelperService.MaskValue(_scrobblingSettings.LastFm.SharedSecret, showLast: 8),
                    sessionKey = envVars.TryGetValue("SCROBBLING_LASTFM_SESSION_KEY", out var sessionKey)
                        ? AdminHelperService.MaskValue(sessionKey, showLast: 8)
                        : AdminHelperService.MaskValue(_scrobblingSettings.LastFm.SessionKey, showLast: 8),
                    username = envVars.TryGetValue("SCROBBLING_LASTFM_USERNAME", out var username)
                        ? (string.IsNullOrEmpty(username) ? "(not set)" : username)
                        : (_scrobblingSettings.LastFm.Username ?? "(not set)"),
                    password = envVars.TryGetValue("SCROBBLING_LASTFM_PASSWORD", out var password)
                        ? AdminHelperService.MaskValue(password, showLast: 0)
                        : AdminHelperService.MaskValue(_scrobblingSettings.LastFm.Password, showLast: 0)
                },
                listenBrainz = new
                {
                    enabled = envVars.TryGetValue("SCROBBLING_LISTENBRAINZ_ENABLED", out var lbEnabled)
                        ? lbEnabled.Equals("true", StringComparison.OrdinalIgnoreCase)
                        : _scrobblingSettings.ListenBrainz.Enabled,
                    userToken = envVars.TryGetValue("SCROBBLING_LISTENBRAINZ_USER_TOKEN", out var userToken)
                        ? AdminHelperService.MaskValue(userToken, showLast: 8)
                        : AdminHelperService.MaskValue(_scrobblingSettings.ListenBrainz.UserToken, showLast: 8)
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read scrobbling settings from .env, falling back to IOptions");
            // Fallback to IOptions
            return new
            {
                enabled = _scrobblingSettings.Enabled,
                lastFm = new
                {
                    enabled = _scrobblingSettings.LastFm.Enabled,
                    apiKey = AdminHelperService.MaskValue(_scrobblingSettings.LastFm.ApiKey, showLast: 8),
                    sharedSecret = AdminHelperService.MaskValue(_scrobblingSettings.LastFm.SharedSecret, showLast: 8),
                    sessionKey = AdminHelperService.MaskValue(_scrobblingSettings.LastFm.SessionKey, showLast: 8),
                    username = _scrobblingSettings.LastFm.Username ?? "(not set)",
                    password = AdminHelperService.MaskValue(_scrobblingSettings.LastFm.Password, showLast: 0)
                },
                listenBrainz = new
                {
                    enabled = _scrobblingSettings.ListenBrainz.Enabled,
                    userToken = AdminHelperService.MaskValue(_scrobblingSettings.ListenBrainz.UserToken, showLast: 8)
                }
            };
        }
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
            if (!System.IO.File.Exists(_helperService.GetEnvFilePath()))
            {
                _logger.LogWarning(".env file not found at {Path}, creating new file", _helperService.GetEnvFilePath());
            }
            
            // Read current .env file or create new one
            var envContent = new Dictionary<string, string>();
            
            if (System.IO.File.Exists(_helperService.GetEnvFilePath()))
            {
                var lines = await System.IO.File.ReadAllLinesAsync(_helperService.GetEnvFilePath());
                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
                        continue;
                    
                    var eqIndex = line.IndexOf('=');
                    if (eqIndex > 0)
                    {
                        var key = line[..eqIndex].Trim();
                        var value = line[(eqIndex + 1)..].Trim();
                        
                        // Remove surrounding quotes if present (for proper re-quoting)
                        if (value.StartsWith("\"") && value.EndsWith("\"") && value.Length >= 2)
                        {
                            value = value[1..^1];
                        }
                        
                        envContent[key] = value;
                    }
                }
                _logger.LogDebug("Loaded {Count} existing env vars from {Path}", envContent.Count, _helperService.GetEnvFilePath());
            }
            
            // Apply updates with validation
            var appliedUpdates = new List<string>();
            foreach (var (key, value) in request.Updates)
            {
                // Validate key format
                if (!AdminHelperService.IsValidEnvKey(key))
                {
                    _logger.LogWarning("Invalid env key rejected: {Key}", key);
                    return BadRequest(new { error = $"Invalid environment variable key: {key}" });
                }
                
                // IMPORTANT: Docker Compose does NOT need quotes in .env files
                // It handles special characters correctly without them
                // When quotes are used, they become part of the value itself
                envContent[key] = value;
                appliedUpdates.Add(key);
                _logger.LogInformation("  Setting {Key} = {Value}", key, 
                    key.Contains("COOKIE") || key.Contains("TOKEN") || key.Contains("KEY") || key.Contains("ARL") || key.Contains("PASSWORD")
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
            
            // Write back to .env file (no quoting needed - Docker Compose handles special chars)
            var newContent = string.Join("\n", envContent.Select(kv => $"{kv.Key}={kv.Value}"));
            await System.IO.File.WriteAllTextAsync(_helperService.GetEnvFilePath(), newContent + "\n");
            
            _logger.LogDebug("Config file updated successfully at {Path}", _helperService.GetEnvFilePath());
            
            // Invalidate playlist summary cache if playlists were updated
            if (appliedUpdates.Contains("SPOTIFY_IMPORT_PLAYLISTS"))
            {
                _helperService.InvalidatePlaylistSummaryCache();
            }
            
            return Ok(new
            {
                message = "Configuration updated. Restart container to apply changes.",
                updatedKeys = appliedUpdates,
                requiresRestart = true,
                envFilePath = _helperService.GetEnvFilePath()
            });
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "Permission denied writing to .env file at {Path}", _helperService.GetEnvFilePath());
            return StatusCode(500, new { 
                error = "Permission denied", 
                details = "Cannot write to .env file. Check file permissions and volume mount.",
                path = _helperService.GetEnvFilePath()
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update configuration at {Path}", _helperService.GetEnvFilePath());
            return StatusCode(500, new { 
                error = "Failed to update configuration", 
                details = ex.Message,
                path = _helperService.GetEnvFilePath()
            });
        }
    }
    
    /// <summary>
    /// Add a new playlist to the configuration
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
                CacheKeyBuilder.BuildSpotifyPlaylistKey(playlist.Name),
                CacheKeyBuilder.BuildSpotifyMissingTracksKey(playlist.Name),
                $"spotify:matched:{playlist.Name}", // Legacy key
                CacheKeyBuilder.BuildSpotifyMatchedTracksKey(playlist.Name),
                CacheKeyBuilder.BuildSpotifyPlaylistItemsKey(playlist.Name)
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
    [HttpGet("export-env")]
    public IActionResult ExportEnv()
    {
        try
        {
            if (!System.IO.File.Exists(_helperService.GetEnvFilePath()))
            {
                return NotFound(new { error = ".env file not found" });
            }

            var envContent = System.IO.File.ReadAllText(_helperService.GetEnvFilePath());
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
            if (System.IO.File.Exists(_helperService.GetEnvFilePath()))
            {
                var backupPath = $"{_helperService.GetEnvFilePath()}.backup.{DateTime.UtcNow:yyyyMMddHHmmss}";
                System.IO.File.Copy(_helperService.GetEnvFilePath(), backupPath, true);
                _logger.LogDebug("Backed up existing .env to {BackupPath}", backupPath);
            }

            // Write new .env file
            await System.IO.File.WriteAllTextAsync(_helperService.GetEnvFilePath(), content);
            
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
}
