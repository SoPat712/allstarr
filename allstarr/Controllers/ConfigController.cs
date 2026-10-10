using allstarr.Core.Identity;
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
using allstarr.Core.Capabilities;
using allstarr.Middleware;
using System.Text.Json;
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
    private readonly AppleDownloadSettings _appleMusicSettings;
    private readonly MusicBrainzSettings _musicBrainzSettings;
    private readonly SpotifyImportSettings _spotifyImportSettings;
    private readonly ScrobblingSettings _scrobblingSettings;
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
        IApplicationCache cache)
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
            audio = new { quality = audioQuality },
            playlistsDirectory = RuntimeString("Library:PlaylistsDirectory", fallbackPlaylistsDirectory),
            providers = new
            {
                metadataOrder = RuntimeString("Providers:MetadataOrder", "apple-download,deezer,qobuz"),
                downloadOrder = RuntimeString("Providers:DownloadOrder", "apple-download,deezer,qobuz"),
                streamingOrder = RuntimeString("Providers:StreamingOrder", "apple-download,deezer,qobuz"),
                playlistOrder = RuntimeString("Providers:PlaylistOrder", "spotify,deezer,qobuz"),
                lyricsOrder = RuntimeString("Providers:LyricsOrder", "spotify,apple-download,lrclib"),
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

            return Ok(new
            {
                message = "Runtime configuration updated.",
                updatedKeys = normalized.Select(item => item.LegacyKey).ToArray(),
                requiresRestart = false,
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
        return Ok(await service.GetStatusAsync(cancellationToken));
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

    [HttpPost("config/migration/reset")]
    public async Task<IActionResult> ResetEnvMigration(
        [FromBody] ResetLegacyEnvMigrationRequest request,
        CancellationToken cancellationToken = default)
    {
        var adminCheck = RequireAdministratorForSensitiveOperation("env migration reset");
        if (adminCheck != null)
        {
            return adminCheck;
        }

        try
        {
            var service = HttpContext.RequestServices.GetRequiredService<LegacyEnvMigrationService>();
            await service.ResetPreviewAsync(
                request.PreviewToken ?? string.Empty,
                CreateMigrationActor(),
                cancellationToken);
            return NoContent();
        }
        catch (LegacyEnvMigrationException ex) when (ex.Code == "preview_owner_mismatch")
        {
            return StatusCode(
                StatusCodes.Status403Forbidden,
                new { error = ex.Message, code = ex.Code });
        }
        catch (LegacyEnvMigrationException ex) when (ex.Code == "preview_applied")
        {
            return Conflict(new { error = ex.Message, code = ex.Code });
        }
        catch (LegacyEnvMigrationException ex)
        {
            return BadRequest(new { error = ex.Message, code = ex.Code });
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
        return new(session.SessionId, session.AllstarrUserId, correlationId);
    }

    public sealed class ApplyLegacyEnvMigrationRequest
    {
        public string? PreviewToken { get; set; }
        public string? Revision { get; set; }
        public bool Confirmed { get; set; }
    }

    public sealed class ResetLegacyEnvMigrationRequest
    {
        public string? PreviewToken { get; set; }
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
                         accountSecrets, account.Revision))
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

            if (!statusManager.CanTestCapability(normalizedProvider, capability))
            {
                return BadRequest(new { success = false, error = "This provider capability has no endpoint probe" });
            }

            if (!statusManager.CanTestAccountFreeCapability(normalizedProvider, capability))
            {
                return BadRequest(new { success = false, error = "Select a provider account for this capability" });
            }

            var accountFreeTimer = System.Diagnostics.Stopwatch.StartNew();
            var testedAccountFree = await statusManager.TestAccountFreeProviderCapabilityAsync(
                normalizedProvider,
                capability,
                HttpContext.RequestAborted);
            accountFreeTimer.Stop();
            var accountFreeLatencyMs = accountFreeTimer.ElapsedMilliseconds;
            return Ok(new
            {
                success = testedAccountFree.Health == allstarr.Services.Common.ProviderHealthState.Healthy,
                provider = testedAccountFree.Provider,
                capability = testedAccountFree.Capability,
                health = testedAccountFree.Health.ToString().ToLowerInvariant(),
                latencyMs = accountFreeLatencyMs,
                bars = ConnectivityQuality.Bars(accountFreeLatencyMs, testedAccountFree.Health == allstarr.Services.Common.ProviderHealthState.Healthy, ConnectivityMetric.ApiLatency),
                metric = "api-latency",
                testedAt = testedAccountFree.TestedAt,
                reasonCode = testedAccountFree.ReasonCode
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

        if (capability == ProviderCapabilities.Streaming)
        {
            var media = await MeasureMediaAsync(account);
            return media == null
                ? Conflict(new { error = "A verified administrator identity is required." })
                : MediaTestResult(account, capability, media, []);
        }

        if (string.IsNullOrWhiteSpace(capability))
        {
            var connectionTimer = System.Diagnostics.Stopwatch.StartNew();
            var healthy = await statusManager.TestManagedProviderConnectionAsync(
                normalizedProvider,
                account.Id,
                accountSecrets,
                HttpContext.RequestAborted, account.Revision);
            connectionTimer.Stop();
            var connectionLatencyMs = connectionTimer.ElapsedMilliseconds;
            var failedCapabilities = statusManager.GetAllManagedStatuses(
                    normalizedProvider,
                    account.Id,
                    accountSecrets, account.Revision)
                .Where(item => item.Health == allstarr.Services.Common.ProviderHealthState.Degraded)
                .Select(item => new FailedCapability(item.Capability, item.ReasonCode))
                .ToArray();
            string? mediaReason = null;
            var mediaStatus = statusManager.GetManagedStatus(normalizedProvider, ProviderCapabilities.Streaming,
                account.Id, accountSecrets, account.Revision);
            if (healthy && mediaStatus.IsSupported && mediaStatus.IsEnabled &&
                mediaStatus.Configuration != ProviderConfigurationState.NeedsConfiguration)
            {
                var media = await MeasureMediaAsync(account);
                if (media != null && media.Stage != "track-selection")
                    return MediaTestResult(account, null, media, failedCapabilities);
                mediaReason = media == null ? "media_sample_unavailable" : "media_sample_needs_known_track";
            }

            return Ok(new
            {
                success = true,
                provider = normalizedProvider,
                providerAccountId = account.Id,
                healthy,
                latencyMs = connectionLatencyMs,
                bars = ConnectivityQuality.Bars(connectionLatencyMs, healthy, ConnectivityMetric.ApiLatency),
                metric = "api-latency",
                reasonCode = failedCapabilities.FirstOrDefault()?.ReasonCode ?? mediaReason,
                failedCapabilities
            });
        }

        var current = statusManager.GetManagedStatus(
            normalizedProvider,
            capability,
            account.Id,
            accountSecrets, account.Revision);
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
            HttpContext.RequestAborted, account.Revision);
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

    private sealed record FailedCapability(string Capability, string? ReasonCode);

    private async Task<ProviderCtsDiagnosticResult?> MeasureMediaAsync(ProviderAccountRecord account)
    {
        var session = GetAdminSession();
        if (session?.AllstarrUserId is not { } userId) return null;
        var token = HttpContext.RequestAborted;
        await using var db = await HttpContext.RequestServices
            .GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync(token);
        var identity = await db.Users.AsNoTracking().SingleOrDefaultAsync(item =>
            item.Id == userId && item.Enabled && item.IsAdmin &&
            item.BackendType == session.BackendType.ToLowerInvariant() &&
            item.BackendInstanceId == session.BackendInstanceId && item.BackendPrincipalId == session.UserId, token);
        if (identity == null) return null;
        var actor = new ProviderActorContext(ProviderActorKind.Administrator, userId,
            new ProviderBackendPrincipal(identity.BackendType, identity.BackendInstanceId, identity.BackendPrincipalId));
        var policy = await HttpContext.RequestServices.GetRequiredService<IEffectiveProviderPolicyResolver>()
            .ResolveForUserAsync(userId, token);
        return await HttpContext.RequestServices.GetRequiredService<ProviderCtsDiagnosticRunner>().MeasureAsync(
            actor, account.ProviderId, account.Id, AudioQualityPolicy.RequestedQuality(policy.AudioQuality),
            HttpContext.TraceIdentifier.Length <= 100 ? HttpContext.TraceIdentifier : HttpContext.TraceIdentifier[..100],
            cancellationToken: token);
    }

    private IActionResult MediaTestResult(ProviderAccountRecord account, string? capability,
        ProviderCtsDiagnosticResult result, IReadOnlyList<FailedCapability> failedCapabilities)
    {
        if (result.RetryAfterSeconds.HasValue) Response.Headers.RetryAfter = result.RetryAfterSeconds.Value.ToString();
        return Ok(new
        {
            success = result.Succeeded,
            healthy = result.Succeeded,
            provider = account.ProviderId,
            providerAccountId = account.Id,
            capability,
            health = result.Succeeded ? "healthy" : "degraded",
            latencyMs = result.ClickToStreamMilliseconds,
            bars = result.Bars,
            metric = "cts",
            testedAt = result.MeasuredAt,
            reasonCode = result.Succeeded ? null :
                result.Error is { } error && !error.Contains(' ') ? error : result.Stage,
            seekRung = result.SeekRung,
            sampleBytes = result.SampleBytes,
            failedCapabilities
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
                account.OwnerUserId,
                $"provider-account:{account.ProviderId}:{account.Id:N}",
                AllowShared: account.OwnerUserId == null),
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
}
