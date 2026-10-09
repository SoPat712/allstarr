using allstarr.Core.Operations;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace allstarr.Tests;

public sealed class PersonalListeningPreferencesTests : IAsyncLifetime
{
    private SqliteTestDatabase _database = null!;
    private TestFactory _factory = null!;
    private FakeClock _clock = null!;
    private Guid _userA;
    private Guid _userB;
    private Guid _disabledUser;
    private Guid _missingUser;

    public async Task InitializeAsync()
    {
        _database = await SqliteTestDatabase.CreateAsync();
        _factory = new(_database.Options);
        var now = DateTimeOffset.Parse("2026-10-08T12:00:00Z");
        _clock = new(now);
        _userA = Guid.CreateVersion7();
        _userB = Guid.CreateVersion7();
        _disabledUser = Guid.CreateVersion7();
        _missingUser = Guid.CreateVersion7();

        await using var db = await _factory.CreateDbContextAsync();
        db.Users.AddRange(
            User(_userA, "listener-a", "Listener A", isAdmin: false, enabled: true, now: now),
            User(_userB, "settings-admin", "Settings Admin", isAdmin: true, enabled: true, now: now),
            User(_disabledUser, "disabled", "Disabled", isAdmin: false, enabled: false, now: now));
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task PersonalUpdate_IsIsolatedFromOtherUsersAndHouseholdSettings()
    {
        var service = CreateService();
        var beforeA = await service.GetPreferencesAsync(_userA);
        var updatedA = await service.UpdatePreferencesAsync(_userA,
            new("CleanOnly", ShowExternalLabel: false, ShowExplicitLabel: false), beforeA.Revision);

        var household = await service.GetPreferencesAsync(null);
        var userB = await service.GetPreferencesAsync(_userB);
        Assert.Equal(new ListeningPreferences("CleanOnly", false, false), updatedA.Values);
        Assert.False(updatedA.UsesHouseholdDefaults);
        Assert.Equal(new ListeningPreferences(), household.Values);
        Assert.Equal(household.Values, userB.Values);
        Assert.True(household.UsesHouseholdDefaults);
        Assert.True(userB.UsesHouseholdDefaults);
    }

    [Fact]
    public async Task HouseholdChangesFlowToInheritedUserWhilePersonalRowsSurviveServiceRecreation()
    {
        var service = CreateService();
        var beforeA = await service.GetPreferencesAsync(_userA);
        var personal = await service.UpdatePreferencesAsync(_userA,
            new("ExplicitOnly", ShowExternalLabel: false, ShowExplicitLabel: true), beforeA.Revision);

        await service.ApplyBatchAsync(
        [
            new("Library:ExplicitFilter", "CleanOnly"),
            new("Playback:ShowExternalLabel", "false"),
            new("Playback:ShowExplicitLabel", "false")
        ], "webui", _userB);

        var recreated = CreateService();
        var restoredA = await recreated.GetPreferencesAsync(_userA);
        var inheritedB = await recreated.GetPreferencesAsync(_userB);
        Assert.Equal(personal.Values, restoredA.Values);
        Assert.Equal(new ListeningPreferences("CleanOnly", false, false), restoredA.HouseholdDefaults);
        Assert.NotEqual(personal.Revision, restoredA.Revision);
        Assert.Equal(restoredA.HouseholdDefaults, inheritedB.Values);
        Assert.True(inheritedB.UsesHouseholdDefaults);
    }

    [Fact]
    public async Task ResetRevealsLatestHouseholdDefaults()
    {
        var service = CreateService();
        var initial = await service.GetPreferencesAsync(_userA);
        var personal = await service.UpdatePreferencesAsync(_userA,
            new("ExplicitOnly", false, true), initial.Revision);
        await service.ApplyBatchAsync(
        [
            new("Library:ExplicitFilter", "CleanOnly"),
            new("Playback:ShowExternalLabel", "true"),
            new("Playback:ShowExplicitLabel", "false")
        ], "webui", _userB);
        var current = await service.GetPreferencesAsync(_userA);

        var reset = await service.UpdatePreferencesAsync(_userA, null, current.Revision);

        Assert.Equal(new ListeningPreferences("CleanOnly", true, false), reset.Values);
        Assert.Equal(reset.HouseholdDefaults, reset.Values);
        Assert.True(reset.UsesHouseholdDefaults);
        Assert.NotEqual(personal.Revision, reset.Revision);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.False(await db.RuntimeSettings.AnyAsync(item => item.OwnerUserId == _userA));
    }

    [Theory]
    [InlineData(allstarr.Core.Protocols.ProtocolKind.Jellyfin)]
    [InlineData(allstarr.Core.Protocols.ProtocolKind.Subsonic)]
    public async Task EffectivePolicyAndRenderersRecheckTheViewerWithoutMutatingSharedMetadata(
        allstarr.Core.Protocols.ProtocolKind protocol)
    {
        var settings = CreateService();
        var initial = await settings.GetPreferencesAsync(_userA);
        await settings.UpdatePreferencesAsync(_userA, new("CleanOnly", false, false), initial.Revision);
        var resolver = new EffectiveProviderPolicyResolver(settings);
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        var accessor = new Microsoft.AspNetCore.Http.HttpContextAccessor { HttpContext = http };
        var jellyfin = new allstarr.Services.Jellyfin.JellyfinResponseBuilder(httpContexts: accessor);
        var subsonic = new allstarr.Services.Subsonic.SubsonicResponseBuilder(accessor);
        var song = new allstarr.Models.Domain.Song
        {
            Id = "ext-deezer-song-42",
            ExternalProvider = "deezer",
            ExternalId = "42",
            Title = "Shared metadata",
            Artist = "Artist",
            ExplicitContentLyrics = 1
        };
        foreach (var user in new[] { _userA, _userB, _userA })
        {
            var policy = await resolver.ResolveForUserAsync(user);
            var principal = new allstarr.Core.Identity.AllstarrPrincipal(user,
                protocol.ToString().ToLowerInvariant(), "backend", "listener", "Listener", false);
            http.Items[allstarr.Core.Protocols.ProtocolExecutionContextFactory.HttpContextItemKey] =
                new allstarr.Core.Protocols.ProtocolExecutionContext(protocol, "backend", "listener", principal,
                    "preferences", DateTimeOffset.UtcNow.AddMinutes(1), default)
                { Policy = policy };
            Assert.Equal(user == _userB, policy.Includes(song));
            Assert.Equal(user, policy.UserId);
            Assert.True(policy.Includes((int?)null));
            Assert.True(policy.Includes(new allstarr.Models.Domain.Song { IsLocal = true, ExplicitContentLyrics = 1 }));
            var expected = user == _userA ? "Shared metadata" : "Shared metadata [A]/[E]";
            Assert.Equal(expected, jellyfin.ConvertSongToJellyfinItem(song)["Name"]);
            Assert.Equal(expected, subsonic.ConvertSongToJson(song)["title"]);
            Assert.Equal(expected, subsonic.ConvertSongToXml(song, "http://subsonic.org/restapi").Attribute("title")!.Value);
        }
        Assert.Equal("Shared metadata", song.Title);

    }

    [Fact]
    public async Task StaleRevisionFailsWithoutPartiallyChangingPreferences()
    {
        var service = CreateService();
        var initial = await service.GetPreferencesAsync(_userA);
        var first = await service.UpdatePreferencesAsync(_userA,
            new("CleanOnly", false, true), initial.Revision);

        await Assert.ThrowsAsync<RuntimeSettingConflictException>(() => service.UpdatePreferencesAsync(
            _userA, new("ExplicitOnly", true, false), initial.Revision));

        var persisted = await service.GetPreferencesAsync(_userA);
        Assert.Equal(first.Values, persisted.Values);
        Assert.Equal(first.Revision, persisted.Revision);
    }

    [Fact]
    public async Task PersonalScopeRequiresAnActivePersistedUserAndAValidFilter()
    {
        var service = CreateService();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetPreferencesAsync(_missingUser));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetPreferencesAsync(_disabledUser));

