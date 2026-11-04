using allstarr.Core.Routing;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using allstarr.Core.Capabilities;

namespace allstarr.Core.Providers.Lyrics;

internal enum LyricsCompletionMode { RequestBound, OwnedAutomatic, CacheOnly }
internal enum LyricsSpeculationMode { ForegroundOnly, Unrestricted, RecentHealthy }
internal readonly record struct LyricsConfigurationId(string Value, long Revision = 0)
{
    internal static LyricsConfigurationId From(string value, long revision = 0) => new(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value))), revision);
}
internal abstract record LyricsSourceSnapshot(LyricsConfigurationId ConfigurationId, LyricsSpeculationMode Speculation, bool DependenciesReady);
internal sealed record StaticLyricsSourceSnapshot(LyricsConfigurationId Id, LyricsSpeculationMode Mode)
    : LyricsSourceSnapshot(Id, Mode, true);
internal sealed record LyricsExecution(LyricsCompletionMode Completion, LyricsSourceSnapshot Source,
    LyricsExecutionSnapshot? Snapshot = null);

internal sealed class LyricsExecutionSnapshot
{
    private readonly ConcurrentDictionary<string, Lazy<Task<object?>>> values = new(StringComparer.Ordinal);

    internal async Task<T> GetOrCreateAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(factory);
        var lazy = values.GetOrAdd(key, _ => new(
            () => LoadAsync(factory, cancellationToken),
            LazyThreadSafetyMode.ExecutionAndPublication));
        var task = lazy.Value;
        try
        {
            var value = await task.WaitAsync(cancellationToken);
            if (value is null) return default!;
            return value is T typed
                ? typed
                : throw new InvalidOperationException($"Lyrics dependency snapshot '{key}' has an unexpected type.");
        }
        catch when (task.IsCanceled || task.IsFaulted)
        {
            values.TryRemove(new KeyValuePair<string, Lazy<Task<object?>>>(key, lazy));
            throw;
        }
    }

    private static async Task<object?> LoadAsync<T>(Func<CancellationToken, Task<T>> factory,
        CancellationToken cancellationToken) => await factory(cancellationToken);
}
internal interface IInternalLyricsCapability
{
    ValueTask<LyricsSourceSnapshot> CaptureSourceAsync(CancellationToken token);
    Task<ProviderOutcome<ProviderLyricsResult>> FetchAsync(ProviderExecutionContext context,
        ProviderLyricsRequest request, LyricsExecution execution);
}
internal interface IHouseholdLyricsRoutePlanner
{
    Task<ProviderRoutePlan<IProviderLyricsCapability>> PlanHouseholdLyricsAsync(ProviderRouteRequest request);
}
internal interface ILyricsOutcomeObserver
{
    void ObserveLyrics(ProviderExecutionContext context, ProviderError? error, TimeSpan elapsed, LyricsConfigurationId configuration);
}
internal static class LyricsAwait
{
    internal static async Task<T> CompleteAsync<T>(Task<T> task, LyricsCompletionMode mode, CancellationToken token)
    {
        var result = mode == LyricsCompletionMode.OwnedAutomatic ? await task : await task.WaitAsync(token);
        token.ThrowIfCancellationRequested();
        return result;
    }
    internal static async Task CompleteAsync(Task task, LyricsCompletionMode mode, CancellationToken token)
    {
        if (mode == LyricsCompletionMode.OwnedAutomatic) await task;
        else await task.WaitAsync(token);
        token.ThrowIfCancellationRequested();
    }
    internal static LyricsSourceSnapshot Static(string provider, LyricsSpeculationMode mode) =>
        new StaticLyricsSourceSnapshot(LyricsConfigurationId.From(provider + ":lyrics:1"), mode);
}
