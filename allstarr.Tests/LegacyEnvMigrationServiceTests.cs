using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Configuration;
using allstarr.Core.Operations;
using allstarr.Core.Secrets;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace allstarr.Tests;

public sealed class LegacyEnvMigrationServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "allstarr-tests", Guid.NewGuid().ToString("N"));
    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly Guid _userId = Guid.CreateVersion7();
    private readonly TestDbContextFactory _factory;
    private readonly string _keyRingPath;

    public LegacyEnvMigrationServiceTests()
    {
        Directory.CreateDirectory(_root);
        var options = new DbContextOptionsBuilder<AllstarrDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_root, "migration.db")}")
            .Options;
        _factory = new TestDbContextFactory(options);
        _keyRingPath = Path.Combine(_root, "keyring.json");
        WriteKeyRing();
        using var db = _factory.CreateDbContext();
        db.Database.Migrate();
        db.Tenants.Add(new TenantRecord
        {
            Id = _tenantId,
            Slug = "migration",
            Name = "Migration",
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.Users.Add(new PlatformUserRecord
        {
            Id = _userId,
            TenantId = _tenantId,
            DisplayName = "Administrator",
            Status = PlatformUserStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task Preview_IsReadOnlyBoundedAndRedactsEverySecret()
    {
        var service = CreateService();
        var preview = await service.PreviewAsync(Source("""
            CACHE_LYRICS_DAYS=21
            DEEZER_ARL=never-return-this-arl
            JELLYFIN_API_KEY=never-return-this-key
            SCROBBLING_LASTFM_SESSION_KEY=never-return-this-session
            SCROBBLING_LOCAL_TRACKS_ENABLED=true
            SPOTIFY_IMPORT_PLAYLISTS=[["Discover Weekly","source-id","target-id","first","0 8 * * *"]]
            """), Actor());

        Assert.True(preview.CanApply);
        Assert.Equal(2, preview.ImportedSettingCount);
        Assert.Equal(1, preview.ProviderAccountCount);
        Assert.Equal(3, preview.ManualCount);
        Assert.Equal(64, preview.SourceSha256.Length);
        Assert.Equal(LegacyEnvParser.ParserVersion, preview.ParserVersion);
        Assert.Equal(64, preview.Revision.Length);
        Assert.True(preview.PreviewToken.Length >= 40);
        var deezer = Assert.Single(preview.Items, item => item.Key == "DEEZER_ARL");
        Assert.Equal(2, deezer.SourceLine);
        Assert.Equal("configured", deezer.ValuePreview);
        Assert.Equal("retain_in_deployment", Assert.Single(preview.Items, item => item.Key == "JELLYFIN_API_KEY").Action);
        Assert.Equal("per_user_manual", Assert.Single(preview.Items, item => item.Key == "SCROBBLING_LASTFM_SESSION_KEY").Action);
        Assert.Contains("duplicate", Assert.Single(preview.Items,
            item => item.Key == "SCROBBLING_LOCAL_TRACKS_ENABLED").Warning, StringComparison.OrdinalIgnoreCase);
        var playlist = Assert.Single(preview.PlaylistHandoffs);
        Assert.Equal("source-id", playlist.SourcePlaylistId);
        Assert.Equal("target-id", playlist.JellyfinTargetPlaylistId);

        var json = JsonSerializer.Serialize(preview);
        Assert.DoesNotContain("never-return-this", json, StringComparison.Ordinal);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Empty(await db.TenantRuntimeSettings.ToListAsync());
        Assert.Empty(await db.ProviderAccounts.ToListAsync());
        Assert.Empty(await db.SecretReferences.ToListAsync());
        Assert.Empty(await db.AuditEvents.ToListAsync());
    }

    [Fact]
    public async Task Apply_AtomicallyCreatesSettingsDisabledAccountsAuditAndIdempotentReplay()
    {
        var service = CreateService();
        var preview = await service.PreviewAsync(Source("""
            CACHE_LYRICS_DAYS=30
            DEEZER_ARL=deezer-secret
            QOBUZ_USER_AUTH_TOKEN=qobuz-token
            QOBUZ_USER_ID=55
            SPOTIFY_API_SESSION_COOKIE=spotify-cookie
            SCROBBLING_LISTENBRAINZ_USER_TOKEN=personal-token
            """), Actor());

        var result = await service.ApplyAsync(preview.PreviewToken, preview.Revision, true, Actor());
        Assert.True(result.Success);
        Assert.False(result.AlreadyApplied);
        Assert.Equal(1, result.SettingsImported);
        Assert.Equal(3, result.ProviderAccountsCreated);
        Assert.Equal(["deezer", "qobuz", "spotify"], result.CreatedProviders.Order().ToArray());
        Assert.Equal(1, result.ManualChecklistItems);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var setting = Assert.Single(await db.TenantRuntimeSettings.ToListAsync());
            Assert.Equal("Cache:LyricsDays", setting.Key);
            Assert.Equal("30", setting.ValueJson);
            Assert.Equal("legacy-env-import", setting.Source);
            var accounts = await db.ProviderAccounts.OrderBy(item => item.ProviderId).ToListAsync();
            Assert.Equal(3, accounts.Count);
            Assert.All(accounts, account => Assert.False(account.Enabled));
            Assert.All(accounts, account => Assert.NotNull(account.SecretReferenceId));
            Assert.Equal(3, await db.SecretReferences.CountAsync());
            Assert.Equal(3, await db.SecretVersions.CountAsync());
            var receipt = Assert.Single(await db.LegacyEnvImports.ToListAsync());
            Assert.Equal(_tenantId, receipt.TenantId);
            Assert.Equal(result.SourceFingerprint, receipt.SourceSha256);
            var audit = Assert.Single(await db.AuditEvents.ToListAsync());
            Assert.Equal(audit.Id, receipt.AuditEventId);
            Assert.Equal("legacy-env.apply", audit.Action);
            Assert.DoesNotContain("secret", audit.DetailsJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("token", audit.DetailsJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("cookie", audit.DetailsJson, StringComparison.OrdinalIgnoreCase);

            var qobuz = Assert.Single(accounts, item => item.ProviderId == "qobuz");
            using var lease = await CreateSecretStore().OpenAsync(
                qobuz.SecretReferenceId!.Value,
                new SecretAccessContext(null, AllowGlobal: true));
            using var secret = JsonDocument.Parse(lease.Value);
            Assert.Equal("qobuz-token", secret.RootElement.GetProperty("userAuthToken").GetString());
            Assert.Equal("55", secret.RootElement.GetProperty("userId").GetString());
        }

        var replay = await service.ApplyAsync(preview.PreviewToken, preview.Revision, true, Actor());
        Assert.True(replay.AlreadyApplied);
        await using var verify = await _factory.CreateDbContextAsync();
        Assert.Equal(3, await verify.ProviderAccounts.CountAsync());
        Assert.Single(await verify.AuditEvents.ToListAsync());
    }

    [Theory]
    [InlineData("CACHE_LYRICS_DAYS=not-a-number")]
    [InlineData("QOBUZ_USER_AUTH_TOKEN=token-without-user-id")]
    [InlineData("SPOTIFY_API_SESSION_COOKIE=cookie\nSPOTIFY_API_SESSION_COOKIE_SET_DATE=not-a-date")]
    public async Task Apply_ServerSideRejectsBlockedPreviews(string source)
    {
        var service = CreateService();
        var preview = await service.PreviewAsync(Source(source), Actor());
        Assert.False(preview.CanApply);

        var error = await Assert.ThrowsAsync<LegacyEnvMigrationException>(() =>
            service.ApplyAsync(preview.PreviewToken, preview.Revision, true, Actor()));
        Assert.Equal("preview_not_applicable", error.Code);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Empty(await db.TenantRuntimeSettings.ToListAsync());
        Assert.Empty(await db.ProviderAccounts.ToListAsync());
        Assert.Empty(await db.AuditEvents.ToListAsync());
    }

    [Fact]
    public async Task Apply_RequiresConfirmationAndExactSubmittedRevision()
    {
        var service = CreateService();
        var preview = await service.PreviewAsync(Source("CACHE_LYRICS_DAYS=30"), Actor());

        var confirmation = await Assert.ThrowsAsync<LegacyEnvMigrationException>(() =>
            service.ApplyAsync(preview.PreviewToken, preview.Revision, false, Actor()));
        Assert.Equal("confirmation_required", confirmation.Code);
        var revision = await Assert.ThrowsAsync<LegacyEnvMigrationException>(() =>
            service.ApplyAsync(preview.PreviewToken, new string('0', 64), true, Actor()));
        Assert.Equal("revision_mismatch", revision.Code);
    }

    [Fact]
    public async Task Apply_RejectsWrongSessionAndChangedRevisionWithoutWriting()
    {
        var service = CreateService();
        var preview = await service.PreviewAsync(Source("CACHE_LYRICS_DAYS=30"), Actor());
        var wrongActor = Actor() with { SessionId = "different-session" };
        var ownerError = await Assert.ThrowsAsync<LegacyEnvMigrationException>(() =>
            service.ApplyAsync(preview.PreviewToken, preview.Revision, true, wrongActor));
        Assert.Equal("preview_owner_mismatch", ownerError.Code);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.TenantRuntimeSettings.Add(new TenantRuntimeSettingRecord
            {
                Id = Guid.CreateVersion7(),
                TenantId = _tenantId,
                Key = "Cache:SearchResultsMinutes",
                ValueType = RuntimeSettingValueType.Integer,
                ValueJson = "5",
                Source = "concurrent-change",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                Revision = 1
            });
            await db.SaveChangesAsync();
        }

        var stateError = await Assert.ThrowsAsync<LegacyEnvMigrationException>(() =>
            service.ApplyAsync(preview.PreviewToken, preview.Revision, true, Actor()));
        Assert.Equal("state_changed", stateError.Code);
        await using var verify = await _factory.CreateDbContextAsync();
        Assert.DoesNotContain(await verify.TenantRuntimeSettings.ToListAsync(), item => item.Key == "Cache:LyricsDays");
        Assert.Empty(await verify.AuditEvents.ToListAsync());
    }

    [Fact]
    public async Task Apply_RollsBackStagedSettingsWhenSecretCannotBeStored()
    {
        var service = CreateService(maxSecretBytes: 16);
        var preview = await service.PreviewAsync(Source("""
            CACHE_LYRICS_DAYS=30
            DEEZER_ARL=this-secret-is-far-too-long-for-the-test-store
            """), Actor());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.ApplyAsync(preview.PreviewToken, preview.Revision, true, Actor()));

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Empty(await db.TenantRuntimeSettings.ToListAsync());
        Assert.Empty(await db.ProviderAccounts.ToListAsync());
        Assert.Empty(await db.SecretReferences.ToListAsync());
        Assert.Empty(await db.AuditEvents.ToListAsync());
    }

    [Fact]
    public async Task Preview_ExistingTargetsAreSkippedWithoutBlockingOtherImports()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.ProviderAccounts.Add(new ProviderAccountRecord
            {
                Id = Guid.CreateVersion7(),
                ProviderId = "deezer",
                DisplayName = "Existing",
                Scope = ProviderAccountScope.Global,
                Enabled = true,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var service = CreateService();
        var preview = await service.PreviewAsync(Source("""
            DEEZER_ARL=do-not-overwrite
            CACHE_LYRICS_DAYS=30
            """), Actor());

        Assert.True(preview.CanApply);
        Assert.Equal("conflict_existing", Assert.Single(preview.ProviderAccounts).Action);
        Assert.Equal(1, preview.ImportedSettingCount);

        var result = await service.ApplyAsync(
            preview.PreviewToken,
            preview.Revision,
            true,
            Actor());
        Assert.Equal(1, result.SettingsImported);
        Assert.Equal(0, result.ProviderAccountsCreated);
        Assert.Equal(1, result.ProviderAccountsSkipped);
        await using var verify = await _factory.CreateDbContextAsync();
        Assert.Contains(await verify.TenantRuntimeSettings.ToListAsync(), item => item.Key == "Cache:LyricsDays");
        Assert.Single(await verify.ProviderAccounts.Where(item => item.ProviderId == "deezer").ToListAsync());
    }

    [Fact]
    public async Task Apply_IsIdempotentAcrossFreshServiceInstancesByAuditFingerprint()
    {
        const string source = "CACHE_LYRICS_DAYS=30";
        var firstService = CreateService();
        var firstPreview = await firstService.PreviewAsync(Source(source), Actor());
        await firstService.ApplyAsync(firstPreview.PreviewToken, firstPreview.Revision, true, Actor());

        var restarted = CreateService();
        var restartedPreview = await restarted.PreviewAsync(Source(source), Actor());
        Assert.True(restartedPreview.CanApply);
        var replay = await restarted.ApplyAsync(
            restartedPreview.PreviewToken,
            restartedPreview.Revision,
            true,
            Actor());
        Assert.True(replay.AlreadyApplied);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Single(await db.TenantRuntimeSettings.ToListAsync());
        Assert.Single(await db.AuditEvents.ToListAsync());
        Assert.Single(await db.LegacyEnvImports.ToListAsync());
    }

    [Fact]
    public async Task Apply_ConcurrentServiceInstancesUseOneDurableTenantSourceReceipt()
    {
        const string source = "CACHE_LYRICS_DAYS=30";
        var first = CreateService();
        var second = CreateService();
        var firstPreview = await first.PreviewAsync(Source(source), Actor());
        var secondPreview = await second.PreviewAsync(Source(source), Actor());

        var results = await Task.WhenAll(
            first.ApplyAsync(firstPreview.PreviewToken, firstPreview.Revision, true, Actor()),
            second.ApplyAsync(secondPreview.PreviewToken, secondPreview.Revision, true, Actor()));

        Assert.Single(results, result => !result.AlreadyApplied);
        Assert.Single(results, result => result.AlreadyApplied);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Single(await db.TenantRuntimeSettings.ToListAsync());
        Assert.Single(await db.LegacyEnvImports.ToListAsync());
        Assert.Single(await db.AuditEvents.ToListAsync());
    }

    [Fact]
    public async Task Database_EnforcesReceiptTenantSourceUniquenessAndActorScope()
    {
        var service = CreateService();
        var preview = await service.PreviewAsync(Source("CACHE_LYRICS_DAYS=30"), Actor());
        var result = await service.ApplyAsync(preview.PreviewToken, preview.Revision, true, Actor());

        await using (var duplicate = await _factory.CreateDbContextAsync())
        {
            var auditId = Guid.CreateVersion7();
            duplicate.AuditEvents.Add(MigrationAudit(auditId, _tenantId, _userId));
            duplicate.LegacyEnvImports.Add(new LegacyEnvImportRecord
            {
                Id = Guid.CreateVersion7(),
                TenantId = _tenantId,
                ActorUserId = _userId,
                SourceSha256 = result.SourceFingerprint,
                AuditEventId = auditId,
                ResultJson = JsonSerializer.Serialize(result),
                AppliedAt = DateTimeOffset.UtcNow
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => duplicate.SaveChangesAsync());
        }

        var otherTenantId = Guid.CreateVersion7();
        var otherUserId = Guid.CreateVersion7();
        await using (var seed = await _factory.CreateDbContextAsync())
        {
            seed.Tenants.Add(new TenantRecord
            {
                Id = otherTenantId,
                Slug = "other-migration",
                Name = "Other migration",
                CreatedAt = DateTimeOffset.UtcNow
            });
            seed.Users.Add(new PlatformUserRecord
            {
                Id = otherUserId,
                TenantId = otherTenantId,
                DisplayName = "Other admin",
                Status = PlatformUserStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        await using var crossed = await _factory.CreateDbContextAsync();
        var crossedAuditId = Guid.CreateVersion7();
        crossed.AuditEvents.Add(MigrationAudit(crossedAuditId, _tenantId, otherUserId));
        crossed.LegacyEnvImports.Add(new LegacyEnvImportRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = _tenantId,
            ActorUserId = otherUserId,
            SourceSha256 = new string('b', 64),
            AuditEventId = crossedAuditId,
            ResultJson = JsonSerializer.Serialize(result),
            AppliedAt = DateTimeOffset.UtcNow
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => crossed.SaveChangesAsync());
    }

    private LegacyEnvMigrationService CreateService(int maxSecretBytes = 65536)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Cache:LyricsDays"] = "14",
            ["Cache:SearchResultsMinutes"] = "1"
        }).Build();
        var clock = new SystemPlatformClock();
        var signal = new RuntimeSettingsChangeSignal();
        var settings = new DurableRuntimeSettingsService(_factory, configuration, clock, signal);
        var options = new SecretStoreOptions { KeyRingPath = _keyRingPath, MaxSecretBytes = maxSecretBytes };
        var secrets = new EncryptedSecretStore(
            _factory,
            new FileSecretKeyRingProvider(options),
            options,
            clock);
        return new LegacyEnvMigrationService(_factory, settings, secrets, clock);
    }

    private EncryptedSecretStore CreateSecretStore(int maxSecretBytes = 65536)
    {
        var options = new SecretStoreOptions { KeyRingPath = _keyRingPath, MaxSecretBytes = maxSecretBytes };
        return new EncryptedSecretStore(
            _factory,
            new FileSecretKeyRingProvider(options),
            options,
            new SystemPlatformClock());
    }

    private LegacyEnvMigrationActor Actor() => new(
        "admin-session",
        _tenantId,
        _userId,
        "migration-correlation");

    private static AuditEventRecord MigrationAudit(Guid id, Guid tenantId, Guid actorUserId) => new()
    {
        Id = id,
        TenantId = tenantId,
        ActorUserId = actorUserId,
        Category = "configuration-migration",
        Action = "legacy-env.apply",
        Outcome = "succeeded",
        CorrelationId = $"migration-{id:N}",
        DetailsJson = "{}",
        CreatedAt = DateTimeOffset.UtcNow
    };

    private static byte[] Source(string value) => Encoding.UTF8.GetBytes(value);

    private void WriteKeyRing()
    {
        File.WriteAllText(_keyRingPath, JsonSerializer.Serialize(new
        {
            activeKeyId = "test-key",
            keys = new Dictionary<string, string>
            {
                ["test-key"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            }
        }));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(_keyRingPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
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
        public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AllstarrDbContext(options));
    }
}
