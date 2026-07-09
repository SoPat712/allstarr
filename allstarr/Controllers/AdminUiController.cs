using allstarr.Filters;
using allstarr.Models.Admin;
using allstarr.Models.Settings;
using allstarr.Services.Common;
using Microsoft.AspNetCore.Mvc;
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
    private readonly AppleMusicSettings _appleMusicSettings;
    private readonly MusicBrainzSettings _musicBrainzSettings;
    private readonly ExtensionManager _extensionManager;

    public AdminUiController(
        IConfiguration configuration,
        IOptions<SpotifyApiSettings> spotifyApiSettings,
        IOptions<DeezerSettings> deezerSettings,
        IOptions<QobuzSettings> qobuzSettings,
        IOptions<SquidWTFSettings> squidWtfSettings,
        IOptions<AppleMusicSettings> appleMusicSettings,
        IOptions<MusicBrainzSettings> musicBrainzSettings,
        ExtensionManager extensionManager)
    {
        _configuration = configuration;
        _spotifyApiSettings = spotifyApiSettings.Value;
        _deezerSettings = deezerSettings.Value;
        _qobuzSettings = qobuzSettings.Value;
        _squidWtfSettings = squidWtfSettings.Value;
        _appleMusicSettings = appleMusicSettings.Value;
        _musicBrainzSettings = musicBrainzSettings.Value;
        _extensionManager = extensionManager;
    }

    [HttpGet("schema")]
    public IActionResult GetSchema()
    {
        var activeBackend = _configuration.GetValue<string>("Backend:Type") ?? "Jellyfin";
        var repositories = _extensionManager.GetConfiguredRepositories();
        var installedExtensionCount = _extensionManager.GetActiveExtensions().Count;

        var schema = new AdminUiSchemaResponse
        {
            ActiveBackend = activeBackend,
            Routes = BuildRoutes(),
            Backends = BuildBackends(),
            Providers = BuildProviders(installedExtensionCount),
            MultiProviderCategories = ["metadata", "download", "playlist", "lyrics"],
            PriorityGroups = BuildPriorityGroups(),
            ConfigSections = BuildConfigSections(),
            ExtensionStore = new AdminUiExtensionStore
            {
                Repositories = repositories,
                RegistryEnvKey = "EXTENSION_REPOSITORIES",
                StoreEndpoint = "/api/admin/extensions/store",
                InstalledEndpoint = "/api/admin/extensions/installed"
            },
            PluginCapabilities =
            [
                new()
                {
                    Id = "metadata",
                    Label = "Metadata and search",
                    Description = "Installed extensions participate in multi-provider song, album, and artist search.",
                    Supported = true
                },
                new()
                {
                    Id = "playlist",
                    Label = "Playlist discovery",
                    Description = "Built-in providers expose playlist search and track expansion through the provider contract.",
                    Supported = true
                },
                new()
                {
                    Id = "download",
                    Label = "Download providers",
                    Description = "Download execution is currently limited to built-in provider services.",
                    Supported = false
                },
                new()
                {
                    Id = "lyrics",
                    Label = "Lyrics providers",
                    Description = "Lyrics are routed through the built-in Spotify sidecar, LyricsPlus, and LRCLib orchestrator.",
                    Supported = false
                }
            ]
        };

        return Ok(schema);
    }

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
                Field("MUSIC_SERVICE", "Default music service", "select", "musicService", ["SquidWTF", "AppleMusic", "Deezer", "Qobuz"]),
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

    private List<AdminUiProvider> BuildProviders(int installedExtensionCount) =>
    [
        new()
        {
            Id = "spotify",
            Name = "Spotify",
            Icon = "spotify",
            Status = _spotifyApiSettings.Enabled
                ? (!string.IsNullOrWhiteSpace(_spotifyApiSettings.SessionCookie) ? "configured" : "needs_config")
                : "disabled",
            Categories = ["metadata", "playlist", "lyrics"],
            ConfigSchema =
            [
                Field("SPOTIFY_API_ENABLED", "Enabled", "toggle", "spotifyApi.enabled"),
                Field("SPOTIFY_API_SESSION_COOKIE", "sp_dc session cookie", "password", "spotifyApi.sessionCookie", sensitive: true),
                Field("SPOTIFY_API_CACHE_DURATION_MINUTES", "Cache minutes", "number", "spotifyApi.cacheDurationMinutes", min: 1),
                Field("SPOTIFY_API_PREFER_ISRC_MATCHING", "Prefer ISRC matching", "toggle", "spotifyApi.preferIsrcMatching")
            ]
        },
        new()
        {
            Id = "applemusic",
            Name = "Apple Music",
            Icon = "applemusic",
            Status = string.IsNullOrWhiteSpace(_appleMusicSettings.BaseUrl) ? "needs_config" : "needs_login",
            Categories = ["metadata", "download", "streaming", "playlist"],
            ConfigSchema =
            [
                Field("APPLE_MUSIC_AIO_URL", "Sidecar URL", "url", "appleMusic.baseUrl"),
                Field("APPLE_MUSIC_QUALITY", "Quality", "select", "appleMusic.quality", ["alac-16-44", "alac-24-48", "alac-24-96", "alac-24-192"])
            ]
        },
        new()
        {
            Id = "deezer",
            Name = "Deezer",
            Icon = "deezer",
            Status = string.IsNullOrWhiteSpace(_deezerSettings.Arl) ? "needs_config" : "configured",
            Categories = ["metadata", "download", "streaming", "playlist"],
            ConfigSchema =
            [
                Field("DEEZER_ARL", "ARL cookie", "password", "deezer.arl", sensitive: true),
                Field("DEEZER_ARL_FALLBACK", "Fallback ARL cookie", "password", "deezer.arlFallback", sensitive: true),
                Field("DEEZER_QUALITY", "Quality", "select", "deezer.quality", ["MP3_128", "MP3_320", "FLAC"]),
                Field("DEEZER_MIN_REQUEST_INTERVAL_MS", "Minimum request interval", "number", "deezer.minRequestIntervalMs", min: 0)
            ]
        },
        new()
        {
            Id = "qobuz",
            Name = "Qobuz",
            Icon = "qobuz",
            Status = string.IsNullOrWhiteSpace(_qobuzSettings.UserAuthToken) ? "needs_config" : "configured",
            Categories = ["metadata", "download", "streaming", "playlist"],
            ConfigSchema =
            [
                Field("QOBUZ_USER_AUTH_TOKEN", "User auth token", "password", "qobuz.userAuthToken", sensitive: true),
                Field("QOBUZ_USER_ID", "User ID", "text", "qobuz.userId"),
                Field("QOBUZ_QUALITY", "Quality", "select", "qobuz.quality", ["MP3_320", "FLAC", "HI_RES"]),
                Field("QOBUZ_MIN_REQUEST_INTERVAL_MS", "Minimum request interval", "number", "qobuz.minRequestIntervalMs", min: 0)
            ]
        },
        new()
        {
            Id = "squidwtf",
            Name = "SquidWTF",
            Icon = "squidwtf",
            Status = string.IsNullOrWhiteSpace(_squidWtfSettings.Quality) ? "unknown" : "configured",
            Categories = ["metadata", "download", "streaming", "playlist"],
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
            Categories = ["metadata"],
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
            Id = "extensions",
            Name = "Installed extensions",
            Icon = "extension",
            Status = installedExtensionCount > 0 ? "configured" : "available",
            Categories = ["metadata"],
            Notes = [$"{installedExtensionCount} installed"]
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

    private List<AdminUiPriorityGroup> BuildPriorityGroups() =>
    [
        Priority("metadata", "Metadata search priority", "MULTI_PROVIDER_METADATA_ORDER", "MULTI_PROVIDER_ENABLED_SEARCH",
            "spotify,applemusic,deezer,qobuz,squidwtf"),
        Priority("download", "Download priority", "MULTI_PROVIDER_DOWNLOAD_ORDER", null,
            "applemusic,deezer,qobuz,squidwtf"),
        Priority("streaming", "Streaming priority", "MULTI_PROVIDER_STREAMING_ORDER", null,
            "applemusic,deezer,qobuz,squidwtf"),
        Priority("playlist", "Playlist discovery priority", "MULTI_PROVIDER_PLAYLIST_ORDER", "MULTI_PROVIDER_ENABLED_PLAYLIST",
            "spotify,applemusic,deezer,qobuz,squidwtf"),
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
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
    }

    private static List<AdminUiConfigSection> BuildConfigSections() =>
    [
        Section("general", "General",
        [
            Field("BACKEND_TYPE", "Backend", "select", "backendType", ["Jellyfin", "Subsonic"]),
            Field("MUSIC_SERVICE", "Primary music service", "select", "musicService", ["SquidWTF", "AppleMusic", "Deezer", "Qobuz"]),
            Field("STORAGE_MODE", "Storage mode", "select", "library.storageMode", ["Permanent", "Cache"]),
            Field("DOWNLOAD_MODE", "Download mode", "select", "library.downloadMode", ["Track", "Album"]),
            Field("EXPLICIT_FILTER", "Explicit filter", "select", "explicitFilter", ["All", "ExplicitOnly", "CleanOnly"]),
            Field("REDIS_ENABLED", "Redis", "toggle", "redisEnabled")
        ]),
        Section("paths", "Library paths",
        [
            Field("LIBRARY_DOWNLOAD_PATH", "Download path", "text", "library.downloadPath"),
            Field("LIBRARY_KEPT_PATH", "Kept downloads path", "text", "library.keptPath"),
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
            Field("ADMIN_BIND_ANY_IP", "Bind admin on all interfaces", "toggle", "admin.bindAnyIp"),
            Field("ADMIN_TRUSTED_SUBNETS", "Trusted admin subnets", "text", "admin.trustedSubnets"),
            Field("DEBUG_LOG_ALL_REQUESTS", "Request usage logging", "toggle", "debug.logAllRequests")
        ]),
        Section("spotify-import", "Spotify import",
        [
            Field("SPOTIFY_IMPORT_ENABLED", "Enabled", "toggle", "spotifyImport.enabled"),
            Field("SPOTIFY_IMPORT_MATCHING_INTERVAL_HOURS", "Matching interval hours", "number", "spotifyImport.matchingIntervalHours", min: 1)
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
        bool requiresRestart = true,
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
            Min = min,
            Max = max
        };
}
