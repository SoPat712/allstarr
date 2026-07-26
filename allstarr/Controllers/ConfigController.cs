using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using allstarr.Models.Settings;
using allstarr.Models.Admin;
using allstarr.Filters;
using allstarr.Services.Admin;
using allstarr.Services.Common;
using allstarr.Services.Spotify;
using allstarr.Core.Secrets;
using allstarr.Core.Storage;
using allstarr.Core.Configuration;
using allstarr.Core.Settings;
using allstarr.Middleware;
using System.Text.Json;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;

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
    private readonly AppleDownloadSettings _appleMusicSettings;
    private readonly MusicBrainzSettings _musicBrainzSettings;
    private readonly SpotifyImportSettings _spotifyImportSettings;
    private readonly ScrobblingSettings _scrobblingSettings;
    private readonly string _envFilePath;
    private readonly SpotifySessionCookieService _spotifySessionCookieService;
    private readonly IApplicationCache _cache;

    public ConfigController(
        ILogger<ConfigController> logger,
        IConfiguration configuration,
        IOptions<SpotifyApiSettings> spotifyApiSettings,
        IOptions<JellyfinSettings> jellyfinSettings,
        IOptions<SubsonicSettings> subsonicSettings,
        IOptions<DeezerSettings> deezerSettings,
        IOptions<QobuzSettings> qobuzSettings,
        IOptions<SquidWTFSettings> squidWtfSettings,
        IOptions<AppleDownloadSettings> appleMusicSettings,
        IOptions<MusicBrainzSettings> musicBrainzSettings,
        IOptions<SpotifyImportSettings> spotifyImportSettings,
        IOptions<ScrobblingSettings> scrobblingSettings,
        IWebHostEnvironment environment,
        SpotifySessionCookieService spotifySessionCookieService,
        IApplicationCache cache)
    {
        _logger = logger;
        _configuration = configuration;
        _spotifyApiSettings = spotifyApiSettings.Value;
        _jellyfinSettings = jellyfinSettings.Value;
        _subsonicSettings = subsonicSettings.Value;
        _deezerSettings = deezerSettings.Value;
        _qobuzSettings = qobuzSettings.Value;
        _squidWtfSettings = squidWtfSettings.Value;
        _appleMusicSettings = appleMusicSettings.Value;
        _musicBrainzSettings = musicBrainzSettings.Value;
        _spotifyImportSettings = spotifyImportSettings.Value;
        _scrobblingSettings = scrobblingSettings.Value;
        _envFilePath = RuntimeEnvConfiguration.ResolveEnvFilePath(environment);
        _spotifySessionCookieService = spotifySessionCookieService;
        _cache = cache;
    }

    [HttpGet("config")]
    public async Task<IActionResult> GetConfig()
    {
        IReadOnlyDictionary<string, EffectiveRuntimeSetting> runtimeSettings =
            new Dictionary<string, EffectiveRuntimeSetting>(StringComparer.OrdinalIgnoreCase);
        if (GetAdminSession()?.TenantId is { } tenantId &&
            HttpContext.RequestServices.GetService<IDurableRuntimeSettings>() is { } settings)
        {
            runtimeSettings = await settings.GetManyAsync(tenantId, RuntimeSettingCatalog.Definitions.Keys);
        }

        string RuntimeString(string key, string fallback) =>
            runtimeSettings.TryGetValue(key, out var setting) ? setting.NormalizedValue : fallback;
        bool RuntimeBool(string key, bool fallback) =>
            runtimeSettings.TryGetValue(key, out var setting) && setting.Value is bool value ? value : fallback;
        int RuntimeInt(string key, int fallback) =>
            runtimeSettings.TryGetValue(key, out var setting) && setting.Value is int value ? value : fallback;

        // Backend selection is resolved once at process startup. Never let a stale
        // legacy .env file disagree with the controller and Home status.
        var backendType = _configuration.GetValue<string>("Backend:Type")
            ?? throw new InvalidOperationException("The deployment backend is unavailable.");
        var useJellyfinSettings = backendType.Equals("Jellyfin", StringComparison.OrdinalIgnoreCase);

        var fallbackExplicitFilter = useJellyfinSettings
            ? _jellyfinSettings.ExplicitFilter.ToString()
            : _subsonicSettings.ExplicitFilter.ToString();
        var fallbackEnableExternalPlaylists = useJellyfinSettings
            ? _jellyfinSettings.EnableExternalPlaylists
            : _subsonicSettings.EnableExternalPlaylists;
        var fallbackPlaylistsDirectory = useJellyfinSettings
            ? _jellyfinSettings.PlaylistsDirectory
            : _subsonicSettings.PlaylistsDirectory;
        var fallbackStorageMode = useJellyfinSettings
            ? _jellyfinSettings.StorageMode.ToString()
            : _subsonicSettings.StorageMode.ToString();
        var fallbackCacheDurationHours = useJellyfinSettings
            ? _jellyfinSettings.CacheDurationHours
            : _subsonicSettings.CacheDurationHours;
        var fallbackDownloadMode = useJellyfinSettings
            ? _jellyfinSettings.DownloadMode.ToString()
            : _subsonicSettings.DownloadMode.ToString();

        var storageModeValue = RuntimeString("Library:StorageMode", fallbackStorageMode);
        var isCacheStorageMode = storageModeValue.Equals(nameof(StorageMode.Cache), StringComparison.OrdinalIgnoreCase);

        var libraryDownloadRoot = _configuration["Library:DownloadPath"] ?? "./downloads";
        var libraryKeptPath = _configuration["Library:KeptPath"] ?? Path.Combine(libraryDownloadRoot, "kept");
        var effectivePlaylists = runtimeSettings.TryGetValue("SpotifyImport:Playlists", out var playlistSetting) &&
                                 playlistSetting.Value is string playlistJson &&
                                 !string.IsNullOrWhiteSpace(playlistJson)
            ? SpotifyPlaylistConfigParser.Parse(playlistJson)
            : _spotifyImportSettings.Playlists;
        var sessionUserId = GetAuthenticatedUserId();
        var cookieStatus = await _spotifySessionCookieService.GetCookieStatusAsync(sessionUserId);
        var effectiveSessionCookie = await _spotifySessionCookieService.ResolveSessionCookieAsync(sessionUserId);
        var userCookieSetDate = !string.IsNullOrWhiteSpace(sessionUserId)
            ? await _spotifySessionCookieService.GetCookieSetDateAsync(sessionUserId)
            : null;
        var effectiveCookieSetDate = userCookieSetDate?.ToString("o");

        if (string.IsNullOrWhiteSpace(effectiveCookieSetDate) && cookieStatus.UsingGlobalFallback)
        {
            effectiveCookieSetDate = _spotifyApiSettings.SessionCookieSetDate ?? string.Empty;
        }

        return Ok(new
        {
            backendType,
            explicitFilter = RuntimeString("Library:ExplicitFilter", fallbackExplicitFilter),
            enableExternalPlaylists = RuntimeBool("Library:EnableExternalPlaylists", fallbackEnableExternalPlaylists),
            playlistsDirectory = RuntimeString("Library:PlaylistsDirectory", fallbackPlaylistsDirectory),
            providers = new
            {
                metadataOrder = RuntimeString("Providers:MetadataOrder", "apple-download,deezer,qobuz"),
                downloadOrder = RuntimeString("Providers:DownloadOrder", "apple-download,deezer,qobuz"),
                streamingOrder = RuntimeString("Providers:StreamingOrder", "apple-download,deezer,qobuz"),
                playlistOrder = RuntimeString("Providers:PlaylistOrder", "spotify,deezer,qobuz"),
                lyricsOrder = RuntimeString("Providers:LyricsOrder", "spotify,apple-download,lyricsplus,lrclib"),
                enabledSearch = RuntimeString("Providers:EnabledSearch", "deezer,qobuz"),
                enabledPlaylist = RuntimeString("Providers:EnabledPlaylist", "spotify"),
                disabledProviders = RuntimeString("Providers:Disabled", string.Empty),
            },
            debug = new
            {
                logAllRequests = _configuration.GetValue<bool>("Debug:LogAllRequests", false),
                redactSensitiveRequestValues = true
            },
            admin = new
            {
                bindAnyIp = AdminNetworkBindingPolicy.ShouldBindAdminAnyIp(_configuration),
                trustedSubnets = _configuration.GetValue<string>("Admin:TrustedSubnets") ?? string.Empty,
                allowEnvExport = IsEnvExportEnabled(),
                redactSensitiveValues = _configuration.GetValue<bool>("Admin:RedactSensitiveValues", false)
            },
            spotifyApi = new
            {
                enabled = RuntimeBool("SpotifyApi:Enabled", _spotifyApiSettings.Enabled),
                sessionCookie = AdminHelperService.MaskValue(effectiveSessionCookie, showLast: 8),
                sessionCookieSetDate = effectiveCookieSetDate ?? string.Empty,
                usingGlobalFallback = cookieStatus.UsingGlobalFallback,
                cacheDurationMinutes = RuntimeInt("SpotifyApi:CacheDurationMinutes", _spotifyApiSettings.CacheDurationMinutes),
                rateLimitDelayMs = RuntimeInt("SpotifyApi:RateLimitDelayMs", _spotifyApiSettings.RateLimitDelayMs),
                preferIsrcMatching = RuntimeBool("SpotifyApi:PreferIsrcMatching", _spotifyApiSettings.PreferIsrcMatching)
            },
            spotifyImport = new
            {
                enabled = RuntimeBool("SpotifyImport:Enabled", _spotifyImportSettings.Enabled),
                matchingIntervalHours = RuntimeInt("SpotifyImport:MatchingIntervalHours", _spotifyImportSettings.MatchingIntervalHours),
                playlists = effectivePlaylists.Select(p => new
                {
                    name = p.Name,
                    id = p.Id,
                    localTracksPosition = p.LocalTracksPosition.ToString()
                })
            },
            jellyfin = new
            {
                url = _jellyfinSettings.Url ?? string.Empty,
                apiKey = AdminHelperService.MaskValue(_jellyfinSettings.ApiKey ?? string.Empty),
                userId = _jellyfinSettings.UserId ?? string.Empty,
                libraryId = _jellyfinSettings.LibraryId ?? string.Empty
            },
            subsonic = new
            {
                url = _subsonicSettings.Url ?? string.Empty
            },
            library = new
            {
                downloadPath = isCacheStorageMode
                    ? Path.Combine(libraryDownloadRoot, "cache")
                    : Path.Combine(libraryDownloadRoot, "permanent"),
                keptPath = libraryKeptPath,
                storageMode = storageModeValue,
                cacheDurationHours = RuntimeInt("Library:CacheDurationHours", fallbackCacheDurationHours),
                downloadMode = RuntimeString("Library:DownloadMode", fallbackDownloadMode)
            },
            deezer = new
            {
                arl = AdminHelperService.MaskValue(_deezerSettings.Arl ?? string.Empty, showLast: 8),
                arlFallback = AdminHelperService.MaskValue(_deezerSettings.ArlFallback ?? string.Empty, showLast: 8),
                quality = RuntimeString("Deezer:Quality", _deezerSettings.Quality ?? "FLAC"),
                minRequestIntervalMs = RuntimeInt("Deezer:MinRequestIntervalMs", _deezerSettings.MinRequestIntervalMs)
            },
            qobuz = new
            {
                userAuthToken = AdminHelperService.MaskValue(_qobuzSettings.UserAuthToken ?? string.Empty, showLast: 8),
                userId = _qobuzSettings.UserId ?? string.Empty,
                quality = RuntimeString("Qobuz:Quality", _qobuzSettings.Quality ?? "FLAC"),
                minRequestIntervalMs = RuntimeInt("Qobuz:MinRequestIntervalMs", _qobuzSettings.MinRequestIntervalMs)
            },
            squidWtf = new
            {
                quality = RuntimeString("SquidWTF:Quality", _squidWtfSettings.Quality ?? "LOSSLESS"),
                minRequestIntervalMs = RuntimeInt("SquidWTF:MinRequestIntervalMs", _squidWtfSettings.MinRequestIntervalMs)
            },
            appleDownload = new
            {
                baseUrl = !string.IsNullOrWhiteSpace(_configuration["AppleDownload:BaseUrl"])
                    ? _appleMusicSettings.BaseUrl ?? string.Empty
                    : RuntimeString("AppleDownload:BaseUrl", _appleMusicSettings.BaseUrl ?? string.Empty),
                endpointManagedByDeployment = !string.IsNullOrWhiteSpace(_configuration["AppleDownload:BaseUrl"]),
                quality = RuntimeString("AppleDownload:Quality", _appleMusicSettings.Quality ?? "alac-16-44")
            },
            musicBrainz = new
            {
                enabled = RuntimeBool("MusicBrainz:Enabled", _musicBrainzSettings.Enabled),
                username = _musicBrainzSettings.Username ?? string.Empty,
                password = AdminHelperService.MaskValue(_musicBrainzSettings.Password ?? string.Empty),
                baseUrl = _musicBrainzSettings.BaseUrl,
                rateLimitMs = _musicBrainzSettings.RateLimitMs
            },
            cache = new
            {
                searchResultsMinutes = RuntimeInt("Cache:SearchResultsMinutes", _configuration.GetValue<int>("Cache:SearchResultsMinutes", 1)),
                playlistImagesHours = RuntimeInt("Cache:PlaylistImagesHours", _configuration.GetValue<int>("Cache:PlaylistImagesHours", 168)),
                lyricsDays = RuntimeInt("Cache:LyricsDays", _configuration.GetValue<int>("Cache:LyricsDays", 14)),
                genreDays = RuntimeInt("Cache:GenreDays", _configuration.GetValue<int>("Cache:GenreDays", 30)),
                metadataDays = RuntimeInt("Cache:MetadataDays", _configuration.GetValue<int>("Cache:MetadataDays", 7)),
                odesliLookupDays = RuntimeInt("Cache:OdesliLookupDays", _configuration.GetValue<int>("Cache:OdesliLookupDays", 60)),
                proxyImagesDays = RuntimeInt("Cache:ProxyImagesDays", _configuration.GetValue<int>("Cache:ProxyImagesDays", 14)),
                mediaDirectory = _configuration["Cache:MediaDirectory"] ?? "/app/cache/media",
                mediaMaximumMegabytes = _configuration.GetValue<int>("Cache:MediaMaximumMegabytes", 512),
                mediaMaximumEntryMegabytes = _configuration.GetValue<int>("Cache:MediaMaximumEntryMegabytes", 16),
                mediaCleanupFileLimit = _configuration.GetValue<int>("Cache:MediaCleanupFileLimit", 10_000),
                transcodeCacheMinutes = RuntimeInt("Cache:TranscodeCacheMinutes", _configuration.GetValue<int>("Cache:TranscodeCacheMinutes", 60))
            },
            extensions = new
            {
                repositories = _configuration.GetValue<string>("EXTENSION_REPOSITORIES") ?? string.Empty
            },
            scrobbling = new
            {
                enabled = RuntimeBool("Scrobbling:Enabled", _scrobblingSettings.Enabled),
                localTracksEnabled = RuntimeBool("Scrobbling:LocalTracksEnabled", _scrobblingSettings.LocalTracksEnabled),
                syntheticLocalPlayedSignalEnabled = RuntimeBool(
                    "Scrobbling:SyntheticLocalPlayedSignalEnabled",
                    _scrobblingSettings.SyntheticLocalPlayedSignalEnabled),
                lastFm = new
                {
                    enabled = RuntimeBool("Scrobbling:LastFm:Enabled", _scrobblingSettings.LastFm.Enabled),
                    apiKey = AdminHelperService.MaskValue(_scrobblingSettings.LastFm.ApiKey, showLast: 8),
                    sharedSecret = AdminHelperService.MaskValue(_scrobblingSettings.LastFm.SharedSecret, showLast: 8),
                    sessionKey = AdminHelperService.MaskValue(_scrobblingSettings.LastFm.SessionKey, showLast: 8),
                    username = _scrobblingSettings.LastFm.Username ?? "(not set)",
                    password = AdminHelperService.MaskValue(_scrobblingSettings.LastFm.Password, showLast: 0)
                },
                listenBrainz = new
                {
                    enabled = RuntimeBool("Scrobbling:ListenBrainz:Enabled", _scrobblingSettings.ListenBrainz.Enabled),
                    userToken = AdminHelperService.MaskValue(_scrobblingSettings.ListenBrainz.UserToken, showLast: 8)
                }
            }
        });
    }

    /// <summary>Update allowlisted tenant runtime settings in durable storage.</summary>
    [HttpPost("config")]
    public async Task<IActionResult> UpdateConfig([FromBody] ConfigUpdateRequest request)
    {
        var adminCheck = RequireAdministratorForSensitiveOperation("config update");
        if (adminCheck != null)
        {
            return adminCheck;
        }

        if (request == null || request.Updates == null || request.Updates.Count == 0)
        {
            return BadRequest(new { error = "No updates provided" });
        }

        _logger.LogDebug("Config update requested: {Count} changes", request.Updates.Count);

        try
        {
            var session = GetAdminSession();
            if (session?.TenantId is not { } tenantId)
            {
                return Conflict(new
                {
                    error = "The administrator session is not linked to an Allstarr tenant.",
                    code = "tenant_required"
                });
            }

            var normalized = new List<(string LegacyKey, string DurableKey, string Value)>();
            foreach (var (key, value) in request.Updates)
            {
                if (!LegacyEnvParser.TryGetDurableAlias(key, out var durableKey))
                {
                    return BadRequest(new
                    {
                        error = $"{key} is deployment-owned, secret, deprecated, or unsupported and cannot be changed through runtime settings.",
                        code = "deployment_setting_read_only",
                        key,
                        message = "Change bootstrap values in the deployment configuration. Manage provider credentials through provider accounts."
                    });
                }

                normalized.Add((key, durableKey, value));
            }

            var settings = HttpContext.RequestServices.GetRequiredService<IDurableRuntimeSettings>();
            var current = await settings.GetManyAsync(tenantId, normalized.Select(item => item.DurableKey));
            var writes = normalized.Select(item =>
            {
                var existing = current[item.DurableKey];
                return new RuntimeSettingWrite(
                    item.DurableKey,
                    item.Value,
                    existing.Origin == RuntimeSettingOrigin.Durable ? existing.Revision : null);
            }).ToArray();
            var result = await settings.ApplyBatchAsync(
                tenantId,
                writes,
                "admin-ui",
                session.AllstarrUserId,
                HttpContext.RequestAborted);

            return Ok(new
            {
                message = "Runtime configuration updated.",
                updatedKeys = normalized.Select(item => item.LegacyKey).ToArray(),
                requiresRestart = false,
                changeVersion = result.ChangeVersion,
                settings = result.Settings.Select(item => new { item.Key, item.Revision, item.UpdatedAt })
            });
        }
        catch (RuntimeSettingConflictException ex)
        {
            return Conflict(new { error = ex.Message, code = "setting_conflict" });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message, code = "invalid_setting" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update durable runtime configuration");
            return StatusCode(500, new
            {
                error = "Failed to update configuration"
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

        var clearedCacheEntries = 0;

        // Clear all search cache keys (pattern-based deletion)
        var searchKeysDeleted =
            await _cache.DeleteByPatternAsync(CacheKeyBuilder.BuildSearchPattern());
        clearedCacheEntries += searchKeysDeleted;

        // Clear all media descriptor and content-addressed payload keys.
        var imageKeysDeleted =
            await _cache.DeleteByPatternAsync(CacheKeyBuilder.BuildMediaDescriptorPattern()) +
            await _cache.DeleteByPatternAsync(CacheKeyBuilder.BuildMediaPayloadPattern());
        clearedCacheEntries += imageKeysDeleted;

        _logger.LogInformation("Cache cleared: {Entries} derived entries (including {SearchKeys} search keys, {ImageKeys} image keys)",
            clearedCacheEntries, searchKeysDeleted, imageKeysDeleted);

        return Ok(new
        {
            message = "Cache cleared successfully",
            cacheEntriesDeleted = clearedCacheEntries
        });
    }

    /// <summary>
    /// Restart the allstarr container to apply configuration changes
    /// </summary>
    [HttpPost("restart")]
    public async Task<IActionResult> RestartContainer()
    {
        var adminCheck = RequireAdministratorForSensitiveOperation("container restart");
        if (adminCheck != null)
        {
            return adminCheck;
        }

        _logger.LogDebug("Container restart requested from admin UI");

        try
        {
            // Use Docker socket to restart the container
            var socketPath = "/var/run/docker.sock";

            if (!System.IO.File.Exists(socketPath))
            {
                _logger.LogWarning("Docker socket not available at {Path}", socketPath);
                return StatusCode(503, new
                {
                    error = "Docker socket not available",
                    message = "Please restart manually: docker restart allstarr"
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
                return StatusCode((int)response.StatusCode, new
                {
                    error = "Failed to restart container",
                    message = "Please restart manually: docker restart allstarr"
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error restarting container");
            return StatusCode(500, new
            {
                error = "Failed to restart container",
                message = "Please restart manually: docker restart allstarr"
            });
        }
    }

    /// <summary>
    /// Get all Jellyfin users
    /// </summary>
    [HttpGet("export-env")]
    public IActionResult ExportEnv()
    {
        var adminCheck = RequireAdministratorForSensitiveOperation("export env");
        if (adminCheck != null)
        {
            return adminCheck;
        }

        if (!IsEnvExportEnabled())
        {
            _logger.LogWarning("Blocked export-env request because ADMIN__ENABLE_ENV_EXPORT is disabled");
            return NotFound(new
            {
                error = "Export endpoint is disabled by default",
                message = "Set ADMIN__ENABLE_ENV_EXPORT=true to temporarily enable .env export."
            });
        }

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
            return StatusCode(500, new { error = "Failed to export .env file" });
        }
    }

    /// <summary>
    /// Import .env file from upload
    /// </summary>
    [HttpPost("import-env")]
    public async Task<IActionResult> ImportEnv([FromForm] IFormFile file)
    {
        var adminCheck = RequireAdministratorForSensitiveOperation("import env");
        if (adminCheck != null)
        {
            return adminCheck;
        }

        await Task.CompletedTask;
        return StatusCode(StatusCodes.Status410Gone, new
        {
            error = "The wholesale .env import endpoint has been retired.",
            message = "Use /api/admin/config/migration/preview and explicitly confirm /api/admin/config/migration/apply."
        });
    }

    [HttpPost("export-selective-state")]
    public async Task<IActionResult> ExportSelectiveState(
        [FromBody] SelectiveStateTransferControllerRequest? request,
        [FromServices] SelectiveStateTransferService transferService,
        CancellationToken cancellationToken = default)
    {
        var adminCheck = RequireAdministratorForSensitiveOperation("export selective state");
        if (adminCheck != null) return adminCheck;

        var session = GetAdminSession();
        if (session == null) return Unauthorized(new { error = "Admin session required" });

        var exportRequest = (request ?? new SelectiveStateTransferControllerRequest()).ToServiceRequest();
        var tempDir = Path.Combine(Path.GetTempPath(), "allstarr-export", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var (artifact, report) = await transferService.ExportAsync(
                tempDir,
                exportRequest,
                writesQuiesced: true,
                cancellationToken);

            var reportJson = JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            var reportBytes = System.Text.Encoding.UTF8.GetBytes(reportJson);
            var filename = $"allstarr-selective-export-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json";

            Response.Headers["X-Allstarr-Selective-Report"] = Convert.ToBase64String(reportBytes);
            return File(
                await System.IO.File.ReadAllBytesAsync(artifact.Path, cancellationToken),
                "application/zip",
                filename);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [HttpPost("import-selective-state")]
    public async Task<IActionResult> ImportSelectiveState(
        [FromBody] SelectiveStateTransferControllerRequest request,
        [FromServices] SelectiveStateTransferService transferService,
        CancellationToken cancellationToken = default)
    {
        var adminCheck = RequireAdministratorForSensitiveOperation("import selective state");
        if (adminCheck != null) return adminCheck;

        var session = GetAdminSession();
        if (session == null) return Unauthorized(new { error = "Admin session required" });
        if (string.IsNullOrWhiteSpace(request?.BackupJson))
        {
            return BadRequest(new { error = "BackupJson payload is required" });
        }

        try
        {
            var importRequest = request.ToServiceImportRequest();
            var report = await transferService.ImportAsync(importRequest, cancellationToken);
            return Ok(new
            {
                success = true,
                message = "Selective import applied cleanly.",
                report = new
                {
                    includedCategories = report.IncludedCategories,
                    excludedCategories = report.ExcludedCategories,
                    totalRows = report.TotalRows,
                    rowsByEntry = report.RowsByEntry
                }
            });
        }
        catch (SelectiveTransferValidationException ex)
        {
            _logger.LogWarning(ex, "Selective state import rejected by validation");
            return BadRequest(new { error = ex.Message });
        }
        catch (SelectiveTransferSchemaMismatchException ex)
        {
            _logger.LogWarning(ex, "Selective state import schema mismatch");
            return StatusCode(StatusCodes.Status409Conflict, new { error = ex.Message });
        }
    }

    [HttpPost("preview-selective-state")]
    public IActionResult PreviewSelectiveState(
        [FromBody] SelectiveStateTransferControllerRequest request,
        [FromServices] SelectiveStateTransferService transferService)
    {
        var adminCheck = RequireAdministratorForSensitiveOperation("preview selective state");
        if (adminCheck != null) return adminCheck;

        if (string.IsNullOrWhiteSpace(request?.BackupJson))
        {
            return BadRequest(new { error = "BackupJson payload is required" });
        }

        try
        {
            var importRequest = request.ToServiceImportRequest();
            var included = transferService.ResolveIncludedCategories(importRequest);
            return Ok(new
            {
                includedCategories = included.Select(category => category.ToString()).ToArray(),
                message = included.Count == 0
                    ? "No categories selected. The import would be a no-op."
                    : $"Preview will apply {included.Count} categor{(included.Count == 1 ? "y" : "ies")}."
            });
        }
        catch (SelectiveTransferValidationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("config/migration/status")]
    public async Task<IActionResult> GetEnvMigrationStatus(CancellationToken cancellationToken = default)
    {
        var adminCheck = RequireAdministratorForSensitiveOperation("env migration status");
        if (adminCheck != null)
        {
            return adminCheck;
        }

        var session = GetAdminSession();
        var service = HttpContext.RequestServices.GetRequiredService<LegacyEnvMigrationService>();
        return Ok(await service.GetStatusAsync(session?.TenantId, cancellationToken));
    }

    [HttpPost("config/migration/preview")]
    [RequestSizeLimit(LegacyEnvParser.MaxBytes * 2L)]
    public async Task<IActionResult> PreviewEnvMigration(
        [FromForm] IFormFile? file,
        CancellationToken cancellationToken = default)
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
        var adminCheck = RequireAdministratorForSensitiveOperation("env migration preview");
        if (adminCheck != null)
        {
            return adminCheck;
        }

        if (file == null || file.Length is <= 0 or > LegacyEnvParser.MaxBytes)
        {
            return BadRequest(new { error = $"Choose a .env file between 1 and {LegacyEnvParser.MaxBytes} bytes." });
        }

        var sourceName = Path.GetFileName(file.FileName);
        if (sourceName.Length is 0 or > 255 ||
            !sourceName.EndsWith(".env", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { error = "Choose a file with a bounded .env filename." });
        }

        try
        {
            await using var source = file.OpenReadStream();
            using var buffer = new MemoryStream((int)file.Length);
            await source.CopyToAsync(buffer, cancellationToken);
            var bytes = buffer.ToArray();
            try
            {
                var service = HttpContext.RequestServices.GetRequiredService<LegacyEnvMigrationService>();
                return Ok(await service.PreviewAsync(
                    bytes,
                    CreateMigrationActor(),
                    cancellationToken));
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
            }
        }
        catch (LegacyEnvParseException ex)
        {
            return BadRequest(new { error = ex.Message, code = "invalid_env" });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message, code = "invalid_setting" });
        }
    }

    [HttpPost("config/migration/apply")]
    public async Task<IActionResult> ApplyEnvMigration(
        [FromBody] ApplyLegacyEnvMigrationRequest request,
        CancellationToken cancellationToken = default)
    {
        var adminCheck = RequireAdministratorForSensitiveOperation("env migration apply");
        if (adminCheck != null)
        {
            return adminCheck;
        }

        try
        {
            var service = HttpContext.RequestServices.GetRequiredService<LegacyEnvMigrationService>();
            return Ok(await service.ApplyAsync(
                request.PreviewToken ?? string.Empty,
                request.Revision ?? string.Empty,
                request.Confirmed,
                CreateMigrationActor(),
                cancellationToken));
        }
        catch (LegacyEnvMigrationException ex) when (ex.Code == "preview_owner_mismatch")
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message, code = ex.Code });
        }
        catch (LegacyEnvMigrationException ex) when (ex.Code is "revision_mismatch" or "state_changed" or
                                                      "provider_account_conflict")
        {
            return Conflict(new { error = ex.Message, code = ex.Code });
        }
        catch (LegacyEnvMigrationException ex)
        {
            return BadRequest(new { error = ex.Message, code = ex.Code });
        }
        catch (RuntimeSettingConflictException ex)
        {
            return Conflict(new { error = ex.Message, code = "setting_conflict" });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message, code = "invalid_setting" });
        }
    }

    private AdminAuthSession? GetAdminSession() =>
        HttpContext.Items.TryGetValue(AdminAuthSessionService.HttpContextSessionItemKey, out var value)
            ? value as AdminAuthSession
            : null;

    private LegacyEnvMigrationActor CreateMigrationActor()
    {
        var session = GetAdminSession() ?? throw new LegacyEnvMigrationException(
            "admin_session_required",
            "An administrator session is required.");
        var correlationId = HttpContext.Items[CorrelationMiddleware.HttpContextItemKey]?.ToString()
                            ?? HttpContext.TraceIdentifier;
        return new(session.SessionId, session.TenantId, session.AllstarrUserId, correlationId);
    }

    public sealed class ApplyLegacyEnvMigrationRequest
    {
        public string? PreviewToken { get; set; }
        public string? Revision { get; set; }
        public bool Confirmed { get; set; }
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

    private IActionResult? RequireAdministratorForSensitiveOperation(string operationName)
    {
        if (HttpContext.Items.TryGetValue(AdminAuthSessionService.HttpContextSessionItemKey, out var sessionObj) &&
            sessionObj is AdminAuthSession session &&
            session.IsAdministrator)
        {
            return null;
        }

        _logger.LogWarning("Blocked sensitive admin operation '{Operation}' due to missing administrator session", operationName);
        return StatusCode(StatusCodes.Status403Forbidden, new
        {
            error = "Administrator permissions required",
            message = "This operation is restricted to Jellyfin administrators."
        });
    }

    private bool IsEnvExportEnabled()
    {
        if (_configuration.GetValue<bool>("Admin:EnableEnvExport"))
        {
            return true;
        }

        if (_configuration.GetValue<bool>("ADMIN__ENABLE_ENV_EXPORT"))
        {
            return true;
        }

        return _configuration.GetValue<bool>("ADMIN_ENABLE_ENV_EXPORT");
    }

    [HttpGet("providers/status")]
    public async Task<IActionResult> GetProvidersStatus(CancellationToken cancellationToken = default)
    {
        var adminCheck = RequireAdministratorForSensitiveOperation("get providers status");
        if (adminCheck != null)
        {
            return adminCheck;
        }

        var statusManager = HttpContext.RequestServices.GetRequiredService<ProviderStatusManager>();
        var contextFactory = HttpContext.RequestServices
            .GetRequiredService<IDbContextFactory<AllstarrDbContext>>();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var accounts = await context.ProviderAccounts.AsNoTracking()
            .OrderBy(item => item.ProviderId)
            .ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);

        var results = new List<object>();
        foreach (var account in accounts)
        {
            IReadOnlyDictionary<string, string> accountSecrets =
                new Dictionary<string, string>(StringComparer.Ordinal);
            if (account.SecretReferenceId.HasValue)
            {
                try
                {
                    accountSecrets = await ReadProviderAccountSecretsAsync(account, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(
                        "Managed provider account status configuration could not be opened ({ExceptionType})",
                        ex.GetType().Name);
                }
            }

            foreach (var status in statusManager.GetAllManagedStatuses(
                         account.ProviderId,
                         account.Id,
                         accountSecrets))
            {
                results.Add(new
                {
                    provider = status.Provider,
                    providerAccountId = account.Id,
                    providerAccountName = account.DisplayName,
                    capability = status.Capability,
                    accountScope = account.Scope.ToString().ToLowerInvariant(),
                    supported = status.IsSupported,
                    enabled = account.Enabled && status.IsEnabled,
                    configuration = status.Configuration switch
                    {
                        ProviderConfigurationState.NotRequired => "not_required",
                        ProviderConfigurationState.Configured => "configured",
                        _ => "needs_configuration"
                    },
                    health = status.Health.ToString().ToLowerInvariant(),
                    ready = account.Enabled && status.IsReady,
                    canAttempt = account.Enabled && status.CanAttempt,
                    testedAt = status.TestedAt,
                    reasonCode = account.Enabled ? status.ReasonCode : "account_disabled",
                    canTest = statusManager.CanTestCapability(status.Provider, status.Capability)
                });
            }
        }

        return Ok(results);
    }

    [HttpPost("providers/test/{provider}")]
    [HttpPost("providers/test/{provider}/{capability}")]
    public async Task<IActionResult> TestProvider(
        string provider,
        string? capability = null,
        [FromQuery] Guid? accountId = null)
    {
        var adminCheck = RequireAdministratorForSensitiveOperation("test provider connection");
        if (adminCheck != null)
        {
            return adminCheck;
        }

        var statusManager = HttpContext.RequestServices.GetRequiredService<ProviderStatusManager>();
        var normalizedProvider = provider.Trim().ToLowerInvariant();
        if (!accountId.HasValue || accountId == Guid.Empty)
        {
            if (string.IsNullOrWhiteSpace(capability))
            {
                return BadRequest(new
                {
                    success = false,
                    error = "Select a capability to test"
                });
            }

            var currentGlobal = statusManager.GetStatus(normalizedProvider, capability);
            if (!currentGlobal.IsSupported || !statusManager.CanTestCapability(normalizedProvider, capability))
            {
                return BadRequest(new { success = false, error = "This provider capability has no endpoint probe" });
            }

            var globalTimer = System.Diagnostics.Stopwatch.StartNew();
            var testedGlobal = await statusManager.TestProviderCapabilityAsync(
                normalizedProvider,
                capability,
                cancellationToken: HttpContext.RequestAborted);
            globalTimer.Stop();
            var globalLatencyMs = globalTimer.ElapsedMilliseconds;
            return Ok(new
            {
                success = testedGlobal.Health == allstarr.Services.Common.ProviderHealthState.Healthy,
                provider = testedGlobal.Provider,
                capability = testedGlobal.Capability,
                health = testedGlobal.Health.ToString().ToLowerInvariant(),
                latencyMs = globalLatencyMs,
                bars = ConnectivityQuality.Bars(globalLatencyMs, testedGlobal.Health == allstarr.Services.Common.ProviderHealthState.Healthy, ConnectivityMetric.ApiLatency),
                metric = "api-latency",
                testedAt = testedGlobal.TestedAt,
                reasonCode = testedGlobal.ReasonCode
            });
        }

        var contextFactory = HttpContext.RequestServices
            .GetRequiredService<IDbContextFactory<AllstarrDbContext>>();
        await using var context = await contextFactory.CreateDbContextAsync(HttpContext.RequestAborted);
        var account = await context.ProviderAccounts.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == accountId.Value,
            HttpContext.RequestAborted);
        if (account == null ||
            !account.ProviderId.Equals(normalizedProvider, StringComparison.OrdinalIgnoreCase))
        {
            return NotFound(new { success = false, error = "Managed provider account not found" });
        }

        IReadOnlyDictionary<string, string> accountSecrets;
        try
        {
            accountSecrets = await ReadProviderAccountSecretsAsync(account, HttpContext.RequestAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                "Managed provider account probe configuration could not be opened ({ExceptionType})",
                ex.GetType().Name);
            return BadRequest(new
            {
                success = false,
                error = "The managed provider account credential is missing or invalid"
            });
        }

        if (string.IsNullOrWhiteSpace(capability))
        {
            var connectionTimer = System.Diagnostics.Stopwatch.StartNew();
            var healthy = await statusManager.TestManagedProviderConnectionAsync(
                normalizedProvider,
                account.Id,
                accountSecrets,
                HttpContext.RequestAborted);
            connectionTimer.Stop();
            var connectionLatencyMs = connectionTimer.ElapsedMilliseconds;
            var failedCapabilities = statusManager.GetAllManagedStatuses(
                    normalizedProvider,
                    account.Id,
                    accountSecrets)
                .Where(item => item.Health == allstarr.Services.Common.ProviderHealthState.Degraded)
                .Select(item => new { capability = item.Capability, reasonCode = item.ReasonCode })
                .ToArray();
            return Ok(new
            {
                success = true,
                provider = normalizedProvider,
                providerAccountId = account.Id,
                healthy,
                latencyMs = connectionLatencyMs,
                bars = ConnectivityQuality.Bars(connectionLatencyMs, healthy, ConnectivityMetric.ApiLatency),
                metric = "api-latency",
                reasonCode = failedCapabilities.FirstOrDefault()?.reasonCode,
                failedCapabilities
            });
        }

        var current = statusManager.GetManagedStatus(
            normalizedProvider,
            capability,
            account.Id,
            accountSecrets);
        if (!current.IsSupported)
        {
            return BadRequest(new
            {
                success = false,
                provider = normalizedProvider,
                capability,
                error = "Unsupported provider capability"
            });
        }

        var capabilityTimer = System.Diagnostics.Stopwatch.StartNew();
        var tested = await statusManager.TestManagedProviderCapabilityAsync(
            normalizedProvider,
            capability,
            account.Id,
            accountSecrets,
            HttpContext.RequestAborted);
        capabilityTimer.Stop();
        var capabilityLatencyMs = capabilityTimer.ElapsedMilliseconds;
        return Ok(new
        {
            success = tested.Health == allstarr.Services.Common.ProviderHealthState.Healthy,
            provider = tested.Provider,
            providerAccountId = account.Id,
            capability = tested.Capability,
            health = tested.Health.ToString().ToLowerInvariant(),
            latencyMs = capabilityLatencyMs,
            bars = ConnectivityQuality.Bars(capabilityLatencyMs, tested.Health == allstarr.Services.Common.ProviderHealthState.Healthy, ConnectivityMetric.ApiLatency),
            metric = "api-latency",
            testedAt = tested.TestedAt,
            reasonCode = tested.ReasonCode
        });
    }

    private async Task<IReadOnlyDictionary<string, string>> ReadProviderAccountSecretsAsync(
        ProviderAccountRecord account,
        CancellationToken cancellationToken)
    {
        if (!account.SecretReferenceId.HasValue)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var secretStore = HttpContext.RequestServices.GetRequiredService<EncryptedSecretStore>();
        using var lease = await secretStore.OpenAsync(
            account.SecretReferenceId.Value,
            new SecretAccessContext(
                account.TenantId,
                AllowGlobal: account.TenantId == null),
            cancellationToken);
        using var document = JsonDocument.Parse(lease.Value);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Provider account credentials must be a JSON object.");
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            var normalizedName = new string(property.Name
                .Where(char.IsLetterOrDigit)
                .Select(char.ToLowerInvariant)
                .ToArray());
            var value = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number => property.Value.GetRawText(),
                _ => null
            };
            if (!string.IsNullOrWhiteSpace(normalizedName) && !string.IsNullOrWhiteSpace(value))
            {
                values[normalizedName] = value;
            }
        }

        return values;
    }

    /// <summary>
    /// Gets detailed memory usage statistics for debugging.
    /// </summary>
}

