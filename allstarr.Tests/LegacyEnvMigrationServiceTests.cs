using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Configuration;
using allstarr.Core.Operations;
using allstarr.Core.Secrets;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using allstarr.Models.Settings;
using allstarr.Services.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace allstarr.Tests;

public sealed class LegacyEnvMigrationServiceTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "allstarr-tests", Guid.NewGuid().ToString("N"));
    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly Guid _userId = Guid.CreateVersion7();
    private PostgresTestDatabase _database = null!;
    private TestDbContextFactory _factory = null!;
    private readonly string _keyRingPath;

    public LegacyEnvMigrationServiceTests()
    {
        Directory.CreateDirectory(_root);
        _keyRingPath = Path.Combine(_root, "keyring.json");
        WriteKeyRing();
    }

    public async Task InitializeAsync()
    {
        _database = await PostgresTestDatabase.CreateAsync();
        _factory = new TestDbContextFactory(_database.Options);
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.MigrateAsync();
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
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Preview_IsReadOnlyBoundedAndRedactsSensitiveValues()
    {
        var service = CreateService();
        var preview = await service.PreviewAsync(Source("""
            CACHE_LYRICS_DAYS=21
            DEEZER_ARL=never-return-this-arl
            JELLYFIN_API_KEY=never-return-this-key
            SCROBBLING_LASTFM_API_KEY=never-return-this-api-key
            SCROBBLING_LASTFM_SHARED_SECRET=never-return-this-shared-secret
            SCROBBLING_LASTFM_USERNAME=administrator
            SCROBBLING_LASTFM_SESSION_KEY=never-return-this-session
            SCROBBLING_LOCAL_TRACKS_ENABLED=true
            SPOTIFY_IMPORT_PLAYLISTS=[["Discover Weekly","source-id","target-id","first","0 8 * * *"]]
            """), Actor());

        Assert.True(preview.CanApply);
        Assert.Equal(2, preview.ImportedSettingCount);
        Assert.Equal(2, preview.ProviderAccountCount);
        Assert.Equal(2, preview.ManualCount);
        Assert.Equal(64, preview.SourceSha256.Length);
        Assert.Equal(LegacyEnvParser.ParserVersion, preview.ParserVersion);
        Assert.Equal(64, preview.Revision.Length);
        Assert.True(preview.PreviewToken.Length >= 40);
        var deezer = Assert.Single(preview.Items, item => item.Key == "DEEZER_ARL");
        Assert.Equal(2, deezer.SourceLine);
        Assert.Null(deezer.ValuePreview);
        Assert.Equal("21", Assert.Single(preview.Items, item => item.Key == "CACHE_LYRICS_DAYS").ValuePreview);
        Assert.All(preview.Items.Where(item => item.Sensitive), item => Assert.Null(item.ValuePreview));
        Assert.Equal("retain_in_deployment", Assert.Single(preview.Items, item => item.Key == "JELLYFIN_API_KEY").Action);
        Assert.Equal("import_for_current_user", Assert.Single(preview.Items, item => item.Key == "SCROBBLING_LASTFM_SESSION_KEY").Action);
        Assert.Contains("duplicate", Assert.Single(preview.Items,
            item => item.Key == "SCROBBLING_LOCAL_TRACKS_ENABLED").Warning, StringComparison.OrdinalIgnoreCase);
        var playlist = Assert.Single(preview.PlaylistHandoffs);
        Assert.Equal("source-id", playlist.SourcePlaylistId);
        Assert.Equal("target-id", playlist.JellyfinTargetPlaylistId);
        var playlistSetting = Assert.Single(preview.Items, item => item.Key == "SPOTIFY_IMPORT_PLAYLISTS");
        Assert.Null(playlistSetting.DurableKey);
        Assert.Equal("requires_target_selection", playlistSetting.Action);

        var json = JsonSerializer.Serialize(preview);
        Assert.DoesNotContain("never-return-this-arl", json, StringComparison.Ordinal);
        Assert.DoesNotContain("never-return-this-session", json, StringComparison.Ordinal);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Empty(await db.TenantRuntimeSettings.ToListAsync());
        Assert.Empty(await db.ProviderAccounts.ToListAsync());
        Assert.Empty(await db.SecretReferences.ToListAsync());
        Assert.Empty(await db.AuditEvents.ToListAsync());
    }

    [Fact]
    public async Task Apply_KeepsAmbiguousLegacyPlaylistsAsReviewHandoffs()
    {
        var service = CreateService();
        var preview = await service.PreviewAsync(Source("""
            SPOTIFY_IMPORT_PLAYLISTS=[["Discover Weekly","source-id","target-id","last","0 8 * * *"]]
            """), Actor());

        var result = await service.ApplyAsync(preview.PreviewToken, preview.Revision, true, Actor());

        Assert.Equal(0, result.SettingsImported);
        Assert.Equal(1, result.PlaylistHandoffsPending);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Empty(await db.TenantRuntimeSettings.ToListAsync());
        Assert.Empty(await db.PlaylistLinks.ToListAsync());
        Assert.Empty(await db.JobSchedules.ToListAsync());
    }

    [Fact]
    public async Task Apply_ImportsBackendIdentityPlaylistLinkAndActiveScheduleWhenExplicit()
    {
        var service = CreateService();
        var preview = await service.PreviewAsync(Source("""
            BACKEND_TYPE=Jellyfin
            ALLSTARR_BACKEND_INSTANCE_ID=primary
            JELLYFIN_USER_ID=jellyfin-user-id
            SPOTIFY_API_SESSION_COOKIE=spotify-cookie
            MULTI_PROVIDER_PLAYLIST_ORDER=spotify,deezer
            SPOTIFY_IMPORT_PLAYLISTS=[["Discover Weekly","source-id","target-id","first","0 8 * * *"]]
            """), Actor());

        Assert.True(preview.CanApply);
        Assert.Equal(1, preview.BackendIdentityCount);
        Assert.Equal(1, preview.PlaylistLinkCount);
        Assert.Equal(1, preview.ScheduleCount);
        Assert.Equal("import_playlist_link", Assert.Single(preview.PlaylistHandoffs).Action);
        Assert.Equal("import_backend_identity",
            Assert.Single(preview.Items, item => item.Key == "JELLYFIN_USER_ID").Action);

        var result = await service.ApplyAsync(preview.PreviewToken, preview.Revision, true, Actor());

        Assert.Equal(1, result.BackendIdentitiesCreated);
        Assert.Equal(1, result.PlaylistLinksCreated);
        Assert.Equal(1, result.SchedulesCreated);
        Assert.Equal(1, result.SettingsImported);
        Assert.Equal(0, result.PlaylistHandoffsPending);
        await using var db = await _factory.CreateDbContextAsync();
        var identity = Assert.Single(await db.BackendIdentities.ToListAsync());
        Assert.Equal("jellyfin", identity.BackendType);
        Assert.Equal("primary", identity.BackendInstanceId);
        Assert.Equal("jellyfin-user-id", identity.PrincipalId);
        var schedule = Assert.Single(await db.JobSchedules.ToListAsync());
        Assert.True(schedule.Enabled);
        Assert.True(schedule.NextRunAt > DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.Equal("0 8 * * *", schedule.CronExpression);
        var link = Assert.Single(await db.PlaylistLinks.ToListAsync());
        Assert.True(link.Enabled);
        Assert.Equal(schedule.Id, link.ScheduleId);
        Assert.Equal("source-id", link.SourcePlaylistId);
        Assert.Equal("target-id", link.TargetPlaylistId);
        Assert.Equal("jellyfin", link.TargetProtocol);
        Assert.Equal("primary", link.TargetBackendInstanceId);
        Assert.Equal("spotify", (await db.ProviderAccounts.SingleAsync()).ProviderId);
        Assert.Equal("[\"spotify\",\"deezer\"]",
            (await db.TenantRuntimeSettings.SingleAsync()).ValueJson);
        using var provenance = JsonDocument.Parse((await db.LegacyEnvImports.SingleAsync()).ProvenanceJson);
        Assert.Single(provenance.RootElement.GetProperty("backendIdentities").EnumerateArray());
        Assert.Single(provenance.RootElement.GetProperty("playlistLinks").EnumerateArray());
        Assert.Single(provenance.RootElement.GetProperty("schedules").EnumerateArray());
        Assert.Equal(
            (await db.OnboardingStates.SingleAsync()).Id,
            provenance.RootElement.GetProperty("onboardingState")
                .GetProperty("recordId").GetGuid());
        var onboarding = await db.OnboardingStates.SingleAsync();
        Assert.NotNull(onboarding.CompletedAt);
        Assert.Contains(OnboardingStateService.BackendIdentityStep, onboarding.CompletedStepsJson);
        Assert.Contains(OnboardingStateService.LegacyEnvironmentStep, onboarding.CompletedStepsJson);

        var revised = await service.PreviewAsync(Source("""
            BACKEND_TYPE=Jellyfin
            ALLSTARR_BACKEND_INSTANCE_ID=primary
            JELLYFIN_USER_ID=jellyfin-user-id
            SPOTIFY_API_SESSION_COOKIE=spotify-cookie
            MULTI_PROVIDER_PLAYLIST_ORDER=spotify,deezer
            CACHE_LYRICS_DAYS=31
            SPOTIFY_IMPORT_PLAYLISTS=[["Discover Weekly","source-id","target-id","first","0 8 * * *"]]
            """), Actor());
        Assert.Equal("conflict_existing", Assert.Single(revised.PlaylistHandoffs).Action);
        Assert.Equal(0, revised.PlaylistLinkCount);
        var revisedResult = await service.ApplyAsync(
            revised.PreviewToken, revised.Revision, true, Actor());
        Assert.Equal(0, revisedResult.PlaylistLinksCreated);
        Assert.Equal(0, revisedResult.PlaylistHandoffsPending);
        Assert.Single(await db.PlaylistLinks.ToListAsync());
        Assert.Single(await db.JobSchedules.ToListAsync());
        Assert.Equal(2, await db.LegacyEnvImports.CountAsync());
    }

    [Fact]
    public async Task Imported_backend_cannot_override_the_deployment_backend()
    {
        var service = CreateService(backend: BackendType.Jellyfin);
        var preview = await service.PreviewAsync(Source("""
            BACKEND_TYPE=Subsonic
            ALLSTARR_BACKEND_INSTANCE_ID=primary
            JELLYFIN_USER_ID=jellyfin-user-id
            """), Actor());

        var backend = Assert.Single(preview.Items, item => item.Key == "BACKEND_TYPE");
        Assert.Equal("quarantine_deployment_backend", backend.Action);
        Assert.Equal(1, preview.BackendIdentityCount);

        await service.ApplyAsync(preview.PreviewToken, preview.Revision, true, Actor());

        await using var db = await _factory.CreateDbContextAsync();
        var identity = Assert.Single(await db.BackendIdentities.ToListAsync());
        Assert.Equal("jellyfin", identity.BackendType);
        Assert.Equal("jellyfin-user-id", identity.PrincipalId);
    }

    [Fact]
    public async Task PreviewAndApply_DuplicateAssignmentsUseLastValueAndOnlyWarnWithSourceLines()
    {
        var service = CreateService();
        var preview = await service.PreviewAsync(Source("""
            CACHE_LYRICS_DAYS=7
            DEEZER_ARL=first-private-value
            CACHE_LYRICS_DAYS=21
            DEEZER_ARL=second-private-value
            """), Actor());

        Assert.True(preview.CanApply);
        Assert.Equal(2, preview.Warnings.Count);
        Assert.All(preview.Warnings, warning => Assert.Contains("last active assignment", warning));
        Assert.Contains(preview.Warnings, warning => warning.Contains("CACHE_LYRICS_DAYS", StringComparison.Ordinal));
        Assert.Contains(preview.Warnings, warning => warning.Contains("DEEZER_ARL", StringComparison.Ordinal));
        var serializedPreview = JsonSerializer.Serialize(preview);
        Assert.DoesNotContain("first-private-value", serializedPreview, StringComparison.Ordinal);
        Assert.DoesNotContain("second-private-value", serializedPreview, StringComparison.Ordinal);

        await service.ApplyAsync(preview.PreviewToken, preview.Revision, true, Actor());

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal("21", Assert.Single(await db.TenantRuntimeSettings.ToListAsync()).ValueJson);
        var deezer = Assert.Single(await db.ProviderAccounts.ToListAsync());
        Assert.Equal("Shared Deezer account", deezer.DisplayName);
        using var lease = await CreateSecretStore().OpenAsync(
            deezer.SecretReferenceId!.Value,
            new SecretAccessContext(null, AllowGlobal: true));
        using var secret = JsonDocument.Parse(lease.Value);
        Assert.Equal("second-private-value", secret.RootElement.GetProperty("arl").GetString());
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
        Assert.Equal(4, result.ProviderAccountsCreated);
        Assert.Equal(["deezer", "listenbrainz", "qobuz", "spotify"], result.CreatedProviders.Order().ToArray());
        Assert.Equal(0, result.ManualChecklistItems);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var setting = Assert.Single(await db.TenantRuntimeSettings.ToListAsync());
            Assert.Equal("Cache:LyricsDays", setting.Key);
            Assert.Equal("30", setting.ValueJson);
            Assert.Equal("legacy-env-import", setting.Source);
            var accounts = await db.ProviderAccounts.OrderBy(item => item.ProviderId).ToListAsync();
            Assert.Equal(4, accounts.Count);
            Assert.All(accounts.Where(account => account.Scope == ProviderAccountScope.Global), account => Assert.False(account.Enabled));
            var listenBrainz = Assert.Single(accounts, account => account.ProviderId == "listenbrainz");
            Assert.Equal("My ListenBrainz account", listenBrainz.DisplayName);
            Assert.Equal("Shared Spotify account", Assert.Single(accounts, account => account.ProviderId == "spotify").DisplayName);
            Assert.True(listenBrainz.Enabled);
            Assert.Equal(ProviderAccountScope.User, listenBrainz.Scope);
            Assert.Equal(_tenantId, listenBrainz.TenantId);
            Assert.Equal(_userId, listenBrainz.OwnerUserId);
            Assert.All(accounts, account => Assert.NotNull(account.SecretReferenceId));
            Assert.Equal(4, await db.SecretReferences.CountAsync());
            Assert.Equal(4, await db.SecretVersions.CountAsync());
            var receipt = Assert.Single(await db.LegacyEnvImports.ToListAsync());
            Assert.Equal(_tenantId, receipt.TenantId);
            Assert.Equal(result.SourceFingerprint, receipt.SourceSha256);
            Assert.Equal("legacy-env-import-v1", receipt.SchemaVersion);
            using var provenance = JsonDocument.Parse(receipt.ProvenanceJson);
            var settingProvenance = Assert.Single(
                provenance.RootElement.GetProperty("settings").EnumerateArray());
            Assert.Equal(setting.Id, settingProvenance.GetProperty("recordId").GetGuid());
            Assert.Equal(setting.Key, settingProvenance.GetProperty("key").GetString());
            var providerRecordIds = provenance.RootElement.GetProperty("providerAccounts")
                .EnumerateArray()
                .Select(item => item.GetProperty("recordId").GetGuid())
                .Order()
                .ToArray();
            Assert.Equal(accounts.Select(item => item.Id).Order().ToArray(), providerRecordIds);
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

            using var listenBrainzLease = await CreateSecretStore().OpenAsync(
                listenBrainz.SecretReferenceId!.Value,
                new SecretAccessContext(_tenantId));
            using var listenBrainzSecret = JsonDocument.Parse(listenBrainzLease.Value);
            Assert.Equal("personal-token", listenBrainzSecret.RootElement.GetProperty("token").GetString());
        }

        var replay = await service.ApplyAsync(preview.PreviewToken, preview.Revision, true, Actor());
        Assert.True(replay.AlreadyApplied);
        await using var verify = await _factory.CreateDbContextAsync();
        Assert.Equal(4, await verify.ProviderAccounts.CountAsync());
        Assert.Single(await verify.AuditEvents.ToListAsync());
    }

    [Fact]
    public async Task Apply_ImportsLastFmBundleIntoCurrentUsersEncryptedAccountOnly()
    {
        var service = CreateService();
        var preview = await service.PreviewAsync(Source("""
            SCROBBLING_LASTFM_API_KEY=lastfm-api
            SCROBBLING_LASTFM_SHARED_SECRET=lastfm-secret
            SCROBBLING_LASTFM_USERNAME=administrator
            SCROBBLING_LASTFM_PASSWORD=legacy-password
            SCROBBLING_LASTFM_SESSION_KEY=lastfm-session
            """), Actor());

        Assert.All(preview.Items, item => Assert.Equal("import_for_current_user", item.Action));
        var result = await service.ApplyAsync(preview.PreviewToken, preview.Revision, true, Actor());

        Assert.Equal(["lastfm"], result.CreatedProviders);
        Assert.DoesNotContain("lastfm-session", JsonSerializer.Serialize(result), StringComparison.Ordinal);
        await using var db = await _factory.CreateDbContextAsync();
        var account = Assert.Single(await db.ProviderAccounts.ToListAsync());
        Assert.Equal(ProviderAccountScope.User, account.Scope);
        Assert.Equal(_tenantId, account.TenantId);
        Assert.Equal(_userId, account.OwnerUserId);
        Assert.True(account.Enabled);
        Assert.Equal("My Last.fm account", account.DisplayName);
        using var lease = await CreateSecretStore().OpenAsync(
            account.SecretReferenceId!.Value,
            new SecretAccessContext(_tenantId));
        using var secret = JsonDocument.Parse(lease.Value);
        Assert.Equal("lastfm-api", secret.RootElement.GetProperty("apiKey").GetString());
        Assert.Equal("lastfm-secret", secret.RootElement.GetProperty("sharedSecret").GetString());
        Assert.Equal("administrator", secret.RootElement.GetProperty("username").GetString());
        Assert.Equal("legacy-password", secret.RootElement.GetProperty("password").GetString());
        Assert.Equal("lastfm-session", secret.RootElement.GetProperty("sessionKey").GetString());
    }

    [Theory]
    [InlineData("CACHE_LYRICS_DAYS=not-a-number")]
    [InlineData("QOBUZ_USER_AUTH_TOKEN=token-without-user-id")]
    [InlineData("SPOTIFY_API_SESSION_COOKIE=cookie\nSPOTIFY_API_SESSION_COOKIE_SET_DATE=not-a-date")]
    [InlineData("BACKEND_TYPE=unsupported")]
    [InlineData("JELLYFIN_URL=not-a-url")]
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
    public async Task Reset_DiscardsOnlyOwnedUnappliedPreview()
    {
        var service = CreateService();
        var preview = await service.PreviewAsync(
            Source("CACHE_LYRICS_DAYS=30"),
            Actor());

        var ownerError = await Assert.ThrowsAsync<LegacyEnvMigrationException>(() =>
            service.ResetPreviewAsync(
                preview.PreviewToken,
                Actor() with { SessionId = "other-session" }));
        Assert.Equal("preview_owner_mismatch", ownerError.Code);

        await service.ResetPreviewAsync(preview.PreviewToken, Actor());
        var reset = await Assert.ThrowsAsync<LegacyEnvMigrationException>(() =>
            service.ApplyAsync(
                preview.PreviewToken,
                preview.Revision,
                true,
                Actor()));
        Assert.Equal("preview_invalid", reset.Code);

        var appliedPreview = await service.PreviewAsync(
            Source("CACHE_LYRICS_DAYS=31"),
            Actor());
        await service.ApplyAsync(
            appliedPreview.PreviewToken,
            appliedPreview.Revision,
            true,
            Actor());
        var applied = await Assert.ThrowsAsync<LegacyEnvMigrationException>(() =>
            service.ResetPreviewAsync(appliedPreview.PreviewToken, Actor()));
        Assert.Equal("preview_applied", applied.Code);
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
        Assert.Empty(await db.LegacyEnvImports.ToListAsync());
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
    public async Task ChangedSourceGetsNewReviewRevisionWithoutDuplicatingDurableRecords()
    {
        var service = CreateService();
        var first = await service.PreviewAsync(Source("""
            CACHE_LYRICS_DAYS=30
            DEEZER_ARL=first-secret
            """), Actor());
        await service.ApplyAsync(first.PreviewToken, first.Revision, true, Actor());

        var changed = await service.PreviewAsync(Source("""
            CACHE_LYRICS_DAYS=45
            DEEZER_ARL=changed-secret
            """), Actor());

        Assert.NotEqual(first.Revision, changed.Revision);
        Assert.Equal("conflict_existing", Assert.Single(changed.Items, item => item.Key == "CACHE_LYRICS_DAYS").Action);
        Assert.Equal("conflict_existing", Assert.Single(changed.ProviderAccounts).Action);
        var result = await service.ApplyAsync(changed.PreviewToken, changed.Revision, true, Actor());
        Assert.Equal(0, result.SettingsImported);
        Assert.Equal(0, result.ProviderAccountsCreated);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Single(await db.TenantRuntimeSettings.ToListAsync());
        Assert.Single(await db.ProviderAccounts.ToListAsync());
        Assert.Equal(2, await db.LegacyEnvImports.CountAsync());
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

    private LegacyEnvMigrationService CreateService(
        int maxSecretBytes = 65536,
        BackendType backend = BackendType.Jellyfin)
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
        return new LegacyEnvMigrationService(
            _factory,
            settings,
            secrets,
            clock,
            new BackendSelectionAuthority(
                backend,
                backend.ToString(),
                "test-deployment",
                true,
                false,
                null));
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

    public async Task DisposeAsync()
    {
        await _database.DisposeAsync();
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
