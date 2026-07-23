using allstarr.Models.Settings;
using allstarr.Services;
using allstarr.Services.Deezer;
using allstarr.Services.Qobuz;
using allstarr.Services.SquidWTF;
using allstarr.Services.AppleMusic;
using allstarr.Services.Local;
using allstarr.Services.Validation;
using allstarr.Services.Subsonic;
using allstarr.Core.Protocols.Subsonic;
using allstarr.Services.Jellyfin;
using allstarr.Services.Common;
using allstarr.Services.Lyrics;
using allstarr.Services.Scrobbling;
using allstarr.Services.Spotify;
using allstarr.Middleware;
using allstarr.Filters;
using allstarr.Core.Storage;
using allstarr.Core.Secrets;
using allstarr.Core.Identity;
using allstarr.Core.Jobs;
using allstarr.Core.Health;
using allstarr.Core.Capabilities;
using allstarr.Core.Matching;
using allstarr.Core.Operations;
using allstarr.Core.Providers.Deezer;
using allstarr.Core.Providers.Spotify;
using allstarr.Core.Providers.AppleMusicKit;
using allstarr.Core.Providers.AppleDownload;
using allstarr.Core.Providers;
using allstarr.Core.Protocols;
using allstarr.Core.Protocols.Jellyfin;
using allstarr.Core.Playlists;
using allstarr.Core.Extensions;
using allstarr.Core.Enrichment;
using allstarr.Core.Favorites;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Intelligence;
using allstarr.Core.Downloads;
using allstarr.Core.Playback;
using allstarr.Core.Settings;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Http;
using System.Net;
using System.IO;

var builder = WebApplication.CreateBuilder(args);
RuntimeEnvConfiguration.AddDotEnvOverrides(builder.Configuration, builder.Environment);
if (StorageOperatorCommand.IsStorageCommand(args))
{
    builder.Configuration["Logging:LogLevel:Default"] = "Warning";
}
builder.Logging.ClearProviders();
builder.Logging.AddProvider(new RedactingConsoleLoggerProvider(builder.Configuration));
builder.Services.AddDurableStorage(builder.Configuration, builder.Environment);
builder.Services.AddDurableRuntimeSettings();
builder.Services.AddEncryptedSecretStore(builder.Configuration);
builder.Services.AddSingleton<allstarr.Core.Configuration.LegacyEnvMigrationService>();
if (StorageOperatorCommand.IsStorageCommand(args))
{
    Environment.ExitCode = await StorageOperatorCommand.RunAsync(
        builder.Services,
        args,
        Console.Out,
        Console.Error);
    return;
}

builder.Services.AddPlatformIdentity(builder.Configuration);
builder.Services.AddHostedService<DefaultTenantRuntimeSettingsProjector>();
builder.Services.AddProtocolExecution(builder.Configuration);
builder.Services.AddScoped<ProtocolExecutionContextFilter>();
builder.Services.AddDurableJobs(builder.Configuration);
builder.Services.AddDurableProviderHealth(builder.Configuration);
builder.Services.AddProviderCapabilities();
builder.Services.AddTrackIdentity();
builder.Services.AddBackendLibraryIndexing();
builder.Services.AddMetadataEnrichment();
builder.Services.AddManagedFilePlacement();
builder.Services.AddProviderDownloadArtifacts(builder.Configuration);
builder.Services.AddFavoriteActions(builder.Configuration);
builder.Services.AddIntelligenceCore();
builder.Services.AddGeneratedSetMaterializers();
builder.Services.AddFirstPartyRecommendationSources();
builder.Services.AddDurablePlaybackSignals();
builder.Services.AddPlaylistOrchestration();
builder.Services.AddExtensionControlPlane();
builder.Services.AddPlatformOperations(builder.Configuration);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ProviderCtsTrackSelector>();
builder.Services.AddSingleton<ProviderCtsDiagnosticRunner>();
builder.Services.AddHostedService<ProviderCtsWarmupService>();
builder.Services.AddHostedService<AuditEventRetentionService>();

