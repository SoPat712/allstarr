using System.Collections.Concurrent;

namespace allstarr.Services.Common;

public sealed class ApplicationCacheRequestCoalescer(
    ApplicationCacheActivityMetrics metrics)
{
    private readonly ConcurrentDictionary<string, Lazy<Task<object?>>> _inflight =
        new(StringComparer.Ordinal);

    public async Task<T> RunAsync<T>(
        string key,
        Func<Task<T>> fetch,
        CancellationToken cancellationToken)
    {
        var created = new Lazy<Task<object?>>(
            async () => await fetch(),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var pending = _inflight.GetOrAdd(key, created);
        if (!ReferenceEquals(pending, created))
        {
            metrics.RecordCoalesced();
        }

        try
        {
            return (T)(await pending.Value.WaitAsync(cancellationToken))!;
        }
        finally
        {
            _inflight.TryRemove(
                new KeyValuePair<string, Lazy<Task<object?>>>(key, pending));
        }
    }
}
