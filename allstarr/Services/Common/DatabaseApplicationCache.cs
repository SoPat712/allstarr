using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<string, byte> _accessed = new(StringComparer.Ordinal);
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
        if (!IsValidKey(key) ||
            !ApplicationCachePolicyRegistry.TryClassify(key, out _) ||
            !ApplicationCachePolicyRegistry.IsEnabled(key, _settings))
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
                RecordAccess(key);
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
            !ApplicationCachePolicyRegistry.TryClassify(key, out _) ||
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
        var effectiveExpiry = expiry ?? ApplicationCachePolicyRegistry.Resolve(key, _settings).FreshFor;
        var expiresAt = now.Add(effectiveExpiry);
        var category = ApplicationCachePolicyRegistry.Classify(key).ToString();
        var nowTicks = now.UtcTicks;
        var expiresAtTicks = expiresAt.UtcTicks;

        try
        {
            await using var database = await contextFactory.CreateDbContextAsync();
            var written = await database.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO application_cache_entries
                    ("Key", "Category", "Value", "PayloadBytes", "CreatedAt", "UpdatedAt", "ExpiresAt")
                VALUES
                    ({key}, {category}, {value}, {payloadBytes}, {nowTicks}, {nowTicks}, {expiresAtTicks})
                ON CONFLICT ("Key") DO UPDATE SET
                    "Category" = EXCLUDED."Category",
                    "Value" = EXCLUDED."Value",
                    "PayloadBytes" = EXCLUDED."PayloadBytes",
                    "UpdatedAt" = EXCLUDED."UpdatedAt",
                    "ExpiresAt" = EXCLUDED."ExpiresAt"
                """);
            if (written > 0)
            {
                Interlocked.Increment(ref _writes);
                return true;
            }

            return false;
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
        if (!IsValidKey(key) ||
            !ApplicationCachePolicyRegistry.TryClassify(key, out _) ||
            !ApplicationCachePolicyRegistry.IsEnabled(key, _settings))
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

    public async Task<int> PurgeAllAsync()
    {
        try
        {
            await using var database = await contextFactory.CreateDbContextAsync();
            var deleted = await database.Set<ApplicationCacheEntryRecord>().ExecuteDeleteAsync();
            Interlocked.Add(ref _evictions, deleted);
            return deleted;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Database cache purge failed");
            return 0;
        }
    }

    public async Task<int> DeleteCategoryAsync(ApplicationCacheCategory category)
    {
        try
        {
            await using var database = await contextFactory.CreateDbContextAsync();
            var name = category.ToString();
            var deleted = await database.Set<ApplicationCacheEntryRecord>()
                .Where(item => item.Category == name)
                .ExecuteDeleteAsync();
            Interlocked.Add(ref _evictions, deleted);
            return deleted;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Database cache category purge failed for {Category}", category);
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

    public void RecordAccess(string key)
    {
        if (IsValidKey(key))
        {
            _accessed[key] = 0;
        }
    }

    public async Task<int> FlushAccessesAsync(
        int batchSize = DefaultCleanupBatchSize,
        CancellationToken cancellationToken = default)
    {
        var keys = _accessed.Keys
            .Order(StringComparer.Ordinal)
            .Take(Math.Clamp(batchSize, 1, DefaultCleanupBatchSize))
            .ToArray();
        if (keys.Length == 0)
        {
            return 0;
        }

        try
        {
            await using var database = await contextFactory.CreateDbContextAsync(cancellationToken);
            var updated = await database.Set<ApplicationCacheEntryRecord>()
                .Where(item => keys.Contains(item.Key))
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(item => item.UpdatedAt, clock.UtcNow),
                    cancellationToken);
            foreach (var key in keys)
            {
                _accessed.TryRemove(key, out _);
            }

            return updated;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Database cache access flush failed");
            return 0;
        }
    }

    public async Task<DatabaseCacheMaintenancePreview> PreviewMaintenanceAsync(
        int batchSize = DefaultCleanupBatchSize,
        CancellationToken cancellationToken = default)
    {
        var take = Math.Clamp(batchSize, 1, DefaultCleanupBatchSize);
        try
        {
            await using var database = await contextFactory.CreateDbContextAsync(cancellationToken);
            var rows = await database.Set<ApplicationCacheEntryRecord>()
                .AsNoTracking()
                .OrderBy(item => item.UpdatedAt)
                .ThenBy(item => item.Key)
                .Take(take + 1)
                .ToArrayAsync(cancellationToken);
            var scanned = rows.Take(take).ToArray();
            var accounts = await ReadAccountScopesAsync(database, scanned, cancellationToken);
            var expired = scanned.Where(item =>
                item.ExpiresAt is not null && item.ExpiresAt <= clock.UtcNow).ToArray();
            var unknown = scanned.Where(HasUnknownOwner).ToArray();
            var disabled = scanned.Where(item =>
                !HasUnknownOwner(item) &&
                !ApplicationCachePolicyRegistry.IsEnabled(
                    Enum.Parse<ApplicationCacheCategory>(item.Category, ignoreCase: true),
                    _settings)).ToArray();
            var noExpiry = scanned.Where(item => item.ExpiresAt is null).ToArray();
            var staleScopes = scanned.Where(item =>
                !HasUnknownOwner(item) &&
                HasStaleAuthorizationScope(item.Key, accounts)).ToArray();
            var superseded = FindSupersededArtworkDescriptors(
                scanned.Except(expired).Except(unknown).Except(disabled)
                    .Except(noExpiry).Except(staleScopes)).ToArray();
            var active = scanned
                .Except(expired)
                .Except(unknown)
                .Except(disabled)
                .Except(noExpiry)
                .Except(staleScopes)
                .Except(superseded)
                .ToArray();
            var overQuota = new List<ApplicationCacheEntryRecord>();
            foreach (var group in active.GroupBy(item =>
                         Enum.Parse<ApplicationCacheCategory>(item.Category, ignoreCase: true)))
            {
                var policy = ApplicationCachePolicyRegistry.Resolve(group.Key, _settings);
                var entries = group.OrderBy(item => item.UpdatedAt).ThenBy(item => item.Key).ToArray();
                var count = entries.LongLength;
                var bytes = entries.Sum(item => (long)item.PayloadBytes);
                foreach (var entry in entries)
                {
                    if (count <= policy.MaximumEntries && bytes <= policy.MaximumBytes)
                    {
                        break;
                    }

                    overQuota.Add(entry);
                    count--;
                    bytes -= entry.PayloadBytes;
                }
            }

            var reclaimable = expired
                .Concat(unknown)
                .Concat(disabled)
                .Concat(noExpiry)
                .Concat(staleScopes)
                .Concat(superseded)
                .Concat(overQuota)
                .DistinctBy(item => item.Key)
                .Sum(item => (long)item.PayloadBytes);
            return new DatabaseCacheMaintenancePreview(
                scanned.Length,
                rows.Length > take,
                expired.Length,
                unknown.Length,
                disabled.Length,
                noExpiry.Length,
                staleScopes.Length,
                superseded.Length,
                overQuota.Count,
                reclaimable,
                clock.UtcNow);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Database cache maintenance preview failed");
            return new DatabaseCacheMaintenancePreview(
                0, false, 0, 0, 0, 0, 0, 0, 0, 0, clock.UtcNow);
        }
    }

    public async Task<int> CleanupInvalidOwnershipAsync(
        int batchSize = DefaultCleanupBatchSize,
        CancellationToken cancellationToken = default)
    {
        var take = Math.Clamp(batchSize, 1, DefaultCleanupBatchSize);
        try
        {
            await using var database = await contextFactory.CreateDbContextAsync(cancellationToken);
            var candidates = await database.Set<ApplicationCacheEntryRecord>()
                .AsNoTracking()
                .OrderBy(item => item.UpdatedAt)
                .ThenBy(item => item.Key)
                .Take(take)
                .ToArrayAsync(cancellationToken);
            var accounts = await ReadAccountScopesAsync(database, candidates, cancellationToken);
            var keys = candidates
                .Where(item =>
                    HasUnknownOwner(item) ||
                    item.ExpiresAt is null ||
                    HasStaleAuthorizationScope(item.Key, accounts) ||
                    !ApplicationCachePolicyRegistry.IsEnabled(
                        Enum.Parse<ApplicationCacheCategory>(item.Category, ignoreCase: true),
                        _settings))
                .Select(item => item.Key)
                .ToArray();
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
            logger.LogWarning(exception, "Database cache ownership cleanup failed");
            return 0;
        }
    }

    public async Task<ArtworkPayloadReferenceSnapshot> GetArtworkPayloadReferencesAsync(
        int batchSize = 10_000,
        CancellationToken cancellationToken = default)
    {
        var take = Math.Clamp(batchSize, 1, 10_000);
        try
        {
            await using var database = await contextFactory.CreateDbContextAsync(cancellationToken);
            var values = await database.Set<ApplicationCacheEntryRecord>()
                .AsNoTracking()
                .Where(item =>
                    item.Key.StartsWith("media:descriptor:v3:") &&
                    (item.ExpiresAt == null || item.ExpiresAt > clock.UtcNow))
                .OrderBy(item => item.Key)
                .Select(item => item.Value)
                .Take(take + 1)
                .ToArrayAsync(cancellationToken);
            var references = values.Take(take)
                .Select(ReadArtworkPayloadKey)
                .ToArray();
            return new(
                references
                    .Where(item => item != null)
                    .Cast<string>()
                    .ToHashSet(StringComparer.Ordinal),
                values.Length > take || references.Any(item => item == null));
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Artwork payload reference scan failed");
            return new(new HashSet<string>(StringComparer.Ordinal), true);
        }
    }

    public async Task<int> CleanupSupersededArtworkDescriptorsAsync(
        int batchSize = DefaultCleanupBatchSize,
        CancellationToken cancellationToken = default)
    {
        var take = Math.Clamp(batchSize, 1, DefaultCleanupBatchSize);
        try
        {
            await using var database = await contextFactory.CreateDbContextAsync(cancellationToken);
            var descriptors = await database.Set<ApplicationCacheEntryRecord>()
                .AsNoTracking()
                .Where(item =>
                    item.Key.StartsWith("media:descriptor:v3:") &&
                    (item.ExpiresAt == null || item.ExpiresAt > clock.UtcNow))
                .OrderBy(item => item.UpdatedAt)
                .ThenBy(item => item.Key)
                .Take(10_000)
                .ToArrayAsync(cancellationToken);
            var keys = FindSupersededArtworkDescriptors(descriptors)
                .Take(take)
                .Select(item => item.Key)
                .ToArray();
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
            logger.LogWarning(exception, "Superseded artwork descriptor cleanup failed");
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

    private static bool IsValidKey(string key) =>
        !string.IsNullOrWhiteSpace(key) && key.Length <= MaximumKeyCharacters;

    private static bool HasUnknownOwner(ApplicationCacheEntryRecord item) =>
        !ApplicationCachePolicyRegistry.TryClassify(item.Key, out var expectedCategory) ||
        !Enum.TryParse<ApplicationCacheCategory>(item.Category, ignoreCase: true, out var category) ||
        !Enum.IsDefined(category) ||
        ApplicationCachePolicyRegistry.Resolve(category).StorageTier != ApplicationCacheStorageTier.Metadata ||
        expectedCategory != category ||
        (CacheKeyBuilder.IsMediaAssetDescriptorKey(item.Key) &&
         ReadArtworkPayloadKey(item.Value) == null);

    private static async Task<IReadOnlyDictionary<Guid, CacheAccountScope>> ReadAccountScopesAsync(
        AllstarrDbContext database,
        IEnumerable<ApplicationCacheEntryRecord> entries,
        CancellationToken cancellationToken)
    {
        var ids = entries
            .Select(item => ReadScopedAccountId(item.Key))
            .Where(item => item.HasValue)
            .Select(item => item!.Value)
            .Distinct()
            .ToArray();
        return ids.Length == 0
            ? new Dictionary<Guid, CacheAccountScope>()
            : await database.ProviderAccounts
                .AsNoTracking()
                .Where(item => ids.Contains(item.Id))
                .ToDictionaryAsync(
                    item => item.Id,
                    item => new CacheAccountScope(
                        item.TenantId,
                        item.OwnerUserId,
                        item.ProviderId,
                        item.Revision,
                        item.Enabled),
                    cancellationToken);
    }

    private static Guid? ReadScopedAccountId(string key)
    {
        var parts = key.Split(':');
        var account = parts.Length switch
        {
            9 when key.StartsWith("playlist:discovery:v2:", StringComparison.Ordinal) => parts[5],
            11 when CacheKeyBuilder.IsMediaAssetDescriptorKey(key) => parts[5],
            _ => null
        };
        return Guid.TryParseExact(account, "N", out var id) ? id : null;
    }

    private static bool HasStaleAuthorizationScope(
        string key,
        IReadOnlyDictionary<Guid, CacheAccountScope> accounts)
    {
        var accountId = ReadScopedAccountId(key);
        if (!accountId.HasValue) return false;
        if (!accounts.TryGetValue(accountId.Value, out var account) || !account.Enabled) return true;

        var parts = key.Split(':');
        var provider = parts.Length == 9 ? parts[7] : parts[6];
        if (!parts[3].Equals(account.TenantId?.ToString("N") ?? "global", StringComparison.Ordinal) ||
            !parts[4].Equals(account.OwnerUserId?.ToString("N") ?? "shared", StringComparison.Ordinal) ||
            !provider.Equals(account.ProviderId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return parts.Length == 9 &&
               (!long.TryParse(parts[6], out var revision) || revision != account.Revision);
    }

    private sealed record CacheAccountScope(
        Guid? TenantId,
        Guid? OwnerUserId,
        string ProviderId,
        long Revision,
        bool Enabled);

    private static string? ReadArtworkPayloadKey(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Name.Equals("PayloadKey", StringComparison.OrdinalIgnoreCase))
                {
                    return property.Value.GetString();
                }
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static IEnumerable<ApplicationCacheEntryRecord> FindSupersededArtworkDescriptors(
        IEnumerable<ApplicationCacheEntryRecord> entries) =>
        entries
            .Where(item => CacheKeyBuilder.IsMediaAssetDescriptorKey(item.Key))
            .GroupBy(item => item.Key[..item.Key.LastIndexOf(':')], StringComparer.Ordinal)
            .SelectMany(group => group
                .OrderByDescending(item => item.UpdatedAt)
                .ThenByDescending(item => item.Key, StringComparer.Ordinal)
                .Skip(1));

    private static string ToLikePattern(string pattern) =>
        pattern
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal)
            .Replace("*", "%", StringComparison.Ordinal)
            .Replace("?", "_", StringComparison.Ordinal);
}