// Configure forwarded headers for reverse proxy support (nginx, etc.)
// Trust should be explicit: set ForwardedHeaders__KnownProxies and/or
// ForwardedHeaders__KnownNetworks (comma-separated) in deployment config.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
                             | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto
                             | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedHost;

    // Keep a bounded chain by default; configurable for multi-hop proxy setups.
    options.ForwardLimit = builder.Configuration.GetValue<int?>("ForwardedHeaders:ForwardLimit") ?? 2;

    // Framework defaults already trust loopback. If explicit trusted proxy/network
    // config is provided, replace defaults with those values.
    var configuredProxies = ParseCsv(builder.Configuration.GetValue<string>("ForwardedHeaders:KnownProxies"));
    var configuredNetworks = ParseCsv(builder.Configuration.GetValue<string>("ForwardedHeaders:KnownNetworks"));

    if (configuredProxies.Count > 0 || configuredNetworks.Count > 0)
    {
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();

        foreach (var proxy in configuredProxies)
        {
            if (IPAddress.TryParse(proxy, out var ip))
            {
                options.KnownProxies.Add(ip);
            }
            else
            {
                throw new InvalidOperationException(
                    "ForwardedHeaders:KnownProxies contains an invalid IP address.");
            }
        }

        foreach (var network in configuredNetworks)
        {
            if (IPNetwork.TryParse(network, out var parsedNetwork))
            {
                options.KnownIPNetworks.Add(parsedNetwork);
            }
            else
            {
                throw new InvalidOperationException(
                    "ForwardedHeaders:KnownNetworks contains an invalid network.");
            }
        }
    }
});

// Legacy implementation intentionally retired.
// var encodedUrls = new[] { "aHR0cHM6Ly90cml0b24uc3F1aWQud3Rm", ... };

static List<string> ParseCsv(string? raw)
{
    if (string.IsNullOrWhiteSpace(raw))
    {
        return new List<string>();
    }

    return raw
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(x => !string.IsNullOrWhiteSpace(x))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
}

static string? GetConfiguredValue(IConfiguration configuration, params string[] keys)
{
    foreach (var key in keys)
    {
        var value = configuration.GetValue<string>(key);
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }
    }

    return null;
}

// Determine backend type FIRST
var backendType = builder.Configuration.GetValue<BackendType>("Backend:Type");

// Configure Kestrel for large responses over VPN/Tailscale
// Also configure admin port on 5275 (internal only, not exposed)
var listenAdminAnyIp = AdminNetworkBindingPolicy.ShouldListenAdminAnyIp(builder.Configuration);
builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.Limits.MaxResponseBufferSize = null; // Disable response buffering limit
    serverOptions.Limits.MaxRequestBodySize = null; // Let nginx enforce body limits
    serverOptions.Limits.MinResponseDataRate = null; // Disable minimum data rate for slow connections

    // Main proxy port (exposed)
    serverOptions.ListenAnyIP(8080);

    // Admin UI port defaults to localhost-only.
    // Override with Admin:BindAnyIp=true if required by your deployment.
    if (listenAdminAnyIp)
    {
        serverOptions.ListenAnyIP(5275);
    }
    else
    {
        serverOptions.ListenLocalhost(5275);
    }
});

// Add response compression for large JSON responses (helps with Tailscale/VPN MTU issues)
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.MimeTypes = new[] { "application/json", "text/json" };
});

// Add services to the container - conditionally register controllers
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        // Use original property names (PascalCase) to match Jellyfin API
        options.JsonSerializerOptions.PropertyNamingPolicy = null;
        options.JsonSerializerOptions.DictionaryKeyPolicy = null;
    })
    .ConfigureApplicationPartManager(manager =>
    {
        // Remove the default controller feature provider
        var defaultProvider = manager.FeatureProviders
            .OfType<Microsoft.AspNetCore.Mvc.Controllers.ControllerFeatureProvider>()
            .FirstOrDefault();
        if (defaultProvider != null)
        {
            manager.FeatureProviders.Remove(defaultProvider);
        }
        // Add our custom provider that filters by backend type
        manager.FeatureProviders.Add(new BackendControllerFeatureProvider(backendType));
    });

builder.Services.AddHttpClient();
builder.Services.AddHttpClient("ExtensionSdkV1")
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        MaxConnectionsPerServer = 8,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    });
builder.Services.AddHttpClient("SquidWTF");
builder.Services.AddHttpClient("AppleMusic")
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        MaxConnectionsPerServer = 4,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2)
    });
builder.Services.AddHttpClient("AppleDownloadDiscovery", client =>
    client.Timeout = TimeSpan.FromSeconds(5))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        MaxConnectionsPerServer = 4,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2)
    });
builder.Services.AddSingleton<IPublicEndpointDnsResolver, SystemPublicEndpointDnsResolver>();
builder.Services.AddSingleton<IResolvedIpConnector, SocketResolvedIpConnector>();
builder.Services.AddSingleton<PublicEndpointConnector>();
builder.Services.AddSingleton<ISafeProxyTransportFactory, SafeProxyTransportFactory>();
builder.Services.AddSingleton<ISafeJsonProxyClient, SafeJsonProxyClient>();
builder.Services.ConfigureAll<HttpClientFactoryOptions>(options =>
{
    options.HttpMessageHandlerBuilderActions.Add(builder =>
    {
        if (builder.Name is "AppleDownloadDiscovery" or "AppleDownloadCapability" or "AppleMusic")
        {
            return;
        }
        builder.PrimaryHandler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5
        };
    });

    // Suppress verbose HTTP logging - these are logged at Debug level by default
    // but we want to reduce noise in production logs
    options.SuppressHandlerScope = true;
});

