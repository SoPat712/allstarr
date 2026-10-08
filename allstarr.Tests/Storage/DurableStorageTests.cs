using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace allstarr.Tests;

public sealed class DurableStorageTests : IAsyncLifetime
{
    private SqliteTestDatabase _database = null!;

    public async Task InitializeAsync() =>
        _database = await SqliteTestDatabase.CreateAsync(useTemplate: false);

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../outside.db")]
    [InlineData("nested/db")]
    [InlineData("nested\\db")]
    [InlineData("/outside.db")]
    public void Options_RejectDatabaseNamesOutsideDataDirectory(string name)
    {
        var options = _database.StorageOptions;
        options.DatabaseFileName = name;
        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(601)]
    public void Options_RejectInvalidCommandTimeout(int seconds)
    {
        var options = _database.StorageOptions;
        options.CommandTimeoutSeconds = seconds;
        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void Options_RequireDataDirectory()
    {
        var options = new StorageOptions { DataDirectory = " " };
        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task Initializer_AppliesBaselineAndConfiguresEveryConnection()
    {
        var state = await Initialize(_database.StorageOptions);
        var snapshot = state.GetSnapshot();
        Assert.Equal(DurableStorageProvider.Sqlite, snapshot.Provider);
        Assert.Equal(DurableStorageReadiness.Ready, snapshot.Readiness);
        await using var context = new AllstarrDbContext(_database.Options);
        Assert.Equal(Assert.Single(context.Database.GetMigrations()), snapshot.SchemaVersion);
        foreach (var table in new[] { "durable_jobs", "canonical_recordings", "provider_track_identities", "tenant_runtime_settings" })
            Assert.True(await context.Database.SqlQuery<bool>($"SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name={table}) AS Value").SingleAsync());
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (attempt == 0) context.Database.OpenConnection();
            else await context.Database.OpenConnectionAsync();
            Assert.Equal("wal", await Scalar(context, "PRAGMA journal_mode;"));
            Assert.Equal(1L, await Scalar(context, "PRAGMA foreign_keys;"));
            Assert.Equal(1L, await Scalar(context, "PRAGMA synchronous;"));
            Assert.Equal(5000L, await Scalar(context, "PRAGMA busy_timeout;"));
            Assert.Equal("ok", await Scalar(context, "PRAGMA quick_check;"));
            await context.Database.CloseConnectionAsync();
        }
    }

    [Fact]
    public async Task AutoMigrateDisabled_WithPendingSchema_RemainsUnready()
    {
        var options = _database.StorageOptions;
        options.AutoMigrate = false;
        var snapshot = (await Initialize(options)).GetSnapshot();
        Assert.Equal(DurableStorageReadiness.SchemaIncompatible, snapshot.Readiness);
        Assert.Equal("schema_migration_required", snapshot.ErrorCode);
        await using var context = new AllstarrDbContext(_database.Options);
        Assert.Empty(await context.Database.GetAppliedMigrationsAsync());
    }

    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task Initializer_RejectsUnknownNewerMigrationWithoutChangingSchema()
    {
        await using var context = new AllstarrDbContext(_database.Options);
        await context.Database.MigrateAsync();
        await context.Database.ExecuteSqlRawAsync("""
            INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
            VALUES ('99991231235959_FutureAllstarrSchema', '99.0.0')
            """);
        var before = await context.Database.GetAppliedMigrationsAsync();
        var snapshot = (await Initialize(_database.StorageOptions)).GetSnapshot();
        Assert.Equal(DurableStorageReadiness.SchemaIncompatible, snapshot.Readiness);
        Assert.Equal(DurableSchemaCompatibility.UnsupportedVersionErrorCode, snapshot.ErrorCode);
        Assert.Equal("99991231235959_FutureAllstarrSchema", snapshot.SchemaVersion);
        Assert.Equal(before, await context.Database.GetAppliedMigrationsAsync());
    }

    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task SchemaCompatibility_RejectsCaseDivergentMigrationId()
    {
        await using var context = new AllstarrDbContext(_database.Options);
        await context.Database.MigrateAsync();
        var migration = Assert.Single(context.Database.GetMigrations());
        var divergent = migration.ToLowerInvariant();
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"__EFMigrationsHistory\" SET \"MigrationId\" = {divergent} WHERE \"MigrationId\" = {migration}");
        var compatibility = await DurableSchemaCompatibility.InspectAsync(context);
        Assert.Equal(DurableSchemaCompatibilityStatus.UnsupportedVersion, compatibility.Status);
        Assert.Contains(migration, compatibility.MissingMigrations);
        Assert.Contains(divergent, compatibility.UnknownMigrations);
    }

