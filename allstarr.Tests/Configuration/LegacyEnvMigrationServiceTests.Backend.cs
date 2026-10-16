using allstarr.Core.Configuration;
using allstarr.Core.Operations;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using allstarr.Models.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Moq;

namespace allstarr.Tests;

public sealed partial class LegacyEnvMigrationServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Backend_ServiceAccessOnlyReviewRestoresSavedConnectionWithoutImportingOtherSettings(bool emptyReference)
    {
        var configuration = BackendConfiguration();
        var settings = new DurableRuntimeSettingsService(_factory, configuration, new SystemPlatformClock(), new RuntimeSettingsChangeSignal());
        await settings.ApplyBatchAsync([new("Backend:Type", "Jellyfin"), new("Backend:Url", "http://backend-fixture:8096"),
            new("Identity:BackendInstanceId", "primary")], "fixture");
        if (emptyReference) await settings.ApplyBatchAsync([new("Backend:ServiceCredentialReference", ""), new("Backend:ServiceUserId", "")], "fixture");
        var verifier = new Mock<IBackendConnectionVerifier>();
        verifier.Setup(item => item.VerifyServiceAsync("http://backend-fixture:8096", "jellyfin-user-id", "restore-fixture", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var service = BackendImporter(configuration, verifier.Object);
        var preview = await service.PreviewServiceAccessAsync(Source("JELLYFIN_API_KEY=restore-fixture\nCACHE_LYRICS_DAYS=99\nSPOTIFY_API_KEY=unused"), Actor());
        Assert.Equal("verify_then_encrypt", preview.Backend!.ServiceAccessAction);
        Assert.DoesNotContain("restore-fixture", System.Text.Json.JsonSerializer.Serialize(preview), StringComparison.Ordinal);
        await using (var before = await _factory.CreateDbContextAsync()) Assert.Empty(await before.SecretReferences.ToArrayAsync());
        var result = await service.ApplyAsync(preview.PreviewToken, preview.Revision, true, Actor());
        Assert.Equal("verified", result.BackendServiceAccess);
        Assert.True(result.RequiresRestart);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Single(await db.SecretReferences.ToArrayAsync());
        Assert.Empty(await db.ProviderAccounts.ToArrayAsync());
        Assert.False(await db.RuntimeSettings.AnyAsync(item => item.Key == "Cache:LyricsDays"));
        Assert.All(await db.RuntimeSettings.Where(item => item.Key == "Backend:Type" || item.Key == "Backend:Url").ToArrayAsync(), item => Assert.Equal(1, item.Revision));
        verifier.Verify(item => item.VerifyServiceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Backend_ServiceAccessReviewCanRetryAfterFailedVerificationWithoutImportingSettings()
    {
        var configuration = BackendConfiguration();
        var settings = new DurableRuntimeSettingsService(_factory, configuration, new SystemPlatformClock(), new RuntimeSettingsChangeSignal());
        await settings.ApplyBatchAsync([new("Backend:Type", "Jellyfin"), new("Backend:Url", "http://backend-fixture:8096"),
            new("Identity:BackendInstanceId", "primary")], "fixture");
        var accepted = false;
        var verifier = new Mock<IBackendConnectionVerifier>();
        verifier.Setup(item => item.VerifyServiceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => accepted);
        var service = BackendImporter(configuration, verifier.Object);
        var source = Source("JELLYFIN_API_KEY=retry-fixture\nCACHE_LYRICS_DAYS=99");
        var first = await service.PreviewServiceAccessAsync(source, Actor());
        Assert.Equal("needs_review", (await service.ApplyAsync(first.PreviewToken, first.Revision, true, Actor())).BackendServiceAccess);
        await using (var before = await _factory.CreateDbContextAsync()) Assert.Empty(await before.SecretReferences.ToArrayAsync());
        accepted = true;
        var retry = await service.PreviewServiceAccessAsync(source, Actor());
        Assert.Equal("verified", (await service.ApplyAsync(retry.PreviewToken, retry.Revision, true, Actor())).BackendServiceAccess);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Single(await db.SecretReferences.ToArrayAsync());
        Assert.False(await db.RuntimeSettings.AnyAsync(item => item.Key == "Cache:LyricsDays"));
    }

    [Fact]
    public async Task Backend_ExplicitApplySavesTheActiveConnectionAndEncryptedServiceAccessOnce()
    {
        var verifier = new Mock<IBackendConnectionVerifier>();
        verifier.Setup(item => item.VerifyServiceAsync("http://backend-fixture:8096", "jellyfin-user-id", "private-backend-key", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var configuration = BackendConfiguration();
        var service = BackendImporter(configuration, verifier.Object);
        var source = Source("BACKEND_TYPE=Jellyfin\nJELLYFIN_URL=http://backend-fixture:8096\nJELLYFIN_API_KEY=private-backend-key\nCACHE_LYRICS_DAYS=30");
        var original = source.ToArray();
        var preview = await service.PreviewAsync(source, Actor());
        Assert.DoesNotContain("private-backend-key", System.Text.Json.JsonSerializer.Serialize(preview), StringComparison.Ordinal);
        await using (var untouched = await _factory.CreateDbContextAsync())
        {
            Assert.Empty(await untouched.RuntimeSettings.ToListAsync());
            Assert.Empty(await untouched.SecretReferences.ToListAsync());
        }
        Assert.NotNull(preview.Backend);
        Assert.True(preview.Backend.SavesConnection);
        var applied = await service.ApplyAsync(preview.PreviewToken, preview.Revision, true, Actor());
        Assert.True(applied.BackendConnectionSaved);
        Assert.Equal("verified", applied.BackendServiceAccess);
        Assert.True(applied.RequiresRestart);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Equal("\"Jellyfin\"", (await db.RuntimeSettings.SingleAsync(item => item.Key == "Backend:Type")).ValueJson);
            Assert.Equal("\"http://backend-fixture:8096\"", (await db.RuntimeSettings.SingleAsync(item => item.Key == "Backend:Url")).ValueJson);
            Assert.Single(await db.SecretReferences.ToListAsync());
            Assert.Empty(await db.ProviderAccounts.ToListAsync());
            Assert.DoesNotContain("private-backend-key", string.Join(' ', await db.RuntimeSettings.Select(item => item.ValueJson).ToListAsync()), StringComparison.Ordinal);
        }
        var replay = await service.ApplyAsync(preview.PreviewToken, preview.Revision, true, Actor());
        Assert.True(replay.AlreadyApplied);
        Assert.False(replay.RequiresRestart);
        verifier.Verify(item => item.VerifyServiceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(original, source.ToArray());
        var restarted = BackendConfiguration();
        restarted["Jellyfin:Url"] = "http://changed-fixture:8096";
        restarted["Cache:LyricsDays"] = "99";
        await StartupConfiguration.PrepareAsync(restarted, Mock.Of<IHostEnvironment>());
        Assert.Equal("http://backend-fixture:8096", restarted["Jellyfin:Url"]);
        Assert.Equal("private-backend-key", restarted["Jellyfin:ApiKey"]);
        Assert.Equal("30", restarted["Cache:LyricsDays"]);
    }

    [Fact]
    public async Task Backend_UnverifiedServiceKeyStaysInactiveWhileTheNativeConnectionIsSaved()
    {
        var verifier = new Mock<IBackendConnectionVerifier>();
        verifier.Setup(item => item.VerifyServiceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var service = BackendImporter(BackendConfiguration(), verifier.Object);
        var preview = await service.PreviewAsync(Source("JELLYFIN_API_KEY=invalid-private-fixture"), Actor());
        var result = await service.ApplyAsync(preview.PreviewToken, preview.Revision, true, Actor());
        Assert.True(result.BackendConnectionSaved);
        Assert.Equal("needs_review", result.BackendServiceAccess);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(3, await db.RuntimeSettings.CountAsync());
        Assert.Empty(await db.SecretReferences.ToListAsync());
        Assert.Empty(await db.ProviderAccounts.ToListAsync());
        var restarted = BackendConfiguration();
        await StartupConfiguration.PrepareAsync(restarted, Mock.Of<IHostEnvironment>());
        Assert.Equal("http://backend-fixture:8096", restarted["Jellyfin:Url"]);
        Assert.Equal("", restarted["Jellyfin:ApiKey"]);
    }

    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task Backend_VerificationOutsideTheWriterCannotSurviveAdministratorRevocation()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var verifier = new Mock<IBackendConnectionVerifier>();
        verifier.Setup(item => item.VerifyServiceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async () => { entered.TrySetResult(); await resume.Task; return true; });
        var service = BackendImporter(BackendConfiguration(), verifier.Object);
        var preview = await service.PreviewAsync(Source("JELLYFIN_API_KEY=private-fixture"), Actor());
        var applying = service.ApplyAsync(preview.PreviewToken, preview.Revision, true, Actor());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await Task.Run(async () =>
            {
                await using var writer = await _factory.CreateDbContextAsync();
                var actor = await writer.Users.SingleAsync(item => item.Id == _userId);
                actor.IsAdmin = false;
                await writer.SaveChangesAsync();
            }).WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally { resume.TrySetResult(); }
        var error = await Assert.ThrowsAsync<LegacyEnvMigrationException>(() => applying);
        Assert.Equal("user_unavailable", error.Code);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Empty(await db.RuntimeSettings.ToListAsync());
        Assert.Empty(await db.SecretReferences.ToListAsync());
        Assert.Empty(await db.LegacyEnvImports.ToListAsync());
    }

    [Fact]
    public async Task Backend_ExistingConnectionAndEncryptedVersionWinWithoutAnotherVerification()
    {
        var configuration = BackendConfiguration();
        var settings = new DurableRuntimeSettingsService(_factory, configuration, new SystemPlatformClock(), new RuntimeSettingsChangeSignal());
        var secret = await CreateSecretStore().StoreAsync(null, StartupConfiguration.ServicePurpose("Jellyfin", "http://backend-fixture:8096"),
            System.Text.Encoding.UTF8.GetBytes("existing-private-key"));
        secret = await CreateSecretStore().StoreAsync(null, StartupConfiguration.ServicePurpose("Jellyfin", "http://backend-fixture:8096"),
            System.Text.Encoding.UTF8.GetBytes("rotated-existing-private-key"), secret.Id);
        await settings.ApplyBatchAsync([new("Backend:Type", "Jellyfin"), new("Backend:Url", "http://backend-fixture:8096"),
            new("Backend:ServiceCredentialReference", secret.Id.ToString("D")), new("Backend:ServiceUserId", "jellyfin-user-id"),
            new("Identity:BackendInstanceId", "primary")], "fixture");
        var verifier = new Mock<IBackendConnectionVerifier>(MockBehavior.Strict);
        var service = BackendImporter(configuration, verifier.Object);
        var preview = await service.PreviewAsync(Source("JELLYFIN_API_KEY=replacement-private-key"), Actor());
        var result = await service.ApplyAsync(preview.PreviewToken, preview.Revision, true, Actor());
        Assert.False(result.BackendConnectionSaved);
        Assert.False(result.RequiresRestart);
        Assert.Equal("kept_existing", result.BackendServiceAccess);
        verifier.VerifyNoOtherCalls();
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(secret.Id, (await db.SecretReferences.SingleAsync()).Id);
        Assert.Equal(secret.ActiveVersion, (await db.SecretReferences.SingleAsync()).ActiveVersion);
        Assert.Equal(2, await db.SecretVersions.CountAsync());
        Assert.All(await db.RuntimeSettings.ToListAsync(), row => Assert.Equal(1, row.Revision));
    }

    [Fact]
    public async Task Backend_OlderImportReceiptIsRetainedWhileTheNewPreviewAddsOnlyMissingConnectionSettings()
    {
        var source = Source("JELLYFIN_URL=http://backend-fixture:8096\nCACHE_LYRICS_DAYS=30");
        var fingerprint = LegacyEnvParser.Parse(source).SourceSha256;
        var oldResult = new LegacyEnvMigrationApplyResult(true, false, 1, 0, 0, 0, 1, 0, [], fingerprint, DateTimeOffset.UtcNow.AddDays(-1));
        var oldJson = System.Text.Json.JsonSerializer.Serialize(oldResult);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var auditId = Guid.CreateVersion7();
            db.AuditEvents.Add(MigrationAudit(auditId, _userId));
            db.RuntimeSettings.Add(new RuntimeSettingRecord
            {
                Id = Guid.CreateVersion7(),
                Key = "Cache:LyricsDays",
                ValueType = RuntimeSettingValueType.Integer,
                ValueJson = "30",
                Source = "legacy-env-import",
                Revision = 5,
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
                UpdatedAt = DateTimeOffset.UtcNow.AddDays(-1)
            });
            db.LegacyEnvImports.Add(new LegacyEnvImportRecord
            {
                Id = Guid.CreateVersion7(),
                SourceSha256 = fingerprint,
                SchemaVersion = "legacy-env-import-v3",
                ActorUserId = _userId,
                AuditEventId = auditId,
                ResultJson = oldJson,
                ProvenanceJson = "{\"settings\":[],\"providerAccounts\":[]}",
                AppliedAt = DateTimeOffset.UtcNow.AddDays(-1)
            });
            await db.SaveChangesAsync();
        }
        var verifier = new Mock<IBackendConnectionVerifier>(MockBehavior.Strict);
        var service = BackendImporter(BackendConfiguration(), verifier.Object);
        var preview = await service.PreviewAsync(source, Actor());
        var result = await service.ApplyAsync(preview.PreviewToken, preview.Revision, true, Actor());
        Assert.True(result.BackendConnectionSaved);
        Assert.Equal(3, result.SettingsImported);
        await using var verify = await _factory.CreateDbContextAsync();
        Assert.Equal(2, await verify.LegacyEnvImports.CountAsync());
        Assert.Equal(oldJson, (await verify.LegacyEnvImports.SingleAsync(item => item.SchemaVersion == "legacy-env-import-v3")).ResultJson);
        Assert.Equal(5, (await verify.RuntimeSettings.SingleAsync(item => item.Key == "Cache:LyricsDays")).Revision);
        Assert.Equal(4, await verify.RuntimeSettings.CountAsync());
        verifier.VerifyNoOtherCalls();
    }

    private ConfigurationManager BackendConfiguration() => new()
    {
        ["Backend:Type"] = "Jellyfin",
        ["Jellyfin:Url"] = "http://backend-fixture:8096",
        ["Storage:DataDirectory"] = _database.StorageOptions.DataDirectory,
        ["Secrets:KeyRingPath"] = _keyRingPath
    };

    private LegacyEnvMigrationService BackendImporter(IConfiguration configuration, IBackendConnectionVerifier verifier)
    {
        var clock = new SystemPlatformClock();
        var settings = new DurableRuntimeSettingsService(_factory, configuration, clock, new RuntimeSettingsChangeSignal());
        return new(_factory, settings, CreateSecretStore(), clock,
            new(BackendType.Jellyfin, "Jellyfin", "legacy-environment", true, false, null),
            configuration: configuration, backendVerifier: verifier);
    }
}