// Register a dedicated named HttpClient for Jellyfin backend with connection pooling.
// SocketsHttpHandler reuses TCP connections across the scoped JellyfinProxyService
// instances, eliminating per-request TCP/TLS handshake overhead.
builder.Services.AddHttpClient(JellyfinProxyService.HttpClientName)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        // Keep up to 20 idle connections to Jellyfin alive at any time
        MaxConnectionsPerServer = 20,
        // Recycle pooled connections every 5 minutes to pick up DNS changes
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        // Close idle connections after 90 seconds to avoid stale sockets
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(90),
        // Allow HTTP/2 multiplexing when Jellyfin supports it
        EnableMultipleHttp2Connections = true,
        // Follow redirects within Jellyfin
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 5
    });
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpContextAccessor();
var dataProtectionKeysPath = builder.Environment.IsEnvironment("Testing")
    ? Path.Combine(Path.GetTempPath(), "allstarr-tests", "data-protection")
    : "/app/cache/data-protection";
var dataProtectionKeysDirectory = new DirectoryInfo(dataProtectionKeysPath);
dataProtectionKeysDirectory.Create();
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(dataProtectionKeysDirectory)
    .SetApplicationName("allstarr-admin");

// Exception handling
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

// Admin port filter (restricts admin API to port 5275)
builder.Services.AddScoped<allstarr.Filters.AdminPortFilter>();

// Admin helper service (shared utilities for admin controllers)
builder.Services.AddSingleton<allstarr.Services.Admin.AdminHelperService>();
builder.Services.AddSingleton<allstarr.Services.Admin.AdminAuthSessionService>();

// Configuration - register both settings, active one determined by backend type
builder.Services.Configure<SubsonicSettings>(
    builder.Configuration.GetSection("Subsonic"));
builder.Services.Configure<JellyfinSettings>(
    builder.Configuration.GetSection("Jellyfin"));
builder.Services.Configure<DeezerSettings>(
    builder.Configuration.GetSection("Deezer"));
builder.Services.Configure<QobuzSettings>(
    builder.Configuration.GetSection("Qobuz"));
builder.Services.Configure<SquidWTFSettings>(
    builder.Configuration.GetSection("SquidWTF"));
builder.Services.Configure<AppleDownloadSettings>(
    builder.Configuration.GetSection("AppleDownload"));
builder.Services.Configure<RedisSettings>(
    builder.Configuration.GetSection("Redis"));
builder.Services.Configure<CacheSettings>(
    builder.Configuration.GetSection("Cache"));
builder.Services.Configure<SpotifyImportSettings>(options =>
{
    builder.Configuration.GetSection("SpotifyImport").Bind(options);
    var playlistJson = builder.Configuration.GetValue<string>("SpotifyImport:Playlists");
    if (!string.IsNullOrWhiteSpace(playlistJson) && playlistJson.TrimStart().StartsWith("[", StringComparison.Ordinal))
    {
        options.Playlists = SpotifyPlaylistConfigParser.Parse(playlistJson);
    }
});

// Discover SquidWTF endpoints for multi-provider usage
var squidWtfEndpointCatalog = builder.Environment.IsEnvironment("Testing")
    ? new SquidWtfEndpointCatalog([], [])
    : await SquidWtfEndpointDiscovery.DiscoverAsync();
var squidWtfApiUrls = squidWtfEndpointCatalog.ApiUrls;
var squidWtfStreamingUrls = squidWtfEndpointCatalog.StreamingUrls;

// Business services - shared across backends
builder.Services.AddSingleton(squidWtfEndpointCatalog);
builder.Services.AddSingleton<RedisCacheService>();
builder.Services.AddSingleton<PlaylistPlayableSearchService>();
builder.Services.AddSingleton<IRedisConnectionFactory, RedisConnectionFactory>();
builder.Services.AddSingleton<OdesliService>();
builder.Services.AddSingleton<ILocalLibraryService, LocalLibraryService>();
builder.Services.AddSingleton<LrclibService>();
builder.Services.AddSingleton<ProtocolStreamingResponseAdapter>();
builder.Services.AddSingleton<JellyfinProxyService>();