    [Fact]
    public async Task CorruptDatabase_RemainsUnavailableWithoutReplacingData()
    {
        byte[] original = [1, 2, 3, 4];
        await File.WriteAllBytesAsync(_database.DatabasePath, original);
        var snapshot = (await Initialize(_database.StorageOptions)).GetSnapshot();
        Assert.Equal(DurableStorageProvider.Sqlite, snapshot.Provider);
        Assert.Equal(DurableStorageReadiness.Unavailable, snapshot.Readiness);
        Assert.Equal("database_initialization_failed", snapshot.ErrorCode);
        Assert.Equal(original, await File.ReadAllBytesAsync(_database.DatabasePath));
    }

    [Theory]
    [InlineData(DriveType.Network, "unknown", true)]
    [InlineData(DriveType.Fixed, "nfs", true)]
    [InlineData(DriveType.Fixed, "SMBFS", true)]
    [InlineData(DriveType.Fixed, "cifs", true)]
    [InlineData(DriveType.Fixed, "ext4", false)]
    [InlineData(DriveType.Fixed, "apfs", false)]
    public void NetworkStorage_IsRejected(DriveType type, string format, bool rejected) =>
        Assert.Equal(rejected, DurableStorageInitializer.IsNetworkFileSystem(type, format));

    [Fact]
    public async Task NativeSqliteLibraryIncludesTheAggregateMemorySafetyFix()
    {
        await using var context = new AllstarrDbContext(_database.Options);
        var version = await context.Database.SqlQueryRaw<string>("SELECT sqlite_version() AS Value").SingleAsync();
        Assert.True(Version.Parse(version) >= new Version(3, 50, 2),
            $"SQLite {version} predates the aggregate memory safety fix.");
    }

    [Theory]
    [InlineData("application_cache_entries")]
    [InlineData("outbox_messages")]
    [InlineData("provider_route_decisions")]
    [InlineData("provider_route_outcomes")]
    [InlineData("favorite_action_policies")]
    public async Task BaselineExcludesRetiredStateOwners(string table)
    {
        await using var context = new AllstarrDbContext(_database.Options);
        await context.Database.MigrateAsync();
        Assert.False(await context.Database.SqlQuery<bool>(
            $"SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name={table}) AS Value").SingleAsync());
    }

    [Fact]
    public void CheckedInMigration_GeneratesNativeSqliteSql()
    {
        using var context = new AllstarrDbContext(_database.Options);
        var script = context.GetService<IMigrator>().GenerateScript();
        Assert.Contains("CREATE TABLE \"tenants\"", script, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE \"canonical_recordings\"", script, StringComparison.Ordinal);
        Assert.Contains(" BLOB", script, StringComparison.Ordinal);
        Assert.Contains(" INTEGER", script, StringComparison.Ordinal);
        Assert.Contains(" TEXT", script, StringComparison.Ordinal);
        Assert.Contains("FK_managed_file_job_owner_lineage", script, StringComparison.Ordinal);
        Assert.Contains("TR_managed_file_reference_count_guard", script, StringComparison.Ordinal);
        Assert.DoesNotContain("AUTOINCREMENT", script, StringComparison.OrdinalIgnoreCase);
        Assert.False(context.Database.HasPendingModelChanges());
    }

    private async Task<DurableStorageState> Initialize(StorageOptions options)
    {
        var state = new DurableStorageState(options);
        await new DurableStorageInitializer(new TestDbContextFactory(_database.Options), options,
            state, NullLogger<DurableStorageInitializer>.Instance).StartAsync(CancellationToken.None);
        return state;
    }

    private static async Task<object?> Scalar(AllstarrDbContext context, string sql)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    public async Task DisposeAsync() => await _database.DisposeAsync();

    private sealed class TestDbContextFactory(DbContextOptions<AllstarrDbContext> options)
        : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
        public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AllstarrDbContext(options));
    }
}
