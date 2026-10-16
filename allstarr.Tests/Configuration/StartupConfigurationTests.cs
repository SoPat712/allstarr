using allstarr.Core.Configuration;
using allstarr.Core.Operations;
using allstarr.Core.Settings;
using allstarr.Core.Secrets;
using System.Text;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Moq;

namespace allstarr.Tests;

public sealed class StartupConfigurationTests
{
    [Fact]
    public async Task Prepare_SavedConnectionWithoutServiceAccessRequiresReviewAndIgnoresPlainKey()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var configuration = Configuration(database);
        var store = new DurableRuntimeSettingsService(new Factory(database.Options), configuration,
            new SystemPlatformClock(), new RuntimeSettingsChangeSignal());
        await store.ApplyBatchAsync([new("Backend:Type", "Jellyfin"),
            new("Backend:Url", "http://backend-fixture:8096")], "fixture");
        configuration["Jellyfin:ApiKey"] = "unconfirmed-fixture";
        await StartupConfiguration.PrepareAsync(configuration, Mock.Of<IHostEnvironment>());
        Assert.Equal("", configuration["Jellyfin:ApiKey"]);
        Assert.Equal("true", configuration["Setup:ServiceCredentialNeedsReview"]);
    }

    [Fact]
    public async Task Prepare_LoadsSavedHouseholdValuesBeforeCompositionAndNeverPersonalOverrides()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new Factory(database.Options);
        var settings = Configuration(database);
        var store = new DurableRuntimeSettingsService(factory, settings, new SystemPlatformClock(), new RuntimeSettingsChangeSignal());
        await store.ApplyBatchAsync([
            new("Cache:LyricsDays", "35"), new("Library:ExplicitFilter", "CleanOnly"), new("Playback:ShowExternalLabel", "true"),
            new("Backend:Type", "Subsonic"), new("Backend:Url", "http://backend-fixture:4533"),
            new("Identity:BackendInstanceId", "saved-instance")], "setup-fixture");
        await using (var personal = new AllstarrDbContext(database.Options))
        {
            var userId = Guid.CreateVersion7();
            personal.Users.Add(new UserRecord
            {
                Id = userId,
                BackendType = "jellyfin",
                BackendInstanceId = "saved-instance",
                BackendPrincipalId = "listener-fixture",
                DisplayName = "Fixture listener",
                Enabled = true,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            personal.RuntimeSettings.Add(new RuntimeSettingRecord
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = userId,
                Key = "Playback:ShowExternalLabel",
                ValueType = RuntimeSettingValueType.Boolean,
                ValueJson = "false",
                Source = "preferences-fixture",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                Revision = 1
            });
            await personal.SaveChangesAsync();
        }
        settings["Cache:LyricsDays"] = "99";
        settings["Backend:Type"] = "Jellyfin";
        settings["Jellyfin:Url"] = "http://changed-fixture:8096";
        var state = await StartupConfiguration.PrepareAsync(settings, Mock.Of<IHostEnvironment>());
        Assert.Equal(DurableStorageReadiness.Ready, state.GetSnapshot().Readiness);
        Assert.Equal("35", settings["Cache:LyricsDays"]);
        Assert.Equal("true", settings["Playback:ShowExternalLabel"]);
        Assert.Equal("CleanOnly", settings["Library:ExplicitFilter"]);
        Assert.Equal("CleanOnly", settings["Subsonic:ExplicitFilter"]);
        Assert.Equal("Subsonic", settings["Backend:Type"]);
        Assert.Equal("http://backend-fixture:4533", settings["Subsonic:Url"]);
        Assert.Equal("saved-instance", settings["Identity:BackendInstanceId"]);
        Assert.Equal("true", settings["Setup:BackendConfigured"]);
        Assert.Equal("database", settings["Setup:BackendSource"]);
    }

    [Fact]
    public async Task Prepare_FreshPersistentFolderStartsReadyForGuidedBackendSelection()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(useTemplate: false);
        var settings = Configuration(database);
        settings["Backend:Type"] = "Jellyfin";
        settings["Jellyfin:Url"] = "http://localhost:8096";
        var state = await StartupConfiguration.PrepareAsync(settings, Mock.Of<IHostEnvironment>());
        Assert.Equal(DurableStorageReadiness.Ready, state.GetSnapshot().Readiness);
        Assert.Equal("false", settings["Setup:BackendConfigured"]);
        Assert.Equal("", settings["Jellyfin:Url"]);
        await using var db = new AllstarrDbContext(database.Options);
        Assert.Empty(await db.RuntimeSettings.ToListAsync());
    }

    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task Prepare_OpensOnlyTheExactEncryptedBackendPurposeAndPreservesItsRevision()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var settings = Configuration(database);
        var factory = new Factory(database.Options);
        var options = new SecretStoreOptions { KeyRingPath = Path.Combine(database.StorageOptions.DataDirectory, "keyring.json") };
        var ring = new FileSecretKeyRingProvider(options);
        await ring.CreateIfMissingAsync(false);
        var secrets = new EncryptedSecretStore(factory, ring, options, new SystemPlatformClock());
        var credential = await secrets.StoreAsync(null, StartupConfiguration.ServicePurpose("Jellyfin", "http://backend-fixture:8096"),
            Encoding.UTF8.GetBytes("encrypted-backend-fixture"));
        var store = new DurableRuntimeSettingsService(factory, settings, new SystemPlatformClock(), new RuntimeSettingsChangeSignal());
        await store.ApplyBatchAsync([new("Backend:Type", "Jellyfin"), new("Backend:Url", "http://backend-fixture:8096"),
            new("Backend:ServiceCredentialReference", credential.Id.ToString("D")), new("Backend:ServiceUserId", "service-fixture"), new("Backend:ServiceCredentialKind", "api-key")], "setup-fixture");
        await StartupConfiguration.PrepareAsync(settings, Mock.Of<IHostEnvironment>());
        Assert.Equal("encrypted-backend-fixture", settings["Jellyfin:ApiKey"]);
        Assert.Equal("service-fixture", settings["Jellyfin:UserId"]);
        await using (var db = new AllstarrDbContext(database.Options))
        {
            Assert.DoesNotContain("encrypted-backend-fixture", string.Join(" ", await db.RuntimeSettings.Select(item => item.ValueJson).ToListAsync()), StringComparison.Ordinal);
            Assert.Equal(credential.ActiveVersion, (await db.SecretReferences.SingleAsync()).ActiveVersion);
            Assert.Single(await db.SecretVersions.ToListAsync());
        }
        await store.ApplyBatchAsync([new("Backend:Url", "http://other-backend-fixture:8096", 1)], "setup-fixture");
        var restarted = Configuration(database);
        await StartupConfiguration.PrepareAsync(restarted, Mock.Of<IHostEnvironment>());
        Assert.Equal("", restarted["Jellyfin:ApiKey"]);
        Assert.Equal("true", restarted["Setup:ServiceCredentialNeedsReview"]);
        Assert.Equal("true", restarted["Setup:BackendConfigured"]);
    }

    [Theory]
    [InlineData(true, false, false, "user-token")]
    [InlineData(true, false, true, "user-token")]
    [InlineData(false, false, false, "api-key")]
    [InlineData(false, true, false, "api-key")]
    public async Task Prepare_ClassifiesUnknownServiceAccessOnceAndReloadsTheRecordedKind(bool userToken, bool unavailable, bool emptyKind, string expected)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var configuration = Configuration(database);
        var factory = new Factory(database.Options);
        var options = new SecretStoreOptions { KeyRingPath = Path.Combine(database.StorageOptions.DataDirectory, "keyring.json") };
        var ring = new FileSecretKeyRingProvider(options);
        await ring.CreateIfMissingAsync(false);
        var secrets = new EncryptedSecretStore(factory, ring, options, new SystemPlatformClock());
        var credential = await secrets.StoreAsync(null, StartupConfiguration.ServicePurpose("Jellyfin", "http://backend-fixture:8096"),
            Encoding.UTF8.GetBytes("private-service-fixture"));
        var store = new DurableRuntimeSettingsService(factory, configuration, new SystemPlatformClock(), new RuntimeSettingsChangeSignal());
        await store.ApplyBatchAsync([new("Backend:Type", "Jellyfin"), new("Backend:Url", "http://backend-fixture:8096"),
            new("Backend:ServiceCredentialReference", credential.Id.ToString("D")), new("Backend:ServiceUserId", "service-fixture")], "fixture");
        if (emptyKind) await store.ApplyBatchAsync([new("Backend:ServiceCredentialKind", "")], "fixture");
        configuration["Backend:ServiceCredentialKind"] = "user-token";
        var verifier = new Mock<IBackendConnectionVerifier>(MockBehavior.Strict);
        var probe = verifier.Setup(item => item.VerifyUserTokenAsync("http://backend-fixture:8096", "service-fixture", "private-service-fixture", It.IsAny<CancellationToken>()));
        if (unavailable) probe.ThrowsAsync(new HttpRequestException("Unavailable"));
        else probe.ReturnsAsync(userToken);
        for (var restart = 0; restart < 2; restart++)
        {
            var restarted = Configuration(database);
            await StartupConfiguration.PrepareAsync(restarted, Mock.Of<IHostEnvironment>(), serviceVerifier: verifier.Object);
            Assert.Equal(unavailable ? null : expected, restarted["Backend:ServiceCredentialKind"]);
            Assert.Equal(expected, restarted["Jellyfin:ServiceCredentialKind"]);
        }
        verifier.Verify(item => item.VerifyUserTokenAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            unavailable ? Times.Exactly(2) : Times.Once());
        await using var db = new AllstarrDbContext(database.Options);
        var kind = await db.RuntimeSettings.SingleOrDefaultAsync(item => item.Key == "Backend:ServiceCredentialKind");
        if (unavailable)
        {
            // An outage keeps server-key limits for that start only and is checked again on the next one.
            Assert.True(kind == null || kind.ValueJson == "\"\"");
            return;
        }
        Assert.NotNull(kind);
        Assert.Equal(emptyKind ? 2 : 1, kind.Revision);
        Assert.Equal("\"" + expected + "\"", kind.ValueJson);
        Assert.DoesNotContain("private-service-fixture", string.Join(' ', await db.RuntimeSettings.Select(item => item.ValueJson).ToListAsync()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Prepare_AppliesTheStagedRestoreBeforeChoosingTheProtocol()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var settings = Configuration(database);
        var factory = new Factory(database.Options);
        var options = new SecretStoreOptions { KeyRingPath = Path.Combine(database.StorageOptions.DataDirectory, "keyring.json") };
        var ring = new FileSecretKeyRingProvider(options);
        await ring.CreateIfMissingAsync(false);
        var store = new DurableRuntimeSettingsService(factory, settings, new SystemPlatformClock(), new RuntimeSettingsChangeSignal());
        await store.ApplyBatchAsync([new("Backend:Type", "Subsonic"), new("Backend:Url", "http://restored-fixture:4533")], "setup-fixture");
        var state = new DurableStorageState(database.StorageOptions);
        state.Set(DurableStorageReadiness.Ready, "fixture");
        var backups = new DurableBackupService(factory, database.StorageOptions, state, options);
        var backup = await backups.CreateAsync();
        await store.ApplyBatchAsync([new("Backend:Type", "Jellyfin", 1), new("Backend:Url", "http://current-fixture:8096", 1)], "setup-fixture");
        await using (var upload = File.OpenRead(Path.Combine(database.StorageOptions.BackupDirectory, backup.FileName)))
            await backups.StageRestoreAsync(upload);
        var restarted = Configuration(database);
        await StartupConfiguration.PrepareAsync(restarted, Mock.Of<IHostEnvironment>());
        Assert.Equal("Subsonic", restarted["Backend:Type"]);
        Assert.Equal("http://restored-fixture:4533", restarted["Subsonic:Url"]);
        Assert.False(backups.HasPendingRestore);
        await using var db = new AllstarrDbContext(database.Options);
        Assert.All(await db.RuntimeSettings.ToListAsync(), item => Assert.Equal(1, item.Revision));
    }

    private static ConfigurationManager Configuration(SqliteTestDatabase database)
    {
        var settings = new ConfigurationManager();
        settings["Storage:DataDirectory"] = database.StorageOptions.DataDirectory;
        settings["Storage:DatabaseFileName"] = database.StorageOptions.DatabaseFileName;
        return settings;
    }
    private sealed class Factory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
    }
}
