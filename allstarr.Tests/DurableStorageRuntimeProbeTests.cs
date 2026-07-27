using allstarr.Core.Operations;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace allstarr.Tests;

public sealed class DurableStorageRuntimeProbeTests : IAsyncLifetime
{
    private readonly FakeClock _clock = new(DateTimeOffset.Parse("2026-07-11T00:00:00Z"));
    private PostgresTestDatabase _database = null!;

    public async Task InitializeAsync()
    {
        _database = await PostgresTestDatabase.CreateAsync();
        await using var context = new AllstarrDbContext(_database.Options);
        await context.Database.MigrateAsync();
    }

    [Fact]
    public async Task ProbeIsCadenceBoundedAndReportsCurrentPostgresSchema()
    {
        var options = RuntimeOptions();
        var state = new DurableStorageState(options);
        state.Set(DurableStorageReadiness.Ready, "startup-schema");
        var factory = new CountingDbContextFactory(_database.Options);
        using var probe = new DurableStorageRuntimeProbe(
            factory,
            options,
            state,
            _clock,
            NullLogger<DurableStorageRuntimeProbe>.Instance);

        var first = await probe.CheckAsync();
        var cached = await probe.CheckAsync();

        Assert.Equal(DurableStorageReadiness.Ready, first.Readiness);
        Assert.Equal(first.SchemaVersion, cached.SchemaVersion);
        Assert.Equal(1, factory.CreateCount);

        _clock.Advance(TimeSpan.FromSeconds(options.RuntimeProbeIntervalSeconds));
        var refreshed = await probe.CheckAsync();

        Assert.Equal(DurableStorageReadiness.Ready, refreshed.Readiness);
        Assert.Equal(2, factory.CreateCount);
    }

    [Fact]
    public async Task ForcedProbeBypassesCadence()
    {
        var options = RuntimeOptions();
        var state = new DurableStorageState(options);
        var factory = new CountingDbContextFactory(_database.Options);
        using var probe = new DurableStorageRuntimeProbe(
            factory,
            options,
            state,
            _clock,
            NullLogger<DurableStorageRuntimeProbe>.Instance);

        await probe.CheckAsync();
        await probe.CheckNowAsync();

        Assert.Equal(2, factory.CreateCount);
    }

    [Fact]
    public async Task ProbeMarksRuntimeSchemaDriftUnready()
    {
        var options = RuntimeOptions();
        var state = new DurableStorageState(options);
        var factory = new CountingDbContextFactory(_database.Options);
        using var probe = new DurableStorageRuntimeProbe(
            factory,
            options,
            state,
            _clock,
            NullLogger<DurableStorageRuntimeProbe>.Instance);
        Assert.Equal(DurableStorageReadiness.Ready, (await probe.CheckAsync()).Readiness);

        await using (var context = new AllstarrDbContext(_database.Options))
        {
            var latest = (await context.Database.GetAppliedMigrationsAsync()).Last();
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = {latest}");
        }
        _clock.Advance(TimeSpan.FromSeconds(options.RuntimeProbeIntervalSeconds));

        var incompatible = await probe.CheckAsync();

        Assert.Equal(DurableStorageReadiness.SchemaIncompatible, incompatible.Readiness);
        Assert.Equal(DurableSchemaCompatibility.MigrationRequiredErrorCode, incompatible.ErrorCode);
    }

    private DurableStorageOptions RuntimeOptions() => new()
    {
        Provider = "Postgres",
        ConnectionString = _database.ConnectionString,
        RuntimeProbeIntervalSeconds = 30,
        RuntimeProbeTimeoutSeconds = 5
    };

    public async Task DisposeAsync()
    {
        await _database.DisposeAsync();
    }

    private sealed class CountingDbContextFactory(DbContextOptions<AllstarrDbContext> options)
        : IDbContextFactory<AllstarrDbContext>
    {
        public int CreateCount { get; private set; }

        public AllstarrDbContext CreateDbContext()
        {
            CreateCount++;
            return new AllstarrDbContext(options);
        }

        public Task<AllstarrDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class FakeClock(DateTimeOffset now) : IPlatformClock
    {
        public DateTimeOffset UtcNow { get; private set; } = now;

        public void Advance(TimeSpan duration) => UtcNow = UtcNow.Add(duration);
    }
}
