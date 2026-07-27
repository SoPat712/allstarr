using allstarr.Core.Operations;
using allstarr.Core.Storage;
using allstarr.Models.Settings;
using allstarr.Services.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace allstarr.Tests;

public sealed class DatabaseApplicationCacheTests : IAsyncLifetime
{
    private PostgresTestDatabase _database = null!;
    private TestFactory _factory = null!;
    private TestClock _clock = null!;
    private DatabaseApplicationCache _cache = null!;
    private readonly List<string> _warnings = [];

    public async Task InitializeAsync()
    {
        _database = await PostgresTestDatabase.CreateAsync();
        _factory = new TestFactory(_database.Options);
        _clock = new TestClock(new DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero));
        _cache = new DatabaseApplicationCache(
            _factory,
            _clock,
            new WarningLogger(_warnings));

        await using var database = await _factory.CreateDbContextAsync();
        await database.Database.MigrateAsync();
    }

    [Fact]
    public async Task SetAndGet_OverwriteOneDisposableEntry()
    {
        Assert.True(await _cache.SetStringAsync("playback:metadata:v1:test:1", "first", TimeSpan.FromMinutes(5)));
        Assert.True(await _cache.SetStringAsync("playback:metadata:v1:test:1", "second", TimeSpan.FromMinutes(10)));

        Assert.Equal("second", await _cache.GetStringAsync("playback:metadata:v1:test:1"));

        await using var database = await _factory.CreateDbContextAsync();
        var entry = await database.ApplicationCacheEntries.SingleAsync();
        Assert.Equal("second", entry.Value);
        Assert.Equal(6, entry.PayloadBytes);
    }

    [Fact]
    public async Task ConcurrentWrites_UpsertOneDisposableEntry()
    {
        const string key = "jellyfin:item-type:v1:concurrent";
        var writes = await Task.WhenAll(Enumerable.Range(0, 32)
            .Select(index => _cache.SetStringAsync(key, $"value-{index}", TimeSpan.FromMinutes(5))));

        Assert.True(_warnings.Count == 0, _warnings.FirstOrDefault());
        Assert.All(writes, Assert.True);
        await using var database = await _factory.CreateDbContextAsync();
        Assert.Single(await database.ApplicationCacheEntries.Where(item => item.Key == key).ToListAsync());
    }

    [Fact]
    public async Task ReadsFlushOneSampledAccessTimestamp()
    {
        await _cache.SetStringAsync("search:v2:touched", "value", TimeSpan.FromHours(1));
        _clock.UtcNow = _clock.UtcNow.AddMinutes(5);

        Assert.Equal("value", await _cache.GetStringAsync("search:v2:touched"));
        Assert.Equal("value", await _cache.GetStringAsync("search:v2:touched"));
        Assert.Equal(1, await _cache.FlushAccessesAsync());

        await using var database = await _factory.CreateDbContextAsync();
        Assert.Equal(
            _clock.UtcNow,
            (await database.ApplicationCacheEntries.SingleAsync()).UpdatedAt);
        Assert.Equal(0, await _cache.FlushAccessesAsync());
    }

    [Fact]
    public async Task ExpiredEntry_IsAColdMissAndIsRemoved()
    {
        await _cache.SetStringAsync("odesli:translate:v2:expired:spotify", "asset", TimeSpan.FromMinutes(1));
        _clock.UtcNow = _clock.UtcNow.AddMinutes(2);

        Assert.Null(await _cache.GetStringAsync("odesli:translate:v2:expired:spotify"));
        Assert.False(await _cache.ExistsAsync("odesli:translate:v2:expired:spotify"));

        await using var database = await _factory.CreateDbContextAsync();
        Assert.Empty(await database.ApplicationCacheEntries.ToListAsync());
    }

    [Fact]
    public async Task CleanupExpired_IsBoundedAndLeavesLiveEntries()
    {
        await _cache.SetStringAsync("odesli:translate:v2:expired-1:spotify", "one", TimeSpan.FromMinutes(1));
        await _cache.SetStringAsync("odesli:translate:v2:expired-2:spotify", "two", TimeSpan.FromMinutes(1));
        await _cache.SetStringAsync("odesli:translate:v2:live:spotify", "live", TimeSpan.FromHours(1));
        _clock.UtcNow = _clock.UtcNow.AddMinutes(2);

        Assert.Equal(1, await _cache.CleanupExpiredAsync(batchSize: 1));

        await using var database = await _factory.CreateDbContextAsync();
        Assert.Equal(2, await database.ApplicationCacheEntries.CountAsync());
        Assert.True(await database.ApplicationCacheEntries.AnyAsync(item => item.Key == "odesli:translate:v2:live:spotify"));
    }

    [Fact]
    public async Task MaintenancePreview_ReportsAndCleanupRemovesOnlyDisposableEntries()
    {
        await _cache.SetStringAsync("search:v2:live", "live", TimeSpan.FromHours(1));
        await _cache.SetStringAsync("search:v2:expired", "expired", TimeSpan.FromMinutes(1));
        _clock.UtcNow = _clock.UtcNow.AddMinutes(2);

        await using (var database = await _factory.CreateDbContextAsync())
        {
            database.ApplicationCacheEntries.Add(new ApplicationCacheEntryRecord
            {
                Key = "lyrics:Artist:Title:Album:240",
                Category = ApplicationCacheCategory.Lyrics.ToString(),
                Value = "orphan",
                PayloadBytes = 6,
                CreatedAt = _clock.UtcNow,
                UpdatedAt = _clock.UtcNow,
                ExpiresAt = _clock.UtcNow.AddHours(1)
            });
            database.ApplicationCacheEntries.Add(new ApplicationCacheEntryRecord
            {
                Key = "abandoned:key",
                Category = ApplicationCacheCategory.ProviderResponse.ToString(),
                Value = "unknown",
                PayloadBytes = 7,
                CreatedAt = _clock.UtcNow,
                UpdatedAt = _clock.UtcNow,
                ExpiresAt = _clock.UtcNow.AddHours(1)
            });
            database.ApplicationCacheEntries.Add(new ApplicationCacheEntryRecord
            {
                Key = "media:descriptor:v3:global:shared:none:jellyfin:playlist:resource:0x0:broken",
                Category = ApplicationCacheCategory.CanonicalMetadata.ToString(),
                Value = "{broken",
                PayloadBytes = 7,
                CreatedAt = _clock.UtcNow,
                UpdatedAt = _clock.UtcNow,
                ExpiresAt = _clock.UtcNow.AddHours(1)
            });
            database.ApplicationCacheEntries.Add(new ApplicationCacheEntryRecord
            {
                Key = "search:v2:no-expiry",
                Category = ApplicationCacheCategory.SearchResults.ToString(),
                Value = "immortal",
                PayloadBytes = 8,
                CreatedAt = _clock.UtcNow,
                UpdatedAt = _clock.UtcNow
            });
            await database.SaveChangesAsync();
        }

        var preview = await _cache.PreviewMaintenanceAsync();
        Assert.Equal(6, preview.ScannedEntries);
        Assert.False(preview.ScanLimitReached);
        Assert.Equal(1, preview.ExpiredEntries);
        Assert.Equal(3, preview.UnknownOwnerEntries);
        Assert.Equal(1, preview.NoExpiryEntries);
        Assert.Equal(35, preview.ReclaimableBytes);

        Assert.Equal(1, await _cache.CleanupExpiredAsync());
        Assert.Equal(4, await _cache.CleanupInvalidOwnershipAsync());
        Assert.Equal("live", await _cache.GetStringAsync("search:v2:live"));

        await using var remaining = await _factory.CreateDbContextAsync();
        Assert.Single(await remaining.ApplicationCacheEntries.ToListAsync());
    }

    [Fact]
    public async Task MaintenanceRemovesStaleProviderAccountScopes()
    {
        var tenantId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();
        var accountId = Guid.CreateVersion7();
        await using (var database = await _factory.CreateDbContextAsync())
        {
            database.AddRange(
                new TenantRecord
                {
                    Id = tenantId,
                    Slug = "cache-scope",
                    Name = "Cache scope",
                    CreatedAt = _clock.UtcNow
                },
                new PlatformUserRecord
                {
                    Id = userId,
                    TenantId = tenantId,
                    DisplayName = "Cache scope",
                    Status = PlatformUserStatus.Active,
                    CreatedAt = _clock.UtcNow,
                    UpdatedAt = _clock.UtcNow
                },
                new ProviderAccountRecord
                {
                    Id = accountId,
                    TenantId = tenantId,
                    OwnerUserId = userId,
                    ProviderId = "spotify",
                    DisplayName = "Cache scope",
                    Scope = ProviderAccountScope.User,
                    Enabled = true,
                    Revision = 2,
                    CreatedAt = _clock.UtcNow,
                    UpdatedAt = _clock.UtcNow
                });
            await database.SaveChangesAsync();
        }

        var current = CacheKeyBuilder.BuildProviderPlaylistDiscoveryKey(
            tenantId, userId, accountId, 2, "spotify", null, null, 100);
        var stale = CacheKeyBuilder.BuildProviderPlaylistDiscoveryKey(
            tenantId, userId, accountId, 1, "spotify", null, null, 100);
        Assert.True(await _cache.SetStringAsync(current, "current"));
        Assert.True(await _cache.SetStringAsync(stale, "stale"));

        Assert.Equal(1, (await _cache.PreviewMaintenanceAsync()).StaleAuthorizationScopeEntries);
        Assert.Equal(1, await _cache.CleanupInvalidOwnershipAsync());
        Assert.Equal("current", await _cache.GetStringAsync(current));
        Assert.Null(await _cache.GetStringAsync(stale));
    }

    [Fact]
    public async Task MaintenanceRemovesOlderArtworkRevisionsDeterministically()
    {
        var first = CacheKeyBuilder.BuildMediaAssetDescriptorKey(new(
            null, null, null, "jellyfin", "playlist", "playlist-1", "revision-1", 96, 96));
        var second = CacheKeyBuilder.BuildMediaAssetDescriptorKey(new(
            null, null, null, "jellyfin", "playlist", "playlist-1", "revision-2", 96, 96));
        const string descriptor = """{"PayloadKey":"artwork:payload:v1:fixture"}""";
        Assert.True(await _cache.SetStringAsync(first, descriptor, TimeSpan.FromHours(1)));
        _clock.UtcNow = _clock.UtcNow.AddSeconds(1);
        Assert.True(await _cache.SetStringAsync(second, descriptor, TimeSpan.FromHours(1)));

        Assert.Equal(1, (await _cache.PreviewMaintenanceAsync()).SupersededEntries);
        Assert.Equal(1, await _cache.CleanupSupersededArtworkDescriptorsAsync());
        Assert.Null(await _cache.GetStringAsync(first));
        Assert.Equal(descriptor, await _cache.GetStringAsync(second));
    }

    [Fact]
    public async Task PatternOperations_UseRedisCompatibleWildcards()
    {
        await _cache.SetStringAsync("odesli:translate:v2:playlist-one:spotify", "1");
        await _cache.SetStringAsync("odesli:translate:v2:playlist-two:spotify", "2");
        await _cache.SetStringAsync("odesli:translate:v2:track-one:spotify", "3");

        Assert.Equal(2, await _cache.DeleteByPatternAsync("odesli:translate:v2:playlist-*:spotify"));
        Assert.Equal("3", await _cache.GetStringAsync("odesli:translate:v2:track-one:spotify"));
    }

    [Fact]
    public async Task OversizedPayload_IsRejectedWithoutWriting()
    {
        var payload = new string('x', DatabaseApplicationCache.MaximumPayloadBytes + 1);

        Assert.False(await _cache.SetStringAsync("odesli:translate:v2:too-large:spotify", payload));

        await using var database = await _factory.CreateDbContextAsync();
        Assert.Empty(await database.ApplicationCacheEntries.ToListAsync());
    }

    [Fact]
    public async Task MediaPayloadKey_IsRejectedWithoutWriting()
    {
        const string key = "artwork:payload:v1:fixture";
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
        Assert.True(await _cache.SetStringAsync("lyrics:v2:disabled-fixture", "lyrics"));

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

        Assert.Null(await disabledCache.GetStringAsync("lyrics:v2:disabled-fixture"));
        Assert.False(await disabledCache.ExistsAsync("lyrics:v2:disabled-fixture"));
        Assert.False(await disabledCache.SetStringAsync("lyrics:v2:new", "blocked"));
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

        Assert.True(await cache.SetStringAsync("odesli:translate:v2:first:spotify", payload, TimeSpan.FromHours(1)));
        _clock.UtcNow = _clock.UtcNow.AddSeconds(1);
        Assert.True(await cache.SetStringAsync("odesli:translate:v2:second:spotify", payload, TimeSpan.FromHours(1)));

        Assert.Equal(1, await cache.CleanupPolicyOverflowAsync());
        Assert.Null(await cache.GetStringAsync("odesli:translate:v2:first:spotify"));
        Assert.Equal(payload, await cache.GetStringAsync("odesli:translate:v2:second:spotify"));
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

        Assert.True(await cache.SetStringAsync("search:v2:first", "one", TimeSpan.FromHours(1)));
        _clock.UtcNow = _clock.UtcNow.AddSeconds(1);
        Assert.True(await cache.SetStringAsync("search:v2:second", "two", TimeSpan.FromHours(1)));
        _clock.UtcNow = _clock.UtcNow.AddSeconds(1);
        Assert.True(await cache.SetStringAsync("search:v2:third", "three", TimeSpan.FromHours(1)));
        Assert.True(await cache.SetStringAsync("lyrics:v2:fixture", "lyrics", TimeSpan.FromHours(1)));

        Assert.Equal(1, await cache.CleanupPolicyOverflowAsync());

        await using var database = await _factory.CreateDbContextAsync();
        var entries = await database.ApplicationCacheEntries
            .OrderBy(item => item.Key)
            .ToListAsync();
        Assert.DoesNotContain(entries, item => item.Key == "search:v2:first");
        Assert.Equal(2, entries.Count(item => item.Category == nameof(ApplicationCacheCategory.SearchResults)));
        Assert.Contains(entries, item =>
            item.Key == "lyrics:v2:fixture" &&
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

    private sealed class WarningLogger(List<string> warnings) : ILogger<DatabaseApplicationCache>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) warnings.Add($"{formatter(state, exception)}: {exception}");
        }
    }
}