        var household = await service.GetPreferencesAsync(null);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.UpdatePreferencesAsync(
            _missingUser, new("All", true, true), household.Revision));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.UpdatePreferencesAsync(
            _disabledUser, new("All", true, true), household.Revision));

        var current = await service.GetPreferencesAsync(_userA);
        await Assert.ThrowsAsync<ArgumentException>(() => service.UpdatePreferencesAsync(
            _userA, new("Unfiltered", true, true), current.Revision));
        await using var db = await _factory.CreateDbContextAsync();
        Assert.False(await db.RuntimeSettings.AnyAsync(item => item.OwnerUserId == _userA));
    }

    [Fact]
    public async Task DatabaseConstraintRejectsDeploymentKeysInPersonalScope()
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.RuntimeSettings.Add(new RuntimeSettingRecord
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = _userA,
            Key = "Cache:SearchResultsMinutes",
            ValueType = RuntimeSettingValueType.Integer,
            ValueJson = "12",
            Source = "test",
            UpdatedByUserId = _userA,
            CreatedAt = _clock.UtcNow,
            UpdatedAt = _clock.UtcNow,
            Revision = 1
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    private DurableRuntimeSettingsService CreateService()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jellyfin:ExplicitFilter"] = "All"
        }).Build();
        return new(_factory, configuration, _clock, new RuntimeSettingsChangeSignal());
    }

    private static UserRecord User(
        Guid id,
        string backendPrincipalId,
        string name,
        bool isAdmin,
        bool enabled,
        DateTimeOffset now) => new()
        {
            Id = id,
            BackendType = "jellyfin",
            BackendInstanceId = "fixture",
            BackendPrincipalId = backendPrincipalId,
            DisplayName = name,
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

    private sealed class FakeClock(DateTimeOffset now) : IPlatformClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    private sealed class TestFactory(DbContextOptions<AllstarrDbContext> options)
        : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
        public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AllstarrDbContext(options));
    }
}
