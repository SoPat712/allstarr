using System.Text;
using System.Text.Json;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using allstarr.Models.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace allstarr.Services.Common;

/// <summary>
/// Disposable database-backed cache. No cache entry owns or references durable records.
/// Every operation fails open so storage pressure cannot change application decisions.
/// </summary>
public sealed class DatabaseApplicationCache(
    IDbContextFactory<AllstarrDbContext> contextFactory,
    IPlatformClock clock,
    ILogger<DatabaseApplicationCache> logger,
    IOptions<CacheSettings>? configuredSettings = null) : IApplicationCache
{
    public const int MaximumKeyCharacters = 512;
    public const int MaximumPayloadBytes = 1024 * 1024;
    public const int DefaultCleanupBatchSize = 1000;
    private long _hits;
    private long _misses;
    private long _writes;
    private long _evictions;
    private readonly CacheSettings _settings = configuredSettings?.Value ?? new CacheSettings();

    public bool IsEnabled => true;

    public async Task<ApplicationCacheTierUsage> GetUsageAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var database = await contextFactory.CreateDbContextAsync(cancellationToken);
            var active = database.Set<ApplicationCacheEntryRecord>()
                .AsNoTracking()
                .Where(item => item.ExpiresAt == null || item.ExpiresAt > clock.UtcNow);
            var entryCount = await active.LongCountAsync(cancellationToken);
            var payloadBytes = entryCount == 0
                ? 0
                : await active.SumAsync(item => (long)item.PayloadBytes, cancellationToken);
            return new ApplicationCacheTierUsage(
                "database",
                entryCount,
                payloadBytes,
                null,
                MaximumPayloadBytes,
                IsEnabled,
                Volatile.Read(ref _hits),
                Volatile.Read(ref _misses),
                Volatile.Read(ref _writes),
                Volatile.Read(ref _evictions));
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Database cache diagnostics failed");
            return new ApplicationCacheTierUsage(
                "database",
                0,
                0,
                null,
                MaximumPayloadBytes,
                IsEnabled,
                Volatile.Read(ref _hits),
                Volatile.Read(ref _misses),
                Volatile.Read(ref _writes),
                Volatile.Read(ref _evictions));
        }
    }

    public async Task<IReadOnlyDictionary<ApplicationCacheCategory, ApplicationCacheCategoryUsage>>
        GetCategoryUsageAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var database = await contextFactory.CreateDbContextAsync(cancellationToken);
            var rows = await database.Set<ApplicationCacheEntryRecord>()
                .AsNoTracking()
                .Where(item => item.ExpiresAt == null || item.ExpiresAt > clock.UtcNow)
                .GroupBy(item => item.Category)
                .Select(group => new
                {
                    Category = group.Key,
                    EntryCount = group.LongCount(),
                    PayloadBytes = group.Sum(item => (long)item.PayloadBytes)
                })
                .ToArrayAsync(cancellationToken);

            return rows
                .Select(row => new
                {
                    Parsed = Enum.TryParse<ApplicationCacheCategory>(
                        row.Category,
                        ignoreCase: true,
                        out var category)
                        ? category
                        : (ApplicationCacheCategory?)null,
                    row.EntryCount,
                    row.PayloadBytes
                })
                .Where(row => row.Parsed.HasValue)
                .ToDictionary(
                    row => row.Parsed!.Value,
                    row => new ApplicationCacheCategoryUsage(
                        row.EntryCount,
                        row.PayloadBytes));
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Database cache category diagnostics failed");
            return new Dictionary<ApplicationCacheCategory, ApplicationCacheCategoryUsage>();
        }
    }

    public async Task<string?> GetStringAsync(string key)
    {
        if (!IsValidKey(key) || !ApplicationCachePolicyRegistry.IsEnabled(key, _settings))
        {
            Interlocked.Increment(ref _misses);
            return null;
        }

        try
        {
            await using var database = await contextFactory.CreateDbContextAsync();
            var entry = await database.Set<ApplicationCacheEntryRecord>()
                .AsNoTracking()
                .SingleOrDefaultAsync(item => item.Key == key);
            if (entry is null)
            {
                Interlocked.Increment(ref _misses);
                return null;
            }

            if (entry.ExpiresAt is null || entry.ExpiresAt > clock.UtcNow)
            {
                Interlocked.Increment(ref _hits);
                return entry.Value;
            }

            await database.Set<ApplicationCacheEntryRecord>()
                .Where(item => item.Key == key)
                .ExecuteDeleteAsync();
            Interlocked.Increment(ref _misses);
            Interlocked.Increment(ref _evictions);
            return null;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Database cache GET failed for key {Key}", key);
            Interlocked.Increment(ref _misses);
            return null;
        }
    }

    public async Task<T?> GetAsync<T>(string key) where T : class
    {
        var value = await GetStringAsync(key);
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(value);
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception, "Database cache payload was invalid for key {Key}", key);
            return null;
        }
    }

    public async Task<bool> SetStringAsync(string key, string value, TimeSpan? expiry = null)
    {
        if (!IsValidKey(key) ||
            !ApplicationCachePayloadPolicy.IsDatabaseEligible(key) ||
            !ApplicationCachePolicyRegistry.IsEnabled(key, _settings))
        {
            return false;
        }

        var payloadBytes = Encoding.UTF8.GetByteCount(value);
        if (payloadBytes > MaximumPayloadBytes)
        {
            logger.LogWarning(
                "Database cache rejected {PayloadBytes} byte payload for key {Key}; limit is {Limit}",
                payloadBytes,
                key,
                MaximumPayloadBytes);
            return false;
        }

        var now = clock.UtcNow;
        DateTimeOffset? expiresAt = expiry.HasValue ? now.Add(expiry.Value) : null;
        var category = ApplicationCachePolicyRegistry.Classify(key).ToString();

        try
        {
            await using var database = await contextFactory.CreateDbContextAsync();
            var updated = await UpdateExistingAsync(
                database,
                key,
                value,
                payloadBytes,
                category,
                now,
                expiresAt);
            if (updated > 0)
            {
                Interlocked.Increment(ref _writes);
                return true;
            }

            database.Set<ApplicationCacheEntryRecord>().Add(new ApplicationCacheEntryRecord
            {
                Key = key,
                Category = category,
                Value = value,
                PayloadBytes = payloadBytes,
                CreatedAt = now,
                UpdatedAt = now,
                ExpiresAt = expiresAt
            });

            try
            {
                await database.SaveChangesAsync();
                Interlocked.Increment(ref _writes);
                return true;
            }
            catch (DbUpdateException)
            {
                database.ChangeTracker.Clear();
                var recovered = await UpdateExistingAsync(
                    database,
                    key,
                    value,
                    payloadBytes,
                    category,
                    now,
                    expiresAt) > 0;
                if (recovered)
                {
                    Interlocked.Increment(ref _writes);
                }

                return recovered;
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Database cache SET failed for key {Key}", key);
            return false;
        }
    }

    public async Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiry = null) where T : class
    {
        try
        {
            return await SetStringAsync(key, JsonSerializer.Serialize(value), expiry);
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception, "Database cache serialization failed for key {Key}", key);
            return false;
        }
    }

    public async Task<bool> DeleteAsync(string key)
    {
        if (!IsValidKey(key))
        {
            return false;
        }

        try
        {
            await using var database = await contextFactory.CreateDbContextAsync();
            var deleted = await database.Set<ApplicationCacheEntryRecord>()
                .Where(item => item.Key == key)
                .ExecuteDeleteAsync();
            Interlocked.Add(ref _evictions, deleted);
            return deleted > 0;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Database cache DELETE failed for key {Key}", key);
            return false;
        }
    }

    public async Task<bool> ExistsAsync(string key)
    {
        if (!IsValidKey(key) || !ApplicationCachePolicyRegistry.IsEnabled(key, _settings))
        {
            return false;
        }

        try
        {
            var now = clock.UtcNow;
            await using var database = await contextFactory.CreateDbContextAsync();
            return await database.Set<ApplicationCacheEntryRecord>()
                .AsNoTracking()
                .AnyAsync(item =>
                    item.Key == key &&
                    (item.ExpiresAt == null || item.ExpiresAt > now));
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Database cache EXISTS failed for key {Key}", key);
            return false;
        }
    }

    public IEnumerable<string> GetKeysByPattern(string pattern)
    {
        try
        {
            var now = clock.UtcNow;
            using var database = contextFactory.CreateDbContext();
            var likePattern = ToLikePattern(pattern);
            return database.Set<ApplicationCacheEntryRecord>()
                .AsNoTracking()
                .Where(item =>
                    (item.ExpiresAt == null || item.ExpiresAt > now) &&
                    EF.Functions.Like(item.Key, likePattern, "\\"))
                .Select(item => item.Key)
                .ToArray();
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Database cache key scan failed for pattern {Pattern}", pattern);
            return Array.Empty<string>();
        }
    }

    public async Task<int> DeleteByPatternAsync(string pattern)
    {
        try
        {
            await using var database = await contextFactory.CreateDbContextAsync();
            var likePattern = ToLikePattern(pattern);
            var deleted = await database.Set<ApplicationCacheEntryRecord>()
                .Where(item => EF.Functions.Like(item.Key, likePattern, "\\"))
                .ExecuteDeleteAsync();
            Interlocked.Add(ref _evictions, deleted);
            return deleted;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Database cache pattern delete failed for pattern {Pattern}", pattern);
            return 0;
        }
    }

    public async Task<int> CleanupExpiredAsync(
        int batchSize = DefaultCleanupBatchSize,
        CancellationToken cancellationToken = default)
    {
        var take = Math.Clamp(batchSize, 1, DefaultCleanupBatchSize);

        try
        {
            var now = clock.UtcNow;
            await using var database = await contextFactory.CreateDbContextAsync(cancellationToken);
            var keys = await database.Set<ApplicationCacheEntryRecord>()
                .AsNoTracking()
                .Where(item => item.ExpiresAt != null && item.ExpiresAt <= now)
                .OrderBy(item => item.ExpiresAt)
                .Select(item => item.Key)
                .Take(take)
                .ToArrayAsync(cancellationToken);
            if (keys.Length == 0)
            {
                return 0;
            }

            var deleted = await database.Set<ApplicationCacheEntryRecord>()
                .Where(item => keys.Contains(item.Key))
                .ExecuteDeleteAsync(cancellationToken);
            Interlocked.Add(ref _evictions, deleted);
            return deleted;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Database cache expiry cleanup failed");
            return 0;
        }
    }

    public async Task<int> CleanupPolicyOverflowAsync(
        int batchSize = DefaultCleanupBatchSize,
        CancellationToken cancellationToken = default)
    {
        var take = Math.Clamp(batchSize, 1, DefaultCleanupBatchSize);
        var deletedTotal = 0;

        try
        {
            await using var database = await contextFactory.CreateDbContextAsync(cancellationToken);
            foreach (var policy in ApplicationCachePolicyRegistry.All(_settings)
                         .Where(item => item.StorageTier == ApplicationCacheStorageTier.Metadata))
            {
                var category = policy.Category.ToString();
                if (!ApplicationCachePolicyRegistry.IsEnabled(policy.Category, _settings))
                {
                    var disabledKeys = await database.Set<ApplicationCacheEntryRecord>()
                        .AsNoTracking()
                        .Where(item => item.Category == category)
                        .OrderBy(item => item.UpdatedAt)
                        .ThenBy(item => item.Key)
                        .Select(item => item.Key)
                        .Take(take)
                        .ToArrayAsync(cancellationToken);
                    if (disabledKeys.Length > 0)
                    {
                        var disabledDeleted = await database.Set<ApplicationCacheEntryRecord>()
                            .Where(item => disabledKeys.Contains(item.Key))
                            .ExecuteDeleteAsync(cancellationToken);
                        deletedTotal += disabledDeleted;
                        Interlocked.Add(ref _evictions, disabledDeleted);
                    }

                    continue;
                }

                while (!cancellationToken.IsCancellationRequested)
                {
                    var active = database.Set<ApplicationCacheEntryRecord>()
                        .AsNoTracking()
                        .Where(item =>
                            item.Category == category &&
                            (item.ExpiresAt == null || item.ExpiresAt > clock.UtcNow));
                    var usage = await active
                        .GroupBy(_ => 1)
                        .Select(group => new
                        {
                            Count = group.LongCount(),
                            Bytes = group.Sum(item => (long)item.PayloadBytes)
                        })
                        .SingleOrDefaultAsync(cancellationToken);
                    if (usage == null ||
                        (usage.Count <= policy.MaximumEntries &&
                         usage.Bytes <= policy.MaximumBytes))
                    {
                        break;
                    }

                    var candidates = await active
                        .OrderBy(item => item.UpdatedAt)
                        .ThenBy(item => item.Key)
                        .Select(item => new { item.Key, item.PayloadBytes })
                        .Take(take)
                        .ToArrayAsync(cancellationToken);
                    if (candidates.Length == 0)
                    {
                        break;
                    }

                    var countOverflow = Math.Max(0, usage.Count - policy.MaximumEntries);
                    var byteOverflow = Math.Max(0, usage.Bytes - policy.MaximumBytes);
                    long reclaimedBytes = 0;
                    var deleteCount = 0;
                    foreach (var candidate in candidates)
                    {
                        deleteCount++;
                        reclaimedBytes += candidate.PayloadBytes;
                        if (deleteCount >= countOverflow && reclaimedBytes >= byteOverflow)
                        {
                            break;
                        }
                    }

                    var keys = candidates.Take(deleteCount).Select(item => item.Key).ToArray();
                    var deleted = await database.Set<ApplicationCacheEntryRecord>()
                        .Where(item => keys.Contains(item.Key))
                        .ExecuteDeleteAsync(cancellationToken);
                    deletedTotal += deleted;
                    Interlocked.Add(ref _evictions, deleted);
                    if (deleted == 0)
                    {
                        break;
                    }
                }
            }

            return deletedTotal;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Database cache policy cleanup failed");
            return deletedTotal;
        }
    }

    private static Task<int> UpdateExistingAsync(
        AllstarrDbContext database,
        string key,
        string value,
        int payloadBytes,
        string category,
        DateTimeOffset now,
        DateTimeOffset? expiresAt) =>
        database.Set<ApplicationCacheEntryRecord>()
            .Where(item => item.Key == key)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Value, value)
                .SetProperty(item => item.PayloadBytes, payloadBytes)
                .SetProperty(item => item.Category, category)
                .SetProperty(item => item.UpdatedAt, now)
                .SetProperty(item => item.ExpiresAt, expiresAt));

    private static bool IsValidKey(string key) =>
        !string.IsNullOrWhiteSpace(key) && key.Length <= MaximumKeyCharacters;

    private static string ToLikePattern(string pattern) =>
        pattern
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal)
            .Replace("*", "%", StringComparison.Ordinal)
            .Replace("?", "_", StringComparison.Ordinal);
}

public sealed class DatabaseApplicationCacheCleanupService(
    DatabaseApplicationCache cache,
    ILogger<DatabaseApplicationCacheCleanupService> logger) : BackgroundService
{
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CleanupInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var deleted = await cache.CleanupExpiredAsync(
                DatabaseApplicationCache.DefaultCleanupBatchSize,
                stoppingToken);
            deleted += await cache.CleanupPolicyOverflowAsync(
                DatabaseApplicationCache.DefaultCleanupBatchSize,
                stoppingToken);
            if (deleted > 0)
            {
                logger.LogDebug("Removed {Count} expired database cache entries", deleted);
            }
        }
    }
}
