using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Configuration;
using allstarr.Core.Downloads;
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

public sealed class StorageIntegrationTests
{
    [Fact]
    public async Task DownloadedTrackCache_SeparatesTenantsAccountsLibrariesAndQuality()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var store = new EfDownloadedSongMappingStore(new TestDbContextFactory(database.Options));
        var tenant = Guid.CreateVersion7();
        var otherTenant = Guid.CreateVersion7();
        var account = Guid.CreateVersion7();
        var scope = new DownloadedSongMappingScope(
            tenant, account, "music", ProviderAudioQuality.Lossless);
        await store.UpsertAsync(new DownloadedSongMappingEntity
        {
            Id = Guid.CreateVersion7(),
            ScopeKey = scope.Key,
            TenantId = tenant,
            ProviderAccountId = account,
            LibraryScopeId = "music",
            AudioQuality = ProviderAudioQuality.Lossless,
            ProviderId = "deezer",
            ExternalId = "track",
            LocalPath = "/managed/cache/track.flac",
            Title = "Track",
            Artist = "Artist",
            Album = "Album",
            DownloadedAt = DateTimeOffset.UtcNow
        });

        Assert.NotNull(await store.FindAsync(scope, "deezer", "track"));
        Assert.Null(await store.FindAsync(
            scope with { TenantId = otherTenant }, "deezer", "track"));
        Assert.Null(await store.FindAsync(
            scope with { ProviderAccountId = Guid.CreateVersion7() }, "deezer", "track"));
        Assert.Null(await store.FindAsync(
            scope with { LibraryScopeId = "other" }, "deezer", "track"));
        Assert.Null(await store.FindAsync(
            scope with { AudioQuality = ProviderAudioQuality.HighResolution }, "deezer", "track"));
        Assert.Null(await store.FindAsync("deezer", "track"));
    }

    [Fact]
    public async Task StorageLineageConstraints_RejectCrossTenantFavoriteJob()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
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


    }

    [Fact]
    public async Task StorageHostOptions_SupportIdentityJobAndScheduleTransactions()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        await using var services = BuildHostStorageServices(database.ConnectionString);
        var factory = services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.MigrateAsync();
        }

        var storageState = new DurableStorageState(services.GetRequiredService<StorageOptions>());
        await using (var db = await factory.CreateDbContextAsync())
        {
            storageState.Set(DurableStorageReadiness.Ready, db.Database.GetMigrations().Last());
        }

        var identityOptions = new IdentityOptions
        {
            Mode = "Hybrid",
            DefaultTenantId = Guid.CreateVersion7().ToString(),
            SingleUserId = Guid.CreateVersion7().ToString(),
            DefaultTenantSlug = "host-sqlite",
            DefaultTenantName = "Host SQLite",
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
            "sqlite.host-transaction",
            "sqlite-host-transaction",
            new { value = "safe" },
            principal!.TenantId,
            principal.UserId));
        Assert.True(enqueued.Created);
        var claim = await queue.ClaimNextAsync("sqlite-host-worker");
        Assert.NotNull(claim);
        await queue.CompleteAsync(claim!, DurableJobCompletion.Success());

        var cancellable = await queue.EnqueueAsync(new DurableJobEnqueueRequest<object>(
            "sqlite.host-cancel",
            "sqlite-host-cancel",
            new { value = "cancel" },
            principal.TenantId,
            principal.UserId));
        Assert.True(await queue.RequestCancellationAsync(cancellable.JobId, principal.TenantId));

        var failing = await queue.EnqueueAsync(new DurableJobEnqueueRequest<object>(
            "sqlite.host-failure",
            "sqlite-host-failure",
            new { value = "fail" },
            principal.TenantId,
            principal.UserId));
        var failureClaim = await queue.ClaimNextAsync(
            "sqlite-host-failure-worker",
            ["sqlite.host-failure"]);
        Assert.NotNull(failureClaim);
        await queue.CompleteAsync(
            failureClaim!,
            DurableJobCompletion.Failure("expected_test_failure", "Expected native SQLite test failure."));

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
                SourcePlaylistId = "native-sqlite-playlist",
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
    }

    [Fact]
    public async Task StorageLegacyEnvMigration_AtomicallyAppliesAndDecryptsSharedAccount()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
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
                    Slug = "sqlite-env-migration",
                    Name = "SQLite environment migration",
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
                activeKeyId = "sqlite-test-key",
                keys = new Dictionary<string, string>
                {
                    ["sqlite-test-key"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
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
                "sqlite-admin-session",
                tenantId,
                userId,
                "sqlite-migration-correlation");
            var source = Encoding.UTF8.GetBytes("""
                CACHE_LYRICS_DAYS=45
                DEEZER_ARL=sqlite-deezer-secret
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
                Assert.Equal("sqlite-deezer-secret", secret.RootElement.GetProperty("arl").GetString());
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
            ["Storage:DataDirectory"] = Path.GetDirectoryName(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString).DataSource),
            ["Storage:DatabaseFileName"] = Path.GetFileName(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString).DataSource),
            ["Storage:AutoMigrate"] = "true"
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDurableStorage(configuration, new StorageTestHostEnvironment());
        return services.BuildServiceProvider();
    }

    private sealed class StorageTestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "allstarr.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task StorageCacheLoss_PreservesDurableWorkAndProgressAcrossCacheRestart()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
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
            "sqlite-cache-loss",
            new { generation = 1 },
            tenantId,
            userId));
        var claim = await queue.ClaimNextAsync("sqlite-cache-worker");
        Assert.NotNull(claim);
        Assert.True(await queue.ReportProgressAsync(
            claim!,
            new DurableJobProgressUpdate(
                "provider-started",
                "Matching Spotify playlists.",
                0,
                1,
                "spotify")));

        using var firstCache = new MemoryApplicationCache(clock);
        Assert.True(await firstCache.SetStringAsync(
            "search:v2:cache-loss",
            "disposable",
            TimeSpan.FromHours(1)));
        Assert.Equal("disposable", await firstCache.GetStringAsync("search:v2:cache-loss"));

        firstCache.Dispose();
        using var restartedCache = new MemoryApplicationCache(clock);
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
    public async Task StorageBackup_ReturnsUnavailableWithoutCreatingAnArtifact()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new TestDbContextFactory(database.Options);
        var options = database.StorageOptions;
        var state = new DurableStorageState(options);
        state.Set(DurableStorageReadiness.Ready);
        var service = new DurableBackupService(factory, options, state, new StorageProcessRunner());
        var controller = new allstarr.Controllers.StorageController(state, service, factory);

        var result = Assert.IsType<Microsoft.AspNetCore.Mvc.ObjectResult>(await controller.CreateBackup());

        Assert.Equal(503, result.StatusCode);
        Assert.Contains("backups_unavailable", System.Text.Json.JsonSerializer.Serialize(result.Value));
        Assert.False(Directory.Exists(options.BackupDirectory));
        await using var context = await factory.CreateDbContextAsync();
        Assert.Empty(await context.Backups.ToListAsync());
        Assert.Empty(await context.Jobs.ToListAsync());
    }

    private sealed class FixedClock(DateTimeOffset now) : IPlatformClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private sealed class TestDbContextFactory(DbContextOptions<AllstarrDbContext> options)
        : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);

        public Task<AllstarrDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(new AllstarrDbContext(options));
    }
}
