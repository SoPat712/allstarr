using System.Collections.Concurrent;
using System.Text.Json;
using allstarr.Services.Common;

namespace allstarr.Tests;

internal sealed class TestMemoryApplicationCache : IApplicationCache
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public bool IsEnabled => true;

    public Task<string?> GetStringAsync(string key)
    {
        if (!_entries.TryGetValue(key, out var entry)) return Task.FromResult<string?>(null);
        if (entry.ExpiresAt is null || entry.ExpiresAt > DateTimeOffset.UtcNow)
            return Task.FromResult<string?>(entry.Value);
        _entries.TryRemove(key, out _);
        return Task.FromResult<string?>(null);
    }

    public async Task<T?> GetAsync<T>(string key) where T : class
    {
        var value = await GetStringAsync(key);
        return value == null ? null : JsonSerializer.Deserialize<T>(value);
    }

    public Task<bool> SetStringAsync(string key, string value, TimeSpan? expiry = null)
    {
        _entries[key] = new(value, expiry.HasValue ? DateTimeOffset.UtcNow.Add(expiry.Value) : null);
        return Task.FromResult(true);
    }

    public Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiry = null) where T : class =>
        SetStringAsync(key, JsonSerializer.Serialize(value), expiry);

    public Task<bool> DeleteAsync(string key) => Task.FromResult(_entries.TryRemove(key, out _));

    public async Task<bool> ExistsAsync(string key) => await GetStringAsync(key) != null;

    public IEnumerable<string> GetKeysByPattern(string pattern)
    {
        var prefix = pattern.TrimEnd('*');
        return _entries.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
    }

    public Task<int> DeleteByPatternAsync(string pattern)
    {
        var deleted = GetKeysByPattern(pattern).Count(key => _entries.TryRemove(key, out _));
        return Task.FromResult(deleted);
    }

    private sealed record Entry(string Value, DateTimeOffset? ExpiresAt);
}
