using System.Collections.Concurrent;

namespace allstarr.Services.Common;

// For pages that show a costly number that rarely changes: a slightly old answer now, then a fresh one for next time.
public sealed class RecentValues(TimeProvider time)
{
    private sealed record Entry(object Value, DateTimeOffset At);
    private readonly ConcurrentDictionary<string, Entry> values = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<object>>> loading = new();

    public async Task<T> GetAsync<T>(string key, TimeSpan freshFor, Func<CancellationToken, Task<T>> load, CancellationToken token) where T : notnull
    {
        if (values.TryGetValue(key, out var known))
        {
            if (time.GetUtcNow() - known.At >= freshFor) _ = Load(key, load);
            return (T)known.Value;
        }
        return (T)await Load(key, load).WaitAsync(token);
    }

    private Task<object> Load<T>(string key, Func<CancellationToken, Task<T>> load) where T : notnull =>
        loading.GetOrAdd(key, _ => new(() => Task.Run(async () =>
        {
            try
            {
                object value = await load(CancellationToken.None);
                values[key] = new(value, time.GetUtcNow());
                return value;
            }
            finally { loading.TryRemove(key, out var _); }
        }))).Value;
}