// Register backend-specific services
if (backendType == BackendType.Jellyfin)
{
    // Jellyfin services
    builder.Services.AddSingleton<JellyfinResponseBuilder>();
    builder.Services.AddSingleton<IJellyfinSearchProtocolAdapter, JellyfinSearchProtocolAdapter>();
    builder.Services.AddSingleton<IJellyfinItemProtocolAdapter, JellyfinItemProtocolAdapter>();
    builder.Services.AddSingleton<IJellyfinImageProtocolAdapter, JellyfinImageProtocolAdapter>();
    builder.Services.AddSingleton<IJellyfinLyricsProtocolAdapter, JellyfinLyricsProtocolAdapter>();
    builder.Services.AddSingleton<IJellyfinInteractionProtocolAdapter, JellyfinInteractionProtocolAdapter>();
    builder.Services.AddSingleton<JellyfinModelMapper>();
    builder.Services.AddSingleton<JellyfinSessionManager>();
    builder.Services.AddSingleton<IPlaybackActivitySource, JellyfinPlaybackActivitySource>();
    builder.Services.AddSingleton<IPlaybackMetadataResolver, JellyfinPlaybackMetadataResolver>();
    builder.Services.AddScoped<JellyfinAuthFilter>();

    // Register JellyfinController as a service for dependency injection
    builder.Services.AddScoped<allstarr.Controllers.JellyfinController>();
}
else
{
    // Subsonic services (default)
    builder.Services.AddSingleton<SubsonicRequestParser>();
    builder.Services.AddSingleton<SubsonicResponseBuilder>();
    builder.Services.AddSingleton<SubsonicModelMapper>();
    builder.Services.AddSingleton<ISubsonicLyricsLookup, SubsonicLyricsLookup>();
    builder.Services.AddScoped<SubsonicProxyService>();
    builder.Services.AddScoped<SubsonicLyricsProtocolAdapter>();
    builder.Services.AddSingleton<SubsonicRelayProtocolAdapter>();
    builder.Services.AddSingleton<SubsonicSearchProtocolAdapter>();
    builder.Services.AddSingleton<SubsonicScrobbleProtocolAdapter>();
    builder.Services.AddScoped<SubsonicAuthFilter>();
}

// ----------------------------------------------------
// Multi-Provider & Concrete Service Registrations
// ----------------------------------------------------
builder.Services.AddSingleton<QobuzBundleService>();

// 1. Concrete Metadata Services
builder.Services.AddSingleton<DeezerMetadataService>();
builder.Services.AddSingleton<IConcreteMetadataService>(provider =>
    provider.GetRequiredService<DeezerMetadataService>());
builder.Services.AddSingleton<IConcreteMetadataService, QobuzMetadataService>();
builder.Services.AddSingleton<IConcreteMetadataService, AppleMusicMetadataService>();
builder.Services.AddSingleton<IAppleDownloadEndpointDiscovery, AppleDownloadEndpointDiscovery>();
builder.Services.AddSingleton<IConcreteMetadataService>(sp =>
    new SquidWTFMetadataService(
        sp.GetRequiredService<IHttpClientFactory>(),
        sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SubsonicSettings>>(),
        sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SquidWTFSettings>>(),
        sp.GetRequiredService<ILogger<SquidWTFMetadataService>>(),
        sp.GetRequiredService<RedisCacheService>(),
        squidWtfApiUrls,
        sp.GetService<GenreEnrichmentService>()));
builder.Services.AddDeezerMetadataCapability();
builder.Services.AddSpotifyPlaylistCapability();
builder.Services.AddAppleMusicKitPlaylistCapability();
builder.Services.AddAppleDownloadCapability();
builder.Services.AddLegacyBuiltInProviderDescriptors();

// 2. Concrete Download Services
builder.Services.AddSingleton<IConcreteDownloadService, DeezerDownloadService>();
builder.Services.AddSingleton<IConcreteDownloadService, QobuzDownloadService>();
builder.Services.AddSingleton<IConcreteDownloadService, AppleMusicDownloadService>();
builder.Services.AddSingleton<IConcreteDownloadService>(sp =>
    new SquidWTFDownloadService(
        sp.GetRequiredService<IHttpClientFactory>(),
        sp.GetRequiredService<IConfiguration>(),
        sp.GetRequiredService<ILocalLibraryService>(),
        sp.GetRequiredService<IMusicMetadataService>(),
        sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SubsonicSettings>>(),
        sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SquidWTFSettings>>(),
        sp,
        sp.GetRequiredService<ILogger<SquidWTFDownloadService>>(),
        sp.GetRequiredService<OdesliService>(),
        squidWtfStreamingUrls));