public sealed class SelectiveStateTransferControllerRequest
{
    public bool IncludeSettings { get; set; } = true;
    public bool IncludeAccounts { get; set; } = true;
    public bool IncludePlaylists { get; set; } = true;
    public bool IncludeIntelligence { get; set; } = true;
    public bool IncludeExtensions { get; set; } = true;
    public bool ImportSettings { get; set; } = true;
    public bool ImportAccounts { get; set; } = true;
    public bool ImportPlaylists { get; set; } = true;
    public bool ImportIntelligence { get; set; } = true;
    public bool ImportExtensions { get; set; } = true;
    public string? BackupJson { get; set; }

    public SelectiveExportRequest ToServiceExportRequest() => new()
    {
        IncludeSettings = IncludeSettings,
        IncludeAccounts = IncludeAccounts,
        IncludePlaylists = IncludePlaylists,
        IncludeIntelligence = IncludeIntelligence,
        IncludeExtensions = IncludeExtensions
    };

    public SelectiveImportRequest ToServiceImportRequest() => new()
    {
        ImportSettings = ImportSettings,
        ImportAccounts = ImportAccounts,
        ImportPlaylists = ImportPlaylists,
        ImportIntelligence = ImportIntelligence,
        ImportExtensions = ImportExtensions,
        BackupJson = BackupJson ?? string.Empty
    };
}

internal static class SelectiveStateTransferRequestAdapter
{
    public static SelectiveExportRequest ToServiceRequest(this SelectiveStateTransferControllerRequest controller)
    {
        var c = controller ?? new SelectiveStateTransferControllerRequest();
        return new SelectiveExportRequest
        {
            IncludeSettings = c.IncludeSettings,
            IncludeAccounts = c.IncludeAccounts,
            IncludePlaylists = c.IncludePlaylists,
            IncludeIntelligence = c.IncludeIntelligence,
            IncludeExtensions = c.IncludeExtensions
        };
    }

    public static SelectiveImportRequest ToServiceImportRequest(this SelectiveStateTransferControllerRequest controller)
    {
        var c = controller ?? new SelectiveStateTransferControllerRequest();
        return new SelectiveImportRequest
        {
            ImportSettings = c.ImportSettings || c.IncludeSettings,
            ImportAccounts = c.ImportAccounts || c.IncludeAccounts,
            ImportPlaylists = c.ImportPlaylists || c.IncludePlaylists,
            ImportIntelligence = c.ImportIntelligence || c.IncludeIntelligence,
            ImportExtensions = c.ImportExtensions || c.IncludeExtensions,
            BackupJson = c.BackupJson ?? string.Empty
        };
    }
}
