using allstarr.Filters;
using allstarr.Core.Identity;
using allstarr.Core.Capabilities;
using allstarr.Models.Admin;
using allstarr.Models.Settings;
using allstarr.Services.Common;
using allstarr.Services.Admin;
using allstarr.Core.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin/ui")]
[ServiceFilter(typeof(AdminPortFilter))]
public class AdminUiController : ControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly SpotifyApiSettings _spotifyApiSettings;
    private readonly DeezerSettings _deezerSettings;
    private readonly QobuzSettings _qobuzSettings;
    private readonly SquidWTFSettings _squidWtfSettings;
    private readonly AppleDownloadSettings _appleMusicSettings;
    private readonly MusicBrainzSettings _musicBrainzSettings;
    private readonly ExtensionManager _extensionManager;
    private readonly ProviderStatusManager _providerStatusManager;
    private readonly ProviderAccountManagementMode _providerAccountManagementMode;
    private readonly IProviderRegistry? _providerRegistry;

    public AdminUiController(
        IConfiguration configuration,
        IOptions<SpotifyApiSettings> spotifyApiSettings,
        IOptions<DeezerSettings> deezerSettings,
        IOptions<QobuzSettings> qobuzSettings,
        IOptions<SquidWTFSettings> squidWtfSettings,
        IOptions<AppleDownloadSettings> appleMusicSettings,
        IOptions<MusicBrainzSettings> musicBrainzSettings,
        ExtensionManager extensionManager,
        ProviderStatusManager providerStatusManager,
        ProviderAccountManagementOptions providerAccountManagementOptions,
        IProviderRegistry? providerRegistry = null)
    {
        _configuration = configuration;
        _spotifyApiSettings = spotifyApiSettings.Value;
        _deezerSettings = deezerSettings.Value;
        _qobuzSettings = qobuzSettings.Value;
        _squidWtfSettings = squidWtfSettings.Value;
        _appleMusicSettings = appleMusicSettings.Value;
        _musicBrainzSettings = musicBrainzSettings.Value;
        _extensionManager = extensionManager;
        _providerStatusManager = providerStatusManager;
        _providerAccountManagementMode = providerAccountManagementOptions.ParseManagementMode();
        _providerRegistry = providerRegistry;
    }

    [HttpGet("schema")]
    public IActionResult GetSchema()
    {
        var activeBackend = _configuration.GetValue<string>("Backend:Type") ?? "Jellyfin";
        if (!IsAdministratorSession())
        {
            return Ok(new AdminUiSchemaResponse
            {
                ActiveBackend = activeBackend,
                ProviderAccountManagementMode = _providerAccountManagementMode.ToString(),
                Providers = BuildProviders().Select(item => new AdminUiProvider
                {
                    Id = item.Id,
                    Name = item.Name,
                    Icon = item.Icon,
                    LogoUrl = item.LogoUrl,
                    AccountSettings = item.AccountSettings
                }).ToList(),
                Routes =
                [
                    Route("sources", "#/sources", "Sources", "sources"),
                    Route("settings", "#/settings", "Settings", "system")
                ]
            });
        }

        var schema = new AdminUiSchemaResponse
        {
            ActiveBackend = activeBackend,
            ProviderAccountManagementMode = _providerAccountManagementMode.ToString(),
            Routes = BuildRoutes(),
            Backends = BuildBackends(),
            Providers = BuildProviders(),
            ProviderSupportMatrix = CurrentProviderSupportCatalog.All.ToList(),
            MultiProviderCategories = ["metadata", "streaming", "download", "playlist", "lyrics", "enrichment"],
            PriorityGroups = BuildPriorityGroups(),
            ConfigSections = BuildConfigSections(),
            ExtensionStore = new AdminUiExtensionStore
            {
                Repositories = [],
                RegistryEnvKey = "",
                StoreEndpoint = "/api/admin/extensions/store",
                InstalledEndpoint = "/api/admin/extensions/installed"
            },
            PluginCapabilities =
            [
                new()
                {
                    Id = "metadata",
                    Label = "Metadata and search",
                    Description = "Enabled metadata extensions participate through the provider router.",
                    Supported = true
                },
                new()
                {
                    Id = "playlist",
                    Label = "Playlist discovery",
                    Description = "Enabled playlist extensions use the same account-scoped provider contract as built-ins.",
                    Supported = true
                },
                new()
                {
                    Id = "download",
                    Label = "Download providers",
                    Description = "Enabled download extensions run as durable, idempotent jobs in a managed workspace.",
                    Supported = true
                },
                new()
                {
                    Id = "lyrics",
                    Label = "Lyrics providers",
                    Description = "Enabled lyrics extensions participate through the typed provider contract.",
                    Supported = true
                }
            ]
        };

        return Ok(schema);
    }

    [HttpGet("provider-summaries")]
    public async Task<IActionResult> GetProviderSummaries(CancellationToken cancellationToken = default)
    {
        if (!IsAdministratorSession())
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Administrator permissions required" });
        }

        var contextFactory = HttpContext.RequestServices.GetRequiredService<IDbContextFactory<AllstarrDbContext>>();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var accounts = await context.ProviderAccounts.AsNoTracking().ToListAsync(cancellationToken);
        var accountIds = accounts.Select(item => item.Id).ToArray();
        var rollups = await context.ProviderHealthRollups.AsNoTracking()
            .Where(item => accountIds.Contains(item.ProviderAccountId))
            .OrderByDescending(item => item.UpdatedAt)
            .Take(1000)
            .ToListAsync(cancellationToken);

        var summaries = accounts
            .GroupBy(item => item.ProviderId, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var ids = group.Select(item => item.Id).ToHashSet();
                var samples = rollups.Where(item => ids.Contains(item.ProviderAccountId)).ToList();
                var sampleCount = samples.Sum(item => item.SampleCount);
                var healthy = samples.Count(item =>
                    string.Equals(item.LastState.ToString(), "Healthy", StringComparison.OrdinalIgnoreCase));
                var failed = samples.Count - healthy;
                return new
                {
                    providerId = group.Key,
                    connectedAccountName = group.Where(item => item.Enabled).Select(item => item.DisplayName).FirstOrDefault(),
                    enabledAccountCount = group.Count(item => item.Enabled),
                    capabilityTotal = samples.Select(item => item.Capability).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    healthyCapabilityCount = healthy,
                    failedCapabilityCount = failed,
                    lastCheckedAt = samples.Count > 0 ? samples.Max(item => item.UpdatedAt) : (DateTimeOffset?)null,
                    successRate = sampleCount > 0
                        ? samples.Sum(item => item.SuccessCount) / (double)sampleCount
                        : (double?)null,
                    p95LatencyMilliseconds = samples.Where(item => item.P95LatencyMilliseconds.HasValue)
                        .Select(item => item.P95LatencyMilliseconds).Max(),
                    lastFailureCode = samples.OrderByDescending(item => item.UpdatedAt)
                        .Select(item => item.LastFailureCode).FirstOrDefault(item => !string.IsNullOrWhiteSpace(item))
                };
            })
            .OrderBy(item => item.providerId)
            .ToList();

        return Ok(new { providers = summaries });
    }

    [HttpGet("activity")]
    public async Task<IActionResult> GetDashboardActivity(
        [FromQuery] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        if (!IsAdministratorSession())
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Administrator permissions required" });
        }

        limit = Math.Clamp(limit, 1, 100);
        var contextFactory = HttpContext.RequestServices.GetRequiredService<IDbContextFactory<AllstarrDbContext>>();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var accounts = await context.ProviderAccounts.AsNoTracking()
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        var jobs = await context.Jobs.AsNoTracking()
            .OrderByDescending(item => item.UpdatedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);
        var health = await context.ProviderHealthSamples.AsNoTracking()
            .OrderByDescending(item => item.ObservedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);

        var activity = new List<AdminUiActivityItem>();
        activity.AddRange(jobs.Select(item => new AdminUiActivityItem(
            item.Id.ToString("N"),
            "job",
            item.ProviderAccountId.HasValue && accounts.TryGetValue(item.ProviderAccountId.Value, out var account)
                ? account.ProviderId
                : "system",
            item.Type,
            item.State.ToString().ToLowerInvariant(),
            item.LastErrorMessage ?? $"{item.AttemptCount} run attempt{(item.AttemptCount == 1 ? "" : "s")}",
            item.UpdatedAt)));
        activity.AddRange(health.Select(item =>
        {
            var provider = accounts.TryGetValue(item.ProviderAccountId, out var account)
                ? account.ProviderId
                : "provider";
            return new AdminUiActivityItem(
                item.Id.ToString("N"),
                "provider_health",
                provider,
                $"{item.Capability} check",
                item.State.ToString().ToLowerInvariant(),
                item.FailureCode ?? (item.LatencyMilliseconds.HasValue ? $"{item.LatencyMilliseconds} ms" : "Connection checked"),
                item.ObservedAt);
        }));

        return Ok(new { items = activity.OrderByDescending(item => item.OccurredAt).Take(limit) });
    }

    private bool IsAdministratorSession() =>
        ControllerContext.HttpContext?.Items.TryGetValue(
            AdminAuthSessionService.HttpContextSessionItemKey,
            out var value) == true &&
        value is AdminAuthSession { IsAdministrator: true };

    private static List<AdminUiRoute> BuildRoutes() =>
    [
        Route("home", "#/home", "Home", "home"),
        Route("library", "#/library", "Library", "library"),
        Route("sources", "#/sources", "Sources", "sources"),
        Route("activity", "#/activity", "Activity", "activity"),
        Route("settings", "#/settings", "Settings", "settings")
    ];

    private static AdminUiRoute Route(string id, string path, string label, string zone) =>
        new() { Id = id, Path = path, Label = label, Zone = zone };

    private static List<AdminUiBackend> BuildBackends() =>
    [
        new()
        {
            Id = "Subsonic",
            Name = "Navidrome / Subsonic",
            Icon = "subsonic",
            ConfigSchema =
            [
                Field("SUBSONIC_URL", "Server URL", "url", "subsonic.url"),
                Field("ENABLE_EXTERNAL_PLAYLISTS", "External playlists", "toggle", "enableExternalPlaylists")
            ]
        },
        new()
        {
            Id = "Jellyfin",
            Name = "Jellyfin",
            Icon = "jellyfin",
            ConfigSchema =
            [
                Field("JELLYFIN_URL", "Server URL", "url", "jellyfin.url"),
                Field("JELLYFIN_API_KEY", "API key", "password", "jellyfin.apiKey", sensitive: true),
                Field("JELLYFIN_USER_ID", "User ID", "text", "jellyfin.userId"),
                Field("JELLYFIN_LIBRARY_ID", "Music library ID", "text", "jellyfin.libraryId")
            ]
        }
    ];

    private List<AdminUiProvider> BuildProviders()
    {
        List<AdminUiProvider> providers =
        [
        new()
        {
            Id = "spotify",
            Name = "Spotify",
            Icon = "spotify",
            Status = ProviderStatus("spotify", _spotifyApiSettings.Enabled
                ? (!string.IsNullOrWhiteSpace(_spotifyApiSettings.SessionCookie) ? "configured" : "needs_config")
                : "disabled"),
            Categories = ["playlist", "lyrics"],
            ConfigSchema =
            [
                Field("SPOTIFY_API_ENABLED", "Enabled", "toggle", "spotifyApi.enabled"),
                Field("SPOTIFY_API_CACHE_DURATION_MINUTES", "Cache minutes", "number", "spotifyApi.cacheDurationMinutes", min: 1),
                Field("SPOTIFY_API_PREFER_ISRC_MATCHING", "Prefer ISRC matching", "toggle", "spotifyApi.preferIsrcMatching")
            ]
        },
        new()
        {
            Id = "apple-download",
            Name = "Apple download",
            Icon = "applemusic",
            Status = ProviderStatus("apple-download", string.IsNullOrWhiteSpace(_appleMusicSettings.BaseUrl) ? "needs_config" : "unknown"),
            Categories = ["metadata", "download", "streaming"],
            ConfigSchema =
            [
                Field("APPLE_DOWNLOAD_URL", "External provider URL", "url", "appleDownload.baseUrl"),
                Field("APPLE_DOWNLOAD_QUALITY", "Quality", "select", "appleDownload.quality", ["alac-16-44", "alac-24-48", "alac-24-96", "alac-24-192"])
            ]
        },
        new()
        {
            Id = "deezer",
            Name = "Deezer",
            Icon = "deezer",
            Status = ProviderStatus("deezer", string.IsNullOrWhiteSpace(_deezerSettings.Arl) ? "needs_config" : "configured"),
            Categories = ["metadata", "download", "streaming", "playlist"],
            ConfigSchema =
            [
                Field("DEEZER_QUALITY", "Quality", "select", "deezer.quality", ["MP3_128", "MP3_320", "FLAC"]),
                Field("DEEZER_MIN_REQUEST_INTERVAL_MS", "Minimum request interval", "number", "deezer.minRequestIntervalMs", min: 0)
            ]
        },
        new()
        {
            Id = "qobuz",
            Name = "Qobuz",
            Icon = "qobuz",
            Status = ProviderStatus("qobuz", string.IsNullOrWhiteSpace(_qobuzSettings.UserAuthToken) ? "needs_config" : "configured"),
            Categories = ["metadata", "download", "streaming", "playlist"],
            ConfigSchema =
            [
                Field("QOBUZ_QUALITY", "Quality", "select", "qobuz.quality", ["MP3_320", "FLAC", "HI_RES"]),
                Field("QOBUZ_MIN_REQUEST_INTERVAL_MS", "Minimum request interval", "number", "qobuz.minRequestIntervalMs", min: 0)
            ]
        },
        new()
        {
            Id = "squidwtf",
            Name = "SquidWTF",
            Icon = "squidwtf",
            Status = ProviderStatus("squidwtf", string.IsNullOrWhiteSpace(_squidWtfSettings.Quality) ? "unknown" : "configured"),
            Categories = ["metadata"],
            ConfigSchema =
            [
                Field("SQUIDWTF_QUALITY", "Quality", "select", "squidWtf.quality", ["LOW", "HIGH", "LOSSLESS"]),
                Field("SQUIDWTF_MIN_REQUEST_INTERVAL_MS", "Minimum request interval", "number", "squidWtf.minRequestIntervalMs", min: 0)
            ]
        },
        new()
        {
            Id = "musicbrainz",
            Name = "MusicBrainz enrichment",
            Icon = "musicbrainz",
            Status = _musicBrainzSettings.Enabled ? "configured" : "disabled",
            Categories = ["enrichment"],
            Notes = ["Genres only", "Optional enrichment"],
            ConfigSchema =
            [
                Field("MUSICBRAINZ_ENABLED", "Enabled", "toggle", "musicBrainz.enabled"),
                Field("MUSICBRAINZ_USERNAME", "Username", "text", "musicBrainz.username"),
                Field("MUSICBRAINZ_PASSWORD", "Password", "password", "musicBrainz.password", sensitive: true)
            ]
        },
        new()
        {
            Id = "lyricsplus",
            Name = "LyricsPlus",
            Icon = "lyrics",
            Status = "available",
            Categories = ["lyrics"]
        },
        new()
        {
            Id = "lrclib",
            Name = "LRCLib",
            Icon = "lyrics",
            Status = "available",
            Categories = ["lyrics"]
        }
        ];

        var runtimeStatuses = _providerStatusManager.GetAllStatuses();
        if (_providerRegistry != null)
        {
            providers.AddRange(_providerRegistry.Providers
                .Where(item => item.Origin == ProviderOrigin.Extension)
                .Select(item => new AdminUiProvider
                {
                    Id = item.Id,
                    Name = item.DisplayName,
                    Icon = "extension",
                    LogoUrl = item.Branding == null ? null : $"/api/admin/extensions/providers/{Uri.EscapeDataString(item.Id)}/icon",
                    Status = "unknown",
                    Categories = item.Capabilities.Where(capability => capability.HasUsableImplementation)
                        .Select(capability => capability.Capability.ToString().ToLowerInvariant()).ToList(),
                    Notes = [$"Extension SDK {item.SdkVersion}"],
                    AccountSettings = item.Settings.Select(setting => new AdminUiConfigField
                    {
                        Key = setting.Key,
                        Label = setting.Label,
                        Type = setting.ValueKind switch
                        {
                            ProviderSettingValueKind.Secret => "password",
                            ProviderSettingValueKind.Boolean => "toggle",
                            ProviderSettingValueKind.Integer => "number",
                            ProviderSettingValueKind.Choice => "select",
                            _ => "text"
                        },
                        Sensitive = setting.ValueKind == ProviderSettingValueKind.Secret,
                        Required = setting.Required,
                        Options = setting.Choices.ToList(),
                        Ownership = "provider-account"
                    }).ToList()
                }));
        }
        foreach (var provider in providers)
        {
            var statuses = runtimeStatuses
                .Where(status => status.Provider.Equals(provider.Id, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (statuses.Count == 0)
            {
                continue;
            }

            provider.RuntimeCapabilities = statuses.Select(ToAdminRuntimeCapability).ToList();
            provider.Status = AggregateProviderStatus(statuses);
        }

        return providers;
    }

    private AdminUiProviderRuntimeCapability ToAdminRuntimeCapability(ProviderRuntimeStatus status) =>
        new()
        {
            Id = status.Capability,
            Supported = status.IsSupported,
            Configuration = !status.IsSupported ? "unsupported" : status.Configuration switch
            {
                ProviderConfigurationState.NotRequired => "not_required",
                ProviderConfigurationState.Configured => "configured",
                _ => "needs_configuration"
            },
            Health = status.Health.ToString().ToLowerInvariant(),
            Ready = status.IsReady,
            CanAttempt = status.CanAttempt,
            CanTest = _providerStatusManager.CanTestCapability(status.Provider, status.Capability),
            TestedAt = status.TestedAt,
            ReasonCode = status.ReasonCode
        };

    private static string AggregateProviderStatus(IReadOnlyList<ProviderRuntimeStatus> statuses)
    {
        if (statuses.All(status => !status.IsSupported))
        {
            return "unsupported";
        }

        if (statuses.All(status => !status.IsEnabled))
        {
            return "disabled";
        }

        if (statuses.Any(status => status.Health == Services.Common.ProviderHealthState.Testing))
        {
            return "testing";
        }

        if (statuses.Any(status => status.Health == Services.Common.ProviderHealthState.Degraded))
        {
            return "degraded";
        }

        var enabledSupported = statuses.Where(status => status.IsEnabled && status.IsSupported).ToList();
        if (enabledSupported.Count > 0 && enabledSupported.All(status =>
                status.Configuration == ProviderConfigurationState.NeedsConfiguration || status.IsReady) &&
            enabledSupported.Any(status => status.IsReady))
        {
            return "healthy";
        }

        if (statuses.All(status => !status.CanAttempt))
        {
            return "needs_config";
        }

        if (statuses.Any(status => status.Configuration == ProviderConfigurationState.NeedsConfiguration))
        {
            return "partial_config";
        }

        return "available";
    }

    private List<AdminUiPriorityGroup> BuildPriorityGroups() =>
    [
        Priority("metadata", "Metadata search priority", "MULTI_PROVIDER_METADATA_ORDER", "MULTI_PROVIDER_ENABLED_SEARCH",
            "apple-download,deezer,qobuz"),
        Priority("download", "Download priority", "MULTI_PROVIDER_DOWNLOAD_ORDER", null,
            "apple-download,deezer,qobuz"),
        Priority("streaming", "Streaming priority", "MULTI_PROVIDER_STREAMING_ORDER", null,
            "apple-download,deezer,qobuz"),
        Priority("playlist", "Playlist discovery priority", "MULTI_PROVIDER_PLAYLIST_ORDER", "MULTI_PROVIDER_ENABLED_PLAYLIST",
            "spotify,deezer,qobuz"),
        Priority("lyrics", "Lyrics priority", "MULTI_PROVIDER_LYRICS_ORDER", null,
            "spotify,lyricsplus,lrclib")
    ];

    private AdminUiPriorityGroup Priority(
        string id,
        string label,
        string envKey,
        string? enabledEnvKey,
        string fallback)
    {
        var value = _configuration[envKey] ?? fallback;
        return new AdminUiPriorityGroup
        {
            Id = id,
            Label = label,
            EnvKey = envKey,
            EnabledEnvKey = enabledEnvKey,
            Providers = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(p => p.ToLowerInvariant())
                .Where(p => id == "metadata" || p != "squidwtf")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
    }

    private string ProviderStatus(string id, string configuredStatus)
    {
        var disabled = (_configuration["MULTI_PROVIDER_DISABLED_PROVIDERS"] ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(p => p.Equals(id, StringComparison.OrdinalIgnoreCase));
        return disabled ? "disabled" : configuredStatus;
    }

    private static List<AdminUiConfigSection> BuildConfigSections() =>
    [
        Section("general", "General",
        [
            DeploymentField("BACKEND_TYPE", "Backend", "select", "backendType", ["Jellyfin", "Subsonic"]),
            Field("STORAGE_MODE", "Storage mode", "select", "library.storageMode", ["Permanent", "Cache"]),
            Field("DOWNLOAD_MODE", "Download mode", "select", "library.downloadMode", ["Track", "Album"]),
            Field("EXPLICIT_FILTER", "Explicit filter", "select", "explicitFilter", ["All", "ExplicitOnly", "CleanOnly"]),
            DeploymentField("REDIS_ENABLED", "Valkey cache", "toggle", "redisEnabled")
        ]),
        Section("paths", "Library paths",
        [
            DeploymentField("LIBRARY_DOWNLOAD_PATH", "Download path", "text", "library.downloadPath"),
            DeploymentField("LIBRARY_KEPT_PATH", "Kept downloads path", "text", "library.keptPath"),
            Field("PLAYLISTS_DIRECTORY", "Playlists directory", "text", "playlistsDirectory")
        ]),
        Section("cache", "Cache",
        [
            Field("CACHE_DURATION_HOURS", "Track cache hours", "number", "library.cacheDurationHours", min: 1),
            Field("CACHE_SEARCH_RESULTS_MINUTES", "Search results minutes", "number", "cache.searchResultsMinutes", min: 1),
            Field("CACHE_SPOTIFY_PLAYLIST_ITEMS_HOURS", "Spotify playlist items hours", "number", "cache.spotifyPlaylistItemsHours", min: 1),
            Field("CACHE_SPOTIFY_MATCHED_TRACKS_DAYS", "Matched tracks days", "number", "cache.spotifyMatchedTracksDays", min: 1),
            Field("CACHE_LYRICS_DAYS", "Lyrics days", "number", "cache.lyricsDays", min: 1),
            Field("CACHE_METADATA_DAYS", "Metadata days", "number", "cache.metadataDays", min: 1),
            Field("CACHE_PROXY_IMAGES_DAYS", "Proxy images days", "number", "cache.proxyImagesDays", min: 1),
            Field("CACHE_TRANSCODE_MINUTES", "Transcode cache minutes", "number", "cache.transcodeCacheMinutes", min: 1)
        ]),
        Section("network", "Network and security",
        [
            DeploymentField("ADMIN_BIND_ANY_IP", "Bind admin on all interfaces", "toggle", "admin.bindAnyIp"),
            DeploymentField("ADMIN_TRUSTED_SUBNETS", "Trusted admin subnets", "text", "admin.trustedSubnets"),
            DeploymentField("DEBUG_LOG_ALL_REQUESTS", "Request usage logging", "toggle", "debug.logAllRequests")
        ]),
        Section("spotify-import", "Spotify import",
        [
            Field("SPOTIFY_IMPORT_ENABLED", "Enabled", "toggle", "spotifyImport.enabled"),
            Field("SPOTIFY_IMPORT_MATCHING_INTERVAL_HOURS", "Matching interval hours", "number", "spotifyImport.matchingIntervalHours", min: 0)
        ])
    ];

    private static AdminUiConfigSection Section(string id, string label, List<AdminUiConfigField> fields) =>
        new() { Id = id, Label = label, Fields = fields };

    private static AdminUiConfigField Field(
        string key,
        string label,
        string type,
        string? valuePath,
        List<string>? options = null,
        string? placeholder = null,
        bool sensitive = false,
        bool requiresRestart = false,
        string ownership = "durable",
        bool readOnly = false,
        string? helpText = null,
        int? min = null,
        int? max = null) =>
        new()
        {
            Key = key,
            Label = label,
            Type = type,
            ValuePath = valuePath,
            Options = options ?? [],
            Placeholder = placeholder,
            Sensitive = sensitive,
            RequiresRestart = requiresRestart,
            Ownership = ownership,
            ReadOnly = readOnly,
            HelpText = helpText,
            Min = min,
            Max = max
        };

    private static AdminUiConfigField DeploymentField(
        string key,
        string label,
        string type,
        string? valuePath,
        List<string>? options = null) =>
        Field(
            key,
            label,
            type,
            valuePath,
            options,
            ownership: "deployment",
            readOnly: true,
            helpText: "Edit in Compose/.env and recreate the container to apply this deployment-owned value.");
}

public sealed record AdminUiActivityItem(
    string Id,
    string Kind,
    string Source,
    string Label,
    string State,
    string Detail,
    DateTimeOffset OccurredAt);
