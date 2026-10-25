using allstarr.Core.Routing;
using System.Security.Cryptography;
using System.Text;
using allstarr.Core.Capabilities;

namespace allstarr.Core.Providers.Lyrics;

internal enum LyricsCompletionMode { RequestBound, OwnedAutomatic }
internal enum LyricsSpeculationMode { ForegroundOnly, Unrestricted, RecentHealthy }
internal readonly record struct LyricsConfigurationId(string Value, long Revision = 0)
{
    internal static LyricsConfigurationId From(string value, long revision = 0) => new(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value))), revision);
}
internal abstract record LyricsSourceSnapshot(LyricsConfigurationId ConfigurationId, LyricsSpeculationMode Speculation, bool DependenciesReady);
internal sealed record StaticLyricsSourceSnapshot(LyricsConfigurationId Id, LyricsSpeculationMode Mode)
    : LyricsSourceSnapshot(Id, Mode, true);
internal sealed record LyricsExecution(LyricsCompletionMode Completion, LyricsSourceSnapshot Source);
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
