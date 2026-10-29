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
    [Fact]
    public async Task PublicStreamingProbeUsesTheSameMediaMeasurement()
    {
        var streaming = new Mock<IProviderStreamingCapability>();
        streaming.SetupGet(item => item.ProviderId).Returns("public-source");
        streaming.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Streaming);
        streaming.Setup(item => item.GetStreamLeaseAsync(It.Is<ProviderExecutionContext>(context => context.Account == null),
                It.Is<ProviderStreamLeaseRequest>(request => request.TrackId.Value == "native-track")))
            .ReturnsAsync(ProviderOutcome<ProviderStreamLease>.Success(new("lease", new("https://media.example.test/track"),
                DateTimeOffset.UtcNow.AddMinutes(1), false, false, new("audio/flac", "flac", "flac"),
                ProviderStreamRetryBehavior.DoNotRetry, (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new ByteArrayContent([102, 76, 97, 67]) }))));
        var metadata = new Mock<IProviderMetadataCapability>();
        metadata.SetupGet(item => item.ProviderId).Returns("public-source");
        metadata.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Metadata);
        metadata.Setup(item => item.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Success(new("public-source",
                [new(new("public-source", ProviderResourceKind.Track, "native-track"), "Song", [new("Artist")])])));
        var registry = new ProviderRegistry([new(new ProviderDescriptor("public-source", "Public source", "Fixture", ProviderOrigin.Extension,
            "1", "1", [new(ProviderCapabilityKind.Streaming, ProviderCapabilitySupportState.Supported, ProviderAccountRequirement.None,
                "1", ["getStreamLease", "probeStream"]), new(ProviderCapabilityKind.Metadata, ProviderCapabilitySupportState.Supported,
                ProviderAccountRequirement.None, "1", ["searchTracks"])], new(), entryPoint: "index.js"), [streaming.Object, metadata.Object])]);
        using var selector = new ProviderCtsTrackSelector(_factory);
        var media = new ProviderCtsDiagnosticRunner(registry, Mock.Of<IProviderRouteAccountResolver>(), selector, new ProviderRuntimeHealth());
        var controller = CreateController(CreateHttpContextWithSession(true), diagnosticRunner: media, providerRegistry: registry);

        var result = Assert.IsType<OkObjectResult>(await Diagnostics(controller).TestProvider("public-source", "streaming"));

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
        Assert.True(json.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("cts", json.RootElement.GetProperty("metric").GetString());
        Assert.Equal(4, json.RootElement.GetProperty("sampleBytes").GetInt32());
        streaming.VerifyAll();
    }

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
    public async Task Song_link_key_is_saved_with_settings_and_only_its_mask_is_returned()
    {
        var controller = CreateController(CreateHttpContextWithSession(true));
        var result = Assert.IsType<OkObjectResult>(await controller.UpdateConfig(new()
        {
            Updates = new() { ["ODESLI_API_KEY"] = "private-song-link", ["ODESLI_ENABLED"] = "true" }
        }));
        var response = JsonSerializer.Serialize(result.Value);
        Assert.DoesNotContain("private-song-link", response, StringComparison.Ordinal);
        Assert.DoesNotContain("ApiKeyReference", response, StringComparison.Ordinal);
        using (var config = JsonDocument.Parse(JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(await controller.GetConfig()).Value)))
        {
            Assert.Equal("********", config.RootElement.GetProperty("operatorSettings").GetProperty("Odesli:ApiKey").GetString());
            Assert.DoesNotContain("private-song-link", config.RootElement.GetRawText(), StringComparison.Ordinal);
            Assert.DoesNotContain("ApiKeyReference", config.RootElement.GetRawText(), StringComparison.Ordinal);
        }
        Assert.IsType<OkObjectResult>(await controller.UpdateConfig(new()
        { Updates = new() { ["ODESLI_API_KEY"] = "" } }));
        using var cleared = JsonDocument.Parse(JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(await controller.GetConfig()).Value));
        Assert.Equal("", cleared.RootElement.GetProperty("operatorSettings").GetProperty("Odesli:ApiKey").GetString());
        await using var db = await _factory.CreateDbContextAsync();
        Assert.NotNull((await db.SecretReferences.SingleAsync()).RevokedAt);
        Assert.DoesNotContain("private-song-link", JsonSerializer.Serialize(await db.AuditEvents.ToListAsync()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpcomingPreparationSettingsUseTheExistingHouseholdSettingsApi()
    {
        var controller = CreateController(CreateHttpContextWithSession(true));
        Assert.IsType<OkObjectResult>(await controller.UpdateConfig(new()
        { Updates = new() { ["PLAYBACK_PREPARE_UPCOMING_SONGS"] = "false", ["PLAYBACK_PREPARE_UPCOMING_COUNT"] = "1" } }));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(await controller.GetConfig()).Value));
        var playback = json.RootElement.GetProperty("playback");
        Assert.False(playback.GetProperty("prepareUpcomingSongs").GetBoolean());
        Assert.Equal(1, playback.GetProperty("prepareUpcomingCount").GetInt32());
        Assert.IsType<OkObjectResult>(await controller.UpdateConfig(new()
        { Updates = new() { ["PLAYBACK_PREPARE_UPCOMING_SONGS"] = "true" } }));
        using var enabled = JsonDocument.Parse(JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(await controller.GetConfig()).Value));
        Assert.True(enabled.RootElement.GetProperty("playback").GetProperty("prepareUpcomingSongs").GetBoolean());
    }

    [Fact]
    public async Task OperatorSettingApiPersistsValidatedPolicyAndReportsRestart()
    {
        var controller = CreateController(CreateHttpContextWithSession(true));
        var result = Assert.IsType<OkObjectResult>(await controller.UpdateConfig(new()
        { Updates = new() { ["CORS_ALLOWED_ORIGINS"] = "https://player.example.test", ["CORS_ALLOW_CREDENTIALS"] = "true" } }));
        using var response = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
        Assert.True(response.RootElement.GetProperty("requiresRestart").GetBoolean());
        var rejected = Assert.IsType<BadRequestObjectResult>(await controller.UpdateConfig(new()
        { Updates = new() { ["CORS_ALLOWED_ORIGINS"] = "*" } }));
        Assert.Equal(400, rejected.StatusCode);
        await using var db = await _factory.CreateDbContextAsync();
        var origins = await db.RuntimeSettings.SingleAsync(item => item.Key == "Cors:AllowedOrigins");
        Assert.Equal(1, origins.Revision);
        Assert.Contains("player.example.test", origins.ValueJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateConfig_WithoutAdminSession_ReturnsForbidden()
    {
        var controller = CreateController(CreateHttpContextWithSession(isAdmin: false));
        var result = await controller.UpdateConfig(new ConfigUpdateRequest
        {
            Updates = new Dictionary<string, string> { ["PLAYBACK_PREPARE_UPCOMING_SONGS"] = "true" }
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
    public async Task UpdateConfig_SavesAndProjectsOptionalLyricsPlusEndpoint()
    {
        var controller = CreateController(CreateHttpContextWithSession(isAdmin: true));
        Assert.IsType<OkObjectResult>(await controller.UpdateConfig(new ConfigUpdateRequest
        { Updates = new Dictionary<string, string> { ["LYRICSPLUS_URL"] = "http://lyrics:3000/prefix" } }));
        await using var db = await _factory.CreateDbContextAsync();
        var stored = Assert.Single(await db.RuntimeSettings.ToListAsync());
        Assert.Equal("LyricsPlus:BaseUrl", stored.Key);
        var result = Assert.IsType<OkObjectResult>(await controller.GetConfig());
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal("http://lyrics:3000/prefix", json.RootElement.GetProperty("lyricsPlus").GetProperty("baseUrl").GetString());
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
        var controller = CreateMigrationController(CreateHttpContextWithSession(isAdmin: false));
        AssertForbidden(await controller.GetEnvMigrationStatus());
        AssertForbidden(await controller.PreviewMountedEnvironment());
        var bytes = Encoding.UTF8.GetBytes("CACHE_LYRICS_DAYS=30");
        AssertForbidden(await controller.PreviewEnvMigration(
            new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "legacy.env")));
        AssertForbidden(await controller.ApplyEnvMigration(new LegacyConfigurationController.ApplyLegacyEnvMigrationRequest
        {
            PreviewToken = "token",
            Revision = "revision",
            Confirmed = true
        }));
        AssertForbidden(await controller.ResetEnvMigration(
            new LegacyConfigurationController.ResetLegacyEnvMigrationRequest
            {
                PreviewToken = "token"
            }));
    }

    [Fact]
    public async Task MigrationController_ReturnsNonCachedAdminPreviewAndRequiresExplicitConfirmation()
    {
        var controller = CreateMigrationController(CreateHttpContextWithSession(isAdmin: true));
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
            new LegacyConfigurationController.ApplyLegacyEnvMigrationRequest
            {
                PreviewToken = token,
                Revision = revision,
                Confirmed = false
            }));
        Assert.Equal(StatusCodes.Status400BadRequest, unconfirmed.StatusCode);

        var applied = Assert.IsType<OkObjectResult>(await controller.ApplyEnvMigration(
            new LegacyConfigurationController.ApplyLegacyEnvMigrationRequest
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
    public async Task MountedMigration_UsesTheSameReadOnlyPreviewAndExplicitApplyBoundary()
    {
        var path = Path.Combine(_root, ".env");
        const string source = "CACHE_LYRICS_DAYS=35\nDEEZER_ARL=mounted-private-fixture";
        await File.WriteAllTextAsync(path, source);
        var modified = File.GetLastWriteTimeUtc(path);
        var environment = Mock.Of<Microsoft.Extensions.Hosting.IHostEnvironment>(item =>
            item.EnvironmentName == "Development" && item.ContentRootPath == Path.Combine(_root, "app"));
        var controller = CreateMigrationController(CreateHttpContextWithSession(true), new MountedLegacyEnvironment(environment));
        var status = Assert.IsType<LegacyEnvMigrationStatus>(Assert.IsType<OkObjectResult>(await controller.GetEnvMigrationStatus()).Value);
        Assert.True(status.SourcePresent);
        var preview = Assert.IsType<LegacyEnvMigrationPreview>(Assert.IsType<OkObjectResult>(await controller.PreviewMountedEnvironment()).Value);
        Assert.DoesNotContain("mounted-private-fixture", JsonSerializer.Serialize(preview), StringComparison.Ordinal);
        Assert.Equal("no-store", controller.Response.Headers.CacheControl);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Empty(await db.RuntimeSettings.ToListAsync());
        Assert.Empty(await db.LegacyEnvImports.ToListAsync());
        Assert.Equal(source, await File.ReadAllTextAsync(path));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(path));
        Assert.IsType<BadRequestObjectResult>(await controller.ApplyEnvMigration(new()
        { PreviewToken = preview.PreviewToken, Revision = preview.Revision, Confirmed = false }));
        Assert.Empty(await db.LegacyEnvImports.ToListAsync());
        Assert.IsType<OkObjectResult>(await controller.ApplyEnvMigration(new()
        { PreviewToken = preview.PreviewToken, Revision = preview.Revision, Confirmed = true }));
        Assert.Single(await db.LegacyEnvImports.ToListAsync());
        Assert.Equal(source, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task GetProvidersStatus_ReturnsExactAccountCapabilitySnapshotsWithNoInventedTimestamp()
    {
        var controller = CreateController(CreateHttpContextWithSession(isAdmin: true));

        var result = Assert.IsType<OkObjectResult>(await Diagnostics(controller).GetProvidersStatus());
        var json = JsonSerializer.Serialize(result.Value);
        using var document = JsonDocument.Parse(json);
        var statuses = document.RootElement.EnumerateArray().Where(item =>
            item.GetProperty("providerAccountId").ValueKind != JsonValueKind.Null).ToArray();

        Assert.Equal(4, statuses.Length);
        Assert.All(statuses, status =>
        {
            Assert.Equal(_providerAccountId, status.GetProperty("providerAccountId").GetGuid());
            Assert.Equal("shared", status.GetProperty("accountScope").GetString());
            Assert.Equal(JsonValueKind.Null, status.GetProperty("testedAt").ValueKind);
            Assert.Equal(JsonValueKind.Null, status.GetProperty("automaticRetryAt").ValueKind);
            Assert.Equal(JsonValueKind.Null, status.GetProperty("failureCount").ValueKind);
            Assert.Equal(JsonValueKind.Null, status.GetProperty("automaticPauseReasonCode").ValueKind);
            Assert.Equal("unknown", status.GetProperty("health").GetString());
            Assert.True(status.TryGetProperty("configuration", out _));
            Assert.True(status.TryGetProperty("capability", out _));
        });
    }

    [Fact]
    public async Task GetProvidersStatus_ExposesOnlyCurrentAutomaticPauseEvidence()
    {
        var health = CreateHealthStore();
        var key = ProviderRuntimeStatusKey.CreateAccountFree("lrclib", ProviderCapabilities.Lyrics);
        health.Record(key, null, false, 4, "rate-limited", retryAfter: TimeSpan.FromMinutes(5));
        var controller = CreateController(CreateHttpContextWithSession(isAdmin: true), healthStore: health);

        var result = Assert.IsType<OkObjectResult>(await Diagnostics(controller).GetProvidersStatus());
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
        var status = document.RootElement.EnumerateArray().Single(item =>
            item.GetProperty("provider").GetString() == "lrclib" &&
            item.GetProperty("capability").GetString() == ProviderCapabilities.Lyrics);

        Assert.True(status.GetProperty("automaticRetryAt").GetDateTimeOffset() > DateTimeOffset.UtcNow);
        Assert.Equal(1, status.GetProperty("failureCount").GetInt32());
        Assert.Equal("rate-limited", status.GetProperty("automaticPauseReasonCode").GetString());
        Assert.False(status.GetProperty("canAttempt").GetBoolean());
    }

    [Theory]
    [InlineData("manualOnly")]
    [InlineData("off")]
    public async Task NonautomaticSourcesDoNotExposeOrResumeAnAutomaticPause(string mode)
    {
        var health = CreateHealthStore();
        var key = ProviderRuntimeStatusKey.CreateAccountFree("lrclib", ProviderCapabilities.Lyrics);
        for (var index = 0; index < 3; index++) health.Record(key, null, false, 4, "timeout");
        var controller = Diagnostics(CreateController(CreateHttpContextWithSession(true), healthStore: health));
        var settings = controller.HttpContext.RequestServices.GetRequiredService<IDurableRuntimeSettings>();
        var preferences = await settings.GetPreferencesAsync(null);
        await settings.UpdateProviderModeAsync(null, "lrclib", mode, preferences.Revision, _userId);
        var result = Assert.IsType<OkObjectResult>(await controller.GetProvidersStatus());
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
        var status = json.RootElement.EnumerateArray().Single(item => item.GetProperty("provider").GetString() == "lrclib");
        Assert.Equal(JsonValueKind.Null, status.GetProperty("automaticPausedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, status.GetProperty("automaticRetryAt").ValueKind);
        Assert.IsType<ConflictObjectResult>(await controller.ResumeProvider("lrclib", "lyrics"));
        Assert.True(health.IsCircuitOpen(health.Get(key)!));
    }

    [Theory]
    [InlineData("lrclib", "lyrics")]
    [InlineData("fixture-extension", "metadata")]
    public async Task ResumeSourceIsAdministratorOnlyAndClearsPauseWithoutProviderCalls(string provider, string capability)
    {
        var implementation = new Mock<IProviderMetadataCapability>(MockBehavior.Strict);
        implementation.SetupGet(item => item.ProviderId).Returns("fixture-extension");
        implementation.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Metadata);
        var registry = new ProviderRegistry([new(new ProviderDescriptor("fixture-extension", "Fixture", "Fixture source",
            ProviderOrigin.Extension, "1", "1", [new(ProviderCapabilityKind.Metadata, ProviderCapabilitySupportState.Supported,
                ProviderAccountRequirement.None, "1", ["searchTracks"])], new(), entryPoint: "index.js"), [implementation.Object])]);
        var clients = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        var health = CreateHealthStore();
        var key = ProviderRuntimeStatusKey.CreateAccountFree(provider, capability);
        for (var index = 0; index < 3; index++) health.Record(key, null, false, 4, "timeout", recordAttempt: true);
        var denied = Diagnostics(CreateController(CreateHttpContextWithSession(false), healthStore: health, providerRegistry: registry, httpClientFactory: clients.Object));
        AssertForbidden(await denied.ResumeProvider(provider, capability));
        Assert.True(health.IsCircuitOpen(health.Get(key)!));
        var controller = Diagnostics(CreateController(CreateHttpContextWithSession(true), healthStore: health, providerRegistry: registry, httpClientFactory: clients.Object));
        var before = Assert.IsType<OkObjectResult>(await controller.GetProvidersStatus());
        using var status = JsonDocument.Parse(JsonSerializer.Serialize(before.Value));
        Assert.NotEqual(JsonValueKind.Null, status.RootElement.EnumerateArray().Single(item =>
            item.GetProperty("provider").GetString() == provider).GetProperty("automaticPausedAt").ValueKind);
        var history = Assert.IsType<OkObjectResult>(await controller.GetProviderHealthHistory(provider));
        using var recent = JsonDocument.Parse(JsonSerializer.Serialize(history.Value));
        Assert.Equal(3, recent.RootElement.GetProperty("checks").GetArrayLength());
        Assert.IsType<OkObjectResult>(await controller.ResumeProvider(provider, capability));
        Assert.False(health.IsCircuitOpen(health.Get(key)!));
        clients.Verify(item => item.CreateClient(It.IsAny<string>()), Times.Never);
        Assert.IsType<NotFoundResult>(await controller.ResumeProvider("unknown-source", "lyrics"));
        Assert.IsType<BadRequestObjectResult>(await controller.ResumeProvider(provider, "streaming"));
        Assert.IsType<BadRequestObjectResult>(await controller.ResumeProvider(provider, capability, Guid.Empty));
    }

    [Fact]
    public async Task ProviderHealthHistoryIsAdministratorOnlyAndReturnsBoundedCurrentDiagnosticRows()
    {
        var nonAdministrator = CreateController(CreateHttpContextWithSession(isAdmin: false));
        AssertForbidden(await Diagnostics(nonAdministrator).GetProviderHealthHistory("deezer", 100));

        var now = DateTimeOffset.UtcNow;
        var managedKey = ProviderRuntimeStatusKey.CreateManaged("deezer", ProviderCapabilities.Streaming, _providerAccountId);
        var publicKey = ProviderRuntimeStatusKey.CreateAccountFree("deezer", ProviderCapabilities.Metadata);
        await using (var context = await _factory.CreateDbContextAsync())
        {
            var account = await context.ProviderAccounts.SingleAsync(item => item.Id == _providerAccountId);
            account.SecretReferenceId = Guid.CreateVersion7();
            context.SecretReferences.Add(new SecretReferenceRecord
            {
                Id = account.SecretReferenceId.Value,
                Purpose = "provider-account",
                ActiveVersion = 1,
                CreatedAt = now,
                UpdatedAt = now
            });
            context.AuditEvents.AddRange(
                HistoryEvent(managedKey, account.Revision, new ProviderRuntimeStatus
                {
                    Provider = managedKey.Provider,
                    Capability = managedKey.Capability,
                    IsSupported = true,
                    IsEnabled = true,
                    Configuration = ProviderConfigurationState.Configured,
                    Health = ProviderHealthState.Degraded,
                    TestedAt = now.AddMinutes(-1),
                    LatencyMilliseconds = 15,
                    ReasonCode = "timeout"
                }, now.AddMinutes(-1)),
                HistoryEvent(publicKey, null, new ProviderRuntimeStatus
                {
                    Provider = publicKey.Provider,
                    Capability = publicKey.Capability,
                    IsSupported = true,
                    IsEnabled = true,
                    Configuration = ProviderConfigurationState.NotRequired,
                    Health = ProviderHealthState.Healthy,
                    TestedAt = now.AddMinutes(-2),
                    LatencyMilliseconds = 8
                }, now.AddMinutes(-2)),
                HistoryEvent(publicKey, null, new ProviderRuntimeStatus
                {
                    Provider = publicKey.Provider,
                    Capability = publicKey.Capability,
                    IsSupported = true,
                    IsEnabled = true,
                    Configuration = ProviderConfigurationState.NotRequired,
                    Health = ProviderHealthState.Unknown,
                    TestedAt = now.AddMinutes(-3),
                    ReasonCode = "sample-unavailable"
                }, now.AddMinutes(-3)),
                HistoryEvent(managedKey, account.Revision + 1, new ProviderRuntimeStatus
                {
                    Provider = managedKey.Provider,
                    Capability = managedKey.Capability,
                    IsSupported = true,
                    IsEnabled = true,
                    Configuration = ProviderConfigurationState.Configured,
                    Health = ProviderHealthState.Healthy,
                    TestedAt = now.AddSeconds(-30)
                }, now.AddSeconds(-30)),
                HistoryEvent(publicKey, null, new ProviderRuntimeStatus
                {
                    Provider = publicKey.Provider,
                    Capability = publicKey.Capability,
                    IsSupported = true,
                    IsEnabled = true,
                    Configuration = ProviderConfigurationState.NotRequired,
                    Health = ProviderHealthState.Healthy,
                    TestedAt = now.AddMinutes(1)
                }, now.AddMinutes(1)),
                new AuditEventRecord
                {
                    Id = Guid.CreateVersion7(),
                    Category = ProviderCheckHistory.Category,
                    SubjectType = ProviderCheckHistory.Category,
                    SubjectId = $"{publicKey.Provider}/{publicKey.Capability}/public",
                    Action = "check-completed",
                    Outcome = "recorded",
                    DetailsJson = "{malformed",
                    CreatedAt = now.AddMinutes(-4)
                });
            await context.SaveChangesAsync();
        }

        var clientFactory = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        var administrator = CreateController(
            CreateHttpContextWithSession(isAdmin: true), httpClientFactory: clientFactory.Object);
        var originalServices = administrator.HttpContext.RequestServices;
        administrator.HttpContext.RequestServices = new ServiceCollection()
            .AddSingleton(originalServices.GetRequiredService<ProviderStatusManager>())
            .AddSingleton<IDbContextFactory<AllstarrDbContext>>(_factory)
            .AddSingleton(new ProviderCheckHistory(_factory, TimeProvider.System))
            .BuildServiceProvider();
        var result = Assert.IsType<OkObjectResult>(await Diagnostics(administrator)
            .GetProviderHealthHistory("deezer", 100));
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
        Assert.Equal("deezer", document.RootElement.GetProperty("provider").GetString());
        Assert.True(document.RootElement.GetProperty("capturedAt").GetDateTimeOffset() >= now);
        var checks = document.RootElement.GetProperty("checks").EnumerateArray().ToArray();
        Assert.Equal(3, checks.Length);
        Assert.Equal(1, document.RootElement.GetProperty("counts").GetProperty("passed").GetInt32());
        Assert.Equal(1, document.RootElement.GetProperty("counts").GetProperty("failed").GetInt32());
        Assert.Equal(1, document.RootElement.GetProperty("counts").GetProperty("neutral").GetInt32());
        Assert.Contains(checks, item => item.GetProperty("accountLabel").GetString() == "Health fixture" &&
            item.GetProperty("accountScope").GetString() == "shared" &&
            item.GetProperty("outcome").GetString() == "failed" &&
            item.GetProperty("reasonCode").GetString() == "timeout");
        Assert.Contains(checks, item => item.GetProperty("accountScope").GetString() == "public" &&
            item.GetProperty("outcome").GetString() == "passed");
        Assert.Contains(checks, item => item.GetProperty("accountScope").GetString() == "public" &&
            item.GetProperty("outcome").GetString() == "neutral");
        Assert.All(checks, item => Assert.False(item.TryGetProperty("providerAccountId", out _)));
        clientFactory.Verify(item => item.CreateClient(It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData("qobuz")]
    [InlineData("spotify")]
    [InlineData("koito")]
    [InlineData("multi-scrobbler")]
    [InlineData("spicylyrics")]
    public async Task KnownAccountOnlyProvidersHaveAnEmptyHistoryBeforeConnecting(string provider)
    {
        var clientFactory = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        var controller = Diagnostics(CreateController(CreateHttpContextWithSession(isAdmin: true),
            httpClientFactory: clientFactory.Object));

        var response = Assert.IsType<OkObjectResult>(await controller.GetProviderHealthHistory(provider));
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(response.Value));
        Assert.Equal(provider, document.RootElement.GetProperty("provider").GetString());
        Assert.Empty(document.RootElement.GetProperty("checks").EnumerateArray());
        Assert.All(document.RootElement.GetProperty("counts").EnumerateObject(), count => Assert.Equal(0, count.Value.GetInt32()));
        clientFactory.Verify(item => item.CreateClient(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task HistoryRejectsUnknownRetiredAndNoncanonicalProviders()
    {
        var controller = Diagnostics(CreateController(CreateHttpContextWithSession(isAdmin: true)));
        Assert.IsType<NotFoundResult>(await controller.GetProviderHealthHistory("unknown-history-source"));
        Assert.IsType<NotFoundResult>(await controller.GetProviderHealthHistory("binilyrics"));
        Assert.IsType<BadRequestObjectResult>(await controller.GetProviderHealthHistory("applemusic"));
        Assert.IsType<BadRequestObjectResult>(await controller.GetProviderHealthHistory(" "));
    }

    [Fact]
    public async Task ProviderHealthEndpoints_RequireAdministratorSessionAndAllowPublicCapabilityProbe()
    {
        var nonAdministrator = CreateController(CreateHttpContextWithSession(isAdmin: false));
        AssertForbidden(await Diagnostics(nonAdministrator).GetProvidersStatus());
        AssertForbidden(await Diagnostics(nonAdministrator).TestProvider(
            "deezer",
            ProviderCapabilities.Metadata,
            _providerAccountId));

        var administrator = CreateController(
            CreateHttpContextWithSession(isAdmin: true),
            healthStore: CreateHealthStore());
        var publicProbe = Assert.IsType<OkObjectResult>(await Diagnostics(administrator).TestProvider(
            "deezer",
            ProviderCapabilities.Metadata));
        Assert.Equal(StatusCodes.Status200OK, publicProbe.StatusCode);
        Assert.IsType<BadRequestObjectResult>(await Diagnostics(administrator).TestProvider(
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

        var result = await Diagnostics(controller).TestProvider(
            "deezer",
            "recommendation",
            _providerAccountId);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, badRequest.StatusCode);
    }

    [Theory]
    [InlineData("paxsenix")]
    [InlineData("lrclib")]
    [InlineData("lyricsplus")]
    [InlineData("musixmatch")]
    [InlineData("spotify")]
    public async Task StreamingTest_ForLyricsSourcesRejectsTheCapabilityBeforeMediaExecution(string provider)
    {
        var accountId = Guid.NewGuid();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.ProviderAccounts.Add(new() { Id = accountId, ProviderId = provider, DisplayName = "Lyrics fixture", Enabled = true });
            await db.SaveChangesAsync();
        }
        var controller = CreateController(CreateHttpContextWithSession(isAdmin: true));
        Assert.IsType<BadRequestObjectResult>(await Diagnostics(controller).TestProvider(provider, "streaming", accountId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManagedMediaTest_UsesExactEncryptedAccountAndVolatileStreamingStatus(bool serviceFailure)
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
            for (var i = 0; i < 3; i++)
            {
                db.ProviderTrackIdentities.Add(new ProviderTrackIdentityRecord
                {
                    Id = Guid.CreateVersion7(),
                    CanonicalRecordingId = recordingId,
                    DecisionVersion = 1,
                    VerificationMethod = "fixture",
                    ProviderId = "deezer",
                    ResourceKind = ProviderResourceKind.Track,
                    Scope = ProviderIdentityScope.Catalog,
                    ExternalId = $"sample-track-{i}",
                    ExternalIdHash = new string((char)('a' + i), 64),
                    Verification = ProviderIdentityVerification.Verified,
                    VerifiedAt = DateTimeOffset.UtcNow,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                });
            }
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
                Assert.StartsWith("sample-track-", request.TrackId.Value);
                if (serviceFailure) return ProviderOutcome<ProviderStreamLease>.Failure(ProviderError.ServiceUnavailable());
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

        var result = Assert.IsType<OkObjectResult>(await Diagnostics(controller).TestProvider(
            "deezer",
            ProviderCapabilities.Streaming,
            _providerAccountId));
        using (var resultDocument = JsonDocument.Parse(JsonSerializer.Serialize(result.Value)))
        {
            Assert.Equal(!serviceFailure, resultDocument.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal(serviceFailure ? "degraded" : "healthy", resultDocument.RootElement.GetProperty("health").GetString());
            Assert.Equal(_providerAccountId, resultDocument.RootElement.GetProperty("providerAccountId").GetGuid());
        }

        if (serviceFailure)
        {
            using var failure = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
            Assert.Equal("service_unavailable", failure.RootElement.GetProperty("reasonCode").GetString());
            Assert.Equal("resolve", failure.RootElement.GetProperty("stage").GetString());
            streaming.Verify(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderStreamLeaseRequest>()), Times.Exactly(3));
            return;
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
        var statusResult = Assert.IsType<OkObjectResult>(await Diagnostics(restartedController).GetProvidersStatus());
        using var statusDocument = JsonDocument.Parse(JsonSerializer.Serialize(statusResult.Value));
        var download = statusDocument.RootElement.EnumerateArray().Single(item =>
            item.GetProperty("providerAccountId").ValueKind != JsonValueKind.Null &&
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

        var result = Assert.IsType<OkObjectResult>(await Diagnostics(controller).TestProvider("deezer", null, _providerAccountId));

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

    private LegacyConfigurationController CreateMigrationController(HttpContext context, MountedLegacyEnvironment? mounted = null)
    {
        _ = CreateController(context, mounted: mounted);
        return new() { ControllerContext = new ControllerContext { HttpContext = context } };
    }

    private static ProviderConnectionController Diagnostics(ConfigController controller) =>
        new(Microsoft.Extensions.Logging.Abstractions.NullLogger<ProviderConnectionController>.Instance)
        { ControllerContext = controller.ControllerContext };

    private ConfigController CreateController(
        HttpContext httpContext,
        Dictionary<string, string?>? configValues = null,
        IHttpClientFactory? httpClientFactory = null,
        ProviderRuntimeHealth? healthStore = null,
        EncryptedSecretStore? secretStore = null,
        IApplicationCache? applicationCache = null,
        ProviderCtsDiagnosticRunner? diagnosticRunner = null,
        MountedLegacyEnvironment? mounted = null,
        IProviderRegistry? providerRegistry = null)
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
            healthStore, services: providerRegistry == null ? null :
                new ServiceCollection().AddSingleton(providerRegistry).BuildServiceProvider());
        var effectiveSecretStore = secretStore ?? CreateSecretStore();
        var clock = new SystemPlatformClock();
        var signal = new RuntimeSettingsChangeSignal();
        var durableSettings = new DurableRuntimeSettingsService(_factory, configuration, clock, signal);
        var effectiveProviderPolicies = new EffectiveProviderPolicyResolver(durableSettings);
        var migration = new LegacyEnvMigrationService(_factory, durableSettings, effectiveSecretStore, clock);
        var services = new ServiceCollection()
            .AddSingleton(providerStatusManager)
            .AddSingleton<IDbContextFactory<AllstarrDbContext>>(_factory)
            .AddSingleton(new ProviderCheckHistory(_factory, TimeProvider.System))
            .AddSingleton(durableSettings)
            .AddSingleton<IDurableRuntimeSettings>(durableSettings)
            .AddSingleton<IEffectiveProviderPolicyResolver>(effectiveProviderPolicies)
            .AddSingleton(migration)
            .AddSingleton(new OdesliApiKeySettingsService(_factory, durableSettings, effectiveSecretStore, clock))
            .AddSingleton(effectiveSecretStore);
        if (mounted != null) services.AddSingleton(mounted);
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

    private static AuditEventRecord HistoryEvent(
        ProviderRuntimeStatusKey key,
        long? revision,
        ProviderRuntimeStatus status,
        DateTimeOffset createdAt) => new()
        {
            Id = Guid.CreateVersion7(),
            Category = ProviderCheckHistory.Category,
            Action = "check-completed",
            Outcome = "recorded",
            SubjectType = ProviderCheckHistory.Category,
            SubjectId = $"{key.Provider}/{key.Capability}/{key.ProviderAccountId?.ToString("N") ?? "public"}",
            DetailsJson = JsonSerializer.Serialize(new ProviderCheckResult(key, revision, status)),
            CreatedAt = createdAt
        };

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