// 3. Status Manager & Multi-Provider Orchestrators
builder.Services.AddSingleton<ExtensionManager>();
builder.Services.AddSingleton<ProviderStatusManager>();
builder.Services.AddSingleton<IMusicMetadataService, MultiProviderMetadataService>();
builder.Services.AddSingleton<IPlaybackMetadataResolver, ExternalPlaybackMetadataResolver>();
builder.Services.AddSingleton<IDownloadService, MultiProviderDownloadService>();
builder.Services.AddSingleton<IProtocolProviderGateway, ProtocolProviderGateway>();

// 4. Playlist Sync Service
builder.Services.AddSingleton<PlaylistSyncService>();

// 5. ParallelMetadataService Wrapper (delegates to MultiProviderMetadataService)
builder.Services.AddSingleton<ParallelMetadataService>();

// Startup validation - register validators based on backend
if (backendType == BackendType.Jellyfin)
{
    builder.Services.AddSingleton<IStartupValidator, JellyfinStartupValidator>();
}
else
{
    builder.Services.AddSingleton<IStartupValidator, SubsonicStartupValidator>();
}

// Register endpoint benchmark service
builder.Services.AddSingleton<EndpointBenchmarkService>();

var probeOptionalProvidersAtStartup =
    builder.Configuration.GetValue<bool>("StartupValidation:ProbeOptionalProviders");
if (probeOptionalProvidersAtStartup)
{
    builder.Services.AddSingleton<IStartupValidator, DeezerStartupValidator>();
    builder.Services.AddSingleton<IStartupValidator, QobuzStartupValidator>();
    var disabledProviders = ParseCsv(builder.Configuration["MULTI_PROVIDER_DISABLED_PROVIDERS"])
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    var enabledMetadataProviders = ParseCsv(
        builder.Configuration["MULTI_PROVIDER_ENABLED_SEARCH"] ?? "deezer,qobuz");
    if (!disabledProviders.Contains("squidwtf") &&
        enabledMetadataProviders.Contains("squidwtf", StringComparer.OrdinalIgnoreCase))
    {
        builder.Services.AddSingleton<IStartupValidator>(sp =>
            new SquidWTFStartupValidator(
                sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SquidWTFSettings>>(),
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("SquidWTF"),
                squidWtfApiUrls,
                squidWtfStreamingUrls,
                sp.GetRequiredService<EndpointBenchmarkService>(),
                sp.GetRequiredService<ILogger<SquidWTFStartupValidator>>()));
    }
    builder.Services.AddSingleton<IStartupValidator, LyricsStartupValidator>();
}

// Tests and local contract hosts must never call live providers during startup.
if (!builder.Environment.IsEnvironment("Testing"))
{
    builder.Services.AddHostedService<ManagedProviderAccountHealthWarmupService>();
    builder.Services.AddHostedService<StartupValidationOrchestrator>();
}

// Register cache cleanup service (only runs when StorageMode is Cache)
builder.Services.AddHostedService<CacheCleanupService>();

// Register cache warming service (loads file caches into Redis on startup)
builder.Services.AddHostedService<CacheWarmingService>();

// Register Redis persistence service (snapshots Redis to files periodically)
builder.Services.AddHostedService<RedisPersistenceService>();

