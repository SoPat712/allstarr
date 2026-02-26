using allstarr.Models.Settings;
using allstarr.Services;
using allstarr.Services.Deezer;
using allstarr.Services.Qobuz;
using allstarr.Services.SquidWTF;
using allstarr.Services.Local;
using allstarr.Services.Validation;
using allstarr.Services.Subsonic;
using allstarr.Services.Jellyfin;
using allstarr.Services.Common;
using allstarr.Services.Lyrics;
using allstarr.Services.Scrobbling;
using allstarr.Middleware;
using allstarr.Filters;
using Microsoft.Extensions.Http;
using System.Net;

var builder = WebApplication.CreateBuilder(args);

// Discover SquidWTF API and streaming endpoints from uptime feeds.
var squidWtfEndpointCatalog = await SquidWtfEndpointDiscovery.DiscoverAsync();
var squidWtfApiUrls = squidWtfEndpointCatalog.ApiUrls;
var squidWtfStreamingUrls = squidWtfEndpointCatalog.StreamingUrls;

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
                Console.WriteLine($"⚠️ Invalid ForwardedHeaders known proxy ignored: {proxy}");
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
                Console.WriteLine($"⚠️ Invalid ForwardedHeaders known network ignored: {network}");
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
var bindAdminAnyIp = AdminNetworkBindingPolicy.ShouldBindAdminAnyIp(builder.Configuration);
builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.Limits.MaxResponseBufferSize = null; // Disable response buffering limit
    serverOptions.Limits.MaxRequestBodySize = null; // Let nginx enforce body limits
    serverOptions.Limits.MinResponseDataRate = null; // Disable minimum data rate for slow connections

    // Main proxy port (exposed)
    serverOptions.ListenAnyIP(8080);

    // Admin UI port defaults to localhost-only.
    // Override with Admin:BindAnyIp=true if required by your deployment.
    if (bindAdminAnyIp)
    {
        Console.WriteLine("⚠️ Admin UI binding override enabled: listening on 0.0.0.0:5275");
        serverOptions.ListenAnyIP(5275);
    }
    else
    {
        Console.WriteLine("Admin UI listening on localhost:5275 (default)");
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
builder.Services.ConfigureAll<HttpClientFactoryOptions>(options =>
{
    options.HttpMessageHandlerBuilderActions.Add(builder =>
    {
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
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpContextAccessor();

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
builder.Services.Configure<RedisSettings>(
    builder.Configuration.GetSection("Redis"));
builder.Services.Configure<CacheSettings>(
    builder.Configuration.GetSection("Cache"));
// Configure Spotify Import settings with custom playlist parsing from env var
builder.Services.Configure<SpotifyImportSettings>(options =>
{
    builder.Configuration.GetSection("SpotifyImport").Bind(options);

    // Debug: Check what Bind() populated
    Console.WriteLine($"DEBUG: After Bind(), Playlists.Count = {options.Playlists.Count}");
#pragma warning disable CS0618 // Type or member is obsolete
    Console.WriteLine($"DEBUG: After Bind(), PlaylistIds.Count = {options.PlaylistIds.Count}");
    Console.WriteLine($"DEBUG: After Bind(), PlaylistNames.Count = {options.PlaylistNames.Count}");
#pragma warning restore CS0618

    // Parse SPOTIFY_IMPORT_PLAYLISTS env var (JSON array format)
    // Format: [["Name","SpotifyId","JellyfinId","first|last","cronSchedule","UserId?"],...]
    var playlistsEnv = builder.Configuration.GetValue<string>("SpotifyImport:Playlists");
    if (!string.IsNullOrWhiteSpace(playlistsEnv))
    {
        Console.WriteLine($"Found SPOTIFY_IMPORT_PLAYLISTS env var: {playlistsEnv.Length} chars");
        try
        {
            // Parse as JSON array of arrays
            var playlistArrays = System.Text.Json.JsonSerializer.Deserialize<string[][]>(playlistsEnv);
            if (playlistArrays != null && playlistArrays.Length > 0)
            {
                // Clear any playlists that Bind() may have incorrectly populated
                options.Playlists.Clear();

                Console.WriteLine($"Parsed {playlistArrays.Length} playlists from JSON format");
                foreach (var arr in playlistArrays)
                {
                    if (arr.Length >= 2)
                    {
                        var jellyfinId = string.Empty;
                        var localTracksPosition = LocalTracksPosition.First;
                        var syncSchedule = "0 8 * * *";
                        string? userId = null;

                        if (arr.Length >= 3)
                        {
                            var third = arr[2].Trim();
                            var thirdIsPosition = third.Equals("first", StringComparison.OrdinalIgnoreCase) ||
                                                  third.Equals("last", StringComparison.OrdinalIgnoreCase);

                            if (thirdIsPosition)
                            {
                                localTracksPosition = third.Equals("last", StringComparison.OrdinalIgnoreCase)
                                    ? LocalTracksPosition.Last
                                    : LocalTracksPosition.First;

                                if (arr.Length >= 4 && !string.IsNullOrWhiteSpace(arr[3]))
                                {
                                    syncSchedule = arr[3].Trim();
                                }

                                if (arr.Length >= 5 && !string.IsNullOrWhiteSpace(arr[4]))
                                {
                                    userId = arr[4].Trim();
                                }
                            }
                            else
                            {
                                jellyfinId = third;

                                if (arr.Length >= 4)
                                {
                                    localTracksPosition = arr[3].Trim().Equals("last", StringComparison.OrdinalIgnoreCase)
                                        ? LocalTracksPosition.Last
                                        : LocalTracksPosition.First;
                                }

                                if (arr.Length >= 5 && !string.IsNullOrWhiteSpace(arr[4]))
                                {
                                    syncSchedule = arr[4].Trim();
                                }

                                if (arr.Length >= 6 && !string.IsNullOrWhiteSpace(arr[5]))
                                {
                                    userId = arr[5].Trim();
                                }
                            }
                        }

                        var config = new SpotifyPlaylistConfig
                        {
                            Name = arr[0].Trim(),
                            Id = arr[1].Trim(),
                            JellyfinId = jellyfinId,
                            LocalTracksPosition = localTracksPosition,
                            SyncSchedule = syncSchedule,
                            UserId = userId
                        };
                        options.Playlists.Add(config);
                        var ownerDisplay = string.IsNullOrWhiteSpace(config.UserId) ? "global" : config.UserId;
                        Console.WriteLine($"  Added: {config.Name} (Spotify: {config.Id}, Jellyfin: {config.JellyfinId}, Position: {config.LocalTracksPosition}, Schedule: {config.SyncSchedule}, Owner: {ownerDisplay})");
                    }
                }
            }
            else
            {
                Console.WriteLine("JSON format was empty or invalid, will try legacy format");
            }
        }
        catch (System.Text.Json.JsonException ex)
        {
            Console.WriteLine($"Warning: Failed to parse SPOTIFY_IMPORT_PLAYLISTS: {ex.Message}");
            Console.WriteLine("Expected format: [[\"Name\",\"SpotifyId\",\"JellyfinId\",\"first|last\",\"cronSchedule\",\"UserId?\"],...]");
            Console.WriteLine("Will try legacy format instead");
        }
    }
    else
    {
        Console.WriteLine("No SPOTIFY_IMPORT_PLAYLISTS env var found, will try legacy format");
    }

    // Legacy support: Parse old SPOTIFY_IMPORT_PLAYLIST_IDS/NAMES env vars
    // Only used if new Playlists format is not configured
    // Check if we have legacy env vars to parse
    var playlistIdsEnv = builder.Configuration.GetValue<string>("SpotifyImport:PlaylistIds");
    var playlistNamesEnv = builder.Configuration.GetValue<string>("SpotifyImport:PlaylistNames");
    var hasLegacyConfig = !string.IsNullOrWhiteSpace(playlistIdsEnv) || !string.IsNullOrWhiteSpace(playlistNamesEnv);

    if (hasLegacyConfig && options.Playlists.Count == 0)
    {
        Console.WriteLine("Parsing legacy Spotify playlist format...");

#pragma warning disable CS0618 // Type or member is obsolete

        // Clear any auto-bound values from the Bind() call above
        // The auto-binder doesn't handle comma-separated strings correctly
        options.PlaylistIds.Clear();
        options.PlaylistNames.Clear();
        options.PlaylistLocalTracksPositions.Clear();

        if (!string.IsNullOrWhiteSpace(playlistIdsEnv))
        {
            options.PlaylistIds = playlistIdsEnv
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(id => id.Trim())
                .Where(id => !string.IsNullOrEmpty(id))
                .ToList();
            Console.WriteLine($"  Parsed {options.PlaylistIds.Count} playlist IDs from env var");
        }

        if (!string.IsNullOrWhiteSpace(playlistNamesEnv))
        {
            options.PlaylistNames = playlistNamesEnv
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(name => name.Trim())
                .Where(name => !string.IsNullOrEmpty(name))
                .ToList();
            Console.WriteLine($"  Parsed {options.PlaylistNames.Count} playlist names from env var");
        }

        var playlistPositionsEnv = builder.Configuration.GetValue<string>("SpotifyImport:PlaylistLocalTracksPositions");
        if (!string.IsNullOrWhiteSpace(playlistPositionsEnv))
        {
            options.PlaylistLocalTracksPositions = playlistPositionsEnv
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(pos => pos.Trim())
                .Where(pos => !string.IsNullOrEmpty(pos))
                .ToList();
            Console.WriteLine($"  Parsed {options.PlaylistLocalTracksPositions.Count} playlist positions from env var");
        }
        else
        {
            Console.WriteLine("  No playlist positions env var found, will use defaults");
        }

        // Convert legacy format to new Playlists array
        Console.WriteLine($"  Converting {options.PlaylistIds.Count} playlists to new format...");
        for (int i = 0; i < options.PlaylistIds.Count; i++)
        {
            var name = i < options.PlaylistNames.Count ? options.PlaylistNames[i] : options.PlaylistIds[i];
            var position = LocalTracksPosition.First; // Default

            // Parse position if provided
            if (i < options.PlaylistLocalTracksPositions.Count)
            {
                var posStr = options.PlaylistLocalTracksPositions[i];
                if (posStr.Equals("last", StringComparison.OrdinalIgnoreCase))
                {
                    position = LocalTracksPosition.Last;
                }
            }

            options.Playlists.Add(new SpotifyPlaylistConfig
            {
                Name = name,
                Id = options.PlaylistIds[i],
                LocalTracksPosition = position
            });
            Console.WriteLine($"    [{i}] {name} (ID: {options.PlaylistIds[i]}, Position: {position})");
        }
#pragma warning restore CS0618
    }
    else if (hasLegacyConfig && options.Playlists.Count > 0)
    {
        // Bind() incorrectly populated Playlists from legacy env vars
        // Clear it and re-parse properly
        Console.WriteLine($"DEBUG: Bind() incorrectly populated {options.Playlists.Count} playlists, clearing and re-parsing...");
        options.Playlists.Clear();

#pragma warning disable CS0618 // Type or member is obsolete
        options.PlaylistIds.Clear();
        options.PlaylistNames.Clear();
        options.PlaylistLocalTracksPositions.Clear();

        Console.WriteLine("Parsing legacy Spotify playlist format...");

        if (!string.IsNullOrWhiteSpace(playlistIdsEnv))
        {
            options.PlaylistIds = playlistIdsEnv
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(id => id.Trim())
                .Where(id => !string.IsNullOrEmpty(id))
                .ToList();
            Console.WriteLine($"  Parsed {options.PlaylistIds.Count} playlist IDs from env var");
        }

        if (!string.IsNullOrWhiteSpace(playlistNamesEnv))
        {
            options.PlaylistNames = playlistNamesEnv
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(name => name.Trim())
                .Where(name => !string.IsNullOrEmpty(name))
                .ToList();
            Console.WriteLine($"  Parsed {options.PlaylistNames.Count} playlist names from env var");
        }

        var playlistPositionsEnv = builder.Configuration.GetValue<string>("SpotifyImport:PlaylistLocalTracksPositions");
        if (!string.IsNullOrWhiteSpace(playlistPositionsEnv))
        {
            options.PlaylistLocalTracksPositions = playlistPositionsEnv
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(pos => pos.Trim())
                .Where(pos => !string.IsNullOrEmpty(pos))
                .ToList();
            Console.WriteLine($"  Parsed {options.PlaylistLocalTracksPositions.Count} playlist positions from env var");
        }
        else
        {
            Console.WriteLine("  No playlist positions env var found, will use defaults");
        }

        // Convert legacy format to new Playlists array
        Console.WriteLine($"  Converting {options.PlaylistIds.Count} playlists to new format...");
        for (int i = 0; i < options.PlaylistIds.Count; i++)
        {
            var name = i < options.PlaylistNames.Count ? options.PlaylistNames[i] : options.PlaylistIds[i];
            var position = LocalTracksPosition.First; // Default

            // Parse position if provided
            if (i < options.PlaylistLocalTracksPositions.Count)
            {
                var posStr = options.PlaylistLocalTracksPositions[i];
                if (posStr.Equals("last", StringComparison.OrdinalIgnoreCase))
                {
                    position = LocalTracksPosition.Last;
                }
            }

            options.Playlists.Add(new SpotifyPlaylistConfig
            {
                Name = name,
                Id = options.PlaylistIds[i],
                LocalTracksPosition = position
            });
            Console.WriteLine($"    [{i}] {name} (ID: {options.PlaylistIds[i]}, Position: {position})");
        }
#pragma warning restore CS0618
    }
    else
    {
        Console.WriteLine($"Using new Playlists format: {options.Playlists.Count} playlists configured");
    }

    // Log configuration at startup
    Console.WriteLine($"Spotify Import: Enabled={options.Enabled}, MatchingInterval={options.MatchingIntervalHours}h");
    Console.WriteLine($"Spotify Import Playlists: {options.Playlists.Count} configured");
    foreach (var playlist in options.Playlists)
    {
        Console.WriteLine($"  - {playlist.Name} (ID: {playlist.Id}, LocalTracks: {playlist.LocalTracksPosition})");
    }
});

// Get shared settings from the active backend config
MusicService musicService;
bool enableExternalPlaylists;

if (backendType == BackendType.Jellyfin)
{
    musicService = builder.Configuration.GetValue<MusicService>("Jellyfin:MusicService");
    enableExternalPlaylists = builder.Configuration.GetValue<bool>("Jellyfin:EnableExternalPlaylists", true);
}
else
{
    // Default to Subsonic
    musicService = builder.Configuration.GetValue<MusicService>("Subsonic:MusicService");
    enableExternalPlaylists = builder.Configuration.GetValue<bool>("Subsonic:EnableExternalPlaylists", true);
}

// Business services - shared across backends
builder.Services.AddSingleton(squidWtfEndpointCatalog);
builder.Services.AddSingleton<RedisCacheService>();
builder.Services.AddSingleton<OdesliService>();
builder.Services.AddSingleton<ILocalLibraryService, LocalLibraryService>();
builder.Services.AddSingleton<LrclibService>();

// Register backend-specific services
if (backendType == BackendType.Jellyfin)
{
    // Jellyfin services
    builder.Services.AddSingleton<JellyfinResponseBuilder>();
    builder.Services.AddSingleton<JellyfinModelMapper>();
    builder.Services.AddScoped<JellyfinProxyService>();
    builder.Services.AddSingleton<JellyfinSessionManager>();
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
    builder.Services.AddScoped<SubsonicProxyService>();
}

// Register music service based on configuration
// IMPORTANT: Primary service MUST be registered LAST because ASP.NET Core DI
// will use the last registered implementation when injecting IMusicMetadataService/IDownloadService
if (musicService == MusicService.Qobuz)
{
    // If playlists enabled, register Deezer FIRST (secondary provider)
    if (enableExternalPlaylists)
    {
        builder.Services.AddSingleton<IMusicMetadataService, DeezerMetadataService>();
        builder.Services.AddSingleton<IDownloadService, DeezerDownloadService>();
        builder.Services.AddSingleton<PlaylistSyncService>();
    }

    // Qobuz services (primary) - registered LAST to be injected by default
    builder.Services.AddSingleton<QobuzBundleService>();
    builder.Services.AddSingleton<IMusicMetadataService, QobuzMetadataService>();
    builder.Services.AddSingleton<IDownloadService, QobuzDownloadService>();
}
else if (musicService == MusicService.Deezer)
{
    // If playlists enabled, register Qobuz FIRST (secondary provider)
    if (enableExternalPlaylists)
    {
        builder.Services.AddSingleton<QobuzBundleService>();
        builder.Services.AddSingleton<IMusicMetadataService, QobuzMetadataService>();
        builder.Services.AddSingleton<IDownloadService, QobuzDownloadService>();
        builder.Services.AddSingleton<PlaylistSyncService>();
    }

    // Deezer services (primary, default) - registered LAST to be injected by default
    builder.Services.AddSingleton<IMusicMetadataService, DeezerMetadataService>();
    builder.Services.AddSingleton<IDownloadService, DeezerDownloadService>();
}
else if (musicService == MusicService.SquidWTF)
{
    // SquidWTF services - pass decoded URLs with fallback support
    builder.Services.AddSingleton<IMusicMetadataService>(sp =>
        new SquidWTFMetadataService(
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SubsonicSettings>>(),
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SquidWTFSettings>>(),
            sp.GetRequiredService<ILogger<SquidWTFMetadataService>>(),
            sp.GetRequiredService<RedisCacheService>(),
            squidWtfApiUrls,
            sp.GetService<GenreEnrichmentService>()));
    builder.Services.AddSingleton<IDownloadService>(sp =>
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
}

// Register ParallelMetadataService to race all registered providers for faster searches
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

builder.Services.AddSingleton<IStartupValidator, DeezerStartupValidator>();
builder.Services.AddSingleton<IStartupValidator, QobuzStartupValidator>();
builder.Services.AddSingleton<IStartupValidator>(sp =>
    new SquidWTFStartupValidator(
        sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SquidWTFSettings>>(),
        sp.GetRequiredService<IHttpClientFactory>().CreateClient(),
        squidWtfApiUrls,
        squidWtfStreamingUrls,
        sp.GetRequiredService<EndpointBenchmarkService>(),
        sp.GetRequiredService<ILogger<SquidWTFStartupValidator>>()));
builder.Services.AddSingleton<IStartupValidator, LyricsStartupValidator>();

// Register orchestrator as hosted service
builder.Services.AddHostedService<StartupValidationOrchestrator>();

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

    // Log configuration (mask sensitive values)
    Console.WriteLine($"SpotifyApi Configuration:");
    Console.WriteLine($"  Enabled: {options.Enabled}");
    Console.WriteLine($"  SessionCookie: {(string.IsNullOrEmpty(options.SessionCookie) ? "(not set)" : "***" + options.SessionCookie[^8..])}");
    Console.WriteLine($"  SessionCookieSetDate: {options.SessionCookieSetDate ?? "(not set)"}");
    Console.WriteLine($"  CacheDurationMinutes: {options.CacheDurationMinutes}");
    Console.WriteLine($"  PreferIsrcMatching: {options.PreferIsrcMatching}");
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

// Register Spotify mapping service (global Spotify ID → Local/External mappings)
builder.Services.AddSingleton<allstarr.Services.Spotify.SpotifyMappingService>();

// Register Spotify mapping validation service (validates and upgrades mappings)
builder.Services.AddSingleton<allstarr.Services.Spotify.SpotifyMappingValidationService>();

// Register Spotify mapping migration service (migrates legacy per-playlist mappings to global format)
builder.Services.AddHostedService<allstarr.Services.Spotify.SpotifyMappingMigrationService>();

// Register Spotify playlist fetcher (uses direct Spotify API when SpotifyApi is enabled)
builder.Services.AddSingleton<allstarr.Services.Spotify.SpotifyPlaylistFetcher>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<allstarr.Services.Spotify.SpotifyPlaylistFetcher>());

// Register Spotify missing tracks fetcher (legacy - only runs when SpotifyImport is enabled and SpotifyApi is disabled)
builder.Services.AddHostedService<allstarr.Services.Spotify.SpotifyMissingTracksFetcher>();

// Register Spotify track matching service (pre-matches tracks with rate limiting)
builder.Services.AddSingleton<allstarr.Services.Spotify.SpotifyTrackMatchingService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<allstarr.Services.Spotify.SpotifyTrackMatchingService>());
builder.Services.AddHostedService<VersionUpgradeRebuildService>();

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

    // Debug logging
    Console.WriteLine($"Scrobbling Configuration:");
    Console.WriteLine($"  Enabled: {options.Enabled}");
    Console.WriteLine($"  Local Tracks Enabled: {options.LocalTracksEnabled}");
    Console.WriteLine($"  Last.fm Enabled: {options.LastFm.Enabled}");
    Console.WriteLine($"  Last.fm Username: {options.LastFm.Username ?? "(not set)"}");
    Console.WriteLine($"  Last.fm Session Key: {(string.IsNullOrEmpty(options.LastFm.SessionKey) ? "(not set)" : "***" + options.LastFm.SessionKey[^8..])}");
    Console.WriteLine($"  ListenBrainz Enabled: {options.ListenBrainz.Enabled}");
    Console.WriteLine($"  ListenBrainz Token: {(string.IsNullOrEmpty(options.ListenBrainz.UserToken) ? "(not set)" : "***" + options.ListenBrainz.UserToken[^8..])}");
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

// Register MusicBrainz service for metadata enrichment (only if enabled)
var musicBrainzEnabled = builder.Configuration.GetValue<bool>("MusicBrainz:Enabled", false);
var musicBrainzEnabledEnv = builder.Configuration.GetValue<string>("MusicBrainz:Enabled");
if (!string.IsNullOrEmpty(musicBrainzEnabledEnv))
{
    musicBrainzEnabled = musicBrainzEnabledEnv.Equals("true", StringComparison.OrdinalIgnoreCase);
}

if (musicBrainzEnabled)
{
    builder.Services.Configure<allstarr.Models.Settings.MusicBrainzSettings>(options =>
    {
        builder.Configuration.GetSection("MusicBrainz").Bind(options);

        // Override from environment variables
        var enabled = builder.Configuration.GetValue<string>("MusicBrainz:Enabled");
        if (!string.IsNullOrEmpty(enabled))
        {
            options.Enabled = enabled.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        var username = builder.Configuration.GetValue<string>("MusicBrainz:Username");
        if (!string.IsNullOrEmpty(username))
        {
            options.Username = username;
        }

        var password = builder.Configuration.GetValue<string>("MusicBrainz:Password");
        if (!string.IsNullOrEmpty(password))
        {
            options.Password = password;
        }
    });
    builder.Services.AddSingleton<allstarr.Services.MusicBrainz.MusicBrainzService>();
    builder.Services.AddSingleton<allstarr.Services.Common.GenreEnrichmentService>();

    Console.WriteLine("✅ MusicBrainz genre enrichment enabled");
}
else
{
    Console.WriteLine("⏭️ MusicBrainz genre enrichment disabled");
}

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

// Migrate old .env file format on startup
try
{
    var migrationService = new EnvMigrationService(app.Services.GetRequiredService<ILogger<EnvMigrationService>>());
    migrationService.MigrateEnvFile();
}
catch (Exception ex)
{
    app.Logger.LogWarning(ex, "Failed to run .env migration");
}

// Configure the HTTP request pipeline.

// IMPORTANT: UseForwardedHeaders must be called BEFORE other middleware
// This processes X-Forwarded-For, X-Real-IP, etc. from nginx
app.UseForwardedHeaders();

// Request logging middleware (when DEBUG_LOG_ALL_REQUESTS=true)
app.UseMiddleware<RequestLoggingMiddleware>();

app.UseExceptionHandler(_ => { }); // Global exception handler

// Enable response compression EARLY in the pipeline
app.UseResponseCompression();

// Enable WebSocket support
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(120)
});

// Add WebSocket proxy middleware (BEFORE routing)
app.UseMiddleware<WebSocketProxyMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

// Serve static files only on admin port (5275)
app.UseMiddleware<allstarr.Middleware.AdminNetworkAllowlistMiddleware>();
app.UseMiddleware<allstarr.Middleware.AdminStaticFilesMiddleware>();
app.UseMiddleware<allstarr.Middleware.AdminAuthenticationMiddleware>();

app.UseAuthorization();

app.UseCors();

app.MapControllers();

// Health check endpoint for monitoring
app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));

app.Run();

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

        // All admin controllers should always be registered (for admin UI)
        // This includes: AdminController, ConfigController, DiagnosticsController, DownloadsController,
        // PlaylistController, JellyfinAdminController, SpotifyAdminController, LyricsController, MappingController, ScrobblingAdminController
        if (typeInfo.Name == "AdminController" ||
            typeInfo.Name == "AdminAuthController" ||
            typeInfo.Name == "ConfigController" ||
            typeInfo.Name == "DiagnosticsController" ||
            typeInfo.Name == "DownloadsController" ||
            typeInfo.Name == "PlaylistController" ||
            typeInfo.Name == "JellyfinAdminController" ||
            typeInfo.Name == "SpotifyAdminController" ||
            typeInfo.Name == "LyricsController" ||
            typeInfo.Name == "MappingController" ||
            typeInfo.Name == "ScrobblingAdminController")
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
