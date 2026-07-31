using allstarr.Core.Configuration;
using allstarr.Core.Jobs;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace allstarr.Tests;

public sealed class DurableStorageTests : IAsyncLifetime
{
    private PostgresTestDatabase _database = null!;

    public async Task InitializeAsync()
    {
        _database = await PostgresTestDatabase.CreateAsync(useTemplate: false);
    }

    [Fact]
    public void Options_RejectUnknownProviderInsteadOfFallingBack()
    {
        var options = new DurableStorageOptions
        {
            Provider = "automatic",
            ConnectionString = _database.ConnectionString
        };

        var exception = Assert.Throws<InvalidOperationException>(() => options.ParseProvider());

        Assert.Contains("Postgres", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Sqlite", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PostgresInitializer_AppliesCheckedInMigrationsAndReportsSchema()
    {
        var options = Options();
        var state = new DurableStorageState(options);
        var runtime = new OperationalRuntimeState();
        using var traces = new PlatformTraceCollector();
        await traces.StartAsync(CancellationToken.None);
        var initializer = new DurableStorageInitializer(
            Factory(),
            options,
            state,
            NullLogger<DurableStorageInitializer>.Instance,
            runtimeState: runtime);

        await initializer.StartAsync(CancellationToken.None);

        var snapshot = state.GetSnapshot();
        Assert.Equal(DurableStorageReadiness.Ready, snapshot.Readiness);
        await using var context = await Factory().CreateDbContextAsync();
        Assert.Equal(context.Database.GetMigrations().Last(), snapshot.SchemaVersion);
        Assert.True(await TableExists(context, "durable_jobs"));
        Assert.True(await TableExists(context, "canonical_recordings"));
        Assert.True(await TableExists(context, "provider_track_identities"));
        Assert.True(await TableExists(context, "tenant_runtime_settings"));
        Assert.Equal(1, runtime.GetSnapshot().MigrationAttempts);
        Assert.Equal(0, runtime.GetSnapshot().MigrationFailures);
        Assert.Contains(traces.GetSnapshot(), span =>
            span.Operation == "storage.migrate" && !span.Failed);
    }

    [Fact]
    public async Task AutoMigrateDisabled_WithPendingSchema_RemainsUnready()
    {
        var options = Options();
        options.AutoMigrate = false;
        var state = new DurableStorageState(options);
        var initializer = new DurableStorageInitializer(
            Factory(),
            options,
            state,
            NullLogger<DurableStorageInitializer>.Instance);

        await initializer.StartAsync(CancellationToken.None);

        var snapshot = state.GetSnapshot();
        Assert.Equal(DurableStorageReadiness.SchemaIncompatible, snapshot.Readiness);
        Assert.Equal("schema_migration_required", snapshot.ErrorCode);
    }

    [Fact]
    public async Task Initializer_RejectsUnknownNewerMigrationWithoutChangingSchema()
    {
        await using (var context = await Factory().CreateDbContextAsync())
        {
            await context.Database.MigrateAsync();
            await context.Database.ExecuteSqlRawAsync(
                "INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") " +
                "VALUES ('99991231235959_FutureAllstarrSchema', '99.0.0')");
        }

        var options = Options();
        var state = new DurableStorageState(options);
        var runtime = new OperationalRuntimeState();
        var initializer = new DurableStorageInitializer(
            Factory(),
            options,
            state,
            NullLogger<DurableStorageInitializer>.Instance,
            runtimeState: runtime);

        await initializer.StartAsync(CancellationToken.None);

        var snapshot = state.GetSnapshot();
        Assert.Equal(DurableStorageReadiness.SchemaIncompatible, snapshot.Readiness);
        Assert.Equal(DurableSchemaCompatibility.UnsupportedVersionErrorCode, snapshot.ErrorCode);
        Assert.Equal("99991231235959_FutureAllstarrSchema", snapshot.SchemaVersion);
        Assert.Equal(1, runtime.GetSnapshot().MigrationFailures);
    }

    [Fact]
    public async Task RestoreTargetVerifier_RejectsUnknownMigrationFromTargetItInspects()
    {
        await using (var context = await Factory().CreateDbContextAsync())
        {
            await context.Database.MigrateAsync();
            await context.Database.ExecuteSqlRawAsync(
                "INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") " +
                "VALUES ('99991231235959_FutureAllstarrSchema', '99.0.0')");
        }

        var exception = await Assert.ThrowsAsync<BackupVerificationException>(() =>
            new DurableRestoreTargetVerifier().VerifyAsync(
                DurableStorageProvider.Postgres,
                _database.ConnectionString));

        Assert.Contains("schema", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PostgresAdditiveMigrations_ApplyCleanly()
    {
        await using var context = await Factory().CreateDbContextAsync();
        await context.Database.MigrateAsync();

        Assert.True(await TableExists(context, "provider_health_rollups"));
        Assert.True(await ColumnExists(context, "durable_jobs", "MaxDeferrals"));
        Assert.True(await ColumnExists(context, "durable_jobs", "PolicySnapshotJson"));
        Assert.True(await ColumnExists(context, "durable_jobs", "RequestFingerprint"));
        Assert.True(await ColumnExists(context, "outbox_messages", "MaxAttempts"));
        Assert.True(await ColumnExists(context, "backups", "RestoreStatus"));
    }

    [Fact]
    public async Task SuggestedTargetMigration_NormalizesLegacyTargetlessRows()
    {
        await using var context = await Factory().CreateDbContextAsync();
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync("20260727233000_AllowExternalAcceptedTrackMatches");
        var now = DateTimeOffset.UtcNow;
        var tenantId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();
        var accountId = Guid.CreateVersion7();
        var snapshotId = Guid.CreateVersion7();
        context.AddRange(
            new TenantRecord
            {
                Id = tenantId,
                Slug = "legacy-suggestion",
                Name = "Legacy suggestion",
                CreatedAt = now
            },
            new PlatformUserRecord
            {
                Id = userId,
                TenantId = tenantId,
                DisplayName = "User",
                Status = PlatformUserStatus.Active,
                CreatedAt = now,
                UpdatedAt = now
            },
            new ProviderAccountRecord
            {
                Id = accountId,
                TenantId = tenantId,
                OwnerUserId = userId,
                ProviderId = "spotify",
                DisplayName = "Spotify",
                Scope = ProviderAccountScope.User,
                Enabled = true,
                CreatedAt = now,
                UpdatedAt = now
            });
        await context.SaveChangesAsync();
        context.ExternalMetadataSnapshots.Add(new ExternalMetadataSnapshotRecord
        {
            Id = snapshotId,
            TenantId = tenantId,
            OwnerUserId = userId,
            ProviderAccountId = accountId,
            LibraryScopeId = "music",
            BackendInstanceId = "home",
            BackendPrincipalId = "principal",
            Protocol = "jellyfin",
            ProviderId = "spotify",
            ResourceKind = "track",
            ExternalIdHash = new('a', 64),
            SnapshotVersion = 1,
            PayloadJson = "{}",
            PayloadSha256 = new('b', 64),
            CorrelationId = "legacy-suggestion",
            RetrievedAt = now
        });
        await context.SaveChangesAsync();
        context.TrackMatches.Add(new TrackMatchRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            OwnerUserId = userId,
            ExternalSnapshotId = snapshotId,
            LibraryScopeId = "music",
            State = TrackMatchState.Suggested,
            Confidence = .8,
            Threshold = .88,
            DecisionVersion = 1,
            PolicyVersion = "normalized-v6",
            ReasonsJson = "[]",
            CandidateResultsJson = "[]",
            WarningsJson = "[]",
            CorrelationId = "legacy-suggestion",
            DecidedAt = now
        });
        await context.SaveChangesAsync();

        await migrator.MigrateAsync();
        context.ChangeTracker.Clear();

        Assert.Equal(TrackMatchState.Unresolved, await context.TrackMatches
            .Select(item => item.State)
            .SingleAsync());
    }

    [Fact]
    public async Task ImportedPlaylistScheduleMigration_ActivatesLegacyLinksIdempotently()
    {
        await using var context = await Factory().CreateDbContextAsync();
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync("20260727234500_AllowSuggestedTrackTargets");
        var now = DateTimeOffset.UtcNow;
        var tenantId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();
        var accountId = Guid.CreateVersion7();
        var scheduleId = Guid.CreateVersion7();
        context.AddRange(
            new TenantRecord
            {
                Id = tenantId,
                Slug = "legacy-playlist",
                Name = "Legacy playlist",
                CreatedAt = now
            },
            new PlatformUserRecord
            {
                Id = userId,
                TenantId = tenantId,
                DisplayName = "User",
                Status = PlatformUserStatus.Active,
                CreatedAt = now,
                UpdatedAt = now
            },
            new ProviderAccountRecord
            {
                Id = accountId,
                TenantId = tenantId,
                OwnerUserId = userId,
                ProviderId = "spotify",
                DisplayName = "Spotify",
                Scope = ProviderAccountScope.User,
                Enabled = true,
                CreatedAt = now,
                UpdatedAt = now
            },
            new JobScheduleRecord
            {
                Id = scheduleId,
                TenantId = tenantId,
                OwnerUserId = userId,
                LibraryScopeId = "music",
                JobType = DurableScheduleEngine.PlaylistSyncJobType,
                CronExpression = "0 8 * * *",
                TimeZoneId = "UTC",
                OverlapPolicy = ScheduleOverlapPolicy.Skip,
                MisfirePolicy = ScheduleMisfirePolicy.RunOnce,
                RetryPolicyJson = "{}",
                PayloadTemplateJson = "{}",
                Enabled = false,
                CreatedAt = now,
                UpdatedAt = now,
                Revision = 1
            });
        context.PlaylistLinks.Add(new PlaylistLinkRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            OwnerUserId = userId,
            ProviderAccountId = accountId,
            ScheduleId = scheduleId,
            Enabled = false,
            LibraryScopeId = "music",
            SourceProviderId = "spotify",
            SourcePlaylistId = "source",
            SourcePlaylistIdHash = new string('a', 64),
            TargetProtocol = "jellyfin",
            TargetBackendInstanceId = "primary",
            Mode = PlaylistLinkMode.Materialized,
            MaterializationMode = PlaylistMaterializationMode.Reconcile,
            RuleVersion = "legacy-env-import-v1",
            PolicyVersion = "legacy-env-import-v1",
            CreatedAt = now,
            UpdatedAt = now,
            Revision = 1
        });
        await context.SaveChangesAsync();

        await migrator.MigrateAsync();
        context.ChangeTracker.Clear();

        var link = await context.PlaylistLinks.SingleAsync();
        var schedule = await context.JobSchedules.SingleAsync();
        Assert.True(link.Enabled);
        Assert.True(schedule.Enabled);
        Assert.NotNull(schedule.NextRunAt);
        Assert.Equal(2, link.Revision);
        Assert.Equal(2, schedule.Revision);
    }

