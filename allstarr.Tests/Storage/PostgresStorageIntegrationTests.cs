using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Configuration;
using allstarr.Core.Favorites;
using allstarr.Core.Jobs;
using allstarr.Core.Identity;
using allstarr.Core.Intelligence;
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

namespace allstarr.Tests;

public sealed class PostgresStorageIntegrationTests
{
    [Fact]
    [Trait("Category", "Postgres")]
    public async Task NativePostgresLineageConstraints_RejectCrossTenantFavoriteJob()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var db = new AllstarrDbContext(database.Options);

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
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var services = BuildHostStorageServices(database.ConnectionString);
        var factory = services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
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
        var root = Path.Combine(Path.GetTempPath(), "allstarr-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await using var sourceDatabase = await PostgresTestDatabase.CreateAsync();
        await using var targetDatabase = await PostgresTestDatabase.CreateAsync();
        try
        {
            var sourceOptions = new DurableStorageOptions
            {
                Provider = "Postgres",
                ConnectionString = sourceDatabase.ConnectionString,
                BackupDirectory = Path.Combine(root, "backups")
            };
            var sourceFactory = new TestDbContextFactory(sourceDatabase.Options);
            string schema;
            await using (var source = await sourceFactory.CreateDbContextAsync())
            {
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

            var sourceState = new DurableStorageState(sourceOptions);
            sourceState.Set(DurableStorageReadiness.Ready, schema);
            var transfer = new DurableStateTransferService(sourceFactory, sourceOptions, sourceState);
            var artifact = await transfer.ExportAsync(Path.Combine(root, "transfer"), writesQuiesced: true);

            var targetFactory = new TestDbContextFactory(targetDatabase.Options);

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
        await using var database = await PostgresTestDatabase.CreateAsync();
        var root = Path.Combine(Path.GetTempPath(), "allstarr-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var hostServices = BuildHostStorageServices(database.ConnectionString);
            var factory = hostServices.GetRequiredService<IDbContextFactory<AllstarrDbContext>>();
            await using (var strategyContext = await factory.CreateDbContextAsync())
            {
                Assert.False(strategyContext.Database.CreateExecutionStrategy().RetriesOnFailure);
            }
            var tenantId = Guid.CreateVersion7();
            var userId = Guid.CreateVersion7();
            await using (var db = await factory.CreateDbContextAsync())
            {
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
            Assert.Equal(2, result.SettingsImported);
            Assert.Equal(2, result.ProviderAccountsCreated);
            await using (var db = await factory.CreateDbContextAsync())
            {
                var storedSettings = await db.TenantRuntimeSettings.AsNoTracking().ToListAsync();
                Assert.Equal(2, storedSettings.Count);
                Assert.Contains(storedSettings, setting =>
                    setting.Key == "Cache:LyricsDays" && setting.ValueJson == "45");
                Assert.Contains(storedSettings, setting =>
                    setting.Key == "Scrobbling:LocalTracksEnabled" && setting.ValueJson == "true");
                Assert.DoesNotContain(storedSettings, setting =>
                    setting.Key == "SpotifyImport:Playlists");
                Assert.Empty(await db.PlaylistLinks.AsNoTracking().ToListAsync());
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
    [Trait("Lane", "ReleaseCritical")]
    public async Task NativePostgresAdditiveMigrations_CanRollBackToFoundationAndReapply()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var context = new AllstarrDbContext(database.Options);
        var migrator = context.GetService<IMigrator>();

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
    [Trait("Lane", "ReleaseCritical")]
    public async Task BackendCredentialMigration_BindsExistingExactIntelligenceScope()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(useTemplate: false);
        await using var context = new AllstarrDbContext(database.Options);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync("20260803040000_AddListeningIntakeTokens");
        var tenant = Guid.CreateVersion7();
        var user = Guid.CreateVersion7();
        var identity = Guid.CreateVersion7();
        var credential = Guid.CreateVersion7();
        var policy = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow.UtcTicks;

        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO tenants ("Id", "Slug", "Name", "CreatedAt")
            VALUES ({tenant}, 'credential-migration', 'Credential migration', {now});
            INSERT INTO users ("Id", "TenantId", "DisplayName", "Status", "CreatedAt", "UpdatedAt")
            VALUES ({user}, {tenant}, 'Listener', 'Active', {now}, {now});
            INSERT INTO backend_identities
                ("Id", "TenantId", "UserId", "BackendType", "BackendInstanceId", "PrincipalId", "CreatedAt", "LastSeenAt")
            VALUES ({identity}, {tenant}, {user}, 'subsonic', 'main', 'listener', {now}, {now});
            INSERT INTO secret_references
                ("Id", "TenantId", "Purpose", "ActiveVersion", "CreatedAt", "UpdatedAt")
            VALUES ({credential}, {tenant}, 'playlist-backend:subsonic', 0, {now}, {now});
            INSERT INTO intelligence_policies
                ("Id", "TenantId", "OwnerUserId", "Protocol", "BackendInstanceId", "LibraryScopeId",
                 "Enabled", "TargetCredentialReferenceId", "RetentionDays", "AllowedSignalTypesJson",
                 "EnabledProvidersJson", "CreatedAt", "UpdatedAt", "Revision")
            VALUES ({policy}, {tenant}, {user}, 'subsonic', 'main', 'music', true, {credential}, 30,
                    '["play"]', '["local"]', {now}, {now}, 1);
            """);

        await migrator.MigrateAsync();

        Assert.Equal(identity, (await context.SecretReferences.AsNoTracking()
            .SingleAsync(item => item.Id == credential)).BackendIdentityId);
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task V3CompatibilityMigration_BackfillsLegacyStateAndReappliesIdempotently()
    {
        const string previous = "20260803210000_OptimizeListeningAnalyticsIndex";
        const string current = "20260804080000_BackfillV3CompatibilityState";
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var context = new AllstarrDbContext(database.Options);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(previous);

        var now = DateTimeOffset.UtcNow;
        var tenant = Guid.CreateVersion7();
        var user = Guid.CreateVersion7();
        var account = Guid.CreateVersion7();
        var ambiguousAccounts = new[] { Guid.CreateVersion7(), Guid.CreateVersion7() };
        var job = Guid.CreateVersion7();
        var run = Guid.CreateVersion7();
        var candidate = Guid.CreateVersion7();
        var ambiguousCandidate = Guid.CreateVersion7();
        var package = Guid.CreateVersion7();
        context.Tenants.Add(new() { Id = tenant, Slug = "v3-backfill", Name = "V3 backfill", CreatedAt = now });
        context.Users.Add(new()
        {
            Id = user,
            TenantId = tenant,
            DisplayName = "Listener",
            Status = PlatformUserStatus.Active,
            CreatedAt = now,
            UpdatedAt = now
        });
        context.ProviderAccounts.Add(new()
        {
            Id = account,
            TenantId = tenant,
            OwnerUserId = user,
            ProviderId = "audiomuse",
            DisplayName = "AudioMuse",
            Scope = ProviderAccountScope.User,
            Enabled = true,
            CreatedAt = now,
            UpdatedAt = now,
            Revision = 1
        });
        context.ProviderAccounts.AddRange(ambiguousAccounts.Select(id => new ProviderAccountRecord
        {
            Id = id,
            TenantId = tenant,
            OwnerUserId = user,
            ProviderId = "qobuz",
            DisplayName = $"Qobuz {id:N}",
            Scope = ProviderAccountScope.User,
            Enabled = true,
            CreatedAt = now,
            UpdatedAt = now,
            Revision = 1
        }));
        context.Jobs.Add(DatabaseLineageConstraintTests.Job(job, tenant, user, "v3-backfill", now));
        context.RecommendationRuns.Add(new()
        {
            Id = run,
            TenantId = tenant,
            OwnerUserId = user,
            Protocol = "jellyfin",
            BackendInstanceId = "main",
            LibraryScopeId = "music",
            JobId = job,
            IdempotencyKey = "v3-backfill",
            Limit = 10,
            State = RecommendationRunState.Succeeded,
            CreatedAt = now,
            UpdatedAt = now,
            CompletedAt = now,
            Revision = 1
        });
        context.RecommendationCandidates.Add(new()
        {
            Id = candidate,
            RunId = run,
            TenantId = tenant,
            OwnerUserId = user,
            Position = 0,
            TrackKey = "audiomuse:track:1",
            Score = .9,
            Source = "audiomuse",
            SignalsJson = "[]",
            IdentityJson = "{}",
            SourceRevision = "legacy",
            ExclusionsJson = "[]",
            CreatedAt = now,
            Revision = 0
        });
        context.RecommendationCandidates.Add(new()
        {
            Id = ambiguousCandidate,
            RunId = run,
            TenantId = tenant,
            OwnerUserId = user,
            Position = 1,
            TrackKey = "qobuz:track:1",
            Score = .8,
            Source = "qobuz",
            SignalsJson = "[]",
            IdentityJson = "{}",
            SourceRevision = "legacy",
            ExclusionsJson = "[]",
            CreatedAt = now,
            Revision = 0
        });
        foreach (var (key, value) in new[]
                 {
                     ("AppleDownload:Quality", "alac-24-96"),
                     ("Deezer:Quality", "FLAC"),
                     ("Qobuz:Quality", "FLAC_24_HIGH")
                 })
        {
            context.TenantRuntimeSettings.Add(new()
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenant,
                Key = key,
                ValueType = RuntimeSettingValueType.String,
                ValueJson = JsonSerializer.Serialize(value),
                Source = "legacy",
                CreatedAt = now,
                UpdatedAt = now,
                Revision = 1
            });
        }
        context.ExtensionPackages.Add(new()
        {
            Id = package,
            ExtensionId = "demo",
            DisplayName = "Demo",
            Version = "1.0.0",
            SdkVersion = "1",
            Sha256 = new string('a', 64),
            ContentSha256 = new string('b', 64),
            PackagePath = "/extensions/demo",
            ManifestJson = """{"id":"spotiflac-demo","compatibility":"spotiflac-v1"}""",
            State = ExtensionPackageState.Active,
            StagedAt = now,
            ActivatedAt = now,
            Revision = 0
        });
        context.ExtensionLogs.Add(new()
        {
            Id = Guid.CreateVersion7(),
            ExtensionPackageId = package,
            ExtensionId = "demo",
            Level = "Info",
            EventCode = "legacy",
            Message = "Legacy",
            CorrelationId = "v3-backfill",
            CreatedAt = now
        });
        await context.SaveChangesAsync();

        await migrator.MigrateAsync(current);
        context.ChangeTracker.Clear();
        var shared = await context.TenantRuntimeSettings.SingleAsync(item => item.Key == AudioQualityPolicy.SettingKey);
        Assert.Equal("\"HiResLossless\"", shared.ValueJson);
        Assert.Equal("v3-compatibility-migration", shared.Source);
        var normalizedPackage = await context.ExtensionPackages.SingleAsync(item => item.Id == package);
        Assert.Equal("spotiflac-demo", normalizedPackage.ExtensionId);
        Assert.Equal(1, normalizedPackage.Revision);
        Assert.Equal("spotiflac-demo", (await context.ExtensionLogs.SingleAsync()).ExtensionId);
        var normalizedCandidate = await context.RecommendationCandidates.SingleAsync(item => item.Id == candidate);
        Assert.Equal($"run:{run:N}", normalizedCandidate.SourceRevision);
        Assert.Equal(account, normalizedCandidate.ProviderAccountId);
        Assert.Equal(2, normalizedCandidate.Revision);
        var unresolvedCandidate = await context.RecommendationCandidates.SingleAsync(item => item.Id == ambiguousCandidate);
        Assert.Equal($"run:{run:N}", unresolvedCandidate.SourceRevision);
        Assert.Null(unresolvedCandidate.ProviderAccountId);
        Assert.Equal(1, unresolvedCandidate.Revision);

        await migrator.MigrateAsync(previous);
        await using var restarted = new AllstarrDbContext(database.Options);
        await restarted.GetService<IMigrator>().MigrateAsync(current);
        Assert.Single(await restarted.TenantRuntimeSettings.Where(item => item.Key == AudioQualityPolicy.SettingKey).ToListAsync());
        Assert.Equal(1, (await restarted.ExtensionPackages.SingleAsync(item => item.Id == package)).Revision);
        Assert.Equal(2, (await restarted.RecommendationCandidates.SingleAsync(item => item.Id == candidate)).Revision);
    }

    [Fact]
    [Trait("Category", "Postgres")]
    [Trait("Lane", "ReleaseCritical")]
    public async Task NativePostgresMigrationLockAndDurableQueue_WorkAgainstSelectedDatabase()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(useTemplate: false);
        var factory = new TestDbContextFactory(database.Options);

        var options = new DurableStorageOptions
        {
            Provider = "Postgres",
            ConnectionString = database.ConnectionString,
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
    [Trait("Lane", "ReleaseCritical")]
    public async Task NativePostgresCacheLoss_PreservesDurableWorkAndProgressAcrossCacheRestart()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var factory = new TestDbContextFactory(database.Options);

        var now = new DateTimeOffset(2026, 7, 24, 17, 0, 0, TimeSpan.Zero);
        var clock = new FixedClock(now);
        var tenantId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();
        var accountId = Guid.CreateVersion7();
        var recordingId = Guid.CreateVersion7();
        var providerIdentityId = Guid.CreateVersion7();
        var linkId = Guid.CreateVersion7();
        var snapshotId = Guid.CreateVersion7();
        var firstExternalId = Guid.CreateVersion7();
        var secondExternalId = Guid.CreateVersion7();
        const string hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        await using (var seed = await factory.CreateDbContextAsync())
        {
            seed.AddRange(
                new TenantRecord
                {
                    Id = tenantId,
                    Slug = "cache-loss",
                    Name = "Cache loss",
                    CreatedAt = now
                },
                new PlatformUserRecord
                {
                    Id = userId,
                    TenantId = tenantId,
                    DisplayName = "Cache owner",
                    Status = PlatformUserStatus.Active,
                    CreatedAt = now,
                    UpdatedAt = now
                },
                new ProviderAccountRecord
                {
                    Id = accountId,
                    TenantId = tenantId,
                    OwnerUserId = userId,
                    ProviderId = "fixture",
                    DisplayName = "Fixture",
                    Scope = ProviderAccountScope.User,
                    Enabled = true,
                    CreatedAt = now,
                    UpdatedAt = now
                },
                new CanonicalRecordingRecord
                {
                    Id = recordingId,
                    TenantId = tenantId,
                    CreatedByUserId = userId,
                    CreatedAt = now,
                    UpdatedAt = now
                },
                new ProviderTrackIdentityRecord
                {
                    Id = providerIdentityId,
                    TenantId = tenantId,
                    CanonicalRecordingId = recordingId,
                    ProviderAccountId = accountId,
                    ProviderId = "fixture",
                    ResourceKind = ProviderResourceKind.Track,
                    Scope = ProviderIdentityScope.Account,
                    ExternalId = "track",
                    ExternalIdHash = hash,
                    Verification = ProviderIdentityVerification.Verified,
                    VerificationMethod = "fixture",
                    DecisionVersion = 1,
                    VerifiedAt = now,
                    CreatedAt = now,
                    UpdatedAt = now
                },
                new PlaylistLinkRecord
                {
                    Id = linkId,
                    TenantId = tenantId,
                    OwnerUserId = userId,
                    ProviderAccountId = accountId,
                    LibraryScopeId = "music",
                    SourceProviderId = "fixture",
                    SourcePlaylistId = "playlist",
                    SourcePlaylistIdHash = hash,
                    TargetProtocol = "jellyfin",
                    TargetBackendInstanceId = "home",
                    Mode = PlaylistLinkMode.Materialized,
                    MaterializationMode = PlaylistMaterializationMode.Reconcile,
                    RuleVersion = "rules-v1",
                    PolicyVersion = "policy-v1",
                    CreatedAt = now,
                    UpdatedAt = now
                },
                External(firstExternalId, 1),
                External(secondExternalId, 2),
                new PlaylistSourceSnapshotRecord
                {
                    Id = snapshotId,
                    TenantId = tenantId,
                    OwnerUserId = userId,
                    PlaylistLinkId = linkId,
                    ProviderAccountId = accountId,
                    SnapshotVersion = 1,
                    ProviderRevision = "revision",
                    Name = "Ordered",
                    PayloadSha256 = hash,
                    CorrelationId = "cache-loss",
                    RetrievedAt = now
                },
                new PlaylistSourceEntryRecord
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenantId,
                    PlaylistSourceSnapshotId = snapshotId,
                    ExternalMetadataSnapshotId = firstExternalId,
                    SourcePosition = 0,
                    SourceEntryIdHash = hash
                },
                new PlaylistSourceEntryRecord
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenantId,
                    PlaylistSourceSnapshotId = snapshotId,
                    ExternalMetadataSnapshotId = secondExternalId,
                    SourcePosition = 1,
                    SourceEntryIdHash = new string('b', 64)
                });
            await seed.SaveChangesAsync();

            ExternalMetadataSnapshotRecord External(Guid id, int version) => new()
            {
                Id = id,
                TenantId = tenantId,
                OwnerUserId = userId,
                ProviderAccountId = accountId,
                LibraryScopeId = "music",
                BackendInstanceId = "home",
                BackendPrincipalId = "principal",
                Protocol = "jellyfin",
                ProviderId = "fixture",
                ResourceKind = "track",
                ExternalIdHash = new string((char)('a' + version), 64),
                SnapshotVersion = 1,
                ProviderRevision = $"revision-{version}",
                PayloadSha256 = new string((char)('c' + version), 64),
                CorrelationId = "cache-loss",
                RetrievedAt = now
            };
        }

        var jobOptions = new DurableJobOptions();
        var queue = new DurableJobQueue(
            factory,
            jobOptions,
            new JobPayloadPolicy(jobOptions),
            clock);
        var enqueued = await queue.EnqueueAsync(new DurableJobEnqueueRequest<object>(
            "playlist.materialize",
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
            "search:v2:cache-loss",
            "disposable",
            TimeSpan.FromHours(1)));
        Assert.Equal("disposable", await firstCache.GetStringAsync("search:v2:cache-loss"));

        await using (var purge = await factory.CreateDbContextAsync())
        {
            await purge.ApplicationCacheEntries.ExecuteDeleteAsync();
        }

        var restartedCache = new DatabaseApplicationCache(
            factory,
            clock,
            NullLogger<DatabaseApplicationCache>.Instance);
        Assert.Null(await restartedCache.GetStringAsync("search:v2:cache-loss"));
        await using var verification = await factory.CreateDbContextAsync();
        Assert.True(await verification.Jobs.AnyAsync(item => item.Id == enqueued.JobId));
        Assert.True(await verification.CanonicalRecordings.AnyAsync(item => item.Id == recordingId));
        Assert.True(await verification.ProviderTrackIdentities.AnyAsync(item =>
            item.Id == providerIdentityId &&
            item.CanonicalRecordingId == recordingId));
        var positions = await verification.PlaylistSourceEntries
            .Where(item => item.PlaylistSourceSnapshotId == snapshotId)
            .OrderBy(item => item.SourcePosition)
            .Select(item => item.SourcePosition)
            .ToArrayAsync();
        Assert.Equal(new[] { 0, 1 }, positions);
        Assert.True(await verification.AuditEvents.AnyAsync(item =>
            item.Category == "job-progress" &&
            item.CorrelationId == claim!.CorrelationId));
    }

    [Fact]
    [Trait("Category", "Postgres")]
    [Trait("Lane", "ReleaseCritical")]
    public async Task NativePostgresBackup_VerifiesAndRestoresIntoIsolatedDatabase()
    {
        await using var sourceDatabase = await PostgresTestDatabase.CreateAsync();
        await using var targetDatabase = await PostgresTestDatabase.CreateAsync();
        var backupRoot = Path.Combine(
            Path.GetTempPath(),
            "allstarr-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backupRoot);
        try
        {
            var factory = new TestDbContextFactory(sourceDatabase.Options);
            await using (var reset = await factory.CreateDbContextAsync())
            {
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
                ConnectionString = sourceDatabase.ConnectionString,
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

            await service.RestorePostgresAsync(
                artifact,
                targetDatabase.ConnectionString,
                destructiveRestoreConfirmed: true,
                isolatedTargetDatabaseConfirmation: targetDatabase.DatabaseName);

            var restoredOptions = new DbContextOptionsBuilder<AllstarrDbContext>()
                .UseNpgsql(targetDatabase.ConnectionString)
                .Options;
            await using var restored = new AllstarrDbContext(restoredOptions);
            var restoredJob = await restored.Jobs.AsNoTracking().SingleAsync();
            Assert.Equal("before-backup", restoredJob.IdempotencyKey);
            Assert.Empty(await restored.Backups.AsNoTracking().ToListAsync());
        }
        finally
        {
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

    private sealed class TestDbContextFactory(DbContextOptions<AllstarrDbContext> options)
        : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);

        public Task<AllstarrDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(new AllstarrDbContext(options));
    }
}
