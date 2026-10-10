using System.IO.Compression;
using System.Text;
using System.Text.Json;
using allstarr.Core.Operations;
using allstarr.Core.Secrets;
using allstarr.Core.Storage;
using allstarr.Core.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace allstarr.Tests;

public sealed class ProviderHealthMigrationTests
{
    private const string Baseline = "20261008235223_UsersBaseline";
    private static readonly string[] Removed = ["provider_health_samples", "provider_health_rollups", "provider_circuits"];

    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task PopulatedUpgradePreservesAllOtherRowsSchemaAndEncryptedCredentialsIncludingOldBackupRestore()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(useTemplate: false);
        var factory = new TestDbContextFactory(database.Options);
        await using var db = await factory.CreateDbContextAsync();
        await db.GetService<IMigrator>().MigrateAsync(Baseline);
        var now = DateTimeOffset.UtcNow;
        var user = Guid.CreateVersion7();
        var account = Guid.CreateVersion7();
        db.Users.Add(new UserRecord
        {
            Id = user,
            BackendType = "jellyfin",
            BackendInstanceId = "fixture",
            BackendPrincipalId = "listener",
            DisplayName = "Listener",
            Enabled = true,
            CreatedAt = now,
            UpdatedAt = now,
            LastSeenAt = now
        });
        db.ProviderAccounts.Add(new ProviderAccountRecord
        {
            Id = account,
            ProviderId = "spotify",
            OwnerUserId = user,
            DisplayName = "Saved account",
            Enabled = true,
            Revision = 9,
            CreatedAt = now,
            UpdatedAt = now
        });
        db.PlaylistLinks.Add(new PlaylistLinkRecord
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = user,
            ProviderAccountId = account,
            SourceProviderId = "spotify",
            SourcePlaylistId = "saved-source",
            SourcePlaylistIdHash = new string('a', 64),
            TargetProtocol = "jellyfin",
            TargetBackendInstanceId = "fixture",
            TargetPlaylistId = "saved-destination",
            Revision = 7,
            CreatedAt = now,
            UpdatedAt = now,
            RuleVersion = "1",
            PolicyVersion = "1"
        });
        db.RuntimeSettings.Add(new RuntimeSettingRecord
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = user,
            Key = "Playback:ShowExternalLabel",
            ValueType = RuntimeSettingValueType.Boolean,
            ValueJson = "false",
            Source = "personal",
            Revision = 3,
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync();
        var keys = new SecretStoreOptions { KeyRingPath = Path.Combine(database.StorageOptions.DataDirectory, "keyring.json") };
        var ring = new FileSecretKeyRingProvider(keys);
        await ring.CreateIfMissingAsync(false);
        var secrets = new EncryptedSecretStore(factory, ring, keys, new SystemPlatformClock());
        var purpose = $"provider-account:spotify:{account:N}";
        var secret = await secrets.StoreAsync(user, purpose, Encoding.UTF8.GetBytes("fixture-credential"));
        (await db.ProviderAccounts.SingleAsync()).SecretReferenceId = secret.Id;
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO provider_health_samples (Id,ProviderAccountId,Capability,State,ObservedAt,ExpiresAt)
            VALUES ({Guid.NewGuid()},{account},'playlist','Healthy',{now.UtcTicks},{now.AddMinutes(5).UtcTicks});
            INSERT INTO provider_circuits (Id,ProviderAccountId,Capability,State,ConsecutiveFailures,UpdatedAt,Revision)
            VALUES ({Guid.NewGuid()},{account},'playlist','Closed',0,{now.UtcTicks},1);
            INSERT INTO provider_health_rollups (Id,ProviderAccountId,Capability,WindowStart,WindowEnd,SampleCount,SuccessCount,FailureCount,SuccessRate,LastState,UpdatedAt,Revision)
            VALUES ({Guid.NewGuid()},{account},'playlist',{now.UtcTicks},{now.AddMinutes(15).UtcTicks},1,1,0,1,'Healthy',{now.UtcTicks},1);
            """);
        var beforeSchema = await Schema(db);
        var beforeRows = await Rows(db);
        Assert.Equal(32, beforeSchema.Count(row => row.StartsWith("trigger|", StringComparison.Ordinal)));
        var originalKeyRing = await File.ReadAllBytesAsync(keys.KeyRingPath);
        var oldArchive = await CreateBaselineBackup(db, keys, database.StorageOptions.DataDirectory);

        await db.Database.MigrateAsync();
        Assert.Equal(beforeSchema, await Schema(db));
        Assert.Equal(beforeRows, await Rows(db));
        foreach (var table in Removed)
            Assert.False(await db.Database.SqlQuery<bool>($"SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name={table}) AS Value").SingleAsync());
        Assert.Equal(originalKeyRing, await File.ReadAllBytesAsync(keys.KeyRingPath));
        using (var credential = await secrets.OpenAsync(secret.Id, new SecretAccessContext(user, purpose)))
            Assert.Equal("fixture-credential", credential.ReadUtf8());

        var state = new DurableStorageState(database.StorageOptions);
        state.Set(DurableStorageReadiness.Ready);
        var backups = new DurableBackupService(factory, database.StorageOptions, state, keys);
        var newBackup = await backups.CreateAsync();
        Assert.Equal("verified", newBackup.Status);
        Assert.Equal("20261009043555_VolatileProviderHealth", newBackup.SchemaVersion);
        await using (var upload = File.OpenRead(oldArchive)) await backups.StageRestoreAsync(upload);
        await db.Database.CloseConnectionAsync();
        state.Set(DurableStorageReadiness.Initializing);
        await new DurableStorageInitializer(factory, database.StorageOptions, state,
            NullLogger<DurableStorageInitializer>.Instance, backups).StartAsync(default);
        Assert.Equal(DurableStorageReadiness.Ready, state.GetSnapshot().Readiness);
        await using var restored = await factory.CreateDbContextAsync();
        Assert.Equal(beforeSchema, await Schema(restored));
        Assert.Equal(beforeRows, await Rows(restored));
        Assert.Equal(originalKeyRing, await File.ReadAllBytesAsync(keys.KeyRingPath));
        using var restoredCredential = await new EncryptedSecretStore(factory, new FileSecretKeyRingProvider(keys), keys,
            new SystemPlatformClock()).OpenAsync(secret.Id, new SecretAccessContext(user, purpose));
        Assert.Equal("fixture-credential", restoredCredential.ReadUtf8());
    }

    private sealed class TestDbContextFactory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
        public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }

    private static async Task<string[]> Schema(AllstarrDbContext db)
    {
        var schema = await db.Database.SqlQueryRaw<string>("""
            SELECT type || '|' || name || '|' || coalesce(sql,'') AS Value FROM sqlite_master
            WHERE name NOT LIKE 'sqlite_%' AND name NOT LIKE '__EFMigrations%'
              AND tbl_name NOT IN ('provider_circuits','provider_health_rollups','provider_health_samples')
            ORDER BY type,name
            """).ToArrayAsync();
        return schema;
    }

    private static async Task<string[]> Rows(AllstarrDbContext db)
    {
        var names = await db.Database.SqlQueryRaw<string>("""
            SELECT name AS Value FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'
              AND name NOT LIKE '__EFMigrations%' AND name NOT IN ('provider_circuits','provider_health_rollups','provider_health_samples')
            ORDER BY name
            """).ToArrayAsync();
        await db.Database.OpenConnectionAsync();
        var rows = new List<string>();
        foreach (var name in names)
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = $"SELECT * FROM \"{name.Replace("\"", "\"\"")}\"";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var values = new object[reader.FieldCount];
                reader.GetValues(values);
                rows.Add(name + ":" + JsonSerializer.Serialize(values));
            }
        }
        return rows.Order(StringComparer.Ordinal).ToArray();
    }

    private static async Task<string> CreateBaselineBackup(AllstarrDbContext db, SecretStoreOptions keys, string root)
    {
        var folder = Path.Combine(root, "old-backup");
        Directory.CreateDirectory(folder);
        var snapshot = Path.Combine(folder, "allstarr.db");
        await db.Database.ExecuteSqlInterpolatedAsync($"VACUUM INTO {snapshot}");
        var copiedKey = Path.Combine(folder, "keyring.json");
        File.Copy(keys.KeyRingPath, copiedKey);
        var manifest = new DurableBackupService.BackupManifest(1, Guid.NewGuid(), "fixture", Baseline, DateTimeOffset.UtcNow,
            new Dictionary<string, string>
            {
                ["allstarr.db"] = await DurableBackupService.HashAsync(snapshot, default),
                ["keyring.json"] = await DurableBackupService.HashAsync(copiedKey, default)
            });
        await File.WriteAllTextAsync(Path.Combine(folder, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var archive = Path.Combine(root, "baseline-backup.zip");
        ZipFile.CreateFromDirectory(folder, archive);
        return archive;
    }
}