// Register Spotify API client, lyrics service, and settings for direct API access
// Configure from environment variables with SPOTIFY_API_ prefix
builder.Services.Configure<allstarr.Models.Settings.SpotifyApiSettings>(options =>
{
    builder.Configuration.GetSection("SpotifyApi").Bind(options);

    // Override from environment variables
    var enabled = builder.Configuration.GetValue<string>("SpotifyApi:Enabled");
    if (!string.IsNullOrEmpty(enabled))
    {
        options.Enabled = enabled.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    var sessionCookie = builder.Configuration.GetValue<string>("SpotifyApi:SessionCookie");
    if (!string.IsNullOrEmpty(sessionCookie))
    {
        options.SessionCookie = sessionCookie;
    }

    var sessionCookieSetDate = builder.Configuration.GetValue<string>("SpotifyApi:SessionCookieSetDate");
    if (!string.IsNullOrEmpty(sessionCookieSetDate))
    {
        options.SessionCookieSetDate = sessionCookieSetDate;
    }

    var cacheDuration = builder.Configuration.GetValue<int?>("SpotifyApi:CacheDurationMinutes");
    if (cacheDuration.HasValue)
    {
        options.CacheDurationMinutes = cacheDuration.Value;
    }

    var preferIsrc = builder.Configuration.GetValue<string>("SpotifyApi:PreferIsrcMatching");
    if (!string.IsNullOrEmpty(preferIsrc))
    {
        options.PreferIsrcMatching = preferIsrc.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

});
builder.Services.AddSingleton<allstarr.Services.Spotify.SpotifyApiClient>();
builder.Services.AddSingleton<allstarr.Services.Spotify.SpotifyApiClientFactory>();
builder.Services.AddSingleton<allstarr.Services.Spotify.SpotifySessionCookieService>();

// Register Spotify lyrics service (uses Spotify's color-lyrics API)
builder.Services.AddSingleton<allstarr.Services.Lyrics.SpotifyLyricsService>();

// Register LyricsPlus service (multi-source lyrics API)
builder.Services.AddSingleton<allstarr.Services.Lyrics.LyricsPlusService>();

// Register Lyrics Orchestrator (manages priority-based lyrics fetching)
builder.Services.AddSingleton<allstarr.Services.Lyrics.LyricsOrchestrator>();
builder.Services.AddSingleton<allstarr.Services.Lyrics.IKeptLyricsSidecarService, allstarr.Services.Lyrics.KeptLyricsSidecarService>();

// Register Spotify mapping service (global Spotify ID → Local/External mappings)
builder.Services.AddSingleton<allstarr.Services.Spotify.SpotifyMappingService>();
builder.Services.AddSingleton<allstarr.Services.Spotify.LegacySpotifyMappingProjector>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<allstarr.Services.Spotify.LegacySpotifyMappingProjector>());

// Register Spotify mapping validation service (validates and upgrades mappings)
builder.Services.AddSingleton<allstarr.Services.Spotify.SpotifyMappingValidationService>();

// Register Spotify playlist fetcher (uses direct Spotify API when SpotifyApi is enabled)
builder.Services.AddSingleton<allstarr.Services.Spotify.SpotifyPlaylistFetcher>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<allstarr.Services.Spotify.SpotifyPlaylistFetcher>());

// Register Spotify missing tracks fetcher (legacy - only runs when SpotifyImport is enabled and SpotifyApi is disabled)
builder.Services.AddHostedService<allstarr.Services.Spotify.SpotifyMissingTracksFetcher>();

// Register Spotify track matching service (pre-matches tracks with rate limiting)
builder.Services.AddSingleton<allstarr.Services.Spotify.SpotifyTrackMatchingService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<allstarr.Services.Spotify.SpotifyTrackMatchingService>());
builder.Services.AddSingleton<allstarr.Core.Jobs.IDurableJobHandler, allstarr.Services.Spotify.LegacyPlaylistMatchAllJobHandler>();

// Register lyrics prefetch service (prefetches lyrics for all playlist tracks)
// DISABLED - No need to prefetch since Jellyfin and Spotify lyrics are fast
// builder.Services.AddSingleton<allstarr.Services.Lyrics.LyricsPrefetchService>();
// builder.Services.AddHostedService(sp => sp.GetRequiredService<allstarr.Services.Lyrics.LyricsPrefetchService>());

// Register scrobbling services (Last.fm, ListenBrainz, etc.)
builder.Services.Configure<allstarr.Models.Settings.ScrobblingSettings>(options =>
{
    // Last.fm settings
    var lastFmEnabled = builder.Configuration.GetValue<bool>("Scrobbling:LastFm:Enabled");
    var lastFmApiKey = builder.Configuration.GetValue<string>("Scrobbling:LastFm:ApiKey");
    var lastFmSharedSecret = builder.Configuration.GetValue<string>("Scrobbling:LastFm:SharedSecret");
    var lastFmSessionKey = builder.Configuration.GetValue<string>("Scrobbling:LastFm:SessionKey");
    var lastFmUsername = builder.Configuration.GetValue<string>("Scrobbling:LastFm:Username");
    var lastFmPassword = builder.Configuration.GetValue<string>("Scrobbling:LastFm:Password");

    options.Enabled = builder.Configuration.GetValue<bool>("Scrobbling:Enabled");
    options.LocalTracksEnabled = builder.Configuration.GetValue<bool>("Scrobbling:LocalTracksEnabled");
    options.SyntheticLocalPlayedSignalEnabled =
        builder.Configuration.GetValue<bool>("Scrobbling:SyntheticLocalPlayedSignalEnabled");
    options.LastFm.Enabled = lastFmEnabled;

    // Only override hardcoded API credentials if explicitly set in config
    if (!string.IsNullOrEmpty(lastFmApiKey))
        options.LastFm.ApiKey = lastFmApiKey;
    if (!string.IsNullOrEmpty(lastFmSharedSecret))
        options.LastFm.SharedSecret = lastFmSharedSecret;

    // These don't have defaults, so set them normally
    options.LastFm.SessionKey = lastFmSessionKey ?? string.Empty;
    options.LastFm.Username = lastFmUsername;
    options.LastFm.Password = lastFmPassword;

    // ListenBrainz settings
    var listenBrainzEnabled = builder.Configuration.GetValue<bool>("Scrobbling:ListenBrainz:Enabled");
    var listenBrainzUserToken = builder.Configuration.GetValue<string>("Scrobbling:ListenBrainz:UserToken") ?? string.Empty;

    options.ListenBrainz.Enabled = listenBrainzEnabled;
    options.ListenBrainz.UserToken = listenBrainzUserToken;

});