    [Fact]
    public async Task ProbeCacheSnapshot_AppliesToFreshPostgres()
    {
        await using var context = await Factory().CreateDbContextAsync();

        await context.Database.MigrateAsync();

        Assert.Contains(
            "20260724012448_ProbeCacheSnapshot",
            await context.Database.GetAppliedMigrationsAsync());
        Assert.Equal("boolean", await ColumnType(context, "playlist_links", "Enabled"));
    }

    [Fact]
    public async Task ProbeCacheSnapshot_UpgradesLegacyIntegerEnabledColumn()
    {
        await using var context = await Factory().CreateDbContextAsync();
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync("20260723233918_AddDownloadArtifactMediaFacts");
        await context.Database.ExecuteSqlRawAsync("""
            ALTER TABLE playlist_links
                ALTER COLUMN "Enabled" DROP DEFAULT,
                ALTER COLUMN "Enabled" TYPE integer USING (CASE WHEN "Enabled" THEN 1 ELSE 0 END),
                ALTER COLUMN "Enabled" SET DEFAULT 1
            """);

        await migrator.MigrateAsync();

        Assert.Equal("boolean", await ColumnType(context, "playlist_links", "Enabled"));
    }

    [Fact]
    public async Task DownloadedSongMappingRepair_UpgradesLegacyLowercaseIdWithoutDataLoss()
    {
        await using var context = await Factory().CreateDbContextAsync();
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync("20260729010000_ActivateImportedPlaylistSchedules");
        await context.Database.ExecuteSqlRawAsync(
            """ALTER TABLE downloaded_song_mappings RENAME COLUMN "Id" TO id""");
        var mappingId = Guid.CreateVersion7();
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO downloaded_song_mappings
                (id, "ProviderId", "ExternalId", "LocalPath", "Title", "Artist", "Album", "DownloadedAt", "Revision")
            VALUES
                ({mappingId}, {"test"}, {"external"}, {"/music/test.flac"}, {"Test"}, {"Artist"}, {"Album"}, {DateTimeOffset.UtcNow.UtcTicks}, {1L})
            """);

        await migrator.MigrateAsync();

        Assert.True(await ColumnExists(context, "downloaded_song_mappings", "Id"));
        Assert.False(await ColumnExists(context, "downloaded_song_mappings", "id"));
        Assert.Equal(mappingId, await context.DownloadedSongMappings.Select(item => item.Id).SingleAsync());
    }

    [Fact]
    public async Task SchemaCompatibility_RejectsCaseDivergentMigrationId()
    {
        await using var context = await Factory().CreateDbContextAsync();
        await context.Database.MigrateAsync();
        const string migration = "20260724012448_ProbeCacheSnapshot";
        var divergent = migration.ToLowerInvariant();
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"__EFMigrationsHistory\" SET \"MigrationId\" = {divergent} WHERE \"MigrationId\" = {migration}");

        var compatibility = await DurableSchemaCompatibility.InspectAsync(context);

        Assert.Equal(DurableSchemaCompatibilityStatus.UnsupportedVersion, compatibility.Status);
        Assert.Contains(migration, compatibility.MissingMigrations);
        Assert.Contains(divergent, compatibility.UnknownMigrations);
    }

    [Fact]
    public async Task OnboardingMigration_BackfillsExistingBackendIdentity()
    {
        await using var context = await Factory().CreateDbContextAsync();
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync("20260726205440_VerifyPlaylistMaterialization");
        var tenantId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        context.Tenants.Add(new TenantRecord
        {
            Id = tenantId,
            Slug = "onboarding-upgrade",
            Name = "Onboarding upgrade",
            CreatedAt = now
        });
        context.Users.Add(new PlatformUserRecord
        {
            Id = userId,
            TenantId = tenantId,
            DisplayName = "Existing user",
            Status = PlatformUserStatus.Active,
            CreatedAt = now,
            UpdatedAt = now
        });
        context.BackendIdentities.Add(new BackendIdentityRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            UserId = userId,
            BackendType = "jellyfin",
            BackendInstanceId = "primary",
            PrincipalId = "existing-user",
            CreatedAt = now,
            LastSeenAt = now
        });
        await context.SaveChangesAsync();

        await migrator.MigrateAsync();

        var state = await context.OnboardingStates.SingleAsync();
        Assert.Equal(OnboardingStateService.SchemaVersion, state.SchemaVersion);
        Assert.Equal("schema-backfill", state.CompletionSource);
        Assert.NotNull(state.CompletedAt);
        Assert.Contains(
            OnboardingStateService.BackendIdentityStep,
            state.CompletedStepsJson,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnavailablePostgres_NeverCreatesFallbackStorage()
    {
        var options = new DurableStorageOptions
        {
            Provider = "Postgres",
            ConnectionString =
                "Host=127.0.0.1;Port=1;Database=allstarr;Username=allstarr;Password=test;Timeout=1;Command Timeout=1",
            ConnectionRetryCount = 0,
            AutoMigrate = true
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
    }

    [Fact]
    public void CheckedInMigration_GeneratesNativePostgresSql()
    {
        using var context = new AllstarrDbContext(_database.Options);
        var script = context.GetService<IMigrator>().GenerateScript();

        Assert.Contains("CREATE TABLE tenants", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CREATE TABLE canonical_recordings", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("uuid", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("bytea", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" BLOB", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AUTOINCREMENT", script, StringComparison.OrdinalIgnoreCase);
        Assert.False(context.Database.HasPendingModelChanges());
    }

    private DurableStorageOptions Options() => new()
    {
        Provider = "Postgres",
        ConnectionString = _database.ConnectionString,
        AutoMigrate = true,
        ConnectionRetryCount = 0
    };

    private TestDbContextFactory Factory() => new(_database.Options);

    private static async Task<bool> TableExists(AllstarrDbContext context, string table) =>
        await context.Database.SqlQuery<bool>(
            $"SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = {table}) AS \"Value\"")
            .SingleAsync();

    private static async Task<bool> ColumnExists(
        AllstarrDbContext context,
        string table,
        string column) =>
        await context.Database.SqlQuery<bool>(
            $"SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = {table} AND column_name = {column}) AS \"Value\"")
            .SingleAsync();

    private static async Task<string> ColumnType(
        AllstarrDbContext context,
        string table,
        string column) =>
        await context.Database.SqlQuery<string>(
            $"SELECT data_type AS \"Value\" FROM information_schema.columns WHERE table_schema = 'public' AND table_name = {table} AND column_name = {column}")
            .SingleAsync();

    public async Task DisposeAsync()
    {
        await _database.DisposeAsync();
    }

    private sealed class TestDbContextFactory(DbContextOptions<AllstarrDbContext> options)
        : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);

        public Task<AllstarrDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AllstarrDbContext(options));
    }
}
