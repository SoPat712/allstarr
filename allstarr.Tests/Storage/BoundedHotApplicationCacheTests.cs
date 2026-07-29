using allstarr.Core.Operations;
using allstarr.Core.Storage;
using allstarr.Services.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace allstarr.Tests;

public sealed class BoundedHotApplicationCacheTests : IAsyncLifetime
{
    private PostgresTestDatabase _testDatabase = null!;
    private TestFactory _factory = null!;
    private DatabaseApplicationCache _database = null!;
    private BoundedHotApplicationCache _cache = null!;

    public async Task InitializeAsync()
    {
        _testDatabase = await PostgresTestDatabase.CreateAsync();
        var options = new DbContextOptionsBuilder<AllstarrDbContext>()
            .UseNpgsql(_testDatabase.ConnectionString)
            .Options;
        _factory = new TestFactory(options);
        _database = new DatabaseApplicationCache(
            _factory,
            new TestClock(),
            NullLogger<DatabaseApplicationCache>.Instance);
        _cache = new BoundedHotApplicationCache(_database);

        await using var context = await _factory.CreateDbContextAsync();
        await context.Database.MigrateAsync();
    }

    [Fact]
    public async Task SuccessfulWrite_IsServedAfterDatabaseRowIsRemoved()
    {
        Assert.True(await _cache.SetStringAsync("odesli:translate:v2:hot-1:spotify", "cached"));
        await using (var context = await _factory.CreateDbContextAsync())
        {
            await context.ApplicationCacheEntries.ExecuteDeleteAsync();
        }

        Assert.Equal("cached", await _cache.GetStringAsync("odesli:translate:v2:hot-1:spotify"));
    }

    [Fact]
    public async Task Delete_RemovesHotAndDatabaseCopies()
    {
        await _cache.SetStringAsync("odesli:translate:v2:hot-2:spotify", "cached");

        Assert.True(await _cache.DeleteAsync("odesli:translate:v2:hot-2:spotify"));
        Assert.Null(await _cache.GetStringAsync("odesli:translate:v2:hot-2:spotify"));
    }

    [Fact]
    public async Task PatternDelete_ClearsHotTierBeforeDeletingDatabaseRows()
    {
        await _cache.SetStringAsync("odesli:translate:v2:playlist-one:spotify", "one");
        await _cache.SetStringAsync("odesli:translate:v2:track-one:spotify", "track");

        Assert.Equal(1, await _cache.DeleteByPatternAsync("odesli:translate:v2:playlist-*:spotify"));

        await using (var context = await _factory.CreateDbContextAsync())
        {
            await context.ApplicationCacheEntries
                .Where(item => item.Key == "odesli:translate:v2:track-one:spotify")
                .ExecuteDeleteAsync();
        }
        Assert.Null(await _cache.GetStringAsync("odesli:translate:v2:track-one:spotify"));
    }

    [Fact]
    public async Task OversizedHotEntry_RemainsAvailableFromDatabaseOnly()
    {
        var value = new string('x', BoundedHotApplicationCache.MaximumEntryBytes + 1);

        Assert.True(await _cache.SetStringAsync("odesli:translate:v2:large:spotify", value));
        Assert.Equal(value, await _cache.GetStringAsync("odesli:translate:v2:large:spotify"));
    }

    public async Task DisposeAsync()
    {
        _cache.Dispose();
        if (_testDatabase is not null)
        {
            await _testDatabase.DisposeAsync();
        }
    }

    private sealed class TestClock : IPlatformClock
    {
        public DateTimeOffset UtcNow => new(2026, 7, 23, 13, 0, 0, TimeSpan.Zero);
    }

    private sealed class TestFactory(DbContextOptions<AllstarrDbContext> options)
        : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);

        public Task<AllstarrDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
