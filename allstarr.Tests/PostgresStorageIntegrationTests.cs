using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Configuration;
using allstarr.Core.Favorites;
using allstarr.Core.Jobs;
using allstarr.Core.Identity;
using allstarr.Core.Operations;
using allstarr.Core.Playlists;
using allstarr.Core.Secrets;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using allstarr.Services.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.FileProviders;
using Npgsql;

namespace allstarr.Tests;

public sealed class PostgresStorageIntegrationTests
{
    [Fact]
    [Trait("Category", "Postgres")]
    public async Task NativePostgresLineageConstraints_RejectCrossTenantFavoriteJob()
    {
        var connectionString = Environment.GetEnvironmentVariable("ALLSTARR_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var options = new DbContextOptionsBuilder<AllstarrDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        await using var db = new AllstarrDbContext(options);
        await db.Database.ExecuteSqlRawAsync("DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public");
        await db.Database.MigrateAsync();

        var now = DateTimeOffset.UtcNow;
        var tenantA = Guid.CreateVersion7();
        var tenantB = Guid.CreateVersion7();
        var userA = Guid.CreateVersion7();
        var userB = Guid.CreateVersion7();
        var jobA = Guid.CreateVersion7();
        db.Tenants.AddRange(
            new TenantRecord { Id = tenantA, Slug = "pg-lineage-a", Name = "Lineage A", CreatedAt = now },
            new TenantRecord { Id = tenantB, Slug = "pg-lineage-b", Name = "Lineage B", CreatedAt = now });
        db.Users.AddRange(
            new PlatformUserRecord { Id = userA, TenantId = tenantA, DisplayName = "A", Status = PlatformUserStatus.Active, CreatedAt = now, UpdatedAt = now },
            new PlatformUserRecord { Id = userB, TenantId = tenantB, DisplayName = "B", Status = PlatformUserStatus.Active, CreatedAt = now, UpdatedAt = now });
        db.Jobs.Add(DatabaseLineageConstraintTests.Job(jobA, tenantA, userA, "pg-lineage", now));
        await db.SaveChangesAsync();

        db.FavoriteEvents.Add(new FavoriteEventRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantB,
            OwnerUserId = userB,
            Protocol = "subsonic",
            BackendInstanceId = "primary",
            BackendPrincipalId = "user-b",
            ItemId = "track",
            Operation = FavoriteOperation.Favorite,
            SourceRevision = "1",
            EventKey = Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLowerInvariant(),
            CorrelationId = "pg-lineage",
            PolicySnapshotJson = "{}",
            JobId = jobA,
            State = FavoriteEventState.Pending,
            CreatedAt = now,
            UpdatedAt = now
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

        db.ChangeTracker.Clear();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT count(*) FROM pg_constraint WHERE conname IN " +
            "('FK_favorite_event_job_lineage', 'FK_managed_file_job_tenant_lineage', " +
            "'FK_download_workspace_job_tenant_lineage', 'FK_enrichment_plan_job_lineage', " +
            "'FK_enrichment_plan_file_lineage', 'FK_enrichment_application_job_lineage')";
        if (command.Connection!.State != System.Data.ConnectionState.Open) await command.Connection.OpenAsync();
        Assert.Equal(6L, Convert.ToInt64(await command.ExecuteScalarAsync()));
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task NativePostgresHostOptions_SupportIdentityJobAndOutboxTransactions()
    {
        var connectionString = Environment.GetEnvironmentVariable("ALLSTARR_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var services = BuildHostStorageServices(connectionString);
        var factory = services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.ExecuteSqlRawAsync("DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public");
            await db.Database.MigrateAsync();
        }

        var storageState = new DurableStorageState(services.GetRequiredService<DurableStorageOptions>());
        await using (var db = await factory.CreateDbContextAsync())
        {
            storageState.Set(DurableStorageReadiness.Ready, db.Database.GetMigrations().Last());
        }

        var identityOptions = new IdentityOptions
        {
            Mode = "Hybrid",
            DefaultTenantId = Guid.CreateVersion7().ToString(),
            SingleUserId = Guid.CreateVersion7().ToString(),
            DefaultTenantSlug = "host-postgres",
            DefaultTenantName = "Host PostgreSQL",
            BackendInstanceId = "primary"
        };
        var clock = new SystemPlatformClock();
        var resolver = new BackendIdentityResolver(factory, storageState, identityOptions, clock);
        var principal = await resolver.ResolveAsync(new BackendIdentityDescriptor(
            "Subsonic", "first-admin", "First administrator", IsAdministrator: true));
        Assert.NotNull(principal);

        var jobOptions = new DurableJobOptions();
        var queue = new DurableJobQueue(factory, jobOptions, new JobPayloadPolicy(jobOptions), clock);
        var enqueued = await queue.EnqueueAsync(new DurableJobEnqueueRequest<object>(
            "postgres.host-transaction",
            "postgres-host-transaction",
            new { value = "safe" },
            principal!.TenantId,
            principal.UserId));
        Assert.True(enqueued.Created);
        var claim = await queue.ClaimNextAsync("postgres-host-worker");
        Assert.NotNull(claim);
        await queue.CompleteAsync(claim!, DurableJobCompletion.Success());

        var cancellable = await queue.EnqueueAsync(new DurableJobEnqueueRequest<object>(
            "postgres.host-cancel",
            "postgres-host-cancel",
            new { value = "cancel" },
            principal.TenantId,
            principal.UserId));
        Assert.True(await queue.RequestCancellationAsync(cancellable.JobId, principal.TenantId));

        var failing = await queue.EnqueueAsync(new DurableJobEnqueueRequest<object>(
            "postgres.host-failure",
            "postgres-host-failure",
            new { value = "fail" },
            principal.TenantId,
            principal.UserId));
        var failureClaim = await queue.ClaimNextAsync(
            "postgres-host-failure-worker",
            ["postgres.host-failure"]);
        Assert.NotNull(failureClaim);
        await queue.CompleteAsync(
            failureClaim!,
            DurableJobCompletion.Failure("expected_test_failure", "Expected native PostgreSQL test failure."));

        var accountId = Guid.CreateVersion7();
        var scheduleId = Guid.CreateVersion7();
        var linkId = Guid.CreateVersion7();
        var now = clock.UtcNow;
        await using (var seedSchedule = await factory.CreateDbContextAsync())
        {
            seedSchedule.ProviderAccounts.Add(new ProviderAccountRecord
            {
                Id = accountId,
                TenantId = principal.TenantId,
                OwnerUserId = principal.UserId,
                ProviderId = "spotify",
                DisplayName = "Schedule source",
                Scope = ProviderAccountScope.User,
                Enabled = true,
                CreatedAt = now,
                UpdatedAt = now
            });
            seedSchedule.JobSchedules.Add(new JobScheduleRecord
            {
                Id = scheduleId,
                TenantId = principal.TenantId,
                OwnerUserId = principal.UserId,
                LibraryScopeId = "music",
                JobType = DurableScheduleEngine.PlaylistSyncJobType,
                CronExpression = "* * * * *",
                TimeZoneId = "UTC",
                OverlapPolicy = ScheduleOverlapPolicy.Skip,
                MisfirePolicy = ScheduleMisfirePolicy.RunOnce,
                RetryPolicyJson = "{}",
                NextRunAt = now.AddMinutes(-1),
                Enabled = true,
                CreatedAt = now,
                UpdatedAt = now
            });
            seedSchedule.PlaylistLinks.Add(new PlaylistLinkRecord
            {
                Id = linkId,
                TenantId = principal.TenantId,
                OwnerUserId = principal.UserId,
                ProviderAccountId = accountId,
                ScheduleId = scheduleId,
                LibraryScopeId = "music",
                SourceProviderId = "spotify",
                SourcePlaylistId = "native-postgres-playlist",
                SourcePlaylistIdHash = new string('a', 64),
                TargetProtocol = "subsonic",
                TargetBackendInstanceId = "primary",
                Mode = PlaylistLinkMode.Materialized,
                MaterializationMode = PlaylistMaterializationMode.Reconcile,
                RuleVersion = "rules-v1",
                PolicyVersion = "policy-v1",
                CreatedAt = now,
                UpdatedAt = now
            });
            await seedSchedule.SaveChangesAsync();
        }
        var scheduleResult = await new DurableScheduleEngine(factory, queue, clock).TickAsync();
        Assert.Equal(1, scheduleResult.Enqueued);

        var outbox = new DurableOutbox(factory, jobOptions, clock);
        var message = await outbox.ClaimNextAsync("postgres-host-dispatcher");
        Assert.NotNull(message);
        Assert.Equal("job.enqueued", message!.Type);
        await outbox.MarkDeliveredAsync(message);

        await using var verify = await factory.CreateDbContextAsync();
        Assert.Single(await verify.Tenants.AsNoTracking().ToListAsync());
        Assert.Single(await verify.Users.AsNoTracking().ToListAsync());
        Assert.Single(await verify.BackendIdentities.AsNoTracking().ToListAsync());
        var jobs = await verify.Jobs.AsNoTracking().OrderBy(item => item.Type).ToListAsync();
        Assert.Equal(4, jobs.Count);
        Assert.Contains(jobs, item => item.State == DurableJobState.Succeeded);
        Assert.Contains(jobs, item => item.State == DurableJobState.Cancelled);
        Assert.Contains(jobs, item => item.Id == failing.JobId && item.State == DurableJobState.Failed);
        Assert.Contains(jobs, item => item.Type == DurableScheduleEngine.PlaylistSyncJobType);
        Assert.NotNull((await verify.OutboxMessages.AsNoTracking()
            .SingleAsync(item => item.Id == message.MessageId)).DeliveredAt);
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task NativePostgresHostOptions_ImportPortableStateInsideExplicitTransaction()
    {
        var connectionString = Environment.GetEnvironmentVariable("ALLSTARR_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var root = Path.Combine(Path.GetTempPath(), "allstarr-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var sqliteOptions = new DurableStorageOptions
            {
                Provider = "Sqlite",
                ConnectionString = $"Data Source={Path.Combine(root, "source.db")}",
                BackupDirectory = Path.Combine(root, "backups")
            };
            var sourceFactory = new TestDbContextFactory(new DbContextOptionsBuilder<AllstarrDbContext>()
                .UseSqlite(sqliteOptions.ConnectionString).Options);
            string schema;
            await using (var source = await sourceFactory.CreateDbContextAsync())
            {
                await source.Database.MigrateAsync();
                schema = source.Database.GetMigrations().Last();
                source.Tenants.Add(new TenantRecord
                {
                    Id = Guid.CreateVersion7(),
                    Slug = "portable-postgres",
                    Name = "Portable PostgreSQL",
                    CreatedAt = DateTimeOffset.UtcNow
                });
                await source.SaveChangesAsync();
            }

            var sourceState = new DurableStorageState(sqliteOptions);
            sourceState.Set(DurableStorageReadiness.Ready, schema);
            var transfer = new DurableStateTransferService(sourceFactory, sqliteOptions, sourceState);
            var artifact = await transfer.ExportAsync(Path.Combine(root, "transfer"), writesQuiesced: true);

            await using var hostServices = BuildHostStorageServices(connectionString);
            var targetFactory = hostServices.GetRequiredService<IDbContextFactory<AllstarrDbContext>>();
            await using (var target = await targetFactory.CreateDbContextAsync())
            {
                await target.Database.ExecuteSqlRawAsync("DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public");
            }

            await DurableStateTransferService.ImportAsync(artifact, targetFactory, targetConfirmedEmpty: true);
            await using var verify = await targetFactory.CreateDbContextAsync();
            Assert.Equal("portable-postgres", (await verify.Tenants.AsNoTracking().SingleAsync()).Slug);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task NativePostgresLegacyEnvMigration_AtomicallyAppliesAndDecryptsSharedAccount()
    {
        var connectionString = Environment.GetEnvironmentVariable("ALLSTARR_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "allstarr-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var hostServices = BuildHostStorageServices(connectionString);
            var factory = hostServices.GetRequiredService<IDbContextFactory<AllstarrDbContext>>();
            await using (var strategyContext = await factory.CreateDbContextAsync())
            {
                Assert.False(strategyContext.Database.CreateExecutionStrategy().RetriesOnFailure);
            }
            var tenantId = Guid.CreateVersion7();
            var userId = Guid.CreateVersion7();
            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.Database.ExecuteSqlRawAsync(
                    "DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public");
                await db.Database.MigrateAsync();
                db.Tenants.Add(new TenantRecord
                {
                    Id = tenantId,
                    Slug = "postgres-env-migration",
                    Name = "Postgres environment migration",
                    CreatedAt = DateTimeOffset.UtcNow
                });
                db.Users.Add(new PlatformUserRecord
                {
                    Id = userId,
                    TenantId = tenantId,
                    DisplayName = "Migration administrator",
                    Status = PlatformUserStatus.Active,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                });
                await db.SaveChangesAsync();
            }

            var keyRingPath = Path.Combine(root, "keyring.json");
            await File.WriteAllTextAsync(keyRingPath, JsonSerializer.Serialize(new
            {
                activeKeyId = "postgres-test-key",
                keys = new Dictionary<string, string>
                {
                    ["postgres-test-key"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                }
            }));
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(keyRingPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cache:LyricsDays"] = "14"
            }).Build();
            var clock = new SystemPlatformClock();
            var settings = new DurableRuntimeSettingsService(
                factory,
                configuration,
                clock,
                new RuntimeSettingsChangeSignal());
            var secretOptions = new SecretStoreOptions { KeyRingPath = keyRingPath };
            var secrets = new EncryptedSecretStore(
                factory,
                new FileSecretKeyRingProvider(secretOptions),
                secretOptions,
                clock);
            var actor = new LegacyEnvMigrationActor(
                "postgres-admin-session",
                tenantId,
                userId,
                "postgres-migration-correlation");
            var source = Encoding.UTF8.GetBytes("""
                CACHE_LYRICS_DAYS=45
                DEEZER_ARL=postgres-deezer-secret
                JELLYFIN_URL=http://old-jellyfin:8096
                SCROBBLING_LASTFM_SESSION_KEY=personal-session-secret
                SCROBBLING_LOCAL_TRACKS_ENABLED=true
                SPOTIFY_IMPORT_PLAYLISTS=[["Browser Mix","spotify-source-id","last"]]
                UNKNOWN_TOKEN=unknown-secret
                """);
            var migration = new LegacyEnvMigrationService(factory, settings, secrets, clock);
            var preview = await migration.PreviewAsync(source, actor);

            Assert.True(preview.CanApply);
            var result = await migration.ApplyAsync(
                preview.PreviewToken,
                preview.Revision,
                confirmed: true,
                actor);

            Assert.True(result.Success);
            Assert.Equal(3, result.SettingsImported);
            Assert.Equal(2, result.ProviderAccountsCreated);
            await using (var db = await factory.CreateDbContextAsync())
            {
                var storedSettings = await db.TenantRuntimeSettings.AsNoTracking().ToListAsync();
                Assert.Equal(3, storedSettings.Count);
                Assert.Contains(storedSettings, setting =>
                    setting.Key == "Cache:LyricsDays" && setting.ValueJson == "45");
                Assert.Contains(storedSettings, setting =>
                    setting.Key == "Scrobbling:LocalTracksEnabled" && setting.ValueJson == "true");
                Assert.Contains(storedSettings, setting =>
                    setting.Key == "SpotifyImport:Playlists" &&
                    setting.ValueJson == JsonSerializer.Serialize(
                        "[[\"Browser Mix\",\"spotify-source-id\",\"last\"]]"));
                var accounts = await db.ProviderAccounts.AsNoTracking().ToListAsync();
                Assert.Equal(2, accounts.Count);
                var account = Assert.Single(accounts, item => item.ProviderId == "deezer");
                Assert.Equal("deezer", account.ProviderId);
                Assert.False(account.Enabled);
                Assert.NotNull(account.SecretReferenceId);
                var personalAccount = Assert.Single(accounts, item => item.ProviderId == "lastfm");
                Assert.True(personalAccount.Enabled);
                Assert.Equal(tenantId, personalAccount.TenantId);
                Assert.Equal(userId, personalAccount.OwnerUserId);
                Assert.Single(await db.AuditEvents.AsNoTracking().ToListAsync());

                using var lease = await secrets.OpenAsync(
                    account.SecretReferenceId!.Value,
                    new SecretAccessContext(null, AllowGlobal: true));
                using var secret = JsonDocument.Parse(lease.Value);
                Assert.Equal("postgres-deezer-secret", secret.RootElement.GetProperty("arl").GetString());
            }

            var restarted = new LegacyEnvMigrationService(factory, settings, secrets, clock);
            var replayPreview = await restarted.PreviewAsync(source, actor);
            var replay = await restarted.ApplyAsync(
                replayPreview.PreviewToken,
                replayPreview.Revision,
                confirmed: true,
                actor);
            Assert.True(replay.AlreadyApplied);
            await using var verify = await factory.CreateDbContextAsync();
            Assert.Equal(2, await verify.ProviderAccounts.AsNoTracking().CountAsync());
            Assert.Single(await verify.AuditEvents.AsNoTracking().ToListAsync());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ServiceProvider BuildHostStorageServices(string connectionString)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "Postgres",
            ["Storage:ConnectionString"] = connectionString,
            ["Storage:AutoMigrate"] = "true",
            ["Storage:ConnectionRetryCount"] = "3",
            ["Storage:BackupDirectory"] = Path.Combine(Path.GetTempPath(), "allstarr-postgres-backups")
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDurableStorage(configuration, new PostgresTestHostEnvironment());
        return services.BuildServiceProvider();
    }

    private sealed class PostgresTestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "allstarr.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task NativePostgresAdditiveMigrations_CanRollBackToFoundationAndReapply()
    {
        var connectionString = Environment.GetEnvironmentVariable("ALLSTARR_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var dbOptions = new DbContextOptionsBuilder<AllstarrDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        await using var context = new AllstarrDbContext(dbOptions);
        await context.Database.ExecuteSqlRawAsync(
            "DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public");
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync();

        await migrator.MigrateAsync("20260710145139_InitialDurableFoundation");

        Assert.False(await RelationExists(context, "provider_health_rollups"));
        Assert.False(await ColumnExists(context, "durable_jobs", "MaxDeferrals"));
        Assert.False(await ColumnExists(context, "durable_jobs", "PolicySnapshotJson"));
        Assert.False(await ColumnExists(context, "durable_jobs", "RequestFingerprint"));
        Assert.False(await ColumnExists(context, "outbox_messages", "MaxAttempts"));
        Assert.False(await ColumnExists(context, "backups", "RestoreStatus"));
        Assert.False(await RelationExists(context, "canonical_recordings"));
        Assert.False(await RelationExists(context, "provider_track_identities"));
        Assert.False(await RelationExists(context, "tenant_runtime_settings"));

        await migrator.MigrateAsync();

        Assert.True(await RelationExists(context, "provider_health_rollups"));
        Assert.True(await ColumnExists(context, "durable_jobs", "MaxDeferrals"));
        Assert.True(await ColumnExists(context, "durable_jobs", "PolicySnapshotJson"));
        Assert.True(await ColumnExists(context, "durable_jobs", "RequestFingerprint"));
        Assert.True(await ColumnExists(context, "outbox_messages", "MaxAttempts"));
        Assert.True(await ColumnExists(context, "backups", "RestoreStatus"));
        Assert.True(await RelationExists(context, "canonical_recordings"));
        Assert.True(await RelationExists(context, "provider_track_identities"));
        Assert.True(await RelationExists(context, "tenant_runtime_settings"));
        Assert.True(await ColumnExists(context, "application_cache_entries", "Category"));
        Assert.False(context.Database.HasPendingModelChanges());
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task NativePostgresMigrationLockAndDurableQueue_WorkAgainstSelectedDatabase()
    {
        var connectionString = Environment.GetEnvironmentVariable("ALLSTARR_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var dbOptions = new DbContextOptionsBuilder<AllstarrDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        var factory = new TestDbContextFactory(dbOptions);
        await using (var reset = await factory.CreateDbContextAsync())
        {
            await reset.Database.ExecuteSqlRawAsync(
                "DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public");
        }
        await using (var sanity = await factory.CreateDbContextAsync())
        {
            await sanity.Database.MigrateAsync();
        }
        await using (var reset = await factory.CreateDbContextAsync())
        {
            await reset.Database.ExecuteSqlRawAsync(
                "DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public");
        }

        var options = new DurableStorageOptions
        {
            Provider = "Postgres",
            ConnectionString = connectionString,
            AutoMigrate = true,
            ConnectionRetryCount = 0,
            BackupDirectory = Path.Combine(Path.GetTempPath(), "allstarr-postgres-backups")
        };
        var firstState = new DurableStorageState(options);
        var secondState = new DurableStorageState(options);
        var first = new DurableStorageInitializer(
            factory,
            options,
            firstState,
            NullLogger<DurableStorageInitializer>.Instance);
        var second = new DurableStorageInitializer(
            factory,
            options,
            secondState,
            NullLogger<DurableStorageInitializer>.Instance);

        await Task.WhenAll(
            first.StartAsync(CancellationToken.None),
            second.StartAsync(CancellationToken.None));

        Assert.Equal(DurableStorageReadiness.Ready, firstState.GetSnapshot().Readiness);
        Assert.Equal(DurableStorageReadiness.Ready, secondState.GetSnapshot().Readiness);
        await using (var context = await factory.CreateDbContextAsync())
        {
            var idType = await ColumnType(context, "tenants", "Id");
            var cipherType = await ColumnType(context, "secret_versions", "Ciphertext");
            var jobAccountType = await ColumnType(context, "durable_jobs", "ProviderAccountId");
            var restoreTimeType = await ColumnType(context, "backups", "RestoreVerifiedAt");
            var canonicalTenantType = await ColumnType(context, "canonical_recordings", "TenantId");
            var identityCanonicalType = await ColumnType(
                context,
                "provider_track_identities",
                "CanonicalRecordingId");
            var identityVerifiedAtType = await ColumnType(
                context,
                "provider_track_identities",
                "VerifiedAt");
            Assert.Equal("uuid", idType);
            Assert.Equal("bytea", cipherType);
            Assert.Equal("uuid", jobAccountType);
            Assert.Equal("bigint", restoreTimeType);
            Assert.Equal("uuid", canonicalTenantType);
            Assert.Equal("uuid", identityCanonicalType);
            Assert.Equal("bigint", identityVerifiedAtType);
            var tenantId = Guid.CreateVersion7();
            var userId = Guid.CreateVersion7();
            context.Tenants.Add(new TenantRecord
            {
                Id = tenantId,
                Slug = "postgres-fixture",
                Name = "Postgres fixture",
                CreatedAt = DateTimeOffset.UtcNow
            });
            context.Users.Add(new PlatformUserRecord
            {
                Id = userId,
                TenantId = tenantId,
                DisplayName = "Postgres user",
                Status = PlatformUserStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await context.SaveChangesAsync();

            var jobOptions = new DurableJobOptions();
            var queue = new DurableJobQueue(
                factory,
                jobOptions,
                new JobPayloadPolicy(jobOptions),
                new SystemPlatformClock());
            var enqueued = await queue.EnqueueAsync(new DurableJobEnqueueRequest<object>(
                "postgres.fixture",
                "postgres-idempotency",
                new { trackId = "fixture" },
                tenantId,
                userId));
            var repeated = await queue.EnqueueAsync(new DurableJobEnqueueRequest<object>(
                "postgres.fixture",
                "postgres-idempotency",
                new { trackId = "fixture" },
                tenantId,
                userId));

            Assert.True(enqueued.Created);
            Assert.False(repeated.Created);
            Assert.Equal(enqueued.JobId, repeated.JobId);
        }
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task NativePostgresCacheLoss_PreservesDurableWorkAndProgressAcrossCacheRestart()
    {
        var connectionString = Environment.GetEnvironmentVariable("ALLSTARR_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var dbOptions = new DbContextOptionsBuilder<AllstarrDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        var factory = new TestDbContextFactory(dbOptions);
        await using (var reset = await factory.CreateDbContextAsync())
        {
            await reset.Database.ExecuteSqlRawAsync(
                "DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public");
            await reset.Database.MigrateAsync();
        }

        var now = new DateTimeOffset(2026, 7, 24, 17, 0, 0, TimeSpan.Zero);
        var clock = new FixedClock(now);
        var tenantId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();
        await using (var seed = await factory.CreateDbContextAsync())
        {
            seed.Tenants.Add(new TenantRecord
            {
                Id = tenantId,
                Slug = "cache-loss",
                Name = "Cache loss",
                CreatedAt = now
            });
            seed.Users.Add(new PlatformUserRecord
            {
                Id = userId,
                TenantId = tenantId,
                DisplayName = "Cache owner",
                Status = PlatformUserStatus.Active,
                CreatedAt = now,
                UpdatedAt = now
            });
            await seed.SaveChangesAsync();
        }

        var jobOptions = new DurableJobOptions();
        var queue = new DurableJobQueue(
            factory,
            jobOptions,
            new JobPayloadPolicy(jobOptions),
            clock);
        var enqueued = await queue.EnqueueAsync(new DurableJobEnqueueRequest<object>(
            "playlist.match-all",
            "postgres-cache-loss",
            new { generation = 1 },
            tenantId,
            userId));
        var claim = await queue.ClaimNextAsync("postgres-cache-worker");
        Assert.NotNull(claim);
        Assert.True(await queue.ReportProgressAsync(
            claim!,
            new DurableJobProgressUpdate(
                "provider-started",
                "Matching Spotify playlists.",
                0,
                1,
                "spotify")));

        var firstCache = new DatabaseApplicationCache(
            factory,
            clock,
            NullLogger<DatabaseApplicationCache>.Instance);
        Assert.True(await firstCache.SetStringAsync(
            "search:cache-loss",
            "disposable",
            TimeSpan.FromHours(1)));
        Assert.Equal("disposable", await firstCache.GetStringAsync("search:cache-loss"));

        await using (var purge = await factory.CreateDbContextAsync())
        {
            await purge.ApplicationCacheEntries.ExecuteDeleteAsync();
        }

        var restartedCache = new DatabaseApplicationCache(
            factory,
            clock,
            NullLogger<DatabaseApplicationCache>.Instance);
        Assert.Null(await restartedCache.GetStringAsync("search:cache-loss"));
        await using var verification = await factory.CreateDbContextAsync();
        Assert.True(await verification.Jobs.AnyAsync(item => item.Id == enqueued.JobId));
        Assert.True(await verification.AuditEvents.AnyAsync(item =>
            item.Category == "job-progress" &&
            item.CorrelationId == claim!.CorrelationId));
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task NativePostgresBackup_VerifiesAndRestoresIntoIsolatedDatabase()
    {
        var connectionString = Environment.GetEnvironmentVariable("ALLSTARR_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var sourceBuilder = new NpgsqlConnectionStringBuilder(connectionString);
        var targetDatabase = $"allstarr_restore_{Guid.NewGuid():N}";
        var backupRoot = Path.Combine(
            Path.GetTempPath(),
            "allstarr-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backupRoot);
        try
        {
            var dbOptions = new DbContextOptionsBuilder<AllstarrDbContext>()
                .UseNpgsql(connectionString)
                .Options;
            var factory = new TestDbContextFactory(dbOptions);
            await using (var reset = await factory.CreateDbContextAsync())
            {
                await reset.Database.ExecuteSqlRawAsync(
                    "DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public");
                await reset.Database.MigrateAsync();
                reset.Jobs.Add(new DurableJobRecord
                {
                    Id = Guid.CreateVersion7(),
                    ScopeKey = "global",
                    Type = "postgres.backup-fixture",
                    PayloadJson = "{}",
                    IdempotencyKey = "before-backup",
                    State = DurableJobState.Pending,
                    MaxAttempts = 3,
                    AvailableAt = DateTimeOffset.UtcNow,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                });
                await reset.SaveChangesAsync();
            }

            var options = new DurableStorageOptions
            {
                Provider = "Postgres",
                ConnectionString = connectionString,
                BackupDirectory = backupRoot
            };
            var state = new DurableStorageState(options);
            state.Set(DurableStorageReadiness.Ready, "InitialDurableFoundation");
            var service = new DurableBackupService(
                factory,
                options,
                state,
                new StorageProcessRunner());

            var artifact = await service.CreateAsync();
            Assert.True(File.Exists(artifact.ArtifactPath));
            Assert.True(File.Exists(artifact.ManifestPath));
            await CreateDatabase(sourceBuilder, targetDatabase);
            var targetBuilder = new NpgsqlConnectionStringBuilder(connectionString)
            {
                Database = targetDatabase
            };

            await service.RestorePostgresAsync(
                artifact,
                targetBuilder.ConnectionString,
                destructiveRestoreConfirmed: true,
                isolatedTargetDatabaseConfirmation: targetDatabase);

            var restoredOptions = new DbContextOptionsBuilder<AllstarrDbContext>()
                .UseNpgsql(targetBuilder.ConnectionString)
                .Options;
            await using var restored = new AllstarrDbContext(restoredOptions);
            var restoredJob = await restored.Jobs.AsNoTracking().SingleAsync();
            Assert.Equal("before-backup", restoredJob.IdempotencyKey);
            Assert.Empty(await restored.Backups.AsNoTracking().ToListAsync());
        }
        finally
        {
            await DropDatabase(sourceBuilder, targetDatabase);
            if (Directory.Exists(backupRoot))
            {
                Directory.Delete(backupRoot, recursive: true);
            }
        }
    }

    private static async Task<string> ColumnType(
        AllstarrDbContext context,
        string table,
        string column)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText =
            "SELECT data_type FROM information_schema.columns " +
            "WHERE table_schema = 'public' AND table_name = @table AND column_name = @column";
        var tableParameter = command.CreateParameter();
        tableParameter.ParameterName = "table";
        tableParameter.Value = table;
        command.Parameters.Add(tableParameter);
        var columnParameter = command.CreateParameter();
        columnParameter.ParameterName = "column";
        columnParameter.Value = column;
        command.Parameters.Add(columnParameter);
        if (command.Connection!.State != System.Data.ConnectionState.Open)
        {
            await command.Connection.OpenAsync();
        }

        return (string)(await command.ExecuteScalarAsync()
                        ?? throw new InvalidOperationException("Column type was not found."));
    }

    private sealed class FixedClock(DateTimeOffset now) : IPlatformClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private static async Task<bool> RelationExists(
        AllstarrDbContext context,
        string relation)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM information_schema.tables " +
            "WHERE table_schema = 'public' AND table_name = @relation";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "relation";
        parameter.Value = relation;
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
        command.CommandText =
            "SELECT COUNT(*) FROM information_schema.columns " +
            "WHERE table_schema = 'public' AND table_name = @table AND column_name = @column";
        var tableParameter = command.CreateParameter();
        tableParameter.ParameterName = "table";
        tableParameter.Value = table;
        command.Parameters.Add(tableParameter);
        var columnParameter = command.CreateParameter();
        columnParameter.ParameterName = "column";
        columnParameter.Value = column;
        command.Parameters.Add(columnParameter);
        if (command.Connection!.State != System.Data.ConnectionState.Open)
        {
            await command.Connection.OpenAsync();
        }

        return Convert.ToInt32(await command.ExecuteScalarAsync()) == 1;
    }

    private static async Task CreateDatabase(
        NpgsqlConnectionStringBuilder source,
        string database)
    {
        var admin = new NpgsqlConnectionStringBuilder(source.ConnectionString)
        {
            Database = "postgres"
        };
        await using var connection = new NpgsqlConnection(admin.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE {QuoteIdentifier(database)}";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DropDatabase(
        NpgsqlConnectionStringBuilder source,
        string database)
    {
        var admin = new NpgsqlConnectionStringBuilder(source.ConnectionString)
        {
            Database = "postgres"
        };
        await using var connection = new NpgsqlConnection(admin.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP DATABASE IF EXISTS {QuoteIdentifier(database)} WITH (FORCE)";
        await command.ExecuteNonQueryAsync();
    }

    private static string QuoteIdentifier(string identifier) =>
        '"' + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';

    private sealed class TestDbContextFactory(DbContextOptions<AllstarrDbContext> options)
        : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);

        public Task<AllstarrDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(new AllstarrDbContext(options));
    }
}
