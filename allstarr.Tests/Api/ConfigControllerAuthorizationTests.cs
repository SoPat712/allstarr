using allstarr.Core.Capabilities;
using allstarr.Core.Routing;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
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
using allstarr.Core.Storage;
using allstarr.Core.Health;
using allstarr.Core.Operations;
using allstarr.Core.Secrets;
using allstarr.Core.Settings;
using allstarr.Core.Configuration;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public class ConfigControllerAuthorizationTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "allstarr-tests",
        Guid.NewGuid().ToString("N"));
    private readonly Guid _providerAccountId = Guid.CreateVersion7();
    private readonly Guid _userId = Guid.CreateVersion7();
    private SqliteTestDatabase _database = null!;
    private TestDbContextFactory _factory = null!;
    private DurableStorageState _storageState = null!;
    private readonly string _keyRingPath;

    public ConfigControllerAuthorizationTests()
    {
        Directory.CreateDirectory(_root);
        _keyRingPath = Path.Combine(_root, "keyring.json");
        WriteKeyRing();
    }

    public async Task InitializeAsync()
    {
        _database = await SqliteTestDatabase.CreateAsync();
        _factory = new TestDbContextFactory(_database.Options);
        _storageState = new DurableStorageState(new StorageOptions
        {
            DataDirectory = _database.StorageOptions.DataDirectory,
            DatabaseFileName = _database.StorageOptions.DatabaseFileName
        });
        _storageState.Set(DurableStorageReadiness.Ready, "fixture");
        await using var context = await _factory.CreateDbContextAsync();
        context.Users.Add(new UserRecord
        {
            Id = _userId,
            DisplayName = "Test administrator",
            Enabled = true,
            IsAdmin = true,
            BackendType = "jellyfin",
            BackendInstanceId = "primary",
            BackendPrincipalId = "administrator",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        context.ProviderAccounts.Add(new ProviderAccountRecord
        {
            Id = _providerAccountId,
            ProviderId = "deezer",
            DisplayName = "Health fixture",
            Enabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await context.SaveChangesAsync();
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
        var cache = new TestMemoryApplicationCache();
        await cache.SetStringAsync("lyrics:v2:fixture", "cached");
        var controller = CreateController(
            CreateHttpContextWithSession(isAdmin: true),
            applicationCache: cache);

        var result = Assert.IsType<OkObjectResult>(await controller.UpdateConfig(new ConfigUpdateRequest
        {
            Updates = new Dictionary<string, string> { ["CACHE_LYRICS_DAYS"] = "45" }
        }));

        Assert.Equal(StatusCodes.Status200OK, result.StatusCode);
        Assert.Equal("CACHE_LYRICS_DAYS=14\n", await File.ReadAllTextAsync(envPath));
        await using var db = await _factory.CreateDbContextAsync();
        var setting = Assert.Single(await db.RuntimeSettings.ToListAsync());
        Assert.Null(setting.OwnerUserId);
        Assert.Equal("Cache:LyricsDays", setting.Key);
        Assert.Equal("45", setting.ValueJson);
        Assert.False(await cache.ExistsAsync("lyrics:v2:fixture"));

        var getResult = Assert.IsType<OkObjectResult>(await controller.GetConfig());
        using var config = JsonDocument.Parse(JsonSerializer.Serialize(
            getResult.Value,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(45, config.RootElement.GetProperty("cache").GetProperty("lyricsDays").GetInt32());
    }

    [Fact]
    public async Task UpdateConfig_SavesTheSharedAudioQualitySetting()
    {
        var controller = CreateController(CreateHttpContextWithSession(isAdmin: true));

        var result = Assert.IsType<OkObjectResult>(await controller.UpdateConfig(new ConfigUpdateRequest
        {
            Updates = new Dictionary<string, string> { ["AUDIO_QUALITY"] = "HiResLossless" }
        }));

        Assert.Equal(StatusCodes.Status200OK, result.StatusCode);
        await using var db = await _factory.CreateDbContextAsync();
        var setting = Assert.Single(await db.RuntimeSettings.ToListAsync());
        Assert.Equal("Audio:Quality", setting.Key);
        Assert.Equal("\"HiResLossless\"", setting.ValueJson);

        var getResult = Assert.IsType<OkObjectResult>(await controller.GetConfig());
        using var config = JsonDocument.Parse(JsonSerializer.Serialize(
            getResult.Value,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal("HiResLossless",
            config.RootElement.GetProperty("audio").GetProperty("quality").GetString());
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
        Assert.Empty(await db.RuntimeSettings.ToListAsync());
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
        AssertForbidden(await controller.ResetEnvMigration(
            new ConfigController.ResetLegacyEnvMigrationRequest
            {
                PreviewToken = "token"
            }));
    }

    [Fact]
    public async Task MigrationController_ReturnsNonCachedAdminPreviewAndRequiresExplicitConfirmation()
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
        Assert.Equal("no-store", controller.Response.Headers.CacheControl);
        Assert.Equal("no-cache", controller.Response.Headers.Pragma);
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
        Assert.Contains(await db.RuntimeSettings.ToListAsync(), item => item.Key == "Cache:LyricsDays");
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
            Assert.Equal("shared", status.GetProperty("accountScope").GetString());
            Assert.Equal(JsonValueKind.Null, status.GetProperty("testedAt").ValueKind);
            Assert.Equal("unknown", status.GetProperty("health").GetString());
            Assert.True(status.TryGetProperty("configuration", out _));
            Assert.True(status.TryGetProperty("capability", out _));
        });
    }

    [Fact]
    public async Task ProviderHealthEndpoints_RequireAdministratorSessionAndAllowPublicCapabilityProbe()
    {
        var nonAdministrator = CreateController(CreateHttpContextWithSession(isAdmin: false));
        AssertForbidden(await nonAdministrator.GetProvidersStatus());
        AssertForbidden(await nonAdministrator.TestProvider(
            "deezer",
            ProviderCapabilities.Metadata,
            _providerAccountId));

        var administrator = CreateController(
            CreateHttpContextWithSession(isAdmin: true),
            healthStore: CreateHealthStore());
        var publicProbe = Assert.IsType<OkObjectResult>(await administrator.TestProvider(
            "deezer",
            ProviderCapabilities.Metadata));
        Assert.Equal(StatusCodes.Status200OK, publicProbe.StatusCode);
        Assert.IsType<BadRequestObjectResult>(await administrator.TestProvider(
            "deezer",
            ProviderCapabilities.Download));
        var publicHealth = administrator.HttpContext.RequestServices.GetRequiredService<ProviderRuntimeHealth>();
        Assert.NotNull(publicHealth.Get(ProviderRuntimeStatusKey.CreateAccountFree("deezer", ProviderCapabilities.Metadata)));
        Assert.Null(publicHealth.Get(ProviderRuntimeStatusKey.CreateManaged("deezer", ProviderCapabilities.Metadata, _providerAccountId)));
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
    public async Task ManagedMediaTest_UsesExactEncryptedAccountAndVolatileStreamingStatus()
    {
        var secretStore = CreateSecretStore();
        var secret = await secretStore.StoreAsync(
            userId: null,
            purpose: $"provider-account:deezer:{_providerAccountId:N}",
            plaintext: Encoding.UTF8.GetBytes("{\"arl\":\"selected-account-arl\"}"));
        await using (var context = await _factory.CreateDbContextAsync())
        {
            var account = await context.ProviderAccounts.SingleAsync(
                item => item.Id == _providerAccountId);
            account.SecretReferenceId = secret.Id;
            await context.SaveChangesAsync();
        }

        var healthStore = CreateHealthStore();
        var accountRevision = 0L;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            accountRevision = (await db.ProviderAccounts.SingleAsync()).Revision;
            var recordingId = Guid.CreateVersion7();
            db.CanonicalRecordings.Add(new CanonicalRecordingRecord { Id = recordingId, CreatedByUserId = _userId, Title = "Sample", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
            db.ProviderTrackIdentities.Add(new ProviderTrackIdentityRecord
            {
                Id = Guid.CreateVersion7(),
                CanonicalRecordingId = recordingId,
                DecisionVersion = 1,
                VerificationMethod = "fixture",
                ProviderId = "deezer",
                ResourceKind = ProviderResourceKind.Track,
                Scope = ProviderIdentityScope.Catalog,
                ExternalId = "sample-track",
                ExternalIdHash = new string('a', 64),
                Verification = ProviderIdentityVerification.Verified,
                VerifiedAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }
        var streaming = new Mock<IProviderStreamingCapability>();
        streaming.Setup(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderStreamLeaseRequest>()))
            .Returns(async (ProviderExecutionContext execution, ProviderStreamLeaseRequest request) =>
            {
                Assert.Equal(_providerAccountId, execution.Account!.AccountId);
                Assert.Equal(accountRevision, execution.Account.Revision);
                using var credential = await secretStore.OpenAsync(secret.Id,
                    new SecretAccessContext(null, $"provider-account:deezer:{_providerAccountId:N}", AllowShared: true));
                Assert.Contains("selected-account-arl", credential.ReadUtf8(), StringComparison.Ordinal);
                Assert.Equal("sample-track", request.TrackId.Value);
                return ProviderOutcome<ProviderStreamLease>.Success(new ProviderStreamLease("sample",
                    new Uri("https://fixture.invalid/media"), DateTimeOffset.UtcNow.AddMinutes(1), false, false,
                    new ProviderMediaFormat("audio/flac", "flac", "flac", 1411000, 44100, 16, 2),
                    ProviderStreamRetryBehavior.DoNotRetry, (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new ByteArrayContent(new byte[80000]) })));
            });
        IProviderStreamingCapability? registered = streaming.Object;
        var registry = new Mock<IProviderRegistry>();
        registry.Setup(item => item.TryGetCapability<IProviderStreamingCapability>("deezer", ProviderCapabilityKind.Streaming, out registered)).Returns(true);
        registry.Setup(item => item.GetRequired("deezer")).Returns(new ProviderDescriptor("deezer", "Deezer", "Fixture provider",
            ProviderOrigin.BuiltIn, "1", "1", [new ProviderCapabilityDescriptor(ProviderCapabilityKind.Streaming,
                ProviderCapabilitySupportState.Supported, ProviderAccountRequirement.Required, "1", ["getStreamLease"], [ProviderAccountScope.Shared])],
            new ProviderPermissionDescriptor()));
        var accounts = new Mock<IProviderRouteAccountResolver>();
        accounts.Setup(item => item.ResolveAsync(It.Is<ProviderRouteAccountRequest>(request =>
                request.Actor.EffectiveUserId == _userId && request.RequestedAccountId == _providerAccountId), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderRouteAccountResolution(new ProviderAccountContext(_providerAccountId, "deezer",
                ProviderAccountScope.Shared, accountRevision, secretReferenceId: secret.Id), accountRevision));
        using var selector = new ProviderCtsTrackSelector(_factory);
        var runner = new ProviderCtsDiagnosticRunner(registry.Object, accounts.Object, selector, healthStore);
        var controller = CreateController(CreateHttpContextWithSession(isAdmin: true), healthStore: healthStore,
            secretStore: secretStore, diagnosticRunner: runner);

        var result = Assert.IsType<OkObjectResult>(await controller.TestProvider(
            "deezer",
            ProviderCapabilities.Streaming,
            _providerAccountId));
        using (var resultDocument = JsonDocument.Parse(JsonSerializer.Serialize(result.Value)))
        {
            Assert.True(resultDocument.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("healthy", resultDocument.RootElement.GetProperty("health").GetString());
            Assert.Equal(_providerAccountId, resultDocument.RootElement.GetProperty("providerAccountId").GetGuid());
        }

        var observation = healthStore.Get(ProviderRuntimeStatusKey.CreateManaged("deezer", "streaming", _providerAccountId), accountRevision);
        Assert.NotNull(observation);
        Assert.Equal(allstarr.Services.Common.ProviderHealthState.Healthy, observation.Health);
        Assert.Null(healthStore.Get(ProviderRuntimeStatusKey.CreateManaged("deezer", "download", _providerAccountId)));
        using var sampleDocument = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
        Assert.Equal(65536, sampleDocument.RootElement.GetProperty("sampleBytes").GetInt32());
        Assert.Equal("sequential", sampleDocument.RootElement.GetProperty("seekRung").GetString());
        streaming.VerifyAll();
        var restartedHealthStore = CreateHealthStore();
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
        Assert.Equal("unknown", download.GetProperty("health").GetString());
        Assert.Equal(JsonValueKind.Null, download.GetProperty("testedAt").ValueKind);
    }

    [Fact]
    public async Task ManagedConnectionTest_WithoutKnownTrackKeepsHealthyConnectionAndExplainsMissingSample()
    {
        var secretStore = CreateSecretStore();
        var secret = await secretStore.StoreAsync(
            userId: null,
            purpose: $"provider-account:deezer:{_providerAccountId:N}",
            plaintext: Encoding.UTF8.GetBytes("{\"arl\":\"selected-account-arl\"}"));
        long accountRevision;
        await using (var context = await _factory.CreateDbContextAsync())
        {
            var account = await context.ProviderAccounts.SingleAsync(item => item.Id == _providerAccountId);
            account.SecretReferenceId = secret.Id;
            await context.SaveChangesAsync();
            accountRevision = account.Revision;
        }

        var streaming = new Mock<IProviderStreamingCapability>(MockBehavior.Strict);
        IProviderStreamingCapability? registered = streaming.Object;
        var registry = new Mock<IProviderRegistry>();
        registry.Setup(item => item.TryGetCapability<IProviderStreamingCapability>("deezer", ProviderCapabilityKind.Streaming, out registered)).Returns(true);
        registry.Setup(item => item.GetRequired("deezer")).Returns(new ProviderDescriptor("deezer", "Deezer", "Fixture provider",
            ProviderOrigin.BuiltIn, "1", "1", [new ProviderCapabilityDescriptor(ProviderCapabilityKind.Streaming,
                ProviderCapabilitySupportState.Supported, ProviderAccountRequirement.Required, "1", ["getStreamLease"], [ProviderAccountScope.Shared])],
            new ProviderPermissionDescriptor()));
        var accounts = new Mock<IProviderRouteAccountResolver>();
        accounts.Setup(item => item.ResolveAsync(It.IsAny<ProviderRouteAccountRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderRouteAccountResolution(new ProviderAccountContext(_providerAccountId, "deezer",
                ProviderAccountScope.Shared, accountRevision, secretReferenceId: secret.Id), accountRevision));
        var healthStore = CreateHealthStore();
        using var selector = new ProviderCtsTrackSelector(_factory);
        var runner = new ProviderCtsDiagnosticRunner(registry.Object, accounts.Object, selector, healthStore);
        var controller = CreateController(CreateHttpContextWithSession(isAdmin: true),
            httpClientFactory: new HandlerHttpClientFactory(new FixtureJsonHandler(
                """{"id":3135556,"results":{"USER":{"USER_ID":5}}}""")),
            healthStore: healthStore, secretStore: secretStore, diagnosticRunner: runner);

        var result = Assert.IsType<OkObjectResult>(await controller.TestProvider("deezer", null, _providerAccountId));

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
        Assert.True(document.RootElement.GetProperty("healthy").GetBoolean());
        Assert.Equal("api-latency", document.RootElement.GetProperty("metric").GetString());
        Assert.Equal("media_sample_needs_known_track", document.RootElement.GetProperty("reasonCode").GetString());
        Assert.False(document.RootElement.TryGetProperty("seekRung", out _));
        streaming.VerifyNoOtherCalls();
    }

    private HttpContext CreateHttpContextWithSession(bool isAdmin)
    {
        var context = new DefaultHttpContext();
        context.Connection.LocalPort = 5275;
        context.Items[AdminAuthSessionService.HttpContextSessionItemKey] = new AdminAuthSession
        {
            SessionId = "session-id",
            UserId = "administrator",
            BackendType = "jellyfin",
            BackendInstanceId = "primary",
            UserName = "user",
            IsAdministrator = isAdmin,
            JellyfinAccessToken = "token",
            JellyfinServerId = "server-id",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
            LastSeenUtc = DateTime.UtcNow,
            AllstarrUserId = _userId
        };

        return context;
    }

    private ConfigController CreateController(
        HttpContext httpContext,
        Dictionary<string, string?>? configValues = null,
        IHttpClientFactory? httpClientFactory = null,
        ProviderRuntimeHealth? healthStore = null,
        EncryptedSecretStore? secretStore = null,
        IApplicationCache? applicationCache = null,
        ProviderCtsDiagnosticRunner? diagnosticRunner = null)
    {
        var logger = new Mock<ILogger<ConfigController>>();
        configValues ??= new Dictionary<string, string?>();
        configValues.TryAdd("Backend:Type", "Jellyfin");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues)
            .Build();

        applicationCache ??= new DisabledApplicationCache();
        var spotifySessionCookieService = new SpotifySessionCookieService(
            Options.Create(new SpotifyApiSettings()));
        var providerStatusManager = new ProviderStatusManager(
            configuration,
            httpClientFactory ?? Mock.Of<IHttpClientFactory>(),
            Mock.Of<ILogger<ProviderStatusManager>>(),
            Options.Create(new SpotifyApiSettings()),
            Options.Create(new AppleDownloadSettings()),
            Options.Create(new DeezerSettings()),
            Options.Create(new QobuzSettings()),
            extensionManager: null,
            healthStore);
        var effectiveSecretStore = secretStore ?? CreateSecretStore();
        var clock = new SystemPlatformClock();
        var signal = new RuntimeSettingsChangeSignal();
        var durableSettings = new DurableRuntimeSettingsService(_factory, configuration, clock, signal);
        var effectiveProviderPolicies = new EffectiveProviderPolicyResolver(durableSettings);
        var migration = new LegacyEnvMigrationService(_factory, durableSettings, effectiveSecretStore, clock);
        var services = new ServiceCollection()
            .AddSingleton(providerStatusManager)
            .AddSingleton<IDbContextFactory<AllstarrDbContext>>(_factory)
            .AddSingleton(durableSettings)
            .AddSingleton<IDurableRuntimeSettings>(durableSettings)
            .AddSingleton<IEffectiveProviderPolicyResolver>(effectiveProviderPolicies)
            .AddSingleton(migration)
            .AddSingleton(effectiveSecretStore);
        services.AddSingleton(healthStore ?? new ProviderRuntimeHealth());
        if (diagnosticRunner != null) services.AddSingleton(diagnosticRunner);
        httpContext.RequestServices = services.BuildServiceProvider();

        var controller = new ConfigController(
            logger.Object,
            configuration,
            Options.Create(new SpotifyApiSettings()),
            Options.Create(new JellyfinSettings()),
            Options.Create(new SubsonicSettings()),
            Options.Create(new DeezerSettings()),
            Options.Create(new QobuzSettings()),
            Options.Create(new AppleDownloadSettings()),
            Options.Create(new MusicBrainzSettings()),
            Options.Create(new SpotifyImportSettings()),
            Options.Create(new ScrobblingSettings()),
            spotifySessionCookieService,
            applicationCache)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = httpContext
            }
        };

        return controller;
    }

    public async Task DisposeAsync()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static ProviderRuntimeHealth CreateHealthStore() => new(new ProviderHealthOptions
    {
        FailureThreshold = 3,
        CircuitOpenSeconds = 30,
        SampleTtlSeconds = 300
    }, new SystemPlatformClock());

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

    private sealed class FixtureJsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
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
