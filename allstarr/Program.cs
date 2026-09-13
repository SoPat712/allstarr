using allstarr.Models.Settings;
using allstarr.Services;
using allstarr.Services.Deezer;
using allstarr.Services.Qobuz;
using allstarr.Core.Providers.Qobuz;
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
using allstarr.Core.Providers.AudioMuse;
using allstarr.Core.Providers;
using allstarr.Core.Providers.Lyrics;
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
var isStorageOperatorCommand = StorageOperatorCommand.IsStorageCommand(args);
if (isStorageOperatorCommand)
{
    builder.Configuration["Logging:LogLevel:Default"] = "Warning";
}
builder.Logging.ClearProviders();
builder.Logging.AddProvider(new RedactingConsoleLoggerProvider(builder.Configuration));
builder.Services.AddDurableStorage(
    builder.Configuration,
    builder.Environment);
builder.Services.AddDurableRuntimeSettings();
builder.Services.AddEncryptedSecretStore(builder.Configuration);
builder.Services.AddSingleton<allstarr.Core.Configuration.LegacyEnvMigrationService>();
builder.Services.AddSingleton<allstarr.Core.Configuration.OnboardingStateService>();
if (isStorageOperatorCommand)
{
    Environment.ExitCode = await StorageOperatorCommand.RunAsync(
        builder.Services,
        args,
        Console.Out,
        Console.Error);
    return;
}

builder.Services.AddPlatformIdentity(builder.Configuration);
builder.Services.AddSingleton<TrackMatchPolicy>();
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
builder.Services.AddListeningHistoryImport(builder.Configuration);
builder.Services.AddGeneratedSetMaterializers();
builder.Services.AddBuiltInRecommendationSources();
builder.Services.AddAudioMuseIntelligenceCapability();
builder.Services.AddDurablePlaybackSignals();
builder.Services.AddPlaylistOrchestration();
builder.Services.AddExtensionControlPlane();
builder.Services.AddPlatformOperations(builder.Configuration);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ProviderCtsTrackSelector>();
builder.Services.AddSingleton<ProviderCtsDiagnosticRunner>();
builder.Services.AddSingleton<EndpointUsageAudit>();
builder.Services.AddHostedService<AuditEventRetentionService>();

// Trust forwarded headers only from proxies or networks named in deployment config.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
                             | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto
                             | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedHost;

    // Bound the chain unless a multi-hop deployment explicitly raises the limit.
    options.ForwardLimit = builder.Configuration.GetValue<int?>("ForwardedHeaders:ForwardLimit") ?? 2;

    // Explicit trust lists replace the framework's loopback defaults.
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

// Require deployment-owned backend identity; enum zero would silently select Subsonic.
var backendSelection = RuntimeEnvConfiguration.ResolveBackendSelection(
    builder.Configuration,
    builder.Environment);
var backendType = backendSelection.Type;
builder.Services.AddSingleton(backendSelection);

var listenAdminAnyIp = AdminNetworkBindingPolicy.ShouldListenAdminAnyIp(builder.Configuration);
builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.Limits.MaxResponseBufferSize = null;
    serverOptions.Limits.MaxRequestBodySize = null; // The deployment proxy enforces body limits.
    serverOptions.Limits.MinResponseDataRate = null;

    serverOptions.ListenAnyIP(8080);

    // Remote admin binding requires the explicit Admin:BindAnyIp opt-in.
    if (listenAdminAnyIp)
    {
        serverOptions.ListenAnyIP(5275);
    }
    else
    {
        serverOptions.ListenLocalhost(5275);
    }
});

// Compress large JSON responses for constrained VPN links.
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.MimeTypes = new[] { "application/json", "text/json" };
});

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        // Jellyfin clients require the protocol's original PascalCase names.
        options.JsonSerializerOptions.PropertyNamingPolicy = null;
        options.JsonSerializerOptions.DictionaryKeyPolicy = null;
    })
    .ConfigureApplicationPartManager(manager =>
    {
        var defaultProvider = manager.FeatureProviders
            .OfType<Microsoft.AspNetCore.Mvc.Controllers.ControllerFeatureProvider>()
            .FirstOrDefault();
        if (defaultProvider != null)
        {
            manager.FeatureProviders.Remove(defaultProvider);
        }
        manager.FeatureProviders.Add(new BackendControllerFeatureProvider(backendType));
    });

builder.Services.AddHttpClient();
builder.Services.AddHttpClient("Odesli", client => client.Timeout = TimeSpan.FromSeconds(5));
builder.Services.AddHttpClient("Lrclib", client => client.Timeout = TimeSpan.FromSeconds(5));
builder.Services.AddHttpClient("ExtensionSdkV1")
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        MaxConnectionsPerServer = 8,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    });
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

    options.SuppressHandlerScope = true;
});

// Preserve pooled connections across scoped Jellyfin proxy instances.
builder.Services.AddHttpClient(JellyfinProxyService.HttpClientName)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        MaxConnectionsPerServer = 20,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(90),
        EnableMultipleHttp2Connections = true,
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

builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

builder.Services.AddScoped<allstarr.Filters.AdminPortFilter>();

builder.Services.AddSingleton<allstarr.Services.Admin.AdminHelperService>();
builder.Services.AddSingleton<allstarr.Services.Admin.IAdminAuthSessionStore, allstarr.Services.Admin.EfAdminAuthSessionStore>();
builder.Services.AddSingleton<allstarr.Services.Admin.AdminAuthSessionService>();
builder.Services.AddSingleton<allstarr.Services.Admin.AdminProtocolExecutionContextFactory>();
builder.Services.AddSingleton<allstarr.Services.Admin.AdminUpdateFeed>();

builder.Services.Configure<SubsonicSettings>(
    builder.Configuration.GetSection("Subsonic"));
builder.Services.Configure<JellyfinSettings>(
    builder.Configuration.GetSection("Jellyfin"));
builder.Services.Configure<DeezerSettings>(
    builder.Configuration.GetSection("Deezer"));
builder.Services.Configure<QobuzSettings>(
    builder.Configuration.GetSection("Qobuz"));
builder.Services.Configure<AppleDownloadSettings>(
    builder.Configuration.GetSection("AppleDownload"));
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

builder.Services.AddSingleton<DatabaseApplicationCache>();
builder.Services.AddSingleton<BoundedHotApplicationCache>();
builder.Services.AddSingleton<FileMediaApplicationCache>();
builder.Services.AddSingleton<ApplicationCacheActivityMetrics>();
builder.Services.AddSingleton<ApplicationCacheRequestCoalescer>();
builder.Services.AddSingleton<HybridApplicationCache>();
builder.Services.AddSingleton<IApplicationCache>(sp =>
    sp.GetRequiredService<HybridApplicationCache>());
builder.Services.AddSingleton<IMediaAssetResolver, MediaAssetResolver>();
builder.Services.AddHostedService<ApplicationCacheMaintenanceService>();
builder.Services.AddSingleton<PlaylistPlayableSearchService>();
builder.Services.AddSingleton<OdesliService>();
builder.Services.AddSingleton<IDownloadedSongMappingStore, EfDownloadedSongMappingStore>();
builder.Services.AddSingleton<IManualLyricsMappingStore, EfManualLyricsMappingStore>();
builder.Services.AddSingleton<ILocalLibraryService, LocalLibraryService>();
builder.Services.AddSingleton<LrclibService>();
builder.Services.AddSingleton<ProtocolStreamingResponseAdapter>();
builder.Services.AddSingleton<ManagedTrackCacheService>();
builder.Services.AddSingleton<IProtocolLyricsResolver, ProtocolLyricsResolver>();
builder.Services.AddSingleton<JellyfinProxyService>();

if (backendType == BackendType.Jellyfin)
{
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

    builder.Services.AddScoped<allstarr.Controllers.JellyfinController>();
}
else if (backendType == BackendType.Subsonic)
{
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
    builder.Services.AddScoped<SubsonicExceptionFilter>();
}
else
{
    throw new InvalidOperationException($"Unsupported backend type '{backendType}'.");
}

builder.Services.AddSingleton<QobuzBundleService>();

builder.Services.AddSingleton<DeezerMetadataService>();
builder.Services.AddSingleton<IConcreteMetadataService>(provider =>
    provider.GetRequiredService<DeezerMetadataService>());
builder.Services.AddSingleton<QobuzMetadataService>();
builder.Services.AddSingleton<IConcreteMetadataService>(provider =>
    provider.GetRequiredService<QobuzMetadataService>());
builder.Services.AddSingleton<AppleMusicMetadataService>();
builder.Services.AddSingleton<IConcreteMetadataService>(provider =>
    provider.GetRequiredService<AppleMusicMetadataService>());
builder.Services.AddSingleton<IAppleDownloadEndpointDiscovery, AppleDownloadEndpointDiscovery>();
builder.Services.AddDeezerMetadataCapability();
builder.Services.AddQobuzDownloadCapability();
builder.Services.AddSpotifyPlaylistCapability();
builder.Services.AddAppleMusicKitPlaylistCapability();
builder.Services.AddAppleDownloadCapability();
builder.Services.AddBuiltInLyricsCapabilities();

builder.Services.AddSingleton<IConcreteDownloadService>(provider =>
    provider.GetRequiredService<DeezerDownloadService>());
builder.Services.AddSingleton<IConcreteDownloadService>(provider =>
    provider.GetRequiredService<QobuzDownloadService>());
builder.Services.AddSingleton<IConcreteDownloadService, AppleMusicDownloadService>();

builder.Services.AddSingleton<ExtensionManager>();
builder.Services.AddSingleton<ProviderStatusManager>();
builder.Services.AddSingleton<IMusicMetadataService, MultiProviderMetadataService>();
builder.Services.AddSingleton<IPlaybackMetadataResolver, ExternalPlaybackMetadataResolver>();
builder.Services.AddSingleton<IDownloadService, MultiProviderDownloadService>();
builder.Services.AddSingleton<IProtocolProviderGateway, ProtocolProviderGateway>();

