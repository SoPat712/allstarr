using System.Text;
using System.Text.Json;
using allstarr.Core.Operations;
using allstarr.Models.Settings;
using Microsoft.Extensions.Options;

namespace allstarr.Services.Common;

/// <summary>Bounded, disposable metadata cache with absolute expiry and least-recently-used eviction.</summary>
public sealed class MemoryApplicationCache(
    IPlatformClock clock,
    IOptions<CacheSettings>? configuredSettings = null) : IApplicationCache, IDisposable
{
    public const long MaximumBytes = 16L * 1024 * 1024;
    public const int MaximumEntryBytes = 1024 * 1024;
    public const int MaximumEntries = 10_000;
    private readonly CacheSettings _settings = configuredSettings?.Value ?? new();
    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> _recent = new();
    private long _bytes, _hits, _misses, _writes, _evictions;
    private DateTimeOffset _nextExpiryScan;
    private readonly Dictionary<ApplicationCacheCategory, (int Count, long Bytes)> _categoryUsage = new();

    public bool IsEnabled => true;

    public Task<string?> GetStringAsync(string key)
    {
        lock (_gate)
        {
            if (CanStore(key) && _entries.TryGetValue(key, out var node))
            {
                if (node.Value.ExpiresAt > clock.UtcNow)
                {
                    _recent.Remove(node);
                    _recent.AddLast(node);
                    _hits++;
                    return Task.FromResult<string?>(node.Value.Value);
                }
                Remove(node);
            }
            _misses++;
            return Task.FromResult<string?>(null);
        }
    }

    public async Task<T?> GetAsync<T>(string key) where T : class
    {
        var value = await GetStringAsync(key);
        try { return value is null ? null : JsonSerializer.Deserialize<T>(value); }
        catch (JsonException) { return null; }
    }

    public Task<bool> SetStringAsync(string key, string value, TimeSpan? expiry = null)
    {
        if (!CanStore(key) || value is null) return Task.FromResult(false);
        var bytes = Encoding.UTF8.GetByteCount(value);
        if (bytes > MaximumEntryBytes) return Task.FromResult(false);
        var policy = ApplicationCachePolicyRegistry.Resolve(key, _settings);
        var now = clock.UtcNow;
        DateTimeOffset expiresAt;
        try { expiresAt = now.Add(expiry ?? policy.FreshFor); }
        catch (ArgumentOutOfRangeException) { return Task.FromResult(false); }

        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var existing)) Remove(existing);
            if (now >= _nextExpiryScan)
            {
                RemoveWhere(entry => entry.ExpiresAt <= now);
                _nextExpiryScan = now.AddMinutes(1);
            }
            if (expiresAt <= now) return Task.FromResult(true);
            var entry = new Entry(key, value, bytes, policy.Category, expiresAt, now);
            _entries.Add(key, _recent.AddLast(entry));
            _bytes += bytes;
            _writes++;
            var usage = _categoryUsage.GetValueOrDefault(policy.Category);
            _categoryUsage[policy.Category] = (++usage.Count, usage.Bytes += bytes);
            if (usage.Count > policy.MaximumEntries || usage.Bytes > policy.MaximumBytes)
            {
                foreach (var candidate in _recent.Where(item => item.Category == policy.Category).ToArray())
                {
                    usage = _categoryUsage.GetValueOrDefault(policy.Category);
                    if (usage.Count <= policy.MaximumEntries && usage.Bytes <= policy.MaximumBytes) break;
                    Remove(_entries[candidate.Key]);
                }
            }
            while (_bytes > MaximumBytes || _entries.Count > MaximumEntries) Remove(_recent.First!);
            return Task.FromResult(true);
        }
    }

    public Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiry = null) where T : class
    {
        try { return SetStringAsync(key, JsonSerializer.Serialize(value), expiry); }
        catch (JsonException) { return Task.FromResult(false); }
    }

    public Task<bool> DeleteAsync(string key)
    {
        lock (_gate)
        {
            if (!ApplicationCachePayloadPolicy.IsValidKey(key) || !_entries.TryGetValue(key, out var node))
                return Task.FromResult(false);
            Remove(node);
            return Task.FromResult(true);
        }
    }

    public async Task<bool> ExistsAsync(string key) => await GetStringAsync(key) is not null;

    public Task<int> DeleteByPatternAsync(string pattern)
    {
        var matcher = ApplicationCachePayloadPolicy.PatternMatcher(pattern);
        lock (_gate) return Task.FromResult(RemoveWhere(entry => matcher(entry.Key)));
    }

    public Task<int> PurgeAllAsync()
    {
        lock (_gate) return Task.FromResult(RemoveWhere(_ => true));
    }

    public Task<int> DeleteCategoryAsync(ApplicationCacheCategory category)
    {
        lock (_gate) return Task.FromResult(RemoveWhere(entry => entry.Category == category));
    }

    public ApplicationCacheTierUsage GetUsageSnapshot()
    {
        lock (_gate)
        {
            var active = _recent.Where(entry => entry.ExpiresAt > clock.UtcNow).ToArray();
            return new("memory", active.Length, active.Sum(entry => (long)entry.Bytes), MaximumBytes,
                MaximumEntryBytes, IsEnabled, _hits, _misses, _writes, _evictions);
        }
    }

    public IReadOnlyDictionary<ApplicationCacheCategory, ApplicationCacheCategoryUsage> GetCategoryUsage()
    {
        lock (_gate) return _recent.Where(entry => entry.ExpiresAt > clock.UtcNow)
            .GroupBy(entry => entry.Category)
            .ToDictionary(group => group.Key,
                group => new ApplicationCacheCategoryUsage(group.Count(), group.Sum(entry => (long)entry.Bytes)));
    }

    internal IReadOnlyList<ApplicationCacheEntryInfo> Snapshot()
    {
        lock (_gate) return _recent.Select(entry => new ApplicationCacheEntryInfo(
            entry.Key, entry.Bytes, entry.ExpiresAt, entry.WrittenAt, ApplicationCacheStorageTier.Metadata)).ToArray();
    }

    public void Dispose()
    {
        lock (_gate) RemoveWhere(_ => true);
    }

    private bool CanStore(string key) => ApplicationCachePayloadPolicy.IsValidKey(key) &&
        ApplicationCachePolicyRegistry.TryClassify(key, out _) &&
        ApplicationCachePayloadPolicy.IsMemoryEligible(key) &&
        ApplicationCachePolicyRegistry.IsEnabled(key, _settings);

    private int RemoveWhere(Func<Entry, bool> predicate)
    {
        var matches = _recent.Where(predicate).ToArray();
        foreach (var entry in matches) Remove(_entries[entry.Key]);
        return matches.Length;
    }

    private void Remove(LinkedListNode<Entry> node)
    {
        _entries.Remove(node.Value.Key);
        _recent.Remove(node);
        _bytes -= node.Value.Bytes;
        var usage = _categoryUsage[node.Value.Category];
        _categoryUsage[node.Value.Category] = (usage.Count - 1, usage.Bytes - node.Value.Bytes);
        _evictions++;
    }

    private sealed record Entry(string Key, string Value, int Bytes, ApplicationCacheCategory Category,
        DateTimeOffset ExpiresAt, DateTimeOffset WrittenAt);
}
