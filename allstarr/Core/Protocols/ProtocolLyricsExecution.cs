using allstarr.Core.Capabilities;
using allstarr.Core.Providers.Lyrics;
using allstarr.Core.Storage;

namespace allstarr.Core.Protocols;

internal sealed record LyricsReadSpec(ProviderActorContext Actor, string ProviderId,
    ProviderExternalResourceId SourceTrackId, ProviderLyricsRequest Request,
    IReadOnlyList<ProviderAccountScope> AllowedAccountScopes, string CorrelationId, DateTimeOffset Deadline, bool HouseholdOnly = false);
internal sealed record LyricsRouteBinding(string ProviderId, ProviderExternalResourceId TrackId, Guid? AccountId, long? AccountRevision);
internal sealed record PreparedLyricsRead(LyricsReadSpec Spec, LyricsRouteBinding Binding,
    LyricsSourceSnapshot Source, bool MaySpeculate);
internal interface IProtocolLyricsExecutionGateway
{
    ValueTask<PreparedLyricsRead?> PrepareAsync(LyricsReadSpec spec, CancellationToken token);
    Task<ProviderLyricsResult?> ExecuteAsync(PreparedLyricsRead prepared, LyricsCompletionMode completion, CancellationToken token);
    ValueTask<bool> IsCurrentAsync(PreparedLyricsRead prepared, CancellationToken token);
}