builder.Services.AddSingleton<PlaylistSyncService>();

if (backendType == BackendType.Jellyfin)
{
    builder.Services.AddSingleton<IStartupValidator, JellyfinStartupValidator>();
}
else
{
    builder.Services.AddSingleton<IStartupValidator, SubsonicStartupValidator>();
}

var probeOptionalProvidersAtStartup =
    builder.Configuration.GetValue<bool>("StartupValidation:ProbeOptionalProviders");
if (probeOptionalProvidersAtStartup)
{
    builder.Services.AddSingleton<IStartupValidator, LyricsStartupValidator>();
}

// Tests and local contract hosts must never call live providers during startup.
if (!builder.Environment.IsEnvironment("Testing"))
{
    builder.Services.AddHostedService<ProviderCtsWarmupService>();
    builder.Services.AddHostedService<ManagedProviderAccountHealthWarmupService>();
    builder.Services.AddHostedService<StartupValidationOrchestrator>();
}

builder.Services.AddHostedService<CacheCleanupService>();

builder.Services.Configure<SpotifyApiSettings>(builder.Configuration.GetSection("SpotifyApi"));
builder.Services.AddSingleton<allstarr.Services.Spotify.SpotifySessionCookieService>();

builder.Services.AddSingleton<allstarr.Services.Lyrics.SpotifyLyricsService>();
builder.Services.AddSingleton<allstarr.Services.Lyrics.LyricsOrchestrator>();
builder.Services.AddSingleton<allstarr.Services.Lyrics.IKeptLyricsSidecarService, allstarr.Services.Lyrics.KeptLyricsSidecarService>();

builder.Services.Configure<ScrobblingSettings>(builder.Configuration.GetSection("Scrobbling"));

// Last.fm requires an identifying User-Agent.
builder.Services.AddHttpClient("LastFm", client =>
{
    client.DefaultRequestHeaders.Add("User-Agent", "Allstarr/1.0 (https://github.com/sopat712/allstarr)");
    client.Timeout = TimeSpan.FromSeconds(30);
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });

builder.Services.AddSingleton<ScrobblingHelper>();

// Registration stays unconditional so durable settings can enable lookups without rebuilding DI.
builder.Services.Configure<MusicBrainzSettings>(builder.Configuration.GetSection("MusicBrainz"));
builder.Services.AddHttpClient(allstarr.Services.MusicBrainz.MusicBrainzService.HttpClientName, client =>
{
    client.DefaultRequestHeaders.UserAgent.ParseAdd(
        allstarr.Services.MusicBrainz.MusicBrainzService.UserAgent);
    client.DefaultRequestHeaders.Accept.Add(
        new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
    client.Timeout = TimeSpan.FromSeconds(15);
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddSingleton<allstarr.Services.MusicBrainz.MusicBrainzService>();

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

CacheExtensions.InitializeCacheSettings(app.Services);

// Downstream policies must see only the forwarded identity accepted by the trust policy.
app.UseForwardedHeaders();

// Drop high-confidence scanner paths before they hit the proxy or request logging.
app.UseMiddleware<BotProbeBlockMiddleware>();

app.UseMiddleware<RequestLoggingMiddleware>();

app.UseExceptionHandler();

app.UseMiddleware<CorrelationMiddleware>();

// Never mutate against a fallback store when the selected durable database is unavailable.
app.UseMiddleware<DurableMutationGuardMiddleware>();

app.UseResponseCompression();

app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(120)
});

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Redirect only when this process owns HTTPS; proxies often terminate it upstream.
if (builder.Configuration.GetValue<bool>("HttpsRedirection:Enabled"))
{
    app.UseHttpsRedirection();
}

// Keep admin assets and authentication on the admin listener.
app.UseMiddleware<allstarr.Middleware.AdminNetworkAllowlistMiddleware>();
app.UseMiddleware<allstarr.Middleware.AdminStaticFilesMiddleware>();
app.UseMiddleware<allstarr.Middleware.AdminAuthenticationMiddleware>();

if (backendType == BackendType.Jellyfin)
{
    app.UseMiddleware<JellyfinMusicEndpointPolicyMiddleware>();
}

// Proxy Jellyfin sockets only after the public API policy classifies them as supported.
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

// Only one protocol catch-all can be registered; Jellyfin and Subsonic routes conflict.
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

        // Only protocol catch-alls and their admin surfaces are conditional; an
        // allowlist would send future admin routes into the selected catch-all.
        if (typeInfo.Name == "JellyfinAdminController")
        {
            return _backendType == BackendType.Jellyfin;
        }

        if (typeInfo.Name != "JellyfinController" && typeInfo.Name != "SubsonicController")
        {
            return true;
        }

        return _backendType switch
        {
            BackendType.Jellyfin => typeInfo.Name == "JellyfinController",
            BackendType.Subsonic => typeInfo.Name == "SubsonicController",
            _ => false
        };
    }
}
