using allstarr.Core.Capabilities;
using allstarr.Core.Providers.Lyrics;
using allstarr.Core.Storage;

namespace allstarr.Core.Protocols;

internal sealed record LyricsReadSpec(ProviderActorContext Actor, string ProviderId,
    ProviderExternalResourceId SourceTrackId, ProviderLyricsRequest Request,
    IReadOnlyList<ProviderAccountScope> AllowedAccountScopes, string CorrelationId, DateTimeOffset Deadline, bool HouseholdOnly = false)
{
    internal bool ManualSelection { get; init; }
    internal LyricsExecutionSnapshot Snapshot { get; init; } = new();
}
internal sealed record LyricsRouteBinding(string ProviderId, ProviderExternalResourceId TrackId, Guid? AccountId, long? AccountRevision);
internal sealed record PreparedLyricsRead(LyricsReadSpec Spec, LyricsRouteBinding Binding,
    LyricsSourceSnapshot Source, bool MaySpeculate);
internal interface IProtocolLyricsExecutionGateway
{
    ValueTask<PreparedLyricsRead?> PrepareAsync(LyricsReadSpec spec, CancellationToken token);
    Task<ProviderLyricsResult?> ExecuteAsync(PreparedLyricsRead prepared, LyricsCompletionMode completion, CancellationToken token);
    ValueTask<bool> IsCurrentAsync(PreparedLyricsRead prepared, CancellationToken token);
}

public sealed class LyricsLookupOptions
{
    public int ResponseBudgetMilliseconds { get; set; } = 1500;
    public int SourceTimeoutMilliseconds { get; set; } = 3000;
    public int BackgroundTimeoutMilliseconds { get; set; } = 15000;
    public int Concurrency { get; set; } = 4;

    internal TimeSpan ResponseBudget => TimeSpan.FromMilliseconds(Math.Clamp(ResponseBudgetMilliseconds, 1, 15000));
    internal TimeSpan SourceTimeout => TimeSpan.FromMilliseconds(Math.Clamp(SourceTimeoutMilliseconds, 1, 3000));
    internal TimeSpan BackgroundTimeout => TimeSpan.FromMilliseconds(Math.Clamp(BackgroundTimeoutMilliseconds, 1, 30000));
    internal int Parallelism => Math.Clamp(Concurrency, 1, 4);
}