// Register Last.fm HTTP client with proper User-Agent
builder.Services.AddHttpClient("LastFm", client =>
{
    client.DefaultRequestHeaders.Add("User-Agent", "Allstarr/1.0 (https://github.com/sopat712/allstarr)");
    client.Timeout = TimeSpan.FromSeconds(30);
});

// Register ListenBrainz HTTP client with proper User-Agent
builder.Services.AddHttpClient("ListenBrainz", client =>
{
    client.DefaultRequestHeaders.Add("User-Agent", "Allstarr/1.0 (https://github.com/sopat712/allstarr)");
    client.Timeout = TimeSpan.FromSeconds(30);
});

// Register scrobbling services
builder.Services.AddSingleton<IScrobblingService, LastFmScrobblingService>();
builder.Services.AddSingleton<IScrobblingService, ListenBrainzScrobblingService>();
builder.Services.AddSingleton<ScrobblingOrchestrator>();
builder.Services.AddSingleton<ScrobblingHelper>();

// Register the capability unconditionally. MusicBrainzSettings.Enabled gates every
// outbound lookup, which lets the durable runtime setting change without rebuilding DI.
builder.Services.Configure<allstarr.Models.Settings.MusicBrainzSettings>(options =>
{
    builder.Configuration.GetSection("MusicBrainz").Bind(options);

    var enabled = builder.Configuration.GetValue<string>("MusicBrainz:Enabled");
    if (!string.IsNullOrEmpty(enabled))
    {
        options.Enabled = enabled.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    var username = builder.Configuration.GetValue<string>("MusicBrainz:Username");
    if (!string.IsNullOrEmpty(username)) options.Username = username;
    var password = builder.Configuration.GetValue<string>("MusicBrainz:Password");
    if (!string.IsNullOrEmpty(password)) options.Password = password;
});
builder.Services.AddSingleton<allstarr.Services.MusicBrainz.MusicBrainzService>();
builder.Services.AddSingleton<allstarr.Services.Common.GenreEnrichmentService>();

builder.Services.AddCors(options =>
{
    var corsAllowedOrigins = ParseCsv(GetConfiguredValue(
        builder.Configuration,
        "Cors:AllowedOrigins",
        "CORS_ALLOWED_ORIGINS",
        "CORS__ALLOWED_ORIGINS"));

    var corsAllowedMethods = ParseCsv(GetConfiguredValue(
        builder.Configuration,
        "Cors:AllowedMethods",
        "CORS_ALLOWED_METHODS",
        "CORS__ALLOWED_METHODS"));
    if (corsAllowedMethods.Count == 0)
    {
        corsAllowedMethods = new List<string> { "GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS", "HEAD" };
    }

    var corsAllowedHeaders = ParseCsv(GetConfiguredValue(
        builder.Configuration,
        "Cors:AllowedHeaders",
        "CORS_ALLOWED_HEADERS",
        "CORS__ALLOWED_HEADERS"));
    if (corsAllowedHeaders.Count == 0)
    {
        corsAllowedHeaders = new List<string>
        {
            "Accept",
            "Authorization",
            "Content-Type",
            "Range",
            "X-Requested-With",
            "X-Emby-Authorization",
            "X-MediaBrowser-Token"
        };
    }

    var corsAllowCredentials =
        builder.Configuration.GetValue<bool?>("Cors:AllowCredentials")
        ?? builder.Configuration.GetValue<bool?>("CORS_ALLOW_CREDENTIALS")
        ?? builder.Configuration.GetValue<bool?>("CORS__ALLOW_CREDENTIALS")
        ?? false;

    options.AddDefaultPolicy(policy =>
    {
        policy.WithMethods(corsAllowedMethods.ToArray())
            .WithHeaders(corsAllowedHeaders.ToArray())
            .WithExposedHeaders("X-Content-Duration", "X-Total-Count", "X-Nd-Authorization");

        if (corsAllowedOrigins.Count > 0)
        {
            policy.WithOrigins(corsAllowedOrigins.ToArray());

            if (corsAllowCredentials)
            {
                policy.AllowCredentials();
            }
        }
    });
});

var app = builder.Build();

// Initialize cache settings for static access
CacheExtensions.InitializeCacheSettings(app.Services);

// Configure the HTTP request pipeline.

// IMPORTANT: UseForwardedHeaders must be called BEFORE other middleware
// This processes X-Forwarded-For, X-Real-IP, etc. from nginx
app.UseForwardedHeaders();

// Drop high-confidence scanner paths before they hit the proxy or request logging.
app.UseMiddleware<BotProbeBlockMiddleware>();

// Request logging middleware (when DEBUG_LOG_ALL_REQUESTS=true)
app.UseMiddleware<RequestLoggingMiddleware>();

app.UseExceptionHandler(); // Use registered GlobalExceptionHandler

app.UseMiddleware<CorrelationMiddleware>();

// Never mutate against a fallback store when the selected durable database is unavailable.
app.UseMiddleware<DurableMutationGuardMiddleware>();

// Enable response compression EARLY in the pipeline
app.UseResponseCompression();

// Enable WebSocket support
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(120)
});

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// LAN installs and reverse proxies commonly terminate HTTP outside this process.
// Enable redirection only when an operator has configured an HTTPS endpoint here.
if (builder.Configuration.GetValue<bool>("HttpsRedirection:Enabled"))
{
    app.UseHttpsRedirection();
}

