using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using allstarr.Models.Settings;
using allstarr.Models.Admin;
using allstarr.Filters;
using allstarr.Services.Admin;
using allstarr.Services.Common;
using allstarr.Services.Spotify;
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

        var backendType = GetEnvString(
            envVars,
            "BACKEND_TYPE",
            _configuration.GetValue<string>("Backend:Type") ?? "Jellyfin");
        var useJellyfinSettings = backendType.Equals("Jellyfin", StringComparison.OrdinalIgnoreCase);

        var fallbackMusicService = useJellyfinSettings
            ? _jellyfinSettings.MusicService.ToString()
            : _subsonicSettings.MusicService.ToString();
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

        var storageModeValue = GetEnvString(envVars, "STORAGE_MODE", fallbackStorageMode);
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
            musicService = GetEnvString(envVars, "MUSIC_SERVICE", fallbackMusicService),
            explicitFilter = GetEnvString(envVars, "EXPLICIT_FILTER", fallbackExplicitFilter),
            enableExternalPlaylists = GetEnvBool(envVars, "ENABLE_EXTERNAL_PLAYLISTS", fallbackEnableExternalPlaylists),
            playlistsDirectory = GetEnvString(envVars, "PLAYLISTS_DIRECTORY", fallbackPlaylistsDirectory),
            redisEnabled = GetEnvBool(envVars, "REDIS_ENABLED", _configuration.GetValue<bool>("Redis:Enabled", false)),
            debug = new
            {
                logAllRequests = GetEnvBool(envVars, "DEBUG_LOG_ALL_REQUESTS", _configuration.GetValue<bool>("Debug:LogAllRequests", false)),
                redactSensitiveRequestValues = GetEnvBool(
                    envVars,
                    "DEBUG_REDACT_SENSITIVE_REQUEST_VALUES",
                    _configuration.GetValue<bool>("Debug:RedactSensitiveRequestValues", false))
            },
            admin = new
            {
                bindAnyIp = GetEnvBool(envVars, "ADMIN_BIND_ANY_IP", AdminNetworkBindingPolicy.ShouldBindAdminAnyIp(_configuration)),
                trustedSubnets = GetEnvString(envVars, "ADMIN_TRUSTED_SUBNETS", _configuration.GetValue<string>("Admin:TrustedSubnets") ?? string.Empty),
                allowEnvExport = IsEnvExportEnabled()
            },
            spotifyApi = new
            {
                enabled = GetEnvBool(envVars, "SPOTIFY_API_ENABLED", _spotifyApiSettings.Enabled),
                sessionCookie = AdminHelperService.MaskValue(effectiveSessionCookie, showLast: 8),
                sessionCookieSetDate = effectiveCookieSetDate ?? string.Empty,
                usingGlobalFallback = cookieStatus.UsingGlobalFallback,
                cacheDurationMinutes = GetEnvInt(envVars, "SPOTIFY_API_CACHE_DURATION_MINUTES", _spotifyApiSettings.CacheDurationMinutes),
                rateLimitDelayMs = _spotifyApiSettings.RateLimitDelayMs,
                preferIsrcMatching = GetEnvBool(envVars, "SPOTIFY_API_PREFER_ISRC_MATCHING", _spotifyApiSettings.PreferIsrcMatching)
            },
            spotifyImport = new
            {
                enabled = GetEnvBool(envVars, "SPOTIFY_IMPORT_ENABLED", _spotifyImportSettings.Enabled),
                matchingIntervalHours = GetEnvInt(envVars, "SPOTIFY_IMPORT_MATCHING_INTERVAL_HOURS", _spotifyImportSettings.MatchingIntervalHours),
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
            library = new
            {
                downloadPath = isCacheStorageMode
                    ? Path.Combine(libraryDownloadRoot, "cache")
                    : Path.Combine(libraryDownloadRoot, "permanent"),
                keptPath = libraryKeptPath,
                storageMode = storageModeValue,
                cacheDurationHours = GetEnvInt(envVars, "CACHE_DURATION_HOURS", fallbackCacheDurationHours),
                downloadMode = GetEnvString(envVars, "DOWNLOAD_MODE", fallbackDownloadMode)
            },
            deezer = new
            {
                arl = AdminHelperService.MaskValue(GetEnvString(envVars, "DEEZER_ARL", _deezerSettings.Arl ?? string.Empty), showLast: 8),
                arlFallback = AdminHelperService.MaskValue(GetEnvString(envVars, "DEEZER_ARL_FALLBACK", _deezerSettings.ArlFallback ?? string.Empty), showLast: 8),
                quality = GetEnvString(envVars, "DEEZER_QUALITY", _deezerSettings.Quality ?? "FLAC"),
                minRequestIntervalMs = GetEnvInt(envVars, "DEEZER_MIN_REQUEST_INTERVAL_MS", _deezerSettings.MinRequestIntervalMs)
            },
            qobuz = new
            {
                userAuthToken = AdminHelperService.MaskValue(GetEnvString(envVars, "QOBUZ_USER_AUTH_TOKEN", _qobuzSettings.UserAuthToken ?? string.Empty), showLast: 8),
                userId = GetEnvString(envVars, "QOBUZ_USER_ID", _qobuzSettings.UserId ?? string.Empty),
                quality = GetEnvString(envVars, "QOBUZ_QUALITY", _qobuzSettings.Quality ?? "FLAC"),
                minRequestIntervalMs = GetEnvInt(envVars, "QOBUZ_MIN_REQUEST_INTERVAL_MS", _qobuzSettings.MinRequestIntervalMs)
            },
            squidWtf = new
            {
                quality = GetEnvString(envVars, "SQUIDWTF_QUALITY", _squidWtfSettings.Quality ?? "LOSSLESS"),
                minRequestIntervalMs = GetEnvInt(envVars, "SQUIDWTF_MIN_REQUEST_INTERVAL_MS", _squidWtfSettings.MinRequestIntervalMs)
            },
            musicBrainz = new
            {
                enabled = GetEnvBool(envVars, "MUSICBRAINZ_ENABLED", _musicBrainzSettings.Enabled),
                username = GetEnvString(envVars, "MUSICBRAINZ_USERNAME", _musicBrainzSettings.Username ?? string.Empty),
                password = AdminHelperService.MaskValue(GetEnvString(envVars, "MUSICBRAINZ_PASSWORD", _musicBrainzSettings.Password ?? string.Empty)),
                baseUrl = _musicBrainzSettings.BaseUrl,
                rateLimitMs = _musicBrainzSettings.RateLimitMs
            },
            cache = new
            {
                searchResultsMinutes = GetEnvInt(envVars, "CACHE_SEARCH_RESULTS_MINUTES", _configuration.GetValue<int>("Cache:SearchResultsMinutes", 1)),
                playlistImagesHours = GetEnvInt(envVars, "CACHE_PLAYLIST_IMAGES_HOURS", _configuration.GetValue<int>("Cache:PlaylistImagesHours", 168)),
                spotifyPlaylistItemsHours = GetEnvInt(envVars, "CACHE_SPOTIFY_PLAYLIST_ITEMS_HOURS", _configuration.GetValue<int>("Cache:SpotifyPlaylistItemsHours", 168)),
                spotifyMatchedTracksDays = GetEnvInt(envVars, "CACHE_SPOTIFY_MATCHED_TRACKS_DAYS", _configuration.GetValue<int>("Cache:SpotifyMatchedTracksDays", 30)),
                lyricsDays = GetEnvInt(envVars, "CACHE_LYRICS_DAYS", _configuration.GetValue<int>("Cache:LyricsDays", 14)),
                genreDays = GetEnvInt(envVars, "CACHE_GENRE_DAYS", _configuration.GetValue<int>("Cache:GenreDays", 30)),
                metadataDays = GetEnvInt(envVars, "CACHE_METADATA_DAYS", _configuration.GetValue<int>("Cache:MetadataDays", 7)),
                odesliLookupDays = GetEnvInt(envVars, "CACHE_ODESLI_LOOKUP_DAYS", _configuration.GetValue<int>("Cache:OdesliLookupDays", 60)),
                proxyImagesDays = GetEnvInt(envVars, "CACHE_PROXY_IMAGES_DAYS", _configuration.GetValue<int>("Cache:ProxyImagesDays", 14)),
                transcodeCacheMinutes = GetEnvInt(envVars, "CACHE_TRANSCODE_MINUTES", _configuration.GetValue<int>("Cache:TranscodeCacheMinutes", 60))
            },
            scrobbling = await GetScrobblingSettingsFromEnvAsync()
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
                    localTracksEnabled = _scrobblingSettings.LocalTracksEnabled,
                    syntheticLocalPlayedSignalEnabled = _scrobblingSettings.SyntheticLocalPlayedSignalEnabled,
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
                localTracksEnabled = envVars.TryGetValue("SCROBBLING_LOCAL_TRACKS_ENABLED", out var localTracksEnabled)
                    ? localTracksEnabled.Equals("true", StringComparison.OrdinalIgnoreCase)
                    : _scrobblingSettings.LocalTracksEnabled,
                syntheticLocalPlayedSignalEnabled = envVars.TryGetValue("SCROBBLING_SYNTHETIC_LOCAL_PLAYED_SIGNAL_ENABLED", out var syntheticPlayedSignalEnabled)
                    ? syntheticPlayedSignalEnabled.Equals("true", StringComparison.OrdinalIgnoreCase)
                    : _scrobblingSettings.SyntheticLocalPlayedSignalEnabled,
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
                localTracksEnabled = _scrobblingSettings.LocalTracksEnabled,
                syntheticLocalPlayedSignalEnabled = _scrobblingSettings.SyntheticLocalPlayedSignalEnabled,
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
                message = "Cannot write to .env file. Check file permissions and volume mount."
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update configuration at {Path}", _helperService.GetEnvFilePath());
            return StatusCode(500, new {
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
            return StatusCode(500, new { error = "Failed to import .env file" });
        }
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

    /// <summary>
    /// Gets detailed memory usage statistics for debugging.
    /// </summary>
}
