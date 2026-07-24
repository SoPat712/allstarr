namespace allstarr.Services.Common;

/// <summary>
/// Disposable application cache. Implementations must fail open: cache outages may reduce
/// performance, but must not change durable application state or user-visible decisions.
/// </summary>
public interface IApplicationCache
{
    bool IsEnabled { get; }

    Task<string?> GetStringAsync(string key);

    Task<T?> GetAsync<T>(string key) where T : class;

    Task<bool> SetStringAsync(string key, string value, TimeSpan? expiry = null);

    Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiry = null) where T : class;

    Task<bool> DeleteAsync(string key);

    Task<bool> ExistsAsync(string key);

    IEnumerable<string> GetKeysByPattern(string pattern);

    Task<int> DeleteByPatternAsync(string pattern);
}
