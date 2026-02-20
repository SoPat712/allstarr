using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using allstarr.Models.Settings;
using allstarr.Filters;
using allstarr.Services.Jellyfin;
using allstarr.Services.Common;
using System.Runtime;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin")]
[ServiceFilter(typeof(AdminPortFilter))]
public class DiagnosticsController : ControllerBase
{
    private readonly ILogger<DiagnosticsController> _logger;
    private readonly IConfiguration _configuration;
    private readonly SpotifyApiSettings _spotifyApiSettings;
    private readonly SpotifyImportSettings _spotifyImportSettings;
    private readonly JellyfinSettings _jellyfinSettings;
    private readonly DeezerSettings _deezerSettings;
    private readonly QobuzSettings _qobuzSettings;
    private readonly SquidWTFSettings _squidWtfSettings;
    private readonly RedisCacheService _cache;
    private readonly List<string> _squidWtfApiUrls;
    private static int _urlIndex = 0;
    private static readonly object _urlIndexLock = new();

    public DiagnosticsController(
        ILogger<DiagnosticsController> logger,
        IConfiguration configuration,
        IOptions<SpotifyApiSettings> spotifyApiSettings,
        IOptions<SpotifyImportSettings> spotifyImportSettings,
        IOptions<JellyfinSettings> jellyfinSettings,
        IOptions<DeezerSettings> deezerSettings,
        IOptions<QobuzSettings> qobuzSettings,
        IOptions<SquidWTFSettings> squidWtfSettings,
        RedisCacheService cache)
    {
        _logger = logger;
        _configuration = configuration;
        _spotifyApiSettings = spotifyApiSettings.Value;
        _spotifyImportSettings = spotifyImportSettings.Value;
        _jellyfinSettings = jellyfinSettings.Value;
        _deezerSettings = deezerSettings.Value;
        _qobuzSettings = qobuzSettings.Value;
        _squidWtfSettings = squidWtfSettings.Value;
        _cache = cache;
        _squidWtfApiUrls = DecodeSquidWtfUrls();
    }

    private static List<string> DecodeSquidWtfUrls()
    {
        var encodedUrls = new[]
        {
            "aHR0cHM6Ly90cml0b24uc3F1aWQud3Rm",
            "aHR0cHM6Ly90aWRhbC5raW5vcGx1cy5vbmxpbmU=",
            "aHR0cHM6Ly9oaWZpLXR3by5zcG90aXNhdmVyLm5ldA==",
            "aHR0cHM6Ly9oaWZpLW9uZS5zcG90aXNhdmVyLm5ldA==",
            "aHR0cHM6Ly93b2xmLnFxZGwuc2l0ZQ==",
            "aHR0cDovL2h1bmQucXFkbC5zaXRl",
            "aHR0cHM6Ly9rYXR6ZS5xcWRsLnNpdGU=",
            "aHR0cHM6Ly92b2dlbC5xcWRsLnNpdGU=",
            "aHR0cHM6Ly9tYXVzLnFxZGwuc2l0ZQ==",
            "aHR0cHM6Ly9ldS1jZW50cmFsLm1vbm9jaHJvbWUudGY=",
            "aHR0cHM6Ly91cy13ZXN0Lm1vbm9jaHJvbWUudGY=",
            "aHR0cHM6Ly9hcnJhbi5tb25vY2hyb21lLnRm",
            "aHR0cHM6Ly9hcGkubW9ub2Nocm9tZS50Zg==",
            "aHR0cHM6Ly9odW5kLnFxZGwuc2l0ZQ=="
        };
        return encodedUrls.Select(encoded => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded))).ToList();
    }

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
            version = AppVersion.Version,
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

    
    
    /// <summary>
    /// Saves a manual mapping to file for persistence across restarts.
    /// Manual mappings NEVER expire - they are permanent user decisions.
    /// </summary>
}
