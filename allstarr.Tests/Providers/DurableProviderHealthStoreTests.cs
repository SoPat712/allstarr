using allstarr.Core.Health;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed class DurableProviderHealthStoreTests : IAsyncLifetime
{
    private PostgresTestDatabase _database = null!;
    private TestDbContextFactory _factory = null!;
    private DurableStorageState _state = null!;
    private FakeClock _clock = null!;
    private ProviderHealthOptions _options = null!;

    public async Task InitializeAsync()
    {
        _database = await PostgresTestDatabase.CreateAsync();
        var storage = new DurableStorageOptions
        {
            Provider = "Postgres",
            ConnectionString = _database.ConnectionString
        };
        _factory = new TestDbContextFactory(_database.Options);
        await using var context = await _factory.CreateDbContextAsync();
        _state = new DurableStorageState(storage);
        _state.Set(DurableStorageReadiness.Ready, "fixture");
        _clock = new FakeClock(new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero));
        _options = new ProviderHealthOptions
        {
            FailureThreshold = 3,
            CircuitOpenSeconds = 30,
            SampleTtlSeconds = 60,
            RollupWindowMinutes = 15,
            SampleRetentionDays = 7
        };
    }

    [Fact]
    public async Task Samples_AreDurableAndIsolatedByAccountAndCapability()
    {
        var accountA = await SeedAccount("deezer", "account-a");
        var accountB = await SeedAccount("deezer", "account-b");
        var store = Store();

        await store.RecordAsync(
            "deezer",
            accountA,
            "metadata",
            ProviderHealthState.Healthy,
            25);
        await store.RecordAsync(
            "deezer",
            accountB,
            "metadata",
            ProviderHealthState.Degraded,
            50,
            "probe_failed token=fixture");
        await store.RecordAsync(
            "deezer",
            accountA,
            "download",
            ProviderHealthState.Unavailable,
            failureCode: "sidecar_unreachable");

        Assert.True(store.TryGetLatest("deezer", accountA, "metadata", out var healthy));
        Assert.Equal(ProviderHealthState.Healthy, healthy.State);
        Assert.True(store.TryGetLatest("deezer", accountB, "metadata", out var degraded));
        Assert.Equal(ProviderHealthState.Degraded, degraded.State);
        Assert.DoesNotContain("fixture", degraded.FailureCode ?? string.Empty, StringComparison.Ordinal);
        Assert.True(store.TryGetLatest("deezer", accountA, "download", out var download));
        Assert.Equal(ProviderHealthState.Unavailable, download.State);
        await using var context = await _factory.CreateDbContextAsync();
        Assert.Equal(2, await context.ProviderAccounts.CountAsync());
        Assert.Equal(3, await context.ProviderHealthSamples.CountAsync());
    }

    [Fact]
    public async Task UnknownAccountObservation_IsIgnoredAndCannotCreateRoutableAccount()
    {
        var store = Store();

        var result = await store.RecordAsync(
            "deezer",
            Guid.CreateVersion7(),
            "metadata",
            ProviderHealthState.Healthy);

        Assert.Null(result);
        await using var context = await _factory.CreateDbContextAsync();
        Assert.Empty(await context.ProviderAccounts.ToListAsync());
        Assert.Empty(await context.ProviderHealthSamples.ToListAsync());
    }

    [Fact]
    public async Task FailureThreshold_OpensCircuitAndHealthyProbeClosesIt()
    {
        var accountId = await SeedAccount("qobuz", "circuit-account");
        var store = Store();
        for (var index = 0; index < 3; index++)
        {
            await store.RecordAsync(
                "qobuz",
                accountId,
                "download",
                ProviderHealthState.Unavailable,
                failureCode: "provider_unreachable");
        }

        Assert.True(store.IsCircuitOpen(accountId, "download"));
        _clock.Advance(TimeSpan.FromSeconds(31));
        Assert.False(store.IsCircuitOpen(accountId, "download"));

        await store.RecordAsync(
            "qobuz",
            accountId,
            "download",
            ProviderHealthState.Healthy,
            12);

        Assert.False(store.IsCircuitOpen(accountId, "download"));
        await using var context = await _factory.CreateDbContextAsync();
        var circuit = await context.ProviderCircuits.SingleAsync();
        Assert.Equal(ProviderCircuitState.Closed, circuit.State);
        Assert.Equal(0, circuit.ConsecutiveFailures);
    }

    [Fact]
    public async Task UnauthorizedObservation_OpensCircuitImmediately()
    {
        var accountId = await SeedAccount("applemusic", "account-a");
        var store = Store();

        await store.RecordAsync(
            "applemusic",
            accountId,
            "personal-library",
            ProviderHealthState.Unauthorized,
            failureCode: "account_unauthorized");

        Assert.True(store.IsCircuitOpen(accountId, "personal-library"));
    }

    [Fact]
    public async Task RestartHydration_RestoresLatestSampleAndCircuitState()
    {
        var accountId = await SeedAccount("deezer", "restart-account");
        var first = Store();
        await first.RecordAsync(
            "deezer",
            accountId,
            "metadata",
            ProviderHealthState.Degraded,
            failureCode: "fixture_failure");

        var restarted = Store();
        await restarted.InitializeAsync();

        Assert.True(restarted.TryGetLatest("deezer", accountId, "metadata", out var latest));
        Assert.Equal(ProviderHealthState.Degraded, latest.State);
        Assert.Equal("fixture_failure", latest.FailureCode);
        Assert.True(restarted.TryGetLatest(
            "deezer",
            accountId,
            "metadata",
            out var routeLatest));
        Assert.Equal(ProviderHealthState.Degraded, routeLatest.State);
    }

    [Fact]
    public async Task ExpiredSample_IsNoLongerReportedAsCurrent()
    {
        var accountId = await SeedAccount("deezer", "expiring-account");
        var store = Store();
        await store.RecordAsync(
            "deezer",
            accountId,
            "metadata",
            ProviderHealthState.Healthy);

        _clock.Advance(TimeSpan.FromSeconds(61));

        Assert.False(store.TryGetLatest("deezer", accountId, "metadata", out _));
    }

    [Fact]
    public async Task Samples_UpdateDurableLatencyAndSuccessRollup()
    {
        var accountId = await SeedAccount("qobuz", "account-rollup");
        var store = Store();
        var first = await store.RecordAsync(
            "qobuz", accountId, "download", ProviderHealthState.Healthy, 10);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await store.RecordAsync(
            "qobuz", accountId, "download", ProviderHealthState.Healthy, 20);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await store.RecordAsync(
            "qobuz", accountId, "download", ProviderHealthState.Degraded, 100, "slow_provider");
        _clock.Advance(TimeSpan.FromMinutes(1));
        await store.RecordAsync(
            "qobuz", accountId, "download", ProviderHealthState.Unauthorized,
            failureCode: "account_unauthorized");

        var rollup = await store.GetLatestRollupAsync(first!.ProviderAccountId, "download");

        Assert.NotNull(rollup);
        Assert.Equal(4, rollup.SampleCount);
        Assert.Equal(2, rollup.SuccessCount);
        Assert.Equal(2, rollup.FailureCount);
        Assert.Equal(0.5, rollup.SuccessRate);
        Assert.Equal(20, rollup.P50LatencyMilliseconds);
        Assert.Equal(100, rollup.P95LatencyMilliseconds);
        Assert.Equal(ProviderHealthState.Unauthorized, rollup.LastState);
        Assert.Equal("account_unauthorized", rollup.LastFailureCode);
        await using var context = await _factory.CreateDbContextAsync();
        Assert.Single(await context.ProviderHealthRollups.ToListAsync());
    }

    [Fact]
    public async Task Retention_PrunesExpiredSamplesAndRollupWindows()
    {
        _options.SampleRetentionDays = 1;
        var accountId = await SeedAccount("deezer", "retention-account");
        var store = Store();
        await store.RecordAsync(
            "deezer", accountId, "metadata", ProviderHealthState.Healthy, 12);
        _clock.Advance(TimeSpan.FromDays(2));

        await store.RecordAsync(
            "deezer", accountId, "metadata", ProviderHealthState.Healthy, 18);

        await using var context = await _factory.CreateDbContextAsync();
        Assert.Single(await context.ProviderHealthSamples.ToListAsync());
        Assert.Single(await context.ProviderHealthRollups.ToListAsync());
    }

    private DurableProviderHealthStore Store() =>
        new(_factory, _state, _options, _clock);

    private async Task<Guid> SeedAccount(string providerId, string displayName)
    {
        await using var context = await _factory.CreateDbContextAsync();
        var accountId = Guid.CreateVersion7();

        context.ProviderAccounts.Add(new ProviderAccountRecord
        {
            Id = accountId,
            ProviderId = providerId,
            DisplayName = $"Fixture {displayName}",
            Scope = ProviderAccountScope.Global,
            Enabled = true,
            CreatedAt = _clock.UtcNow,
            UpdatedAt = _clock.UtcNow
        });
        await context.SaveChangesAsync();
        return accountId;
    }

    public async Task DisposeAsync() => await _database.DisposeAsync();

    private sealed class FakeClock(DateTimeOffset now) : IPlatformClock
    {
        public DateTimeOffset UtcNow { get; private set; } = now;
        public void Advance(TimeSpan duration) => UtcNow = UtcNow.Add(duration);
    }

    private sealed class TestDbContextFactory(DbContextOptions<AllstarrDbContext> options)
        : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);

        public Task<AllstarrDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(new AllstarrDbContext(options));
    }
}
