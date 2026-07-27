using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace allstarr.Services.Common;

/// <summary>
/// Small write-through process cache in front of the disposable database cache.
/// Database reads are intentionally not promoted: that keeps restart behavior cold and
/// prevents broad scans from filling process memory.
/// </summary>
public sealed class BoundedHotApplicationCache : IApplicationCache, IDisposable
{
    public const long MaximumBytes = 16L * 1024 * 1024;
    public const int MaximumEntryBytes = 256 * 1024;
    public static readonly TimeSpan MaximumResidence = TimeSpan.FromMinutes(5);

    private readonly DatabaseApplicationCache _database;
    private readonly MemoryCache _memory = new(new MemoryCacheOptions
    {
        SizeLimit = MaximumBytes
    });
    private readonly object _residentGate = new();
    private readonly Dictionary<string, HotEntry> _residents = new(StringComparer.Ordinal);
    private long _residentBytes;
    private long _hits;
    private long _misses;
    private long _writes;
    private long _evictions;

    public BoundedHotApplicationCache(DatabaseApplicationCache database)
    {
        _database = database;
    }

    public bool IsEnabled => _database.IsEnabled;

    public async Task<string?> GetStringAsync(string key)
    {
        if (_memory.TryGetValue(key, out HotEntry? entry))
        {
            Interlocked.Increment(ref _hits);
            return entry?.Value;
        }

        Interlocked.Increment(ref _misses);
        return await _database.GetStringAsync(key);
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
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<bool> SetStringAsync(string key, string value, TimeSpan? expiry = null)
    {
        var stored = await _database.SetStringAsync(key, value, expiry);
        if (!stored)
        {
            return false;
        }

        var payloadBytes = Encoding.UTF8.GetByteCount(value);
        if (payloadBytes <= MaximumEntryBytes && (!expiry.HasValue || expiry.Value > TimeSpan.Zero))
        {
            var residence = expiry.HasValue && expiry.Value < MaximumResidence
                ? expiry.Value
                : MaximumResidence;
            var entry = new HotEntry(value, Math.Max(1, payloadBytes));
            AddResident(key, entry);
            var options = new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = residence,
                Size = entry.PayloadBytes
            };
            options.RegisterPostEvictionCallback(static (evictedKey, evictedValue, _, state) =>
            {
                if (state is BoundedHotApplicationCache cache &&
                    evictedKey is string key &&
                    evictedValue is HotEntry removed)
                {
                    cache.RemoveResident(key, removed);
                }
            }, this);
            _memory.Set(key, entry, options);
            Interlocked.Increment(ref _writes);
        }

        return true;
    }

    public Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiry = null) where T : class
    {
        try
        {
            return SetStringAsync(key, JsonSerializer.Serialize(value), expiry);
        }
        catch (JsonException)
        {
            return Task.FromResult(false);
        }
    }

    public async Task<bool> DeleteAsync(string key)
    {
        _memory.Remove(key);
        return await _database.DeleteAsync(key);
    }

    public async Task<bool> ExistsAsync(string key)
    {
        if (_memory.TryGetValue(key, out _))
        {
            return true;
        }

        return await _database.ExistsAsync(key);
    }

    public IEnumerable<string> GetKeysByPattern(string pattern) =>
        _database.GetKeysByPattern(pattern);

    public async Task<int> DeleteByPatternAsync(string pattern)
    {
        _memory.Clear();
        Interlocked.Add(ref _evictions, ClearResidents());
        return await _database.DeleteByPatternAsync(pattern);
    }

    public Task<ApplicationCacheTierUsage> GetDatabaseUsageAsync(
        CancellationToken cancellationToken = default) =>
        _database.GetUsageAsync(cancellationToken);

    public Task<IReadOnlyDictionary<ApplicationCacheCategory, ApplicationCacheCategoryUsage>>
        GetDatabaseCategoryUsageAsync(CancellationToken cancellationToken = default) =>
        _database.GetCategoryUsageAsync(cancellationToken);

    public Task<DatabaseCacheMaintenancePreview> PreviewDatabaseMaintenanceAsync(
        CancellationToken cancellationToken = default) =>
        _database.PreviewMaintenanceAsync(cancellationToken: cancellationToken);

    public async Task<int> CleanupDatabaseAsync(CancellationToken cancellationToken = default)
    {
        _memory.Clear();
        Interlocked.Add(ref _evictions, ClearResidents());
        var deleted = await _database.CleanupExpiredAsync(
            cancellationToken: cancellationToken);
        deleted += await _database.CleanupInvalidOwnershipAsync(
            cancellationToken: cancellationToken);
        deleted += await _database.CleanupPolicyOverflowAsync(
            cancellationToken: cancellationToken);
        return deleted;
    }

    public ApplicationCacheTierUsage GetUsageSnapshot()
    {
        lock (_residentGate)
        {
            return new ApplicationCacheTierUsage(
                "hot",
                _residents.Count,
                _residentBytes,
                MaximumBytes,
                MaximumEntryBytes,
                IsEnabled,
                Volatile.Read(ref _hits),
                Volatile.Read(ref _misses),
                Volatile.Read(ref _writes),
                Volatile.Read(ref _evictions));
        }
    }

    public void Dispose()
    {
        _memory.Dispose();
        ClearResidents();
    }

    private void AddResident(string key, HotEntry entry)
    {
        lock (_residentGate)
        {
            if (_residents.TryGetValue(key, out var existing))
            {
                _residentBytes -= existing.PayloadBytes;
            }

            _residents[key] = entry;
            _residentBytes += entry.PayloadBytes;
        }
    }

    private void RemoveResident(string key, HotEntry entry)
    {
        lock (_residentGate)
        {
            if (!_residents.TryGetValue(key, out var current) ||
                !ReferenceEquals(current, entry))
            {
                return;
            }

            _residents.Remove(key);
            _residentBytes -= entry.PayloadBytes;
            Interlocked.Increment(ref _evictions);
        }
    }

    private int ClearResidents()
    {
        lock (_residentGate)
        {
            var removed = _residents.Count;
            _residents.Clear();
            _residentBytes = 0;
            return removed;
        }
    }

    private sealed record HotEntry(string Value, int PayloadBytes);
}