// Serve static files only on admin port (5275)
app.UseMiddleware<allstarr.Middleware.AdminNetworkAllowlistMiddleware>();
app.UseMiddleware<allstarr.Middleware.AdminStaticFilesMiddleware>();
app.UseMiddleware<allstarr.Middleware.AdminAuthenticationMiddleware>();

if (backendType == BackendType.Jellyfin)
{
    app.UseMiddleware<JellyfinMusicEndpointPolicyMiddleware>();
}

// Proxy authenticated Jellyfin client sockets only after the public API policy
// has classified the request as part of the supported music-client surface.
app.UseMiddleware<WebSocketProxyMiddleware>();

app.UseAuthorization();

app.UseCors();

app.MapControllers();

app.MapGet("/health/live", () => Results.Ok(new
{
    status = "live",
    timestamp = DateTimeOffset.UtcNow
}));

static async Task<IResult> StorageReadinessResult(
    PlatformReadinessService readinessService,
    CancellationToken cancellationToken)
{
    var snapshot = await readinessService.CheckAsync(cancellationToken);
    return snapshot.Ready
        ? Results.Ok(snapshot)
        : Results.Json(snapshot, statusCode: StatusCodes.Status503ServiceUnavailable);
}

app.MapGet("/health/ready", StorageReadinessResult);
app.MapGet("/health", StorageReadinessResult);
app.MapGet("/metrics", async (
    HttpContext context,
    allstarr.Core.Operations.OperationalMetricsService metrics,
    CancellationToken cancellationToken) =>
{
    if (context.Connection.LocalPort != 5275)
    {
        return Results.NotFound();
    }

    return Results.Text(
        await metrics.RenderPrometheusAsync(cancellationToken),
        "text/plain; version=0.0.4; charset=utf-8");
});

app.Run();

public partial class Program
{
}

/// <summary>
/// Controller feature provider that conditionally registers controllers based on backend type.
/// This prevents route conflicts between JellyfinController and SubsonicController catch-all routes.
/// </summary>
class BackendControllerFeatureProvider : Microsoft.AspNetCore.Mvc.Controllers.ControllerFeatureProvider
{
    private readonly BackendType _backendType;

    public BackendControllerFeatureProvider(BackendType backendType)
    {
        _backendType = backendType;
    }

    protected override bool IsController(System.Reflection.TypeInfo typeInfo)
    {
        var isController = base.IsController(typeInfo);
        if (!isController) return false;

        // Only the protocol catch-all controllers and their backend-specific admin
        // surfaces are conditional. Every other controller is backend-neutral and
        // must remain registered; an allowlist here silently sends new admin routes
        // into the selected protocol catch-all.
        if (typeInfo.Name == "JellyfinAdminController")
        {
            return _backendType == BackendType.Jellyfin;
        }

        if (typeInfo.Name != "JellyfinController" && typeInfo.Name != "SubsonicController")
        {
            return true;
        }

        // Only register the controller matching the configured backend type
        return _backendType switch
        {
            BackendType.Jellyfin => typeInfo.Name == "JellyfinController",
            BackendType.Subsonic => typeInfo.Name == "SubsonicController",
            _ => false
        };
    }
}
