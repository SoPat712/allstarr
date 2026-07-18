using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using allstarr.Models.Settings;
using allstarr.Filters;
using allstarr.Models.Admin;
using allstarr.Services.Jellyfin;
using allstarr.Services.Common;
using allstarr.Services.Admin;
using allstarr.Services.Spotify;
using allstarr.Services.Scrobbling;
using allstarr.Services.SquidWTF;
using System.Runtime;
using allstarr.Core.Storage;

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
    private readonly SpotifySessionCookieService _spotifySessionCookieService;
    private readonly List<string> _squidWtfApiUrls;
    private readonly DurableStorageState _storageState;
    private readonly ISafeJsonProxyClient _safeJsonProxyClient;
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
        SpotifySessionCookieService spotifySessionCookieService,
        SquidWtfEndpointCatalog squidWtfEndpointCatalog,
        RedisCacheService cache,
        DurableStorageState storageState,
        ISafeJsonProxyClient safeJsonProxyClient)
    {
        _logger = logger;
        _configuration = configuration;
        _spotifyApiSettings = spotifyApiSettings.Value;
        _spotifyImportSettings = spotifyImportSettings.Value;
        _jellyfinSettings = jellyfinSettings.Value;
        _deezerSettings = deezerSettings.Value;
        _qobuzSettings = qobuzSettings.Value;
        _squidWtfSettings = squidWtfSettings.Value;
        _spotifySessionCookieService = spotifySessionCookieService;
        _cache = cache;
        _squidWtfApiUrls = squidWtfEndpointCatalog.ApiUrls;
        _storageState = storageState;
        _safeJsonProxyClient = safeJsonProxyClient;
    }

    [HttpGet("status")]
    public async Task<IActionResult> GetStatus()
    {
        // Determine Spotify auth status based on configuration only
        // DO NOT call Spotify API here - this endpoint is polled frequently
        var spotifyAuthStatus = "not_configured";
        string? spotifyUser = null;
        var sessionUserId = GetAuthenticatedUserId();
        var cookieStatus = await _spotifySessionCookieService.GetCookieStatusAsync(sessionUserId);
        var userCookieSetDate = !string.IsNullOrWhiteSpace(sessionUserId)
            ? await _spotifySessionCookieService.GetCookieSetDateAsync(sessionUserId)
            : null;
        var effectiveCookieSetDate = userCookieSetDate?.ToString("o");

        if (string.IsNullOrWhiteSpace(effectiveCookieSetDate) && cookieStatus.UsingGlobalFallback)
        {
            effectiveCookieSetDate = _spotifyApiSettings.SessionCookieSetDate;
        }

        if (_spotifyApiSettings.Enabled && cookieStatus.HasCookie)
        {
            // If cookie is set, assume it's working until proven otherwise
            // Actual validation happens when playlists are fetched
            spotifyAuthStatus = "configured";
            spotifyUser = cookieStatus.UsingGlobalFallback ? "(global fallback cookie set)" : "(user cookie set)";
        }
        else if (_spotifyApiSettings.Enabled)
        {
            spotifyAuthStatus = "missing_cookie";
        }

        var storage = _storageState.GetSnapshot();
        return Ok(new
        {
            version = AppVersion.Version,
            backendType = _configuration.GetValue<string>("Backend:Type") ?? "Jellyfin",
            durableStorage = new
            {
                provider = storage.Provider.ToString(),
                readiness = storage.Readiness.ToString(),
                storage.SchemaVersion,
                storage.ErrorCode,
                storage.CheckedAt
            },
            jellyfinUrl = string.IsNullOrWhiteSpace(_jellyfinSettings.Url)
                ? "Not configured"
                : "Configured",
            spotify = new
            {
                apiEnabled = _spotifyApiSettings.Enabled,
                authStatus = spotifyAuthStatus,
                user = spotifyUser,
                hasCookie = cookieStatus.HasCookie,
                usingGlobalFallback = cookieStatus.UsingGlobalFallback,
                cookieSetDate = effectiveCookieSetDate,
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

    private string? GetAuthenticatedUserId()
    {
        if (HttpContext.Items.TryGetValue(AdminAuthSessionService.HttpContextSessionItemKey, out var sessionObj) &&
            sessionObj is AdminAuthSession session &&
            !string.IsNullOrWhiteSpace(session.UserId))
        {
            return session.UserId;
        }

        return null;
    }

    [HttpGet("media-probe")]
    public async Task<IActionResult> ProbeMediaPipeline(CancellationToken cancellationToken = default)
    {
        var backendType = _configuration.GetValue<string>("Backend:Type") ?? "Jellyfin";
        if (!backendType.Equals("Jellyfin", StringComparison.OrdinalIgnoreCase))
        {
            return Ok(new
            {
                success = false,
                backend = backendType,
                code = "probe_not_supported",
                message = "The media probe currently supports Jellyfin backends."
            });
        }

        var proxy = HttpContext.RequestServices.GetService<JellyfinProxyService>();
        if (proxy == null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                success = false,
                backend = "Jellyfin",
                code = "proxy_unavailable",
                message = "The Jellyfin proxy service is unavailable."
            });
        }

        try
        {
            var itemsEndpoint = string.IsNullOrWhiteSpace(_jellyfinSettings.UserId)
                ? "Items"
                : $"Users/{Uri.EscapeDataString(_jellyfinSettings.UserId)}/Items";
            var (itemsDocument, statusCode) = await proxy.GetJsonAsyncInternal(
                itemsEndpoint,
                new Dictionary<string, string>
                {
                    ["Recursive"] = "true",
                    ["IncludeItemTypes"] = "Audio",
                    ["Limit"] = "25",
                    ["Fields"] = "PrimaryImageAspectRatio,ProviderIds"
                });
            using (itemsDocument)
            {
                if (statusCode < 200 || statusCode >= 300 || itemsDocument == null)
                {
                    return StatusCode(StatusCodes.Status502BadGateway, new
                    {
                        success = false,
                        backend = "Jellyfin",
                        code = "metadata_probe_failed",
                        metadataStatus = statusCode,
                        message = "Jellyfin did not return library metadata."
                    });
                }

                if (!itemsDocument.RootElement.TryGetProperty("Items", out var items) ||
                    items.ValueKind != System.Text.Json.JsonValueKind.Array)
                {
                    return StatusCode(StatusCodes.Status502BadGateway, new
                    {
                        success = false,
                        backend = "Jellyfin",
                        code = "metadata_shape_invalid",
                        metadataStatus = statusCode,
                        message = "Jellyfin returned an unexpected library response."
                    });
                }

                var candidate = items.EnumerateArray().FirstOrDefault(item =>
                    item.TryGetProperty("Id", out var id) &&
                    id.ValueKind == System.Text.Json.JsonValueKind.String &&
                    item.TryGetProperty("ImageTags", out var tags) &&
                    tags.ValueKind == System.Text.Json.JsonValueKind.Object &&
                    tags.TryGetProperty("Primary", out var primary) &&
                    primary.ValueKind == System.Text.Json.JsonValueKind.String);

                if (candidate.ValueKind == System.Text.Json.JsonValueKind.Undefined)
                {
                    return Ok(new
                    {
                        success = false,
                        backend = "Jellyfin",
                        code = "no_artwork_candidate",
                        metadataStatus = statusCode,
                        checkedItems = items.GetArrayLength(),
                        message = "No audio item with primary artwork was found in the probe sample."
                    });
                }

                var itemId = candidate.GetProperty("Id").GetString()!;
                var imageTag = candidate.GetProperty("ImageTags").GetProperty("Primary").GetString();
                var (imageBytes, contentType) = await proxy.GetBytesAsync(
                    $"Items/{Uri.EscapeDataString(itemId)}/Images/Primary",
                    new Dictionary<string, string>
                    {
                        ["maxWidth"] = "300",
                        ["maxHeight"] = "300",
                        ["tag"] = imageTag ?? string.Empty
                    });
                var validImage = imageBytes is { Length: > 0 } &&
                                 contentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true;

                return Ok(new
                {
                    success = validImage,
                    backend = "Jellyfin",
                    code = validImage ? "media_pipeline_healthy" : "artwork_probe_failed",
                    metadataStatus = statusCode,
                    checkedItems = items.GetArrayLength(),
                    artwork = new
                    {
                        available = validImage,
                        contentType = validImage ? contentType : null,
                        bytes = imageBytes?.Length ?? 0
                    },
                    message = validImage
                        ? "Jellyfin metadata and album artwork are available through Allstarr."
                        : "Jellyfin metadata worked, but Allstarr could not retrieve the selected artwork."
                });
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Jellyfin media pipeline probe failed");
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                success = false,
                backend = "Jellyfin",
                code = "media_probe_failed",
                message = "The Jellyfin media pipeline probe failed."
            });
        }
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

        return Ok(new { baseUrl = "/api/admin/squidwtf-browser-proxy" });
    }

    [HttpGet("squidwtf-browser-proxy/search")]
    public async Task<IActionResult> SearchSquidWtf(
        [FromQuery(Name = "s")] string search,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(search) || search.Length > 300)
        {
            return BadRequest(new { error = "A search value between 1 and 300 characters is required" });
        }

        if (_squidWtfApiUrls.Count == 0)
        {
            return NotFound(new { error = "No SquidWTF search endpoint is available" });
        }

        string baseUrl;
        lock (_urlIndexLock)
        {
            baseUrl = _squidWtfApiUrls[_urlIndex];
            _urlIndex = (_urlIndex + 1) % _squidWtfApiUrls.Count;
        }

        try
        {
            if (!OutboundRequestGuard.TryCreateSafeHttpUri(
                    baseUrl.TrimEnd('/') + "/",
                    out var safeBaseUri,
                    out _))
            {
                return StatusCode(
                    StatusCodes.Status502BadGateway,
                    new { error = "The provider search endpoint was unavailable" });
            }

            var endpoint = new Uri(
                safeBaseUri!,
                $"search/?s={Uri.EscapeDataString(search.Trim())}");
            const long maximumResponseBytes = 2 * 1024 * 1024;
            var result = await _safeJsonProxyClient.GetAsync(
                endpoint,
                maximumResponseBytes,
                cancellationToken);
            if (result.Outcome == SafeJsonProxyOutcome.Success && result.Payload.HasValue)
            {
                return new JsonResult(result.Payload.Value);
            }

            if (result.Outcome == SafeJsonProxyOutcome.ResponseTooLarge)
            {
                return StatusCode(
                    StatusCodes.Status502BadGateway,
                    new { error = "The provider search response exceeded the safe size limit" });
            }

            return StatusCode(
                StatusCodes.Status502BadGateway,
                new { error = "The provider search endpoint was unavailable" });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "SquidWTF browser search proxy failed ({ExceptionType})",
                ex.GetType().Name);
            return StatusCode(
                StatusCodes.Status502BadGateway,
                new { error = "The provider search endpoint was unavailable" });
        }
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

            return Ok(new
            {
                Timestamp = DateTime.UtcNow,
                BeforeGC = new
                {
                    GCMemoryBytes = memoryBeforeGC,
                    GCMemoryMB = Math.Round(memoryBeforeGC / (1024.0 * 1024.0), 2)
                },
                AfterGC = new
                {
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
                GCCollections = new
                {
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
            _logger.LogError(ex, "Failed to collect memory statistics");
            return BadRequest(new { error = "Failed to collect memory statistics" });
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

            return Ok(new
            {
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
            _logger.LogError(ex, "Failed to force garbage collection");
            return BadRequest(new { error = "Failed to force garbage collection" });
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
            _logger.LogError(ex, "Failed to get active sessions");
            return BadRequest(new { error = "Failed to get active sessions" });
        }
    }

    /// <summary>
    /// Gets current active scrobbling sessions for debugging.
    /// </summary>
    [HttpGet("scrobbling-sessions")]
    public IActionResult GetScrobblingSessions()
    {
        try
        {
            var scrobblingOrchestrator = HttpContext.RequestServices.GetService<ScrobblingOrchestrator>();
            if (scrobblingOrchestrator == null)
            {
                return BadRequest(new { error = "Scrobbling orchestrator not available" });
            }

            var sessionInfo = scrobblingOrchestrator.GetSessionsInfo();
            return Ok(sessionInfo);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get scrobbling sessions");
            return BadRequest(new { error = "Failed to get scrobbling sessions" });
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
            var logFile = EndpointUsagePathResolver.GetLogFile(_configuration);

            if (!System.IO.File.Exists(logFile))
            {
                return Ok(new
                {
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

            return Ok(new
            {
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
            var logFile = EndpointUsagePathResolver.GetLogFile(_configuration);

            if (System.IO.File.Exists(logFile))
            {
                System.IO.File.Delete(logFile);
                _logger.LogDebug("Cleared endpoint usage log via admin endpoint");

                return Ok(new
                {
                    message = "Endpoint usage log cleared successfully",
                    timestamp = DateTime.UtcNow
                });
            }
            else
            {
                return Ok(new
                {
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
