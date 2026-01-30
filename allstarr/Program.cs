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
using allstarr.Middleware;
using allstarr.Filters;
using Microsoft.Extensions.Http;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

// Decode SquidWTF API base URLs once at startup
var squidWtfApiUrls = DecodeSquidWtfUrls();
static List<string> DecodeSquidWtfUrls()
{
    var encodedUrls = new[]
    {
        "aHR0cHM6Ly90cml0b24uc3F1aWQud3Rm",      // triton
        "aHR0cHM6Ly93b2xmLnFxZGwuc2l0ZQ==",      // wolf
        "aHR0cDovL2h1bmQucXFkbC5zaXRl",          // hund
        "aHR0cHM6Ly9tYXVzLnFxZGwuc2l0ZQ==",      // maus
        "aHR0cHM6Ly92b2dlbC5xcWRsLnNpdGU=",      // vogel
        "aHR0cHM6Ly9rYXR6ZS5xcWRsLnNpdGU="       // katze
    };
    
    return encodedUrls
        .Select(encoded => Encoding.UTF8.GetString(Convert.FromBase64String(encoded)))
        .ToList();
}

// Determine backend type FIRST
var backendType = builder.Configuration.GetValue<BackendType>("Backend:Type");

// Configure Kestrel for large responses over VPN/Tailscale
builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.Limits.MaxResponseBufferSize = null; // Disable response buffering limit
    serverOptions.Limits.MaxRequestBodySize = null; // Allow large request bodies
    serverOptions.Limits.MinResponseDataRate = null; // Disable minimum data rate for slow connections
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
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpContextAccessor();

// Exception handling
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

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
builder.Services.AddSingleton<RedisCacheService>();
builder.Services.AddSingleton<ILocalLibraryService, LocalLibraryService>();
builder.Services.AddSingleton<LrclibService>();

// Register backend-specific services
if (backendType == BackendType.Jellyfin)
{
    // Jellyfin services
    builder.Services.AddSingleton<JellyfinResponseBuilder>();
    builder.Services.AddSingleton<JellyfinModelMapper>();
    builder.Services.AddScoped<JellyfinProxyService>();
    builder.Services.AddScoped<JellyfinAuthFilter>();
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
            squidWtfApiUrls));
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
            squidWtfApiUrls));
}

// Startup validation - register validators based on backend
if (backendType == BackendType.Jellyfin)
{
    builder.Services.AddSingleton<IStartupValidator, JellyfinStartupValidator>();
}
else
{
    builder.Services.AddSingleton<IStartupValidator, SubsonicStartupValidator>();
}

builder.Services.AddSingleton<IStartupValidator, DeezerStartupValidator>();
builder.Services.AddSingleton<IStartupValidator, QobuzStartupValidator>();
builder.Services.AddSingleton<IStartupValidator>(sp =>
    new SquidWTFStartupValidator(
        sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SquidWTFSettings>>(),
        sp.GetRequiredService<IHttpClientFactory>().CreateClient(),
        squidWtfApiUrls));

// Register orchestrator as hosted service
builder.Services.AddHostedService<StartupValidationOrchestrator>();

// Register cache cleanup service (only runs when StorageMode is Cache)
builder.Services.AddHostedService<CacheCleanupService>();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
            .AllowAnyMethod()
            .AllowAnyHeader()
            .WithExposedHeaders("X-Content-Duration", "X-Total-Count", "X-Nd-Authorization");
    });
});

var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseExceptionHandler(_ => { }); // Global exception handler

// Enable response compression EARLY in the pipeline
app.UseResponseCompression();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

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

        // Only register the controller matching the configured backend type
        return _backendType switch
        {
            BackendType.Jellyfin => typeInfo.Name == "JellyfinController",
            BackendType.Subsonic => typeInfo.Name == "SubsonicController",
            _ => false
        };
    }
}
