using allstarr.Core.Storage;

namespace allstarr.Core.Matching;

public enum TrackRouteKind
{
    Unresolved,
    Local,
    External
}

public sealed record TrackClassification(
    TrackMatchState State,
    Guid? LibraryTrackId,
    IReadOnlyList<DurableProviderRoute> ProviderRoutes)
{
    public TrackRouteKind RouteKind => LibraryTrackId.HasValue
        ? TrackRouteKind.Local
        : ProviderRoutes.Count > 0
            ? TrackRouteKind.External
            : TrackRouteKind.Unresolved;

    public DurableProviderRoute? PrimaryProviderRoute => ProviderRoutes.FirstOrDefault();

    public TrackMatchState ReviewState =>
        State != TrackMatchState.Rejected && PrimaryProviderRoute?.IsManual == true
            ? TrackMatchState.Pinned
            : State == TrackMatchState.Unresolved && RouteKind != TrackRouteKind.Unresolved
            ? TrackMatchState.Accepted
            : State;
}

public static class TrackClassifier
{
    public static TrackClassification Classify(
        ManualTrackOverrideRecord? manual,
        TrackMatchRecord? decision,
        ProviderTrackIdentityRecord? sourceIdentity = null,
        IEnumerable<ProviderTrackIdentityRecord>? providerIdentities = null,
        IReadOnlyList<string>? providerPriority = null,
        IReadOnlySet<Guid>? playableLibraryTrackIds = null)
    {
        var rejected = TrackMatchOverridePolicy.IsEffectiveRejection(manual, decision);
        var state = manual?.Decision switch
        {
            ManualOverrideDecision.Pin => TrackMatchState.Pinned,
            ManualOverrideDecision.Reject when rejected => TrackMatchState.Rejected,
            _ when decision?.State == TrackMatchState.Accepted &&
                   decision.Confidence < decision.Threshold => TrackMatchState.Unresolved,
            _ => decision?.State ?? TrackMatchState.Unresolved
        };
        var libraryTrackId = manual?.Decision == ManualOverrideDecision.Pin
            ? manual.LibraryTrackId
            : rejected
                ? null
                : decision?.State switch
                {
                    TrackMatchState.Pinned => decision.LibraryTrackId,
                    TrackMatchState.Accepted when decision.Confidence >= decision.Threshold =>
                        decision.LibraryTrackId,
                    TrackMatchState.Suggested => decision.LibraryTrackId,
                    _ => null
                };
        if (libraryTrackId.HasValue &&
            playableLibraryTrackIds != null &&
            !playableLibraryTrackIds.Contains(libraryTrackId.Value))
            libraryTrackId = null;

        var providerRoutes = manual?.Decision == ManualOverrideDecision.Reject &&
                             !manual.LibraryTrackId.HasValue
            ? []
            : DurableProviderRouteSelector.Select(
                sourceIdentity,
                providerIdentities ?? [],
                providerPriority ?? []);
        return new TrackClassification(state, libraryTrackId, providerRoutes);
    }
}
