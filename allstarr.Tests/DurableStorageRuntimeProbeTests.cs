using allstarr.Core.Operations;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace allstarr.Tests;

public sealed class DurableStorageRuntimeProbeTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "allstarr-tests",
        Guid.NewGuid().ToString("N"));
    private readonly FakeClock _clock = new(DateTimeOffset.Parse("2026-07-11T00:00:00Z"));

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ProbeIsCadenceBounded_DetectsLossWithoutRecreation_AndRecovers()
    {
        var databasePath = Path.Combine(_root, "runtime.db");
        await CreateCurrentDatabase(databasePath);
        var options = RuntimeOptions(databasePath);
        var state = new DurableStorageState(options);
        state.Set(DurableStorageReadiness.Ready, "startup-schema");
        var factory = new CountingDbContextFactory(CreateOptions(options.ConnectionString));
        using var probe = new DurableStorageRuntimeProbe(
            factory,
            options,
            state,
            _clock,
            NullLogger<DurableStorageRuntimeProbe>.Instance);

        var first = await probe.CheckAsync();
        var cached = await probe.CheckAsync();

        Assert.Equal(DurableStorageReadiness.Ready, first.Readiness);
        Assert.Equal(DurableStorageReadiness.Ready, cached.Readiness);
        Assert.Equal(1, factory.CreateCount);

        var offlinePath = databasePath + ".offline";
        File.Move(databasePath, offlinePath);
        _clock.Advance(TimeSpan.FromSeconds(options.RuntimeProbeIntervalSeconds));

        var unavailable = await probe.CheckAsync();

        Assert.Equal(DurableStorageReadiness.Unavailable, unavailable.Readiness);
        Assert.Equal("database_unavailable", unavailable.ErrorCode);
        Assert.False(File.Exists(databasePath));
        Assert.Equal(2, factory.CreateCount);

        File.Move(offlinePath, databasePath);
        _clock.Advance(TimeSpan.FromSeconds(options.RuntimeProbeIntervalSeconds));

        var recovered = await probe.CheckAsync();

        Assert.Equal(DurableStorageReadiness.Ready, recovered.Readiness);
        await using var recoveredContext = new AllstarrDbContext(CreateOptions(options.ConnectionString));
        Assert.Equal(recoveredContext.Database.GetMigrations().Last(), recovered.SchemaVersion);
        Assert.Equal(3, factory.CreateCount);
    }

    [Fact]
    public async Task ProbeMarksRuntimeSchemaDriftUnreadyUntilCurrentSchemaReturns()
    {
        var databasePath = Path.Combine(_root, "schema.db");
        await CreateCurrentDatabase(databasePath);
        var options = RuntimeOptions(databasePath);
        var state = new DurableStorageState(options);
        var factory = new CountingDbContextFactory(CreateOptions(options.ConnectionString));
        using var probe = new DurableStorageRuntimeProbe(
            factory,
            options,
            state,
            _clock,
            NullLogger<DurableStorageRuntimeProbe>.Instance);
        Assert.Equal(
            DurableStorageReadiness.Ready,
            (await probe.CheckAsync()).Readiness);

        await using (var context = new AllstarrDbContext(CreateOptions(options.ConnectionString)))
        {
            await context.Database.ExecuteSqlRawAsync(
                "DELETE FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = " +
                "'20260711141123_Phase2TrackIdentityFoundation'");
        }
        _clock.Advance(TimeSpan.FromSeconds(options.RuntimeProbeIntervalSeconds));

        var incompatible = await probe.CheckAsync();

        Assert.Equal(DurableStorageReadiness.SchemaIncompatible, incompatible.Readiness);
        Assert.Equal(DurableSchemaCompatibility.MigrationRequiredErrorCode, incompatible.ErrorCode);
    }

    private static DurableStorageOptions RuntimeOptions(string databasePath)
    {
        var options = new DurableStorageOptions
        {
            Provider = "Sqlite",
            ConnectionString = $"Data Source={databasePath};Pooling=False",
            RuntimeProbeIntervalSeconds = 30,
            RuntimeProbeTimeoutSeconds = 5
        };
        return options;
    }

    private static async Task CreateCurrentDatabase(string databasePath)
    {
        await using var context = new AllstarrDbContext(CreateOptions(
            $"Data Source={databasePath};Pooling=False"));
        await context.Database.MigrateAsync();
    }

    private static DbContextOptions<AllstarrDbContext> CreateOptions(string connectionString) =>
        new DbContextOptionsBuilder<AllstarrDbContext>()
            .UseSqlite(connectionString)
            .Options;

    public Task DisposeAsync()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        return Task.CompletedTask;
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
