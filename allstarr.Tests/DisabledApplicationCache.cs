using allstarr.Services.Common;

namespace allstarr.Tests;

internal sealed class DisabledApplicationCache : IApplicationCache
{
    public bool IsEnabled => false;

    public Task<string?> GetStringAsync(string key) => Task.FromResult<string?>(null);

    public Task<T?> GetAsync<T>(string key) where T : class => Task.FromResult<T?>(null);

    public Task<bool> SetStringAsync(string key, string value, TimeSpan? expiry = null) =>
        Task.FromResult(false);

    public Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiry = null) where T : class =>
        Task.FromResult(false);

    public Task<bool> DeleteAsync(string key) => Task.FromResult(false);

    public Task<bool> ExistsAsync(string key) => Task.FromResult(false);

    public Task<int> DeleteByPatternAsync(string pattern) => Task.FromResult(0);

    public Task<int> PurgeAllAsync() => Task.FromResult(0);
}
