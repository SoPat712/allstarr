using allstarr.Core.Operations;
using allstarr.Core.Storage;
using allstarr.Models.Settings;
using allstarr.Services.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace allstarr.Tests;

public sealed class DatabaseApplicationCacheTests : IAsyncLifetime
{
    private PostgresTestDatabase _database = null!;
    private TestFactory _factory = null!;
    private TestClock _clock = null!;
    private DatabaseApplicationCache _cache = null!;

    public async Task InitializeAsync()
    {
        _database = await PostgresTestDatabase.CreateAsync();
        _factory = new TestFactory(_database.Options);
        _clock = new TestClock(new DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero));
        _cache = new DatabaseApplicationCache(
            _factory,
            _clock,
            NullLogger<DatabaseApplicationCache>.Instance);

        await using var database = await _factory.CreateDbContextAsync();
        await database.Database.MigrateAsync();
    }

    [Fact]
    public async Task SetAndGet_OverwriteOneDisposableEntry()
    {
        Assert.True(await _cache.SetStringAsync("metadata:track:1", "first", TimeSpan.FromMinutes(5)));
        Assert.True(await _cache.SetStringAsync("metadata:track:1", "second", TimeSpan.FromMinutes(10)));

        Assert.Equal("second", await _cache.GetStringAsync("metadata:track:1"));

        await using var database = await _factory.CreateDbContextAsync();
        var entry = await database.ApplicationCacheEntries.SingleAsync();
        Assert.Equal("second", entry.Value);
        Assert.Equal(6, entry.PayloadBytes);
    }

    [Fact]
    public async Task ExpiredEntry_IsAColdMissAndIsRemoved()
    {
        await _cache.SetStringAsync("playlist:artwork:1", "asset", TimeSpan.FromMinutes(1));
        _clock.UtcNow = _clock.UtcNow.AddMinutes(2);

        Assert.Null(await _cache.GetStringAsync("playlist:artwork:1"));
        Assert.False(await _cache.ExistsAsync("playlist:artwork:1"));

        await using var database = await _factory.CreateDbContextAsync();
        Assert.Empty(await database.ApplicationCacheEntries.ToListAsync());
    }

    [Fact]
    public async Task CleanupExpired_IsBoundedAndLeavesLiveEntries()
    {
        await _cache.SetStringAsync("expired:1", "one", TimeSpan.FromMinutes(1));
        await _cache.SetStringAsync("expired:2", "two", TimeSpan.FromMinutes(1));
        await _cache.SetStringAsync("live:1", "live", TimeSpan.FromHours(1));
        _clock.UtcNow = _clock.UtcNow.AddMinutes(2);

        Assert.Equal(1, await _cache.CleanupExpiredAsync(batchSize: 1));

        await using var database = await _factory.CreateDbContextAsync();
        Assert.Equal(2, await database.ApplicationCacheEntries.CountAsync());
        Assert.True(await database.ApplicationCacheEntries.AnyAsync(item => item.Key == "live:1"));
    }

    [Fact]
    public async Task MaintenancePreview_ReportsAndCleanupRemovesOnlyDisposableEntries()
    {
        await _cache.SetStringAsync("search:live", "live", TimeSpan.FromHours(1));
        await _cache.SetStringAsync("search:expired", "expired", TimeSpan.FromMinutes(1));
        _clock.UtcNow = _clock.UtcNow.AddMinutes(2);

        await using (var database = await _factory.CreateDbContextAsync())
        {
            database.ApplicationCacheEntries.Add(new ApplicationCacheEntryRecord
            {
                Key = "legacy:no-owner",
                Category = "Legacy",
                Value = "orphan",
                PayloadBytes = 6,
                CreatedAt = _clock.UtcNow,
                UpdatedAt = _clock.UtcNow
            });
            await database.SaveChangesAsync();
        }

        var preview = await _cache.PreviewMaintenanceAsync();
        Assert.Equal(3, preview.ScannedEntries);
        Assert.False(preview.ScanLimitReached);
        Assert.Equal(1, preview.ExpiredEntries);
        Assert.Equal(1, preview.UnknownOwnerEntries);
        Assert.Equal(13, preview.ReclaimableBytes);

        Assert.Equal(1, await _cache.CleanupExpiredAsync());
        Assert.Equal(1, await _cache.CleanupInvalidOwnershipAsync());
        Assert.Equal("live", await _cache.GetStringAsync("search:live"));

        await using var remaining = await _factory.CreateDbContextAsync();
        Assert.Single(await remaining.ApplicationCacheEntries.ToListAsync());
    }

    [Fact]
    public async Task PatternOperations_UseRedisCompatibleWildcards()
    {
        await _cache.SetStringAsync("playlist:one", "1");
        await _cache.SetStringAsync("playlist:two", "2");
        await _cache.SetStringAsync("track:one", "3");

        Assert.Equal(
            ["playlist:one", "playlist:two"],
            _cache.GetKeysByPattern("playlist:*").Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(2, await _cache.DeleteByPatternAsync("playlist:*"));
        Assert.Equal("3", await _cache.GetStringAsync("track:one"));
    }

    [Fact]
    public async Task OversizedPayload_IsRejectedWithoutWriting()
    {
        var payload = new string('x', DatabaseApplicationCache.MaximumPayloadBytes + 1);

        Assert.False(await _cache.SetStringAsync("too-large", payload));

        await using var database = await _factory.CreateDbContextAsync();
        Assert.Empty(await database.ApplicationCacheEntries.ToListAsync());
    }

    [Theory]
    [InlineData("image:jellyfin:primary:track")]
    [InlineData("playlist:image:release-radar")]
    [InlineData("artwork:spotify:album")]
    [InlineData("cover:qobuz:playlist")]
    public async Task MediaPayloadKey_IsRejectedWithoutWriting(string key)
    {
        Assert.False(await _cache.SetStringAsync(key, "base64-or-binary-json"));

        await using var database = await _factory.CreateDbContextAsync();
        Assert.Empty(await database.ApplicationCacheEntries.ToListAsync());
    }

    [Fact]
    public async Task CacheEntity_HasNoDurableForeignKeys()
    {
        await using var database = await _factory.CreateDbContextAsync();
        var entity = database.Model.FindEntityType(typeof(ApplicationCacheEntryRecord));

        Assert.NotNull(entity);
        Assert.Empty(entity!.GetForeignKeys());
    }

    [Fact]
    public async Task DisabledCategory_RejectsAccessAndCleanupRemovesExistingEntries()
    {
        Assert.True(await _cache.SetStringAsync("lyrics:disabled-fixture", "lyrics"));

        var disabledSettings = new CacheSettings
        {
            CategoryEnabled = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                [nameof(ApplicationCacheCategory.Lyrics)] = false
            }
        };
        var disabledCache = new DatabaseApplicationCache(
            _factory,
            _clock,
            NullLogger<DatabaseApplicationCache>.Instance,
            Options.Create(disabledSettings));

        Assert.Null(await disabledCache.GetStringAsync("lyrics:disabled-fixture"));
        Assert.False(await disabledCache.ExistsAsync("lyrics:disabled-fixture"));
        Assert.False(await disabledCache.SetStringAsync("lyrics:new", "blocked"));
        Assert.Equal(1, await disabledCache.CleanupPolicyOverflowAsync());

        await using var database = await _factory.CreateDbContextAsync();
        Assert.Empty(await database.ApplicationCacheEntries.ToListAsync());
    }

    [Fact]
    public async Task CleanupPolicyOverflow_EnforcesConfiguredByteQuotaOldestFirst()
    {
        var settings = new CacheSettings
        {
            CategoryMaximumMegabytes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                [nameof(ApplicationCacheCategory.ProviderResponse)] = 1
            }
        };
        var cache = new DatabaseApplicationCache(
            _factory,
            _clock,
            NullLogger<DatabaseApplicationCache>.Instance,
            Options.Create(settings));
        var payload = new string('x', 600 * 1024);

        Assert.True(await cache.SetStringAsync("provider:first", payload, TimeSpan.FromHours(1)));
        _clock.UtcNow = _clock.UtcNow.AddSeconds(1);
        Assert.True(await cache.SetStringAsync("provider:second", payload, TimeSpan.FromHours(1)));

        Assert.Equal(1, await cache.CleanupPolicyOverflowAsync());
        Assert.Null(await cache.GetStringAsync("provider:first"));
        Assert.Equal(payload, await cache.GetStringAsync("provider:second"));
    }

    [Fact]
    public async Task WritesPersistSemanticCategoryAndCleanupEnforcesConfiguredCountQuota()
    {
        var settings = new CacheSettings
        {
            CategoryMaximumEntries = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                [nameof(ApplicationCacheCategory.SearchResults)] = 2
            }
        };
        var cache = new DatabaseApplicationCache(
            _factory,
            _clock,
            NullLogger<DatabaseApplicationCache>.Instance,
            Options.Create(settings));

        Assert.True(await cache.SetStringAsync("search:first", "one", TimeSpan.FromHours(1)));
        _clock.UtcNow = _clock.UtcNow.AddSeconds(1);
        Assert.True(await cache.SetStringAsync("search:second", "two", TimeSpan.FromHours(1)));
        _clock.UtcNow = _clock.UtcNow.AddSeconds(1);
        Assert.True(await cache.SetStringAsync("search:third", "three", TimeSpan.FromHours(1)));
        Assert.True(await cache.SetStringAsync("lyrics:fixture", "lyrics", TimeSpan.FromHours(1)));

        Assert.Equal(1, await cache.CleanupPolicyOverflowAsync());

        await using var database = await _factory.CreateDbContextAsync();
        var entries = await database.ApplicationCacheEntries
            .OrderBy(item => item.Key)
            .ToListAsync();
        Assert.DoesNotContain(entries, item => item.Key == "search:first");
        Assert.Equal(2, entries.Count(item => item.Category == nameof(ApplicationCacheCategory.SearchResults)));
        Assert.Contains(entries, item =>
            item.Key == "lyrics:fixture" &&
            item.Category == nameof(ApplicationCacheCategory.Lyrics));
    }

    public async Task DisposeAsync()
    {
        if (_database is not null) await _database.DisposeAsync();
    }

    private sealed class TestClock(DateTimeOffset now) : IPlatformClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
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
