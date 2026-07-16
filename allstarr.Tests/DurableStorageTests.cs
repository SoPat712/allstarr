using allstarr.Core.Storage;
using allstarr.Core.Operations;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace allstarr.Tests;

public sealed class DurableStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "allstarr-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void Options_RejectUnknownProviderInsteadOfFallingBack()
    {
        var options = new DurableStorageOptions
        {
            Provider = "automatic",
            ConnectionString = "Data Source=should-not-exist.db"
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
        {
            options.ParseProvider();
        });

        Assert.Contains("Postgres", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Sqlite", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SqliteInitializer_AppliesCheckedInMigrationAndReportsSchema()
    {
        var databasePath = Path.Combine(_root, "state", "allstarr.db");
        var options = SqliteOptions(databasePath);
        var state = new DurableStorageState(options);
        var runtime = new OperationalRuntimeState();
        using var traces = new PlatformTraceCollector();
        await traces.StartAsync(CancellationToken.None);
        var initializer = new DurableStorageInitializer(
            CreateFactory(options),
            options,
            state,
            NullLogger<DurableStorageInitializer>.Instance,
            runtimeState: runtime);

        await initializer.StartAsync(CancellationToken.None);

        var snapshot = state.GetSnapshot();
        Assert.Equal(DurableStorageReadiness.Ready, snapshot.Readiness);
        await using var context = await CreateFactory(options).CreateDbContextAsync();
        Assert.Equal(context.Database.GetMigrations().Last(), snapshot.SchemaVersion);
        var tableCount = await context.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS Value FROM sqlite_master WHERE type = 'table' AND name = 'durable_jobs'")
            .SingleAsync();
        Assert.Equal(1, tableCount);
        Assert.True(await TableExists(context, "canonical_recordings"));
        Assert.True(await TableExists(context, "provider_track_identities"));
        Assert.True(await TableExists(context, "tenant_runtime_settings"));
        var runtimeSnapshot = runtime.GetSnapshot();
        Assert.Equal(1, runtimeSnapshot.MigrationAttempts);
        Assert.Equal(0, runtimeSnapshot.MigrationFailures);
        Assert.True(runtimeSnapshot.LastMigrationDurationMilliseconds >= 0);
        Assert.Contains(
            traces.GetSnapshot(),
            span => span.Operation == "storage.migrate" && !span.Failed);
        Assert.False(File.Exists(options.SqliteBootstrapConfirmationFile));
    }

    [Fact]
    public async Task MissingSqliteDatabase_WithoutOneShotConfirmationIsNotCreated()
    {
        var databasePath = Path.Combine(_root, "missing-volume", "allstarr.db");
        var options = new DurableStorageOptions
        {
            Provider = "Sqlite",
            ConnectionString = $"Data Source={databasePath}",
            AutoMigrate = true,
            ConnectionRetryCount = 0,
            BackupDirectory = Path.Combine(_root, "backups")
        };
        var state = new DurableStorageState(options);
        var initializer = new DurableStorageInitializer(
            CreateFactory(options),
            options,
            state,
            NullLogger<DurableStorageInitializer>.Instance);

        await initializer.StartAsync(CancellationToken.None);

        Assert.Equal(DurableStorageReadiness.Unavailable, state.GetSnapshot().Readiness);
        Assert.Equal("sqlite_database_missing", state.GetSnapshot().ErrorCode);
        Assert.False(File.Exists(databasePath));
        Assert.False(Directory.Exists(Path.GetDirectoryName(databasePath)));
    }

    [Fact]
    public async Task ConsumedSqliteBootstrapConfirmation_CannotRecreateLostDatabase()
    {
        var databasePath = Path.Combine(_root, "lost-volume", "allstarr.db");
        var options = SqliteOptions(databasePath);
        var factory = CreateFactory(options);
        var firstState = new DurableStorageState(options);
        await new DurableStorageInitializer(
                factory,
                options,
                firstState,
                NullLogger<DurableStorageInitializer>.Instance)
            .StartAsync(CancellationToken.None);
        Assert.Equal(DurableStorageReadiness.Ready, firstState.GetSnapshot().Readiness);
        Assert.False(File.Exists(options.SqliteBootstrapConfirmationFile));

        File.Delete(databasePath);
        Assert.False(File.Exists(databasePath));
        var restartedState = new DurableStorageState(options);
        await new DurableStorageInitializer(
                factory,
                options,
                restartedState,
                NullLogger<DurableStorageInitializer>.Instance)
            .StartAsync(CancellationToken.None);

        Assert.Equal(DurableStorageReadiness.Unavailable, restartedState.GetSnapshot().Readiness);
        Assert.Equal("sqlite_database_missing", restartedState.GetSnapshot().ErrorCode);
        Assert.False(File.Exists(databasePath));
    }

    [Fact]
    public async Task ExistingOnlySqliteConnectionMode_DoesNotCreateAMissingFile()
    {
        var databasePath = Path.Combine(_root, "existing-only", "allstarr.db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        var options = new DurableStorageOptions
        {
            Provider = "Sqlite",
            ConnectionString = $"Data Source={databasePath}"
        };
        options.RequireExistingSqliteFile(options.ParseProvider());

        await using var connection = new SqliteConnection(options.ConnectionString);
        await Assert.ThrowsAsync<SqliteException>(() => connection.OpenAsync());
        Assert.False(File.Exists(databasePath));
    }

    [Fact]
    public async Task ConcurrentSqliteInitializers_SerializeMigrationAndBothBecomeReady()
    {
        var databasePath = Path.Combine(_root, "concurrent", "allstarr.db");
        var options = SqliteOptions(databasePath);
        var firstState = new DurableStorageState(options);
        var secondState = new DurableStorageState(options);
        var first = new DurableStorageInitializer(
            CreateFactory(options),
            options,
            firstState,
            NullLogger<DurableStorageInitializer>.Instance);
        var second = new DurableStorageInitializer(
            CreateFactory(options),
            options,
            secondState,
            NullLogger<DurableStorageInitializer>.Instance);

        await Task.WhenAll(
            first.StartAsync(CancellationToken.None),
            second.StartAsync(CancellationToken.None));

        Assert.Equal(DurableStorageReadiness.Ready, firstState.GetSnapshot().Readiness);
        Assert.Equal(DurableStorageReadiness.Ready, secondState.GetSnapshot().Readiness);
        await using var context = await CreateFactory(options).CreateDbContextAsync();
        var migrations = (await context.Database.GetAppliedMigrationsAsync()).ToArray();
        Assert.Equal(context.Database.GetMigrations().Count(), migrations.Length);
        Assert.Contains(migrations, item => item.EndsWith("Phase2TrackIdentityFoundation", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AutoMigrateDisabled_WithPendingSchema_RemainsUnready()
    {
        var databasePath = Path.Combine(_root, "manual", "allstarr.db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE existing_state (id INTEGER PRIMARY KEY)";
            await command.ExecuteNonQueryAsync();
        }
        var options = SqliteOptions(databasePath);
        options.AutoMigrate = false;
        var state = new DurableStorageState(options);
        var initializer = new DurableStorageInitializer(
            CreateFactory(options),
            options,
            state,
            NullLogger<DurableStorageInitializer>.Instance);

        await initializer.StartAsync(CancellationToken.None);

        var snapshot = state.GetSnapshot();
        Assert.True(
            snapshot.Readiness == DurableStorageReadiness.SchemaIncompatible,
            snapshot.ToString());
        Assert.Equal("schema_migration_required", snapshot.ErrorCode);
    }

    [Fact]
    public async Task Initializer_RejectsUnknownNewerMigrationWithoutChangingSchema()
    {
        var databasePath = Path.Combine(_root, "future", "allstarr.db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        var options = SqliteOptions(databasePath);
        var factory = CreateFactory(options);
        await using (var context = await factory.CreateDbContextAsync())
        {
            await context.Database.MigrateAsync();
            await context.Database.ExecuteSqlRawAsync(
                "INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") " +
                "VALUES ('99991231235959_FutureAllstarrSchema', '99.0.0')");
        }

        var state = new DurableStorageState(options);
        var runtime = new OperationalRuntimeState();
        var initializer = new DurableStorageInitializer(
            factory,
            options,
            state,
            NullLogger<DurableStorageInitializer>.Instance,
            runtimeState: runtime);

        await initializer.StartAsync(CancellationToken.None);

        var snapshot = state.GetSnapshot();
        Assert.Equal(DurableStorageReadiness.SchemaIncompatible, snapshot.Readiness);
        Assert.Equal(DurableSchemaCompatibility.UnsupportedVersionErrorCode, snapshot.ErrorCode);
        Assert.Equal("99991231235959_FutureAllstarrSchema", snapshot.SchemaVersion);
        Assert.Equal(1, runtime.GetSnapshot().MigrationAttempts);
        Assert.Equal(1, runtime.GetSnapshot().MigrationFailures);
        await using var verification = await factory.CreateDbContextAsync();
        Assert.Contains(
            "99991231235959_FutureAllstarrSchema",
            await verification.Database.GetAppliedMigrationsAsync());
    }

    [Fact]
    public async Task RestoreTargetVerifier_RejectsUnknownMigrationFromTargetItInspects()
    {
        var databasePath = Path.Combine(_root, "restore-verifier", "allstarr.db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        var options = SqliteOptions(databasePath);
        var factory = CreateFactory(options);
        await using (var context = await factory.CreateDbContextAsync())
        {
            await context.Database.MigrateAsync();
            await context.Database.ExecuteSqlRawAsync(
                "INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") " +
                "VALUES ('99991231235959_FutureAllstarrSchema', '99.0.0')");
        }

        var exception = await Assert.ThrowsAsync<BackupVerificationException>(() =>
            new DurableRestoreTargetVerifier().VerifyAsync(
                DurableStorageProvider.Sqlite,
                options.ConnectionString));

        Assert.Contains("schema", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SqliteAdditiveMigrations_CanRollBackToFoundationAndReapply()
    {
        var databasePath = Path.Combine(_root, "rollback", "allstarr.db");
        var options = SqliteOptions(databasePath);
        var state = new DurableStorageState(options);
        var factory = CreateFactory(options);
        await new DurableStorageInitializer(
            factory,
            options,
            state,
            NullLogger<DurableStorageInitializer>.Instance)
            .StartAsync(CancellationToken.None);
        await using var context = await factory.CreateDbContextAsync();
        var migrator = context.GetService<IMigrator>();

        await migrator.MigrateAsync("20260710145139_InitialDurableFoundation");

        Assert.False(await TableExists(context, "provider_health_rollups"));
        Assert.False(await ColumnExists(context, "durable_jobs", "MaxDeferrals"));
        Assert.False(await ColumnExists(context, "durable_jobs", "PolicySnapshotJson"));
        Assert.False(await ColumnExists(context, "durable_jobs", "RequestFingerprint"));
        Assert.False(await ColumnExists(context, "outbox_messages", "MaxAttempts"));
        Assert.False(await ColumnExists(context, "backups", "RestoreStatus"));
        Assert.False(await TableExists(context, "canonical_recordings"));
        Assert.False(await TableExists(context, "provider_track_identities"));

        await migrator.MigrateAsync();

        Assert.True(await TableExists(context, "provider_health_rollups"));
        Assert.True(await ColumnExists(context, "durable_jobs", "MaxDeferrals"));
        Assert.True(await ColumnExists(context, "durable_jobs", "PolicySnapshotJson"));
        Assert.True(await ColumnExists(context, "durable_jobs", "RequestFingerprint"));
        Assert.True(await ColumnExists(context, "outbox_messages", "MaxAttempts"));
        Assert.True(await ColumnExists(context, "backups", "RestoreStatus"));
        Assert.True(await TableExists(context, "canonical_recordings"));
        Assert.True(await TableExists(context, "provider_track_identities"));
        Assert.False(context.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task UnavailablePostgres_RemainsPostgresAndNeverCreatesSqliteFallback()
    {
        var fallbackPath = Path.Combine(_root, "forbidden-fallback.db");
        var options = new DurableStorageOptions
        {
            Provider = "Postgres",
            ConnectionString =
                "Host=127.0.0.1;Port=1;Database=allstarr;Username=allstarr;Password=test;Timeout=1;Command Timeout=1",
            ConnectionRetryCount = 0,
            AutoMigrate = true,
            BackupDirectory = Path.Combine(_root, "backups")
        };
        var state = new DurableStorageState(options);
        var dbOptions = new DbContextOptionsBuilder<AllstarrDbContext>()
            .UseNpgsql(options.ConnectionString)
            .Options;
        var initializer = new DurableStorageInitializer(
            new TestDbContextFactory(dbOptions),
            options,
            state,
            NullLogger<DurableStorageInitializer>.Instance);

        await initializer.StartAsync(CancellationToken.None);

        var snapshot = state.GetSnapshot();
        Assert.Equal(DurableStorageProvider.Postgres, snapshot.Provider);
        Assert.Equal(DurableStorageReadiness.Unavailable, snapshot.Readiness);
        Assert.Equal("database_initialization_failed", snapshot.ErrorCode);
        Assert.False(File.Exists(fallbackPath));
    }

    [Fact]
    public void CheckedInMigration_GeneratesNativePostgresSqlWithoutSqliteTypes()
    {
        var options = new DbContextOptionsBuilder<AllstarrDbContext>()
            .UseNpgsql("Host=database;Database=allstarr;Username=allstarr;Password=not-used")
            .Options;
        using var context = new AllstarrDbContext(options);

        Assert.False(context.Database.HasPendingModelChanges());
        var script = context.GetService<IMigrator>().GenerateScript();

        Assert.Contains("CREATE TABLE tenants", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CREATE TABLE canonical_recordings", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CREATE TABLE provider_track_identities", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("uuid", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("bytea", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ALTER TABLE recommendation_runs ADD \"ScheduleId\" uuid", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ALTER TABLE recommendation_runs ADD \"ScheduledFor\" bigint", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ALTER TABLE job_schedules ADD \"PayloadTemplateJson\" text", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" BLOB", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AUTOINCREMENT", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CheckedInMigration_MatchesSqliteModel()
    {
        var options = new DbContextOptionsBuilder<AllstarrDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        using var context = new AllstarrDbContext(options);

        Assert.False(context.Database.HasPendingModelChanges());
    }

    private DurableStorageOptions SqliteOptions(string databasePath)
    {
        var confirmationPath = databasePath + ".create-confirmation";
        Directory.CreateDirectory(Path.GetDirectoryName(confirmationPath)!);
        File.WriteAllText(
            confirmationPath,
            DurableStorageOptions.SqliteBootstrapConfirmation);
        return new DurableStorageOptions
        {
            Provider = "Sqlite",
            ConnectionString = $"Data Source={databasePath}",
            AutoMigrate = true,
            ConnectionRetryCount = 0,
            SqliteBootstrapConfirmationFile = confirmationPath,
            BackupDirectory = Path.Combine(_root, "backups")
        };
    }

    private static TestDbContextFactory CreateFactory(DurableStorageOptions options)
    {
        var dbOptions = new DbContextOptionsBuilder<AllstarrDbContext>()
            .UseSqlite(options.ConnectionString)
            .Options;
        return new TestDbContextFactory(dbOptions);
    }

    private static async Task<bool> TableExists(AllstarrDbContext context, string table)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$name";
        parameter.Value = table;
        command.Parameters.Add(parameter);
        if (command.Connection!.State != System.Data.ConnectionState.Open)
        {
            await command.Connection.OpenAsync();
        }

        return Convert.ToInt32(await command.ExecuteScalarAsync()) == 1;
    }

    private static async Task<bool> ColumnExists(
        AllstarrDbContext context,
        string table,
        string column)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table.Replace("\"", "\"\"")}\")";
        if (command.Connection!.State != System.Data.ConnectionState.Open)
        {
            await command.Connection.OpenAsync();
        }

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<AllstarrDbContext> options)
        : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);

        public Task<AllstarrDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(new AllstarrDbContext(options));
    }
}
