using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using allstarr.Controllers;
using allstarr.Models.Admin;
using allstarr.Models.Settings;
using allstarr.Services.Admin;
using allstarr.Services.Common;
using allstarr.Services.Spotify;
using allstarr.Services.SquidWTF;
using allstarr.Core.Storage;
using allstarr.Core.Health;
using allstarr.Core.Operations;
using allstarr.Core.Secrets;
using allstarr.Core.Settings;
using allstarr.Core.Configuration;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public class ConfigControllerAuthorizationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "allstarr-tests",
        Guid.NewGuid().ToString("N"));
    private readonly Guid _providerAccountId = Guid.CreateVersion7();
    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly Guid _userId = Guid.CreateVersion7();
    private readonly TestDbContextFactory _factory;
    private readonly DurableStorageState _storageState;
    private readonly string _keyRingPath;

    public ConfigControllerAuthorizationTests()
    {
        Directory.CreateDirectory(_root);
        var options = new DbContextOptionsBuilder<AllstarrDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_root, "config-controller.db")}")
            .Options;
        _factory = new TestDbContextFactory(options);
        _storageState = new DurableStorageState(new DurableStorageOptions
        {
            Provider = "Sqlite",
            ConnectionString = $"Data Source={Path.Combine(_root, "config-controller.db")}"
        });
        _storageState.Set(DurableStorageReadiness.Ready, "fixture");
        _keyRingPath = Path.Combine(_root, "keyring.json");
        WriteKeyRing();
        using var context = _factory.CreateDbContext();
        context.Database.Migrate();
        context.Tenants.Add(new TenantRecord
        {
            Id = _tenantId,
            Slug = "config-test",
            Name = "Config test",
            CreatedAt = DateTimeOffset.UtcNow
        });
        context.Users.Add(new PlatformUserRecord
        {
            Id = _userId,
            TenantId = _tenantId,
            DisplayName = "Test administrator",
            Status = PlatformUserStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        context.ProviderAccounts.Add(new ProviderAccountRecord
        {
            Id = _providerAccountId,
            ProviderId = "deezer",
            DisplayName = "Health fixture",
            Scope = ProviderAccountScope.Global,
            Enabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        context.SaveChanges();
    }
    [Fact]
    public async Task UpdateConfig_WithoutAdminSession_ReturnsForbidden()
    {
        var controller = CreateController(CreateHttpContextWithSession(isAdmin: false));
        var result = await controller.UpdateConfig(new ConfigUpdateRequest
        {
            Updates = new Dictionary<string, string> { ["TEST_KEY"] = "value" }
        });

        AssertForbidden(result);
    }

    [Fact]
    public async Task RestartContainer_WithoutAdminSession_ReturnsForbidden()
    {
        var controller = CreateController(CreateHttpContextWithSession(isAdmin: false));
        var result = await controller.RestartContainer();

        AssertForbidden(result);
    }

    [Fact]
    public void ExportEnv_WithoutAdminSession_ReturnsForbidden()
    {
        var controller = CreateController(CreateHttpContextWithSession(isAdmin: false));
        var result = controller.ExportEnv();

        AssertForbidden(result);
    }

    [Fact]
    public async Task ImportEnv_WithoutAdminSession_ReturnsForbidden()
    {
        var controller = CreateController(CreateHttpContextWithSession(isAdmin: false));
        var file = new FormFile(Stream.Null, 0, 0, "file", "config.env");
        var result = await controller.ImportEnv(file);

        AssertForbidden(result);
    }

    [Fact]
    public async Task UpdateConfig_WithAdminSession_ContinuesToValidation()
    {
        var controller = CreateController(CreateHttpContextWithSession(isAdmin: true));
        var result = await controller.UpdateConfig(new ConfigUpdateRequest());

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, badRequest.StatusCode);
    }

    [Fact]
    public async Task UpdateConfig_WritesDurableSettingsWithoutModifyingEnvFile()
    {
        var envPath = Path.Combine(_root, ".env");
        await File.WriteAllTextAsync(envPath, "CACHE_LYRICS_DAYS=14\n");
        var controller = CreateController(CreateHttpContextWithSession(isAdmin: true));

        var result = Assert.IsType<OkObjectResult>(await controller.UpdateConfig(new ConfigUpdateRequest
        {
            Updates = new Dictionary<string, string> { ["CACHE_LYRICS_DAYS"] = "45" }
        }));

        Assert.Equal(StatusCodes.Status200OK, result.StatusCode);
        Assert.Equal("CACHE_LYRICS_DAYS=14\n", await File.ReadAllTextAsync(envPath));
        await using var db = await _factory.CreateDbContextAsync();
        var setting = Assert.Single(await db.TenantRuntimeSettings.ToListAsync());
        Assert.Equal(_tenantId, setting.TenantId);
        Assert.Equal("Cache:LyricsDays", setting.Key);
        Assert.Equal("45", setting.ValueJson);

        var getResult = Assert.IsType<OkObjectResult>(await controller.GetConfig());
        using var config = JsonDocument.Parse(JsonSerializer.Serialize(
            getResult.Value,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(45, config.RootElement.GetProperty("cache").GetProperty("lyricsDays").GetInt32());
    }

    [Fact]
    public async Task WholesaleImportEnv_IsRetiredForAdministrators()
    {
        var controller = CreateController(CreateHttpContextWithSession(isAdmin: true));
        var result = Assert.IsType<ObjectResult>(await controller.ImportEnv(
            new FormFile(Stream.Null, 0, 0, "file", "legacy.env")));
        Assert.Equal(StatusCodes.Status410Gone, result.StatusCode);
    }

    [Fact]
    public async Task UpdateConfig_RejectsDeploymentKeysWithoutModifyingEnvFile()
    {
        var envPath = Path.Combine(_root, ".env");
        await File.WriteAllTextAsync(envPath, "JELLYFIN_URL=http://old\n");
        var controller = CreateController(CreateHttpContextWithSession(isAdmin: true));

        var result = Assert.IsType<BadRequestObjectResult>(await controller.UpdateConfig(new ConfigUpdateRequest
        {
            Updates = new Dictionary<string, string> { ["JELLYFIN_URL"] = "http://new" }
        }));

        Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
        Assert.Equal("JELLYFIN_URL=http://old\n", await File.ReadAllTextAsync(envPath));
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Empty(await db.TenantRuntimeSettings.ToListAsync());
    }

    [Fact]
    public async Task MigrationEndpoints_RequireAdministratorSession()
    {
        var controller = CreateController(CreateHttpContextWithSession(isAdmin: false));
        AssertForbidden(await controller.GetEnvMigrationStatus());
        var bytes = Encoding.UTF8.GetBytes("CACHE_LYRICS_DAYS=30");
        AssertForbidden(await controller.PreviewEnvMigration(
            new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "legacy.env")));
        AssertForbidden(await controller.ApplyEnvMigration(new ConfigController.ApplyLegacyEnvMigrationRequest
        {
            PreviewToken = "token",
            Revision = "revision",
            Confirmed = true
        }));
    }

    [Fact]
    public async Task MigrationController_RedactsPreviewAndRequiresExplicitConfirmation()
    {
        var controller = CreateController(CreateHttpContextWithSession(isAdmin: true));
        const string source = "CACHE_LYRICS_DAYS=33\nSPOTIFY_API_SESSION_COOKIE=controller-secret";
        var bytes = Encoding.UTF8.GetBytes(source);
        var previewResult = Assert.IsType<OkObjectResult>(await controller.PreviewEnvMigration(
            new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "legacy.env")));
        var previewJson = JsonSerializer.Serialize(
            previewResult.Value,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("controller-secret", previewJson, StringComparison.Ordinal);
        using var previewDocument = JsonDocument.Parse(previewJson);
        var token = previewDocument.RootElement.GetProperty("previewToken").GetString();
        var revision = previewDocument.RootElement.GetProperty("revision").GetString();

        var unconfirmed = Assert.IsType<BadRequestObjectResult>(await controller.ApplyEnvMigration(
            new ConfigController.ApplyLegacyEnvMigrationRequest
            {
                PreviewToken = token,
                Revision = revision,
                Confirmed = false
            }));
        Assert.Equal(StatusCodes.Status400BadRequest, unconfirmed.StatusCode);

        var applied = Assert.IsType<OkObjectResult>(await controller.ApplyEnvMigration(
            new ConfigController.ApplyLegacyEnvMigrationRequest
            {
                PreviewToken = token,
                Revision = revision,
                Confirmed = true
            }));
        Assert.Equal(StatusCodes.Status200OK, applied.StatusCode);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Contains(await db.TenantRuntimeSettings.ToListAsync(), item => item.Key == "Cache:LyricsDays");
        var account = Assert.Single(await db.ProviderAccounts.Where(item => item.ProviderId == "spotify").ToListAsync());
        Assert.False(account.Enabled);
        Assert.NotNull(account.SecretReferenceId);

        var status = Assert.IsType<OkObjectResult>(await controller.GetEnvMigrationStatus());
        using var statusDocument = JsonDocument.Parse(JsonSerializer.Serialize(
            status.Value,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.True(statusDocument.RootElement.GetProperty("completed").GetBoolean());
        Assert.False(statusDocument.RootElement.GetProperty("firstRun").GetBoolean());
        Assert.False(statusDocument.RootElement.GetProperty("sourcePresent").GetBoolean());
    }

    [Fact]
    public void ExportEnv_WithAdminSession_WhenFeatureDisabled_ReturnsNotFound()
    {
        var controller = CreateController(CreateHttpContextWithSession(isAdmin: true));
        var result = controller.ExportEnv();

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        Assert.Equal(StatusCodes.Status404NotFound, notFound.StatusCode);
    }

    [Fact]
    public async Task GetProvidersStatus_ReturnsExactAccountCapabilitySnapshotsWithNoInventedTimestamp()
    {
        var controller = CreateController(CreateHttpContextWithSession(isAdmin: true));

        var result = Assert.IsType<OkObjectResult>(await controller.GetProvidersStatus());
        var json = JsonSerializer.Serialize(result.Value);
        using var document = JsonDocument.Parse(json);
        var statuses = document.RootElement;

        Assert.Equal(4, statuses.GetArrayLength());
        Assert.All(statuses.EnumerateArray(), status =>
        {
            Assert.Equal(_providerAccountId, status.GetProperty("providerAccountId").GetGuid());
            Assert.Equal("global", status.GetProperty("accountScope").GetString());
            Assert.Equal(JsonValueKind.Null, status.GetProperty("testedAt").ValueKind);
            Assert.Equal("unknown", status.GetProperty("health").GetString());
            Assert.True(status.TryGetProperty("configuration", out _));
            Assert.True(status.TryGetProperty("capability", out _));
        });
    }

    [Fact]
    public async Task ProviderHealthEndpoints_RequireAdministratorSessionAndManagedAccountId()
    {
        var nonAdministrator = CreateController(CreateHttpContextWithSession(isAdmin: false));
        AssertForbidden(await nonAdministrator.GetProvidersStatus());
        AssertForbidden(await nonAdministrator.TestProvider(
            "deezer",
            ProviderCapabilities.Metadata,
            _providerAccountId));

        var administrator = CreateController(CreateHttpContextWithSession(isAdmin: true));
        var missingAccount = Assert.IsType<BadRequestObjectResult>(await administrator.TestProvider(
            "deezer",
            ProviderCapabilities.Metadata));
        Assert.Equal(StatusCodes.Status400BadRequest, missingAccount.StatusCode);
    }

    [Fact]
    public async Task TestProvider_RejectsUnsupportedCapabilityWithoutProbing()
    {
        var controller = CreateController(CreateHttpContextWithSession(isAdmin: true));

        var result = await controller.TestProvider(
            "deezer",
            "recommendation",
            _providerAccountId);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, badRequest.StatusCode);
    }

    [Fact]
    public async Task ManagedDownloadProbe_UsesEncryptedAccountSecretAndPersistsExactCapability()
    {
        var secretStore = CreateSecretStore();
        var secret = await secretStore.StoreAsync(
            tenantId: null,
            purpose: $"provider-account:deezer:{_providerAccountId:N}",
            plaintext: Encoding.UTF8.GetBytes("{\"arl\":\"selected-account-arl\"}"));
        await using (var context = await _factory.CreateDbContextAsync())
        {
            var account = await context.ProviderAccounts.SingleAsync(
                item => item.Id == _providerAccountId);
            account.SecretReferenceId = secret.Id;
            await context.SaveChangesAsync();
        }

        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"results\":{\"USER\":{\"USER_ID\":42}}}",
                Encoding.UTF8,
                "application/json")
        });
        var healthStore = CreateHealthStore();
        var controller = CreateController(
            CreateHttpContextWithSession(isAdmin: true),
            httpClientFactory: new HandlerHttpClientFactory(handler),
            healthStore: healthStore,
            secretStore: secretStore);

        var result = Assert.IsType<OkObjectResult>(await controller.TestProvider(
            "deezer",
            ProviderCapabilities.Download,
            _providerAccountId));
        using (var resultDocument = JsonDocument.Parse(JsonSerializer.Serialize(result.Value)))
        {
            Assert.True(resultDocument.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("healthy", resultDocument.RootElement.GetProperty("health").GetString());
            Assert.Equal(_providerAccountId, resultDocument.RootElement.GetProperty("providerAccountId").GetGuid());
        }

        Assert.Equal("arl=selected-account-arl", handler.CookieHeader);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Contains("deezer.getUserData", handler.RequestUri?.Query, StringComparison.Ordinal);

        await using (var context = await _factory.CreateDbContextAsync())
        {
            var sample = await context.ProviderHealthSamples.SingleAsync();
            Assert.Equal(_providerAccountId, sample.ProviderAccountId);
            Assert.Equal(ProviderCapabilities.Download, sample.Capability);
            Assert.Equal(allstarr.Core.Storage.ProviderHealthState.Healthy, sample.State);
            var rollup = await context.ProviderHealthRollups.SingleAsync();
            Assert.Equal(_providerAccountId, rollup.ProviderAccountId);
            Assert.Equal(1, rollup.SuccessCount);
        }

        var restartedHealthStore = CreateHealthStore();
        await restartedHealthStore.InitializeAsync();
        var restartedController = CreateController(
            CreateHttpContextWithSession(isAdmin: true),
            healthStore: restartedHealthStore,
            secretStore: secretStore);
        var statusResult = Assert.IsType<OkObjectResult>(await restartedController.GetProvidersStatus());
        using var statusDocument = JsonDocument.Parse(JsonSerializer.Serialize(statusResult.Value));
        var download = statusDocument.RootElement.EnumerateArray().Single(item =>
            item.GetProperty("providerAccountId").GetGuid() == _providerAccountId &&
            item.GetProperty("capability").GetString() == ProviderCapabilities.Download);
        Assert.Equal("configured", download.GetProperty("configuration").GetString());
        Assert.Equal("healthy", download.GetProperty("health").GetString());
        Assert.Equal(JsonValueKind.String, download.GetProperty("testedAt").ValueKind);
    }

    private HttpContext CreateHttpContextWithSession(bool isAdmin)
    {
        var context = new DefaultHttpContext();
        context.Connection.LocalPort = 5275;
        context.Items[AdminAuthSessionService.HttpContextSessionItemKey] = new AdminAuthSession
        {
            SessionId = "session-id",
            UserId = "user-id",
            UserName = "user",
            IsAdministrator = isAdmin,
            JellyfinAccessToken = "token",
            JellyfinServerId = "server-id",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
            LastSeenUtc = DateTime.UtcNow,
            TenantId = _tenantId,
            AllstarrUserId = _userId
        };

        return context;
    }

    private ConfigController CreateController(
        HttpContext httpContext,
        Dictionary<string, string?>? configValues = null,
        IHttpClientFactory? httpClientFactory = null,
        DurableProviderHealthStore? healthStore = null,
        EncryptedSecretStore? secretStore = null)
    {
        var logger = new Mock<ILogger<ConfigController>>();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues ?? new Dictionary<string, string?>())
            .Build();

        var webHostEnvironment = new Mock<IWebHostEnvironment>();
        webHostEnvironment.SetupGet(e => e.EnvironmentName).Returns(Environments.Development);
        var contentRoot = Path.Combine(_root, "app");
        Directory.CreateDirectory(contentRoot);
        webHostEnvironment.SetupGet(e => e.ContentRootPath).Returns(contentRoot);
        var helperLogger = new Mock<ILogger<AdminHelperService>>();
        var helperService = new AdminHelperService(
            helperLogger.Object,
            Options.Create(new JellyfinSettings()),
            webHostEnvironment.Object);

        var redisLogger = new Mock<ILogger<RedisCacheService>>();
        var redisCache = new RedisCacheService(
            Options.Create(new RedisSettings
            {
                Enabled = false,
                ConnectionString = "localhost:6379"
            }),
            redisLogger.Object);
        var spotifyCookieLogger = new Mock<ILogger<SpotifySessionCookieService>>();
        var spotifySessionCookieService = new SpotifySessionCookieService(
            Options.Create(new SpotifyApiSettings()),
            helperService,
            spotifyCookieLogger.Object);
        var providerStatusManager = new ProviderStatusManager(
            configuration,
            httpClientFactory ?? Mock.Of<IHttpClientFactory>(),
            Mock.Of<ILogger<ProviderStatusManager>>(),
            Options.Create(new SpotifyApiSettings()),
            Options.Create(new AppleDownloadSettings()),
            Options.Create(new DeezerSettings()),
            Options.Create(new QobuzSettings()),
            Options.Create(new SquidWTFSettings()),
            new SquidWtfEndpointCatalog([], []),
            healthStore);
        var effectiveSecretStore = secretStore ?? CreateSecretStore();
        var clock = new SystemPlatformClock();
        var signal = new RuntimeSettingsChangeSignal();
        var durableSettings = new DurableRuntimeSettingsService(_factory, configuration, clock, signal);
        var migration = new LegacyEnvMigrationService(_factory, durableSettings, effectiveSecretStore, clock);
        var services = new ServiceCollection()
            .AddSingleton(providerStatusManager)
            .AddSingleton<IDbContextFactory<AllstarrDbContext>>(_factory)
            .AddSingleton(durableSettings)
            .AddSingleton<IDurableRuntimeSettings>(durableSettings)
            .AddSingleton(migration)
            .AddSingleton(effectiveSecretStore);
        httpContext.RequestServices = services.BuildServiceProvider();

        var controller = new ConfigController(
            logger.Object,
            configuration,
            Options.Create(new SpotifyApiSettings()),
            Options.Create(new JellyfinSettings()),
            Options.Create(new SubsonicSettings()),
            Options.Create(new DeezerSettings()),
            Options.Create(new QobuzSettings()),
            Options.Create(new SquidWTFSettings()),
            Options.Create(new AppleDownloadSettings()),
            Options.Create(new MusicBrainzSettings()),
            Options.Create(new SpotifyImportSettings()),
            Options.Create(new ScrobblingSettings()),
            helperService,
            spotifySessionCookieService,
            redisCache)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = httpContext
            }
        };

        return controller;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private DurableProviderHealthStore CreateHealthStore() => new(
        _factory,
        _storageState,
        new ProviderHealthOptions
        {
            FailureThreshold = 3,
            CircuitOpenSeconds = 30,
            SampleTtlSeconds = 300,
            RollupWindowMinutes = 15,
            SampleRetentionDays = 7
        },
        new SystemPlatformClock());

    private EncryptedSecretStore CreateSecretStore()
    {
        var options = new SecretStoreOptions { KeyRingPath = _keyRingPath };
        return new EncryptedSecretStore(
            _factory,
            new FileSecretKeyRingProvider(options),
            options,
            new SystemPlatformClock());
    }

    private void WriteKeyRing()
    {
        var document = JsonSerializer.Serialize(new
        {
            activeKeyId = "fixture-key",
            keys = new Dictionary<string, string>
            {
                ["fixture-key"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            }
        });
        File.WriteAllText(_keyRingPath, document);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                _keyRingPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static void AssertForbidden(IActionResult result)
    {
        var forbidden = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);

        var payload = JsonSerializer.Serialize(forbidden.Value);
        using var document = JsonDocument.Parse(payload);
        Assert.Equal("Administrator permissions required", document.RootElement.GetProperty("error").GetString());
    }

    private sealed class TestDbContextFactory(DbContextOptions<AllstarrDbContext> options)
        : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);

        public Task<AllstarrDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AllstarrDbContext(options));
    }

    private sealed class HandlerHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class CapturingHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public string? CookieHeader { get; private set; }
        public HttpMethod? Method { get; private set; }
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CookieHeader = request.Headers.TryGetValues("Cookie", out var cookies)
                ? Assert.Single(cookies)
                : null;
            Method = request.Method;
            RequestUri = request.RequestUri;
            return Task.FromResult(response);
        }
    }
}
