using allstarr.Core.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using allstarr.Models.Settings;
using allstarr.Models.Admin;
using allstarr.Filters;
using allstarr.Services.Admin;
using allstarr.Services.Common;
using allstarr.Services.Spotify;
using allstarr.Core.Configuration;
using allstarr.Core.Settings;
using allstarr.Core.Capabilities;
using allstarr.Middleware;

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
    private readonly AppleDownloadSettings _appleMusicSettings;
    private readonly MusicBrainzSettings _musicBrainzSettings;
    private readonly SpotifyImportSettings _spotifyImportSettings;
    private readonly ScrobblingSettings _scrobblingSettings;
    private readonly PlaybackSettings _playbackSettings;
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
        IOptions<AppleDownloadSettings> appleMusicSettings,
        IOptions<MusicBrainzSettings> musicBrainzSettings,
        IOptions<SpotifyImportSettings> spotifyImportSettings,
        IOptions<ScrobblingSettings> scrobblingSettings,
        SpotifySessionCookieService spotifySessionCookieService,
        IApplicationCache cache,
        IOptions<PlaybackSettings>? playbackSettings = null)
    {
        _logger = logger;
        _configuration = configuration;
        _spotifyApiSettings = spotifyApiSettings.Value;
        _jellyfinSettings = jellyfinSettings.Value;
        _subsonicSettings = subsonicSettings.Value;
        _deezerSettings = deezerSettings.Value;
        _qobuzSettings = qobuzSettings.Value;
        _appleMusicSettings = appleMusicSettings.Value;
        _musicBrainzSettings = musicBrainzSettings.Value;
        _spotifyImportSettings = spotifyImportSettings.Value;
        _scrobblingSettings = scrobblingSettings.Value;
        _playbackSettings = playbackSettings?.Value ?? new();
        _spotifySessionCookieService = spotifySessionCookieService;
        _cache = cache;
    }

    [HttpGet("config")]
    public async Task<IActionResult> GetConfig()
    {
        IReadOnlyDictionary<string, EffectiveRuntimeSetting> runtimeSettings =
            new Dictionary<string, EffectiveRuntimeSetting>(StringComparer.OrdinalIgnoreCase);
        if (GetAdminSession()?.AllstarrUserId is not null &&
            HttpContext.RequestServices.GetService<IDurableRuntimeSettings>() is { } settings)
        {
            runtimeSettings = await settings.GetManyAsync(RuntimeSettingCatalog.Definitions.Keys);
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

        var fallbackExplicitFilter = Enum.TryParse<ExplicitFilter>(
            _configuration[$"{backendType}:ExplicitFilter"], true, out var configuredFilter)
            ? configuredFilter.ToString() : ExplicitFilter.All.ToString();
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
        runtimeSettings.TryGetValue(AudioQualityPolicy.SettingKey, out var sharedAudioQuality);
        var audioQuality = sharedAudioQuality != null &&
                           (sharedAudioQuality.Origin == RuntimeSettingOrigin.Durable ||
                            _configuration[AudioQualityPolicy.SettingKey] != null)
            ? sharedAudioQuality.NormalizedValue
            : AudioQualityPolicy.FromProviderCeilings(
                _appleMusicSettings.Quality, _deezerSettings.Quality, _qobuzSettings.Quality);

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
            providerAccounts = new
            {
                listenersCanConnectOwnAccounts = RuntimeBool(ProviderAccountOptions.ListenerConnectionsKey,
                    _configuration.GetValue(ProviderAccountOptions.ListenerConnectionsKey, true))
            },
            explicitFilter = RuntimeString("Library:ExplicitFilter", fallbackExplicitFilter),
            enableExternalPlaylists = RuntimeBool("Library:EnableExternalPlaylists", fallbackEnableExternalPlaylists),
            matching = new
            {
                localPreferencePercent = RuntimeInt("Matching:LocalPreferencePercent", 7)
            },
            playback = new
            {
                lastFmSimilarEnabled = RuntimeBool(
                    "Playback:LastFmSimilarEnabled", _playbackSettings.LastFmSimilarEnabled),
                listenBrainzSimilarEnabled = RuntimeBool(
                    "Playback:ListenBrainzSimilarEnabled", _playbackSettings.ListenBrainzSimilarEnabled)
            },
            audio = new { quality = audioQuality },
            playlistsDirectory = RuntimeString("Library:PlaylistsDirectory", fallbackPlaylistsDirectory),
            providers = new
            {
                metadataOrder = RuntimeString("Providers:MetadataOrder", "apple-download,deezer,qobuz"),
                downloadOrder = RuntimeString("Providers:DownloadOrder", "apple-download,deezer,qobuz"),
                streamingOrder = RuntimeString("Providers:StreamingOrder", "apple-download,deezer,qobuz"),
                playlistOrder = RuntimeString("Providers:PlaylistOrder", "spotify,deezer,qobuz"),
                lyricsOrder = RuntimeString("Providers:LyricsOrder", allstarr.Core.Settings.ProviderOrderPolicyCatalog.Find(ProviderCapabilityKind.Lyrics)!.DefaultValue),
                enabledSearch = RuntimeString("Providers:EnabledSearch", "deezer,qobuz"),
                enabledPlaylist = RuntimeString("Providers:EnabledPlaylist", "spotify"),
                disabledProviders = RuntimeString("Providers:Disabled", string.Empty),
            },
            debug = new
            {
                logAllRequests = RuntimeBool("Debug:LogAllRequests", _configuration.GetValue<bool>("Debug:LogAllRequests", false)),
                redactSensitiveRequestValues = true
            },
            admin = new
            {
                bindAnyIp = AdminNetworkBindingPolicy.ShouldBindAdminAnyIp(_configuration),
                trustedSubnets = _configuration.GetValue<string>("Admin:TrustedSubnets") ?? string.Empty,
                redactSensitiveValues = _configuration.GetValue<bool>("Admin:RedactSensitiveValues", false)
            },
            lyricsPlus = new { baseUrl = RuntimeString("LyricsPlus:BaseUrl", _configuration["LyricsPlus:BaseUrl"] ?? string.Empty) },
            spotifyApi = new
            {
                enabled = RuntimeBool("SpotifyApi:Enabled", _spotifyApiSettings.Enabled),
                sessionCookie = AdminHelperService.MaskValue(effectiveSessionCookie, showLast: 8),
                sessionCookieSetDate = effectiveCookieSetDate ?? string.Empty,
                usingGlobalFallback = cookieStatus.UsingGlobalFallback,
                cacheDurationMinutes = RuntimeInt("SpotifyApi:CacheDurationMinutes", _spotifyApiSettings.CacheDurationMinutes),
                rateLimitDelayMs = RuntimeInt("SpotifyApi:RateLimitDelayMs", _spotifyApiSettings.RateLimitDelayMs),
                pathfinderEndpoint = RuntimeString("SpotifyApi:PathfinderEndpoint", _spotifyApiSettings.PathfinderEndpoint),
                libraryQueryHash = RuntimeString("SpotifyApi:LibraryQueryHash", _spotifyApiSettings.LibraryQueryHash),
                playlistQueryHash = RuntimeString("SpotifyApi:PlaylistQueryHash", _spotifyApiSettings.PlaylistQueryHash),
                likedSongsQueryHash = RuntimeString("SpotifyApi:LikedSongsQueryHash", _spotifyApiSettings.LikedSongsQueryHash),
                totpSecretsUrl = RuntimeString("SpotifyApi:TotpSecretsUrl", _spotifyApiSettings.TotpSecretsUrl),
                tokenEndpoint = RuntimeString("SpotifyApi:TokenEndpoint", _spotifyApiSettings.TokenEndpoint),
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
                sourceId = RuntimeString("MusicBrainz:SourceId", _musicBrainzSettings.SourceId),
                baseUrl = RuntimeString("MusicBrainz:BaseUrl", _musicBrainzSettings.BaseUrl),
                rateLimitMs = RuntimeInt("MusicBrainz:RateLimitMs", _musicBrainzSettings.RateLimitMs),
                authorizedUserAgentOverride = RuntimeString("MusicBrainz:AuthorizedUserAgentOverride", _musicBrainzSettings.AuthorizedUserAgentOverride ?? "")
            },
            operatorSettings = OperatorSettingPolicy.Definitions.ToDictionary(
                item => item.Key, item => runtimeSettings.TryGetValue(item.Key, out var setting) ? setting.Value : (object)(_configuration[item.Key] ?? item.DefaultValue ?? "")),
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
                mediaMaximumMegabytes = RuntimeInt("Cache:MediaMaximumMegabytes", _configuration.GetValue<int>("Cache:MediaMaximumMegabytes", 512)),
                mediaMaximumEntryMegabytes = RuntimeInt("Cache:MediaMaximumEntryMegabytes", _configuration.GetValue<int>("Cache:MediaMaximumEntryMegabytes", 16)),
                mediaCleanupFileLimit = RuntimeInt("Cache:MediaCleanupFileLimit", _configuration.GetValue<int>("Cache:MediaCleanupFileLimit", 10_000)),
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
            if (session?.AllstarrUserId is null)
            {
                return Conflict(new
                {
                    error = "The administrator session is not linked to an Allstarr user.",
                    code = "user_required"
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
            var current = await settings.GetManyAsync(normalized.Select(item => item.DurableKey));
            var writes = normalized.Select(item =>
            {
                var existing = current[item.DurableKey];
                return new RuntimeSettingWrite(
                    item.DurableKey,
                    item.Value,
                    existing.Origin == RuntimeSettingOrigin.Durable ? existing.Revision : null);
            }).ToArray();
            var result = await settings.ApplyBatchAsync(
                writes,
                "admin-ui",
                session.AllstarrUserId,
                HttpContext.RequestAborted);
            var cacheEntriesInvalidated = normalized.Any(item =>
                !item.DurableKey.StartsWith("Scrobbling:", StringComparison.OrdinalIgnoreCase) &&
                !item.DurableKey.StartsWith("WebUi:", StringComparison.OrdinalIgnoreCase))
                ? await _cache.PurgeAllAsync()
                : 0;

            var requiresRestart = normalized.Any(item => RuntimeSettingCatalog.Require(item.DurableKey).RequiresRestart);
            if (requiresRestart && HttpContext.RequestServices.GetService<IHostApplicationLifetime>() is { } lifetime)
                AdminProcessRestart.Schedule(HttpContext, lifetime);
            return Ok(new
            {
                message = requiresRestart ? "Settings saved. Allstarr will restart." : "Runtime configuration updated.",
                updatedKeys = normalized.Select(item => item.LegacyKey).ToArray(),
                requiresRestart,
                changeVersion = result.ChangeVersion,
                cacheEntriesInvalidated,
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

    [HttpPost("cache/clear")]
    public async Task<IActionResult> ClearCache()
    {
        _logger.LogDebug("Cache clear requested from admin UI");

        var clearedCacheEntries = await _cache.PurgeAllAsync();
        _logger.LogInformation("Cache cleared: {Entries} disposable entries", clearedCacheEntries);

        return Ok(new
        {
            message = "Cache cleared successfully",
            cacheEntriesDeleted = clearedCacheEntries
        });
    }

    private AdminAuthSession? GetAdminSession() =>
        HttpContext.Items.TryGetValue(AdminAuthSessionService.HttpContextSessionItemKey, out var value)
            ? value as AdminAuthSession
            : null;

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

}
