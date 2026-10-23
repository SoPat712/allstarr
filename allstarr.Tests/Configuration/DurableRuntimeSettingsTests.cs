using System.Text.Json;
using allstarr.Core.Operations;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using allstarr.Core.Identity;
using allstarr.Core.Matching;
using allstarr.Core.Capabilities;
using allstarr.Models.Settings;
using allstarr.Services.AppleMusic;
using allstarr.Services.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace allstarr.Tests;

public sealed class DurableRuntimeSettingsTests : IAsyncLifetime
{
    private SqliteTestDatabase _database = null!;
    private TestFactory _factory = null!;
    private Guid _userId;
    private Guid _userB;
    private Guid _disabledUser;
    private FakeClock _clock = null!;

    public async Task InitializeAsync()
    {
        _database = await SqliteTestDatabase.CreateAsync();
        _factory = new(_database.Options);
        await using var db = await _factory.CreateDbContextAsync();
        _userId = Guid.CreateVersion7();
        _userB = Guid.CreateVersion7();
        _disabledUser = Guid.CreateVersion7();
        var now = DateTimeOffset.Parse("2026-07-13T12:00:00Z"); _clock = new(now);
        db.Users.AddRange(
            User(_userId, "settings-admin", "Admin", isAdmin: true, enabled: true, now: now),
            User(_userB, "listener-b", "Listener B", isAdmin: false, enabled: true, now: now),
            User(_disabledUser, "disabled", "Disabled", isAdmin: false, enabled: false, now: now));
        await db.SaveChangesAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovedLyricsSourceIsIgnoredInSavedOrdersAndSourceModes(bool personal)
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            foreach (var (key, type, json) in new[]
            {
                ("Providers:LyricsOrder", RuntimeSettingValueType.StringList, "[\"lrclib\",\"BiniLyrics\",\"lyricsplus\"]"),
                (ProviderUsePolicy.SettingKey, RuntimeSettingValueType.String,
                    JsonSerializer.Serialize("{\"binilyrics\":\"off\",\"lyricsplus\":\"manualOnly\"}"))
            })
                db.RuntimeSettings.Add(new()
                {
                    Id = Guid.CreateVersion7(),
                    OwnerUserId = personal ? _userId : null,
                    Key = key,
                    ValueType = type,
                    ValueJson = json,
                    Source = "fixture",
                    Revision = 1,
                    CreatedAt = _clock.UtcNow,
                    UpdatedAt = _clock.UtcNow
                });
            await db.SaveChangesAsync();
        }
        var service = CreateService([]);
        var preferences = await service.GetPreferencesAsync(_userId);
        Assert.Equal("lrclib,lyricsplus", preferences.Values.LyricsOrder);
        Assert.Equal(new KeyValuePair<string, string>("lyricsplus", "manualOnly"), Assert.Single(preferences.Values.ProviderModes!));
        var policy = await new EffectiveProviderPolicyResolver(service).ResolveForUserAsync(_userId);
        Assert.DoesNotContain("binilyrics", policy.GetProviderOrder(ProviderCapabilityKind.Lyrics));
        Assert.DoesNotContain("binilyrics", policy.ProviderModes.Keys);
    }

    [Theory]
    [InlineData("MULTI_PROVIDER_LYRICS_ORDER")]
    public async Task RemovedLyricsSourceIsIgnoredInBootstrapOrder(string key)
    {
        var service = CreateService(new Dictionary<string, string?> { [key] = "lrcred,binilyrics,lyricsplus" });
        Assert.Equal("lrcred,lyricsplus", (await service.GetAsync("Providers:LyricsOrder")).NormalizedValue);
    }

    [Fact]
    public async Task StoredChecksForRemovedSourceAreIgnored()
    {
        var history = new ProviderCheckHistory(_factory, TimeProvider.System);
        foreach (var provider in new[] { "binilyrics", "lrcred" })
        {
            var status = new ProviderRuntimeStatus
            {
                Provider = provider,
                Capability = "lyrics",
                IsSupported = true,
                IsEnabled = true,
                Configuration = ProviderConfigurationState.NotRequired,
                Health = ProviderHealthState.Healthy,
                TestedAt = DateTimeOffset.UtcNow
            };
            await history.RecordAsync(new(new(provider, "lyrics", null), null, status), "fixture", default);
        }
        Assert.Equal("lrcred", Assert.Single(await history.ReadLatestAsync(default)).ProviderId);
    }

    [Fact]
    public async Task StartupRemovesRetiredSourceSettingsChecksAndSecretsAndPreservesKeptLyrics()
    {
        var secretId = Guid.CreateVersion7();
        var recordingId = Guid.CreateVersion7();
        var retainedSecretId = Guid.CreateVersion7();
        var keptPath = Path.Combine(_database.StorageOptions.DataDirectory, "kept.lrc");
        const string keptLyrics = "[00:01.000]Kept words";
        await File.WriteAllTextAsync(keptPath, keptLyrics);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            foreach (var owner in new Guid?[] { null, _userId })
            {
                foreach (var key in owner == null ? new[] { "SpotifyApi:Enabled", "MusicBrainz:Enabled" } : [])
                    db.RuntimeSettings.Add(new()
                    {
                        Id = Guid.CreateVersion7(),
                        OwnerUserId = owner,
                        Key = key,
                        ValueType = RuntimeSettingValueType.Boolean,
                        ValueJson = "false",
                        Source = "fixture",
                        Revision = 1,
                        CreatedAt = _clock.UtcNow,
                        UpdatedAt = _clock.UtcNow
                    });
                db.RuntimeSettings.Add(new()
                {
                    Id = Guid.CreateVersion7(),
                    OwnerUserId = owner,
                    Key = ProviderUsePolicy.SettingKey,
                    ValueType = RuntimeSettingValueType.String,
                    ValueJson = JsonSerializer.Serialize(owner == null
                        ? "{\"binilyrics\":\"off\",\"lyricsplus\":\"manualOnly\"}" : "{\"binilyrics\":\"off\"}"),
                    Source = "fixture",
                    Revision = 1,
                    CreatedAt = _clock.UtcNow,
                    UpdatedAt = _clock.UtcNow
                });
                db.RuntimeSettings.Add(new()
                {
                    Id = Guid.CreateVersion7(),
                    OwnerUserId = owner,
                    Key = "Providers:LyricsOrder",
                    ValueType = RuntimeSettingValueType.StringList,
                    ValueJson = "[\"binilyrics\",\"lrcred\",\"lyricsplus\"]",
                    Source = "fixture",
                    Revision = 1,
                    CreatedAt = _clock.UtcNow,
                    UpdatedAt = _clock.UtcNow
                });
            }
            db.RuntimeSettings.Add(new()
            {
                Id = Guid.CreateVersion7(),
                Key = "BiniLyrics:Token",
                ValueType = RuntimeSettingValueType.String,
                ValueJson = "\"unused\"",
                Source = "fixture",
                Revision = 1,
                CreatedAt = _clock.UtcNow,
                UpdatedAt = _clock.UtcNow
            });
            db.SecretReferences.Add(new() { Id = secretId, Purpose = "provider-account:binilyrics:fixture", ActiveVersion = 1 });
            db.SecretReferences.Add(new() { Id = retainedSecretId, Purpose = "provider-account:lyricsplus:fixture", ActiveVersion = 1 });
            db.ProviderAccounts.Add(new()
            {
                Id = Guid.CreateVersion7(),
                ProviderId = "binilyrics",
                OwnerUserId = _userId,
                DisplayName = "Fixture",
                Enabled = true,
                Revision = 1,
                SecretReferenceId = secretId
            });
            db.SecretVersions.Add(new()
            {
                Id = Guid.CreateVersion7(),
                SecretReferenceId = secretId,
                Version = 1,
                KeyId = "fixture",
                Nonce = new byte[12],
                Ciphertext = [1],
                AuthenticationTag = new byte[16]
            });
            db.AuditEvents.Add(new()
            {
                Id = Guid.CreateVersion7(),
                Category = "provider-check",
                Action = "check-completed",
                Outcome = "recorded",
                SubjectId = "binilyrics/lyrics/public",
                DetailsJson = "{}",
                CreatedAt = _clock.UtcNow
            });
            db.AuditEvents.Add(new()
            {
                Id = Guid.CreateVersion7(),
                Category = "provider-check",
                Action = "check-completed",
                Outcome = "recorded",
                SubjectId = "lrcred/lyrics/public",
                DetailsJson = "{}",
                CreatedAt = _clock.UtcNow
            });
            db.CanonicalRecordings.Add(new()
            {
                Id = recordingId,
                CreatedByUserId = _userId,
                Title = "Fixture",
                PublicId = CanonicalRecordingRecord.DefaultPublicId(recordingId),
                Revision = 1
            });
            db.RecordingLyricsChoices.Add(new()
            {
                Id = Guid.CreateVersion7(),
                CanonicalRecordingId = recordingId,
                SourceId = "binilyrics",
                Revision = 1
            });
            db.RecordingLyricsChoices.Add(new()
            {
                Id = Guid.CreateVersion7(),
                CanonicalRecordingId = recordingId,
                OwnerUserId = _userId,
                SourceId = "kept",
                Revision = 1
            });
            await db.SaveChangesAsync();
        }
        var options = _database.StorageOptions;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var state = new DurableStorageState(options);
            await new DurableStorageInitializer(_factory, options, state,
                NullLogger<DurableStorageInitializer>.Instance,
                new DurableBackupService(_factory, options, state,
                    new allstarr.Core.Secrets.SecretStoreOptions { KeyRingPath = Path.Combine(options.DataDirectory, "keyring.json") }))
                .StartAsync(CancellationToken.None);
            Assert.Equal(DurableStorageReadiness.Ready, state.GetSnapshot().Readiness);
        }
        await using var read = await _factory.CreateDbContextAsync();
        var orders = await read.RuntimeSettings.ToArrayAsync();
        Assert.Equal(3, orders.Length);
        var modes = Assert.Single(orders, row => row.Key == ProviderUsePolicy.SettingKey);
        Assert.Null(modes.OwnerUserId);
        Assert.Equal("{\"lyricsplus\":\"manualOnly\"}", JsonSerializer.Deserialize<string>(modes.ValueJson));
        Assert.Equal(2, modes.Revision);
        Assert.All(orders.Where(row => row.Key == "Providers:LyricsOrder"), row =>
        {
            Assert.Equal("Providers:LyricsOrder", row.Key);
            Assert.Equal("[\"lrcred\",\"lyricsplus\"]", row.ValueJson);
            Assert.Equal(2, row.Revision);
        });
        Assert.Equal(retainedSecretId, Assert.Single(await read.SecretReferences.ToArrayAsync()).Id);
        Assert.Empty(await read.SecretVersions.ToArrayAsync());
        Assert.Equal("lrcred/lyrics/public", Assert.Single(await read.AuditEvents.ToArrayAsync()).SubjectId);
        Assert.Equal("kept", Assert.Single(await read.RecordingLyricsChoices.ToArrayAsync()).SourceId);
        var retiredAccount = Assert.Single(await read.ProviderAccounts.ToArrayAsync());
        Assert.False(retiredAccount.Enabled);
        Assert.Null(retiredAccount.SecretReferenceId);
        Assert.True(ProviderAccountSettings.IsRemoved(retiredAccount));
        Assert.Equal(2, retiredAccount.Revision);
        Assert.Single(await read.CanonicalRecordings.ToArrayAsync());
        Assert.Equal(keptLyrics, await File.ReadAllTextAsync(keptPath));
        Assert.Equal("lrcred,lyricsplus", (await CreateService([]).GetPreferencesAsync(_userId)).Values.LyricsOrder);
    }

    [Fact]
    public async Task UpcomingPreparationSettingsPersistWithHouseholdDefaultsAndCountBounds()
    {
        var service = CreateService([]);
        Assert.Equal("false", (await service.GetAsync("Playback:PrepareUpcomingSongs")).NormalizedValue);
        Assert.Equal("1", (await service.GetAsync("Playback:PrepareUpcomingCount")).NormalizedValue);
        await service.ApplyBatchAsync([new("Playback:PrepareUpcomingSongs", "true"),
            new("Playback:PrepareUpcomingCount", "2")], "fixture", _userId);
        Assert.Equal("true", (await CreateService([]).GetAsync("Playback:PrepareUpcomingSongs")).NormalizedValue);
        Assert.Equal("2", (await CreateService([]).GetAsync("Playback:PrepareUpcomingCount")).NormalizedValue);
        foreach (var value in new[] { "0", "3" })
            await Assert.ThrowsAsync<ArgumentException>(() => service.ApplyBatchAsync(
                [new("Playback:PrepareUpcomingCount", value)], "fixture", _userId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://flaresolverr:8191")]
    [InlineData("http://192.168.1.40:8191")]
    [InlineData("http://[::1]:8191/solver/")]
    [InlineData("https://solver.example.test/base")]
    public async Task ChallengeSolverUrlPersistsAndAllowsHouseholdServices(string value)
    {
        var service = CreateService([]);
        Assert.Equal("", (await service.GetAsync("ChallengeSolver:Url")).NormalizedValue);
        Assert.Equal("60", (await service.GetAsync("ChallengeSolver:TimeoutSeconds")).NormalizedValue);
        await service.ApplyBatchAsync([new("ChallengeSolver:Url", value)], "fixture", _userId);
        Assert.Equal(value, (await CreateService([]).GetAsync("ChallengeSolver:Url")).NormalizedValue);
        Assert.False(RuntimeSettingCatalog.Require("ChallengeSolver:Url").RequiresRestart);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ApplyBatchAsync(
            [new("ChallengeSolver:Url", value)], "fixture", _userB));
    }

    [Theory]
    [InlineData("ChallengeSolver:Url", "ftp://solver.test")]
    [InlineData("ChallengeSolver:Url", "solver:8191")]
    [InlineData("ChallengeSolver:Url", "https://user:secret@solver.test")]
    [InlineData("ChallengeSolver:Url", "http://solver.test/?secret=value")]
    [InlineData("ChallengeSolver:Url", "http://solver.test/#fragment")]
    [InlineData("ChallengeSolver:TimeoutSeconds", "9")]
    [InlineData("ChallengeSolver:TimeoutSeconds", "181")]
    public async Task ChallengeSolverSettingsRejectInvalidValues(string key, string value)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => CreateService([]).ApplyBatchAsync(
            [new(key, value)], "fixture", _userId));
    }

    [Fact]
    public async Task SourceModesMergeHouseholdDefaultsWithoutChangingOtherPreferencesOrUsers()
    {
        var service = CreateService([]);
        var household = await service.GetPreferencesAsync(null);
        await service.UpdateProviderModeAsync(null, "manual", "manualOnly", household.Revision, _userId);
        var beforeA = await service.GetPreferencesAsync(_userId);
        await service.UpdateProviderModeAsync(_userId, "manual", "off", beforeA.Revision, _userId);
        var afterA = await service.GetPreferencesAsync(_userId);
        var other = await service.GetPreferencesAsync(_userB);
        Assert.Equal("off", afterA.Values.ProviderModes!["manual"]);
        Assert.Equal("off", afterA.ProviderModeOverrides!["manual"]);
        Assert.Null(other.ProviderModeOverrides);
        Assert.Equal("manualOnly", other.Values.ProviderModes!["manual"]);
        Assert.Equal(beforeA.Values.ExplicitFilter, afterA.Values.ExplicitFilter);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.All(await db.RuntimeSettings.Where(item => item.OwnerUserId == _userId).ToArrayAsync(),
            item => Assert.Equal(ProviderUsePolicy.SettingKey, item.Key));
        await Assert.ThrowsAsync<RuntimeSettingConflictException>(() =>
            service.UpdateProviderModeAsync(_userId, "manual", "automatic", beforeA.Revision, _userId));
        await service.UpdateProviderModeAsync(_userId, "manual", null, afterA.Revision, _userId);
        var inherited = await service.GetPreferencesAsync(_userId);
        Assert.Equal("manualOnly", inherited.Values.ProviderModes!["manual"]);
        Assert.True(inherited.UsesHouseholdDefaults);
        Assert.Null(inherited.ProviderModeOverrides);
        Assert.Equal("manualOnly", (await CreateService([]).GetPreferencesAsync(_userB)).Values.ProviderModes!["manual"]);
    }

    [Fact]
    public async Task ListenersCannotChangeHouseholdOrAnotherUsersSourceUse()
    {
        var service = CreateService([]);
        var household = await service.GetPreferencesAsync(null);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.UpdateProviderModeAsync(null, "manual", "off", household.Revision, _userB));
        var other = await service.GetPreferencesAsync(_userId);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.UpdateProviderModeAsync(_userId, "manual", "off", other.Revision, _userB));
        Assert.Null((await service.GetPreferencesAsync(_userId)).Values.ProviderModes);
    }

    [Fact]
    public async Task PersonalSourceModesRetainNewDefaultsForOtherSources()
    {
        var service = CreateService([]);
        var personal = await service.GetPreferencesAsync(_userB);
        await service.UpdateProviderModeAsync(_userB, "first", "off", personal.Revision, _userB);
        var household = await service.GetPreferencesAsync(null);
        await service.UpdateProviderModeAsync(null, "second", "manualOnly", household.Revision, _userId);
        var result = await service.GetPreferencesAsync(_userB);
        Assert.Equal("off", result.Values.ProviderModes!["first"]);
        Assert.Equal("manualOnly", result.Values.ProviderModes!["second"]);
    }

    [Theory]
    [InlineData("MusicBrainz:BaseUrl", "https://musicbrainz.org")]
    [InlineData("MusicBrainz:SourceId", "household-metadata")]
    [InlineData("MusicBrainz:RateLimitMs", "1500")]
    [InlineData("Odesli:Enabled", "true")]
    [InlineData("Odesli:RateLimitMs", "7000")]
    [InlineData("Extensions:AllowRemoteInstall", "true")]
    [InlineData("Debug:LogAllRequests", "true")]
    [InlineData("Operations:EventLog:RetentionDays", "40")]
    [InlineData("Operations:EventLog:MaximumRows", "300000")]
    [InlineData("Operations:EventLog:CleanupIntervalMinutes", "400")]
    [InlineData("Operations:EventLog:CleanupBatchSize", "2000")]
    [InlineData("Operations:EventLog:JobHistoryRetentionDays", "5")]
    [InlineData("Storage:BackupRetentionCount", "12")]
    [InlineData("Backend:MusicLibraryIds", "Library-A,Library-B")]
    public async Task OperatorSettingsSurviveRestartWithoutEnvironment(string key, string raw)
    {
        await CreateService([]).ApplyBatchAsync([new(key, raw)], "webui", _userId);
        var saved = await CreateService([]).GetAsync(key);
        Assert.Equal(raw, saved.NormalizedValue);
        Assert.True(RuntimeSettingCatalog.Require(key).RequiresRestart);
    }

    [Theory]
    [InlineData("MusicBrainz:BaseUrl", "https://user:password@example.test")]
    [InlineData("MusicBrainz:BaseUrl", "https://example.test/?token=secret")]
    [InlineData("MusicBrainz:SourceId", "invalid source")]
    [InlineData("MusicBrainz:RateLimitMs", "999")]
    [InlineData("Backend:MusicLibraryIds", "A, A")]
    public async Task OperatorSettingsRejectInvalidValuesWithoutWriting(string key, string raw)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => CreateService([]).ApplyBatchAsync([new(key, raw)], "webui", _userId));
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Empty(await db.RuntimeSettings.ToListAsync());
    }

    [Theory]
    [InlineData("Admin:Oidc:Enabled", "true")]
    [InlineData("Admin:Oidc:Authority", "https://identity.example.test")]
    [InlineData("Admin:Oidc:ClientId", "household-fixture")]
    [InlineData("Admin:Oidc:PublicUrl", "https://admin.example.test/allstarr")]
    [InlineData("Admin:Oidc:DisplayName", "Household login")]
    public async Task SingleSignOnPolicyPersistsWithoutStoringAPlaintextClientSecret(string key, string value)
    {
        var service = CreateService([]);
        await service.ApplyBatchAsync([new(key, value)], "webui", _userId);
        Assert.Equal(value, (await CreateService([]).GetAsync(key)).NormalizedValue);
        await Assert.ThrowsAsync<ArgumentException>(() => service.ApplyBatchAsync(
            [new("Admin:Oidc:ClientSecret", "private-fixture")], "webui", _userId));
    }

    [Fact]
    public async Task JellyfinWordGapDefaultsOnAndCanBeDisabledDurably()
    {
        Assert.True((bool)(await CreateService([]).GetAsync("Lyrics:JellyfinWordGap")).Value);
        await CreateService([]).ApplyBatchAsync([new("Lyrics:JellyfinWordGap", "false")], "fixture", _userId);
        Assert.False((bool)(await CreateService([]).GetAsync("Lyrics:JellyfinWordGap")).Value);
    }

    [Fact]
    public async Task MetadataDefaultsUseTheSamePublicContractAsTheCatalogClient()
    {
        Assert.Equal(new MusicBrainzSettings().BaseUrl, (await CreateService([]).GetAsync("MusicBrainz:BaseUrl")).Value);
    }

    [Fact]
    public async Task BrowserCredentialsRequireExplicitOriginsIncludingAcrossRevisions()
    {
        var service = CreateService([]);
        await service.ApplyBatchAsync([new("Cors:AllowedOrigins", "*")], "webui", _userId);
        await Assert.ThrowsAsync<ArgumentException>(() => service.ApplyBatchAsync(
            [new("Cors:AllowCredentials", "true")], "webui", _userId));
        await service.ApplyBatchAsync([
            new("Cors:AllowedOrigins", "https://player.example.test", 1),
            new("Cors:AllowCredentials", "true")], "webui", _userId);
        await Assert.ThrowsAsync<ArgumentException>(() => service.ApplyBatchAsync(
            [new("Cors:AllowedOrigins", "*", 2)], "webui", _userId));
        Assert.Equal("https://player.example.test", (await service.GetAsync("Cors:AllowedOrigins")).Value);
    }

    [Theory]
    [InlineData("Cors:AllowedOrigins", "https://example.test/path")]
    [InlineData("Cors:AllowedOrigins", "https://user:password@example.test")]
    [InlineData("Cors:AllowedHeaders", "Authorization\r\nInjected: value")]
    [InlineData("Cors:AllowedMethods", "GET POST")]
    public async Task BrowserSettingsRejectMalformedValues(string key, string value)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => CreateService([]).ApplyBatchAsync([new(key, value)], "webui", _userId));
    }

    [Fact]
    public async Task RetiredSpotifySidecarCannotBeConfigured()
    {
        var service = CreateService([]);
        await Assert.ThrowsAsync<ArgumentException>(() => service.ApplyBatchAsync(
            [new("SpotifyApi:LyricsApiUrl", "http://obsolete.example.test")], "webui", _userId));
    }

    [Theory]
    [InlineData("SpotifyApi:Enabled")]
    [InlineData("MusicBrainz:Enabled")]
    public async Task DuplicateSourceSwitchCannotBeWrittenOrProjectedFromSavedState(string key)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [key] = "false"
        }).Build();
        var spotify = new SpotifyApiSettings();
        configuration.GetSection("SpotifyApi").Bind(spotify);
        Assert.True(spotify.Enabled);
        var musicBrainz = new MusicBrainzSettings();
        configuration.GetSection("MusicBrainz").Bind(musicBrainz);
        Assert.True(musicBrainz.Enabled);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.RuntimeSettings.Add(new()
            {
                Id = Guid.CreateVersion7(),
                Key = key,
                ValueType = RuntimeSettingValueType.Boolean,
                ValueJson = "false",
                Source = "fixture",
                Revision = 1,
                CreatedAt = _clock.UtcNow,
                UpdatedAt = _clock.UtcNow
            });
            await db.SaveChangesAsync();
        }
        var service = new DurableRuntimeSettingsService(_factory, configuration, _clock,
            new RuntimeSettingsChangeSignal());
        await LiveOptions(configuration, new PlaybackSettings(), spotify).ProjectAsync(service, default);
        Assert.True(spotify.Enabled);
        Assert.DoesNotContain(key, RuntimeSettingCatalog.Definitions.Keys);
        await Assert.ThrowsAsync<ArgumentException>(() => service.ApplyBatchAsync(
            [new(key, "false", 1)], "webui", _userId));
    }

    [Fact]
    public async Task Get_ReportsBootstrapFallbackThenTypedDurableOverride()
    {
        var service = CreateService(new Dictionary<string, string?> { ["Cache:SearchResultsMinutes"] = "7" });
        var fallback = await service.GetAsync("Cache:SearchResultsMinutes");
        Assert.Equal(RuntimeSettingOrigin.Bootstrap, fallback.Origin);
        Assert.Equal(7, fallback.Value);
        Assert.Null(fallback.Revision);

        var applied = await service.ApplyBatchAsync(
            [new("Cache:SearchResultsMinutes", "12")], "webui", _userId);
        var persisted = Assert.Single(applied.Settings);
        Assert.Equal(RuntimeSettingOrigin.Durable, persisted.Origin);
        Assert.Equal(12, persisted.Value);
        Assert.Equal(1, persisted.Revision);
        Assert.Equal(1, applied.ChangeVersion);
        await using var db = await _factory.CreateDbContextAsync();
        var audit = await db.AuditEvents.SingleAsync();
        Assert.Equal("runtime-settings.batch-apply", audit.Action);
        Assert.DoesNotContain("12", audit.DetailsJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SpotifyCompatibilityEditsProjectIntoTheExistingLiveOptions()
    {
        var configuration = new ConfigurationBuilder().Build();
        var spotify = new SpotifyApiSettings();
        var service = new DurableRuntimeSettingsService(_factory, configuration, _clock,
            new RuntimeSettingsChangeSignal(), LiveOptions(configuration, new PlaybackSettings(), spotify),
            NullLogger<DurableRuntimeSettingsService>.Instance);
        var hash = new string('d', 64);
        await service.ApplyBatchAsync([
            new("SpotifyApi:LibraryQueryHash", hash),
            new("SpotifyApi:PathfinderEndpoint", "https://api-partner.spotify.com/pathfinder/v2/query"),
            new("SpotifyApi:TokenEndpoint", "https://api-partner.spotify.com/api/token")
        ], "webui", _userId);
        Assert.Equal(hash, spotify.LibraryQueryHash);
        Assert.Equal("https://api-partner.spotify.com/pathfinder/v2/query", spotify.PathfinderEndpoint);
        Assert.Equal("https://api-partner.spotify.com/api/token", spotify.TokenEndpoint);
    }

    [Fact]
    public async Task SpotifyCompatibilitySettingsPersistAndRejectCredentialDestinations()
    {
        var service = CreateService([]);
        Assert.Equal("https://api-partner.spotify.com/pathfinder/v1/query", (await service.GetAsync("SpotifyApi:PathfinderEndpoint")).Value);
        var hash = new string('a', 64);
        await service.ApplyBatchAsync([
            new("SpotifyApi:PathfinderEndpoint", "https://api-partner.spotify.com/pathfinder/v2/query"),
            new("SpotifyApi:LibraryQueryHash", hash),
            new("SpotifyApi:LikedSongsQueryHash", hash),
            new("SpotifyApi:TotpSecretsUrl", "https://example.test/public-secrets.json")
        ], "webui", _userId);
        var restarted = CreateService([]);
        Assert.Equal(hash, (await restarted.GetAsync("SpotifyApi:LibraryQueryHash")).Value);
        Assert.Equal("https://api-partner.spotify.com/pathfinder/v2/query", (await restarted.GetAsync("SpotifyApi:PathfinderEndpoint")).Value);
        await Assert.ThrowsAsync<ArgumentException>(() => service.ApplyBatchAsync(
            [new("SpotifyApi:PathfinderEndpoint", "https://example.test/query", 1)], "webui", _userId));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ApplyBatchAsync(
            [new("SpotifyApi:PlaylistQueryHash", "invalid")], "webui", _userId));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ApplyBatchAsync(
            [new("SpotifyApi:TotpSecretsUrl", "http://example.test/secrets", 1)], "webui", _userId));
    }

    [Fact]
    public async Task ApplyBatch_IsAtomicAndRejectsDeploymentOrSecretKeys()
    {
        var service = CreateService([]);
        await Assert.ThrowsAsync<ArgumentException>(() => service.ApplyBatchAsync(
            [new("Cache:LyricsDays", "20"), new("Jellyfin:ApiKey", "do-not-store")], "legacy-import"));
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Empty(await db.RuntimeSettings.ToListAsync());

        await Assert.ThrowsAsync<ArgumentException>(() => service.ApplyBatchAsync(
            [new("Cache:LyricsDays", "0")], "webui"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ApplyBatchAsync(
            [new("Library:PlaylistsDirectory", "../outside")], "webui"));
        Assert.Empty(await db.RuntimeSettings.ToListAsync());
    }

    [Fact]
    public async Task ApplyBatch_UsesCreateOnlyAndOptimisticRevisionContracts()
    {
        var service = CreateService([]);
        var created = await service.ApplyBatchAsync([new("SpotifyApi:PreferIsrcMatching", "false")], "webui", _userId);
        Assert.Equal(1, Assert.Single(created.Settings).Revision);
        await Assert.ThrowsAsync<RuntimeSettingConflictException>(() => service.ApplyBatchAsync(
            [new("SpotifyApi:PreferIsrcMatching", "true")], "webui", _userId));
        await Assert.ThrowsAsync<RuntimeSettingConflictException>(() => service.ApplyBatchAsync(
            [new("SpotifyApi:PreferIsrcMatching", "true", 99)], "webui", _userId));
        _clock.UtcNow = _clock.UtcNow.AddMinutes(1);
        var updated = await service.ApplyBatchAsync(
            [new("SpotifyApi:PreferIsrcMatching", "true", 1)], "webui", _userId);
        var value = Assert.Single(updated.Settings);
        Assert.Equal(2, value.Revision); Assert.Equal(true, value.Value);
        Assert.Equal(_clock.UtcNow, value.UpdatedAt);
    }

    [Fact]
    public async Task ProviderLists_AreNormalizedAndDuplicateIdsAreRejected()
    {
        var service = CreateService([]);
        var result = await service.ApplyBatchAsync(
            [new("Providers:StreamingOrder", " Deezer, QOBUZ ")], "legacy-import");
        Assert.Equal("deezer,qobuz", Assert.Single(result.Settings).NormalizedValue);
        await Assert.ThrowsAsync<ArgumentException>(() => service.ApplyBatchAsync(
            [new("Providers:DownloadOrder", "deezer,DEEZER")], "legacy-import"));

        var lyrics = await service.ApplyBatchAsync(
            [new("Providers:LyricsOrder", "spotify,lyricsplus,apple-download,lrclib")], "legacy-import");
        Assert.Equal("spotify,lyricsplus,apple-download,lrclib", Assert.Single(lyrics.Settings).NormalizedValue);
    }

    [Theory]
    [InlineData(false, "lrclib,spotify", "backend,kept,lrclib,spotify")]
    [InlineData(true, "lrclib,spotify", "backend,kept,lrclib,spotify")]
    [InlineData(false, "lrclib,backend,spotify", "kept,lrclib,backend,spotify")]
    [InlineData(true, "lrclib,backend,spotify", "kept,lrclib,backend,spotify")]
    [InlineData(false, "kept,spotify,backend,lrclib", "kept,spotify,backend,lrclib")]
    [InlineData(true, "kept,spotify,backend,lrclib", "kept,spotify,backend,lrclib")]
    public async Task LegacyLyricsOrderKeepsMissingLocalSourcesFirstAndPreservesExplicitPositions(
        bool durable, string savedOrder, string expected)
    {
        var service = CreateService(durable ? [] : new Dictionary<string, string?>
        {
            ["MULTI_PROVIDER_LYRICS_ORDER"] = savedOrder
        });
        if (durable)
            await service.ApplyBatchAsync([new("Providers:LyricsOrder", savedOrder)], "legacy-import");
        var policy = await new EffectiveProviderPolicyResolver(service).ResolveForUserAsync(_userB);

        Assert.Equal(expected.Split(','), policy.ApplyProviderAvailability(
            ProviderCapabilityKind.Lyrics, ["spotify", "backend", "lrclib", "kept"]));
        Assert.Equal(savedOrder, (await service.GetAsync("Providers:LyricsOrder")).NormalizedValue);
    }

    [Fact]
    public async Task LyricsPlusEndpointIsOptionalAndRejectsCredentialsOrQueryStrings()
    {
        var service = CreateService([]);
        Assert.Equal(string.Empty, (await service.GetAsync("LyricsPlus:BaseUrl")).Value);
        var saved = await service.ApplyBatchAsync([new("LyricsPlus:BaseUrl", "http://lyrics:3000/prefix")], "webui", _userId);
        Assert.Equal("http://lyrics:3000/prefix", Assert.Single(saved.Settings).Value);
        foreach (var value in new[] { "http://user:password@lyrics", "file:///tmp/lyrics", "https://lyrics.example/?secret=unsafe" })
            await Assert.ThrowsAsync<ArgumentException>(() => service.ApplyBatchAsync(
                [new("LyricsPlus:BaseUrl", value, 1)], "webui", _userId));
    }

    [Fact]
    public async Task OptionalProviderSettings_AllowShippedEmptyDefaultsAndDurableDisable()
    {
        var service = CreateService(new Dictionary<string, string?>
        {
            ["AppleDownload:BaseUrl"] = string.Empty,
            ["AppleDownload:Quality"] = string.Empty,
            ["Deezer:Quality"] = string.Empty,
            ["Qobuz:Quality"] = string.Empty
        });

        var settings = await service.GetManyAsync(RuntimeSettingCatalog.Definitions.Keys);
        var bootstrap = settings["AppleDownload:BaseUrl"];
        Assert.Equal(RuntimeSettingOrigin.Bootstrap, bootstrap.Origin);
        Assert.Equal(string.Empty, bootstrap.Value);
        Assert.Equal(string.Empty, settings["Deezer:Quality"].Value);
        Assert.Equal(string.Empty, settings["Qobuz:Quality"].Value);

        var applied = await service.ApplyBatchAsync(
        [
            new("AppleDownload:BaseUrl", string.Empty),
            new("Deezer:Quality", string.Empty),
            new("Qobuz:Quality", string.Empty)
        ], "webui", _userId);
        Assert.All(applied.Settings, durable =>
        {
            Assert.Equal(RuntimeSettingOrigin.Durable, durable.Origin);
            Assert.Equal(string.Empty, durable.Value);
            Assert.Equal("", durable.NormalizedValue);
        });
    }

    [Fact]
    public async Task StageBatch_LeavesCommitAndChangePublicationToOuterTransaction()
    {
        var service = CreateService([]);
        await using var db = await _factory.CreateDbContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var staged = await service.StageBatchAsync(db,
            [new("SpotifyApi:PreferIsrcMatching", "true")], "legacy-import", _userId);
        Assert.Single(staged);
        await db.SaveChangesAsync(); await transaction.RollbackAsync();
        Assert.False(await db.RuntimeSettings.AsNoTracking().AnyAsync());
    }

    [Fact]
    public async Task HouseholdAndPersonalPreferences_RemainIsolatedByOwner()
    {
        var service = CreateService([]);
        await service.ApplyBatchAsync(
        [
            new("Library:ExplicitFilter", "ExplicitOnly"),
            new("Playback:ShowExternalLabel", "false"),
            new("Playback:ShowExplicitLabel", "true")
        ], "webui", _userId);
        var beforeA = await service.GetPreferencesAsync(_userId);

        var personalA = await service.UpdatePreferencesAsync(
            _userId, new("CleanOnly", true, false), beforeA.Revision);
        var household = await service.GetPreferencesAsync(null);
        var inheritedB = await service.GetPreferencesAsync(_userB);

        var defaultLyricsOrder = ProviderOrderPolicyCatalog.Find(allstarr.Core.Capabilities.ProviderCapabilityKind.Lyrics)!.DefaultValue;
        Assert.Equal(new ListeningPreferences("CleanOnly", true, false, defaultLyricsOrder), personalA.Values);
        Assert.Equal(new ListeningPreferences("ExplicitOnly", false, true, defaultLyricsOrder), household.Values);
        Assert.Equal(household.Values, inheritedB.Values);
        Assert.False(personalA.UsesHouseholdDefaults);
        Assert.True(inheritedB.UsesHouseholdDefaults);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.GetPreferencesAsync(_disabledUser));

        await using var db = await _factory.CreateDbContextAsync();
        var personalRows = await db.RuntimeSettings.AsNoTracking()
            .Where(item => item.OwnerUserId == _userId)
            .ToListAsync();
        Assert.Equal(4, personalRows.Count);
        Assert.All(personalRows, item => Assert.Contains(item.Key, new[]
        {
            "Library:ExplicitFilter",
            "Playback:ShowExternalLabel",
            "Playback:ShowExplicitLabel",
            "Playback:StartPreference"
        }));
        Assert.DoesNotContain(await db.RuntimeSettings.AsNoTracking().ToListAsync(),
            item => item.OwnerUserId == _userB);
    }

    [Fact]
    public async Task Service_AppliesLiveOperationalOptionsWithoutProjectingSecretsOrBootstrapOverrides()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Cache:SearchResultsMinutes"] = "1",
            ["Deezer:Arl"] = "bootstrap-secret",
            ["AppleDownload:BaseUrl"] = "http://compose-apple-gateway:8000",
            ["MULTI_PROVIDER_STREAMING_ORDER"] = "qobuz"
        }).Build();
        var signal = new RuntimeSettingsChangeSignal();
        var cache = new CacheSettings();
        var deezer = new DeezerSettings { Arl = "bootstrap-secret", Quality = "MP3_128" };
        var qobuz = new QobuzSettings { Quality = "MP3_320" };
        var apple = new AppleDownloadSettings
        {
            BaseUrl = "http://compose-apple-gateway:8000",
            Quality = "aac-96"
        };
        var spotifyApi = new SpotifyApiSettings();
        var spotifyImport = new SpotifyImportSettings();
        var jellyfin = new JellyfinSettings(); var subsonic = new SubsonicSettings();
        var accounts = new ProviderAccountOptions();
        var liveOptions = new RuntimeSettingsLiveOptions(configuration,
            Options.Create(cache), Options.Create(deezer), Options.Create(qobuz), Options.Create(apple),
            Options.Create(spotifyApi), Options.Create(spotifyImport),
            Options.Create(new MusicBrainzSettings()), Options.Create(new ScrobblingSettings()), Options.Create(jellyfin), Options.Create(subsonic),
            accounts);
        var service = new DurableRuntimeSettingsService(_factory, configuration, _clock, signal,
            liveOptions, NullLogger<DurableRuntimeSettingsService>.Instance);
        await service.ApplyBatchAsync(
        [
            new(ProviderAccountOptions.ListenerConnectionsKey, "false"),
            new("Cache:SearchResultsMinutes", "15"), new("Deezer:Quality", "FLAC"),
            new("Cache:SearchBudgetMilliseconds", "2000"), new("Cache:SearchBackgroundSeconds", "20"),
            new("Cache:SearchFailureCooldownSeconds", "120"),
            new("Providers:StreamingOrder", "deezer,qobuz"), new("Library:DownloadMode", "Album"),
            new("AppleDownload:BaseUrl", "http://apple-gateway.lan/base"),
            new("AppleDownload:Quality", "alac-24-96"),
            new("Qobuz:Quality", "FLAC_24_LOW"),
            new("Matching:LocalPreferencePercent", "11"),
            new("SpotifyImport:Playlists", "[[\"Discover Weekly\",\"source-id\",\"target-id\",\"last\",\"0 8 * * *\"]]")
        ], "webui", _userId);
        Assert.False(accounts.ListenersCanConnectOwnAccounts);
        await service.StartAsync(CancellationToken.None);
        using var projectionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (cache.SearchResultsMinutes != 15) await Task.Delay(50, projectionTimeout.Token);
        Assert.Equal(2000, cache.SearchBudgetMilliseconds);
        Assert.Equal(20, cache.SearchBackgroundSeconds);
        Assert.Equal(120, cache.SearchFailureCooldownSeconds);
        var migrated = await service.GetAsync(AudioQualityPolicy.SettingKey);
        Assert.Equal(RuntimeSettingOrigin.Durable, migrated.Origin);
        Assert.Equal("HiResLossless", migrated.Value);
        await service.ApplyBatchAsync(
            [new(ProviderAccountOptions.ListenerConnectionsKey, "true", 1), new("Cache:SearchResultsMinutes", "22", 1), new(AudioQualityPolicy.SettingKey, "CdLossless", migrated.Revision)],
            "webui", _userId);
        Assert.True(accounts.ListenersCanConnectOwnAccounts);
        Assert.Equal(22, cache.SearchResultsMinutes);
        await service.StopAsync(CancellationToken.None);

        Assert.True(accounts.ListenersCanConnectOwnAccounts);
        Assert.Equal(22, cache.SearchResultsMinutes); Assert.Equal("MP3_128", deezer.Quality);
        Assert.Equal("bootstrap-secret", deezer.Arl);
        Assert.Equal("qobuz", configuration["MULTI_PROVIDER_STREAMING_ORDER"]);
        Assert.Equal("http://compose-apple-gateway:8000", apple.BaseUrl);
        Assert.Equal("aac-96", apple.Quality);
        Assert.Equal("MP3_320", qobuz.Quality);
        var importedPlaylist = Assert.Single(spotifyImport.Playlists);
        Assert.Equal("Discover Weekly", importedPlaylist.Name);
        Assert.Equal("target-id", importedPlaylist.JellyfinId);
        Assert.Equal(LocalTracksPosition.Last, importedPlaylist.LocalTracksPosition);
        var handler = new RecordingHandler();
        var discovery = new AppleDownloadEndpointDiscovery(
            new RecordingFactory(new HttpClient(handler)), Options.Create(apple));
        var discoveryResult = await discovery.DiscoverAsync();
        Assert.Equal(AppleDownloadEndpointState.Incompatible, discoveryResult.State);
        Assert.Equal("/api/capabilities", handler.RequestedPath);
        Assert.Equal(DownloadMode.Album, jellyfin.DownloadMode); Assert.Equal(DownloadMode.Album, subsonic.DownloadMode);
    }

    [Fact]
    public async Task SimilarFallbackSettings_PersistAndProjectLiveAcrossServiceRestart()
    {
        var configuration = new ConfigurationBuilder().Build();
        var playback = new PlaybackSettings();
        var service = new DurableRuntimeSettingsService(_factory, configuration, _clock,
            new RuntimeSettingsChangeSignal(), LiveOptions(configuration, playback),
            NullLogger<DurableRuntimeSettingsService>.Instance);

        await service.StartAsync(CancellationToken.None);
        Assert.True(playback.LastFmSimilarEnabled);
        Assert.True(playback.ListenBrainzSimilarEnabled);

        await service.ApplyBatchAsync(
        [
            new("Playback:LastFmSimilarEnabled", "false"),
            new("Playback:ListenBrainzSimilarEnabled", "false")
        ], "webui", _userId);

        Assert.False(playback.LastFmSimilarEnabled);
        Assert.False(playback.ListenBrainzSimilarEnabled);
        Assert.False((bool)(await service.GetAsync("Playback:LastFmSimilarEnabled")).Value);
        Assert.False((bool)(await service.GetAsync("Playback:ListenBrainzSimilarEnabled")).Value);
        await service.StopAsync(CancellationToken.None);

        var restartedPlayback = new PlaybackSettings();
        var restarted = new DurableRuntimeSettingsService(_factory, configuration, _clock,
            new RuntimeSettingsChangeSignal(), LiveOptions(configuration, restartedPlayback),
            NullLogger<DurableRuntimeSettingsService>.Instance);
        await restarted.StartAsync(CancellationToken.None);
        Assert.False(restartedPlayback.LastFmSimilarEnabled);
        Assert.False(restartedPlayback.ListenBrainzSimilarEnabled);
        await restarted.StopAsync(CancellationToken.None);
    }

    private DurableRuntimeSettingsService CreateService(IEnumerable<KeyValuePair<string, string?>> values)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return new(_factory, config, _clock, new RuntimeSettingsChangeSignal());
    }

    private static RuntimeSettingsLiveOptions LiveOptions(
        IConfiguration configuration,
        PlaybackSettings playback, SpotifyApiSettings? spotify = null) =>
        new(configuration,
            Options.Create(new CacheSettings()),
            Options.Create(new DeezerSettings()),
            Options.Create(new QobuzSettings()),
            Options.Create(new AppleDownloadSettings()),
            Options.Create(spotify ?? new SpotifyApiSettings()),
            Options.Create(new SpotifyImportSettings()),
            Options.Create(new MusicBrainzSettings()),
            Options.Create(new ScrobblingSettings()),
            Options.Create(new JellyfinSettings()),
            Options.Create(new SubsonicSettings()),
            new ProviderAccountOptions(),
            Options.Create(playback));

    private static UserRecord User(
        Guid id,
        string backendPrincipalId,
        string displayName,
        bool isAdmin,
        bool enabled,
        DateTimeOffset now) => new()
        {
            Id = id,
            BackendType = "jellyfin",
            BackendInstanceId = "fixture",
            BackendPrincipalId = backendPrincipalId,
            DisplayName = displayName,
            IsAdmin = isAdmin,
            Enabled = enabled,
            CreatedAt = now,
            UpdatedAt = now,
            LastSeenAt = now
        };

    public async Task DisposeAsync()
    {
        if (_database is not null) await _database.DisposeAsync();
    }

    private sealed class FakeClock(DateTimeOffset now) : IPlatformClock { public DateTimeOffset UtcNow { get; set; } = now; }
    private sealed class RecordingFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string? RequestedPath { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedPath = request.RequestUri?.AbsolutePath;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }
    private sealed class TestFactory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
        public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AllstarrDbContext(options));
    }
}
