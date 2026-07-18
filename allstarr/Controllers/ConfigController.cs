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
    private readonly AdminHelperService _helperService;
    private readonly SpotifySessionCookieService _spotifySessionCookieService;
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
        IOptions<AppleDownloadSettings> appleMusicSettings,
        IOptions<MusicBrainzSettings> musicBrainzSettings,
        IOptions<SpotifyImportSettings> spotifyImportSettings,
        IOptions<ScrobblingSettings> scrobblingSettings,
        AdminHelperService helperService,
        SpotifySessionCookieService spotifySessionCookieService,
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
        _appleMusicSettings = appleMusicSettings.Value;
        _musicBrainzSettings = musicBrainzSettings.Value;
        _spotifyImportSettings = spotifyImportSettings.Value;
        _scrobblingSettings = scrobblingSettings.Value;
        _helperService = helperService;
        _spotifySessionCookieService = spotifySessionCookieService;
        _cache = cache;
    }

    [HttpGet("config")]
    public async Task<IActionResult> GetConfig()
    {
        var envVars = await ReadEnvSettingsAsync();
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

        var backendType = GetEnvString(
            envVars,
            "BACKEND_TYPE",
            _configuration.GetValue<string>("Backend:Type") ?? "Jellyfin");
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

        var libraryDownloadRoot = GetEnvString(
            envVars,
            "LIBRARY_DOWNLOAD_PATH",
            GetEnvString(
                envVars,
                "Library__DownloadPath",
                _configuration["Library:DownloadPath"] ?? "./downloads",
                treatEmptyAsMissing: true),
            treatEmptyAsMissing: true);
        var libraryKeptPath = GetEnvString(
            envVars,
            "LIBRARY_KEPT_PATH",
            Path.Combine(libraryDownloadRoot, "kept"),
            treatEmptyAsMissing: true);

        var envPlaylists = await _helperService.ReadPlaylistsFromEnvFileAsync();
        var hasEnvPlaylistKey = envVars.ContainsKey("SPOTIFY_IMPORT_PLAYLISTS");
        var effectivePlaylists = hasEnvPlaylistKey ? envPlaylists : _spotifyImportSettings.Playlists;
        var sessionUserId = GetAuthenticatedUserId();
        var cookieStatus = await _spotifySessionCookieService.GetCookieStatusAsync(sessionUserId);
        var effectiveSessionCookie = await _spotifySessionCookieService.ResolveSessionCookieAsync(sessionUserId);
        var userCookieSetDate = !string.IsNullOrWhiteSpace(sessionUserId)
            ? await _spotifySessionCookieService.GetCookieSetDateAsync(sessionUserId)
            : null;
        var effectiveCookieSetDate = userCookieSetDate?.ToString("o");

        if (string.IsNullOrWhiteSpace(effectiveCookieSetDate) && cookieStatus.UsingGlobalFallback)
        {
            effectiveCookieSetDate = GetEnvString(
                envVars,
                "SPOTIFY_API_SESSION_COOKIE_SET_DATE",
                _spotifyApiSettings.SessionCookieSetDate ?? string.Empty);
        }

        return Ok(new
        {
            backendType,
            explicitFilter = RuntimeString("Library:ExplicitFilter", fallbackExplicitFilter),
            enableExternalPlaylists = RuntimeBool("Library:EnableExternalPlaylists", fallbackEnableExternalPlaylists),
            playlistsDirectory = RuntimeString("Library:PlaylistsDirectory", fallbackPlaylistsDirectory),
            redisEnabled = GetEnvBool(envVars, "REDIS_ENABLED", _configuration.GetValue<bool>("Redis:Enabled", false)),
            providers = new
            {
                metadataOrder = RuntimeString("Providers:MetadataOrder", "deezer,qobuz,squidwtf"),
                downloadOrder = RuntimeString("Providers:DownloadOrder", "deezer,qobuz"),
                streamingOrder = RuntimeString("Providers:StreamingOrder", "deezer,qobuz"),
                playlistOrder = RuntimeString("Providers:PlaylistOrder", "spotify,deezer,qobuz"),
                lyricsOrder = RuntimeString("Providers:LyricsOrder", "spotify,lyricsplus,lrclib"),
                enabledSearch = RuntimeString("Providers:EnabledSearch", "deezer,qobuz,squidwtf"),
                enabledPlaylist = RuntimeString("Providers:EnabledPlaylist", "spotify"),
                disabledProviders = RuntimeString("Providers:Disabled", string.Empty),
            },
            debug = new
            {
                logAllRequests = GetEnvBool(envVars, "DEBUG_LOG_ALL_REQUESTS", _configuration.GetValue<bool>("Debug:LogAllRequests", false)),
                redactSensitiveRequestValues = true
            },
            admin = new
            {
                bindAnyIp = GetEnvBool(envVars, "ADMIN_BIND_ANY_IP", AdminNetworkBindingPolicy.ShouldBindAdminAnyIp(_configuration)),
                trustedSubnets = GetEnvString(envVars, "ADMIN_TRUSTED_SUBNETS", _configuration.GetValue<string>("Admin:TrustedSubnets") ?? string.Empty),
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
                url = GetEnvString(envVars, "JELLYFIN_URL", _jellyfinSettings.Url ?? string.Empty),
                apiKey = AdminHelperService.MaskValue(GetEnvString(envVars, "JELLYFIN_API_KEY", _jellyfinSettings.ApiKey ?? string.Empty)),
                userId = GetEnvString(envVars, "JELLYFIN_USER_ID", _jellyfinSettings.UserId ?? string.Empty),
                libraryId = GetEnvString(envVars, "JELLYFIN_LIBRARY_ID", _jellyfinSettings.LibraryId ?? string.Empty)
            },
            subsonic = new
            {
                url = GetEnvString(envVars, "SUBSONIC_URL", _subsonicSettings.Url ?? string.Empty)
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
                arl = AdminHelperService.MaskValue(GetEnvString(envVars, "DEEZER_ARL", _deezerSettings.Arl ?? string.Empty), showLast: 8),
                arlFallback = AdminHelperService.MaskValue(GetEnvString(envVars, "DEEZER_ARL_FALLBACK", _deezerSettings.ArlFallback ?? string.Empty), showLast: 8),
                quality = RuntimeString("Deezer:Quality", _deezerSettings.Quality ?? "FLAC"),
                minRequestIntervalMs = RuntimeInt("Deezer:MinRequestIntervalMs", _deezerSettings.MinRequestIntervalMs)
            },
            qobuz = new
            {
                userAuthToken = AdminHelperService.MaskValue(GetEnvString(envVars, "QOBUZ_USER_AUTH_TOKEN", _qobuzSettings.UserAuthToken ?? string.Empty), showLast: 8),
                userId = GetEnvString(envVars, "QOBUZ_USER_ID", _qobuzSettings.UserId ?? string.Empty),
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
                baseUrl = RuntimeString("AppleDownload:BaseUrl", _appleMusicSettings.BaseUrl ?? string.Empty),
                quality = RuntimeString("AppleDownload:Quality", _appleMusicSettings.Quality ?? "alac-16-44")
            },
            musicBrainz = new
            {
                enabled = RuntimeBool("MusicBrainz:Enabled", _musicBrainzSettings.Enabled),
                username = GetEnvString(envVars, "MUSICBRAINZ_USERNAME", _musicBrainzSettings.Username ?? string.Empty),
                password = AdminHelperService.MaskValue(GetEnvString(envVars, "MUSICBRAINZ_PASSWORD", _musicBrainzSettings.Password ?? string.Empty)),
                baseUrl = _musicBrainzSettings.BaseUrl,
                rateLimitMs = _musicBrainzSettings.RateLimitMs
            },
            cache = new
            {
                searchResultsMinutes = RuntimeInt("Cache:SearchResultsMinutes", _configuration.GetValue<int>("Cache:SearchResultsMinutes", 1)),
                playlistImagesHours = RuntimeInt("Cache:PlaylistImagesHours", _configuration.GetValue<int>("Cache:PlaylistImagesHours", 168)),
                spotifyPlaylistItemsHours = RuntimeInt("Cache:SpotifyPlaylistItemsHours", _configuration.GetValue<int>("Cache:SpotifyPlaylistItemsHours", 168)),
                spotifyMatchedTracksDays = RuntimeInt("Cache:SpotifyMatchedTracksDays", _configuration.GetValue<int>("Cache:SpotifyMatchedTracksDays", 30)),
                lyricsDays = RuntimeInt("Cache:LyricsDays", _configuration.GetValue<int>("Cache:LyricsDays", 14)),
                genreDays = RuntimeInt("Cache:GenreDays", _configuration.GetValue<int>("Cache:GenreDays", 30)),
                metadataDays = RuntimeInt("Cache:MetadataDays", _configuration.GetValue<int>("Cache:MetadataDays", 7)),
                odesliLookupDays = RuntimeInt("Cache:OdesliLookupDays", _configuration.GetValue<int>("Cache:OdesliLookupDays", 60)),
                proxyImagesDays = RuntimeInt("Cache:ProxyImagesDays", _configuration.GetValue<int>("Cache:ProxyImagesDays", 14)),
                transcodeCacheMinutes = RuntimeInt("Cache:TranscodeCacheMinutes", _configuration.GetValue<int>("Cache:TranscodeCacheMinutes", 60))
            },
            extensions = new
            {
                repositories = GetEnvString(
                    envVars,
                    "EXTENSION_REPOSITORIES",
                    _configuration.GetValue<string>("EXTENSION_REPOSITORIES") ?? string.Empty)
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

    private async Task<Dictionary<string, string>> ReadEnvSettingsAsync()
    {
        var envVars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var envPath = _helperService.GetEnvFilePath();
            if (!System.IO.File.Exists(envPath))
            {
                return envVars;
            }

            var lines = await System.IO.File.ReadAllLinesAsync(envPath);
            foreach (var line in lines)
            {
                if (AdminHelperService.ShouldSkipEnvLine(line))
                    continue;

                var (key, value) = AdminHelperService.ParseEnvLine(line);
                if (!string.IsNullOrWhiteSpace(key))
                {
                    envVars[key] = value;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse env settings for config view");
        }

        return envVars;
    }

    private static string GetEnvString(
        IReadOnlyDictionary<string, string> envVars,
        string key,
        string fallback,
        bool treatEmptyAsMissing = false)
    {
        if (!envVars.TryGetValue(key, out var value))
        {
            return fallback;
        }

        if (treatEmptyAsMissing && string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        return value;
    }

    private static bool GetEnvBool(IReadOnlyDictionary<string, string> envVars, string key, bool fallback)
    {
        if (!envVars.TryGetValue(key, out var rawValue))
        {
            return fallback;
        }

        if (bool.TryParse(rawValue, out var parsed))
        {
            return parsed;
        }

        if (rawValue.Equals("1", StringComparison.OrdinalIgnoreCase) ||
            rawValue.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
            rawValue.Equals("on", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (rawValue.Equals("0", StringComparison.OrdinalIgnoreCase) ||
            rawValue.Equals("no", StringComparison.OrdinalIgnoreCase) ||
            rawValue.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return fallback;
    }

    private static int GetEnvInt(IReadOnlyDictionary<string, string> envVars, string key, int fallback)
    {
        if (!envVars.TryGetValue(key, out var rawValue))
        {
            return fallback;
        }

        return int.TryParse(rawValue, out var parsed) ? parsed : fallback;
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

        return Ok(new
        {
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
    /// Initialize cookie date to current date if cookie exists but date is not set
    /// </summary>
    [HttpPost("config/init-cookie-date")]
    public async Task<IActionResult> InitCookieDate()
    {
        var adminCheck = RequireAdministratorForSensitiveOperation("init cookie date");
        if (adminCheck != null)
        {
            return adminCheck;
        }

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
            if (account.Enabled && account.SecretReferenceId.HasValue)
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
                    reasonCode = account.Enabled ? status.ReasonCode : "account_disabled"
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

        if (!accountId.HasValue || accountId == Guid.Empty)
        {
            return BadRequest(new
            {
                success = false,
                error = "Select a managed provider account before testing a capability"
            });
        }

        var normalizedProvider = provider.Trim().ToLowerInvariant();
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

        if (!account.Enabled)
        {
            return Conflict(new { success = false, error = "Managed provider account is disabled" });
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

        var statusManager = HttpContext.RequestServices.GetRequiredService<ProviderStatusManager>();
        if (string.IsNullOrWhiteSpace(capability))
        {
            var healthy = await statusManager.TestManagedProviderConnectionAsync(
                normalizedProvider,
                account.Id,
                accountSecrets,
                HttpContext.RequestAborted);
            return Ok(new
            {
                success = true,
                provider = normalizedProvider,
                providerAccountId = account.Id,
                healthy
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

        var tested = await statusManager.TestManagedProviderCapabilityAsync(
            normalizedProvider,
            capability,
            account.Id,
            accountSecrets,
            HttpContext.RequestAborted);
        return Ok(new
        {
            success = tested.Health == allstarr.Services.Common.ProviderHealthState.Healthy,
            provider = tested.Provider,
            providerAccountId = account.Id,
            capability = tested.Capability,
            health = tested.Health.ToString().ToLowerInvariant(),
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
