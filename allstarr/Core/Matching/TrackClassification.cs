using allstarr.Core.Storage;
using allstarr.Services.Spotify;

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

public sealed record TrackRouteProjection(
    TrackClassification Classification,
    Guid? CanonicalRecordingId,
    LibraryTrackRecord? LibraryTrack,
    IReadOnlyList<ProviderTrackIdentityRecord> ProviderIdentities)
{
    public TrackMatchState ReviewState => Classification.ReviewState;
    public DurableProviderRoute? PrimaryProviderRoute =>
        LibraryTrack == null ? Classification.PrimaryProviderRoute : null;
}

public static class TrackRouteProjector
{
    public static TrackRouteProjection Project(
        ExternalMetadataSnapshotRecord snapshot,
        TrackMatchRecord? decision,
        ManualTrackOverrideRecord? manual,
        ProviderTrackIdentityRecord? sourceIdentity,
        IEnumerable<LibraryTrackRecord> accessibleLibraryTracks,
        IEnumerable<ProviderTrackIdentityRecord> providerIdentities,
        IReadOnlyCollection<string>? providerPriority = null)
    {
        var canonicalId = decision?.CanonicalRecordingId ?? sourceIdentity?.CanonicalRecordingId;
        var identities = canonicalId.HasValue
            ? providerIdentities
                .Where(item => item.CanonicalRecordingId == canonicalId.Value)
                .ToArray()
            : [];
        var providerOrder = (providerPriority ?? identities.Select(item => item.ProviderId).ToArray())
            .Select(providerId => providerId.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var scopedLibrary = accessibleLibraryTracks
            .Where(item =>
                item.TenantId == snapshot.TenantId &&
                item.BackendInstanceId == snapshot.BackendInstanceId)
            .ToArray();
        var libraryById = scopedLibrary.ToDictionary(item => item.Id);
        var classification = TrackClassifier.Classify(
            manual,
            decision,
            sourceIdentity,
            identities,
            providerOrder,
            libraryById.Keys.ToHashSet());
        libraryById.TryGetValue(classification.LibraryTrackId ?? Guid.Empty, out var local);
        canonicalId ??= local?.CanonicalRecordingId;
        if (local == null &&
            classification.PrimaryProviderRoute?.IsManual != true &&
            classification.ReviewState is TrackMatchState.Accepted or TrackMatchState.Pinned &&
            canonicalId.HasValue)
        {
            local = scopedLibrary
                .Where(item => item.CanonicalRecordingId == canonicalId.Value)
                .OrderBy(item => item.LibraryScopeId, StringComparer.Ordinal)
                .ThenBy(item => item.BackendItemId, StringComparer.Ordinal)
                .ThenBy(item => item.Id)
                .FirstOrDefault();
            if (local != null) classification = classification with { LibraryTrackId = local.Id };
        }
        return new(classification, canonicalId, local, identities);
    }
}

public static class ManualTrackAuthorityPolicy
{
    public const string ReleasedProviderVerificationMethod = "manual-released";
    public const string ReplacedProviderVerificationMethod = "manual-replaced";
    public const string RematchPolicyVersion = "manual-authority-rematch-v1";
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
        var providerRoutes = manual?.Decision == ManualOverrideDecision.Reject &&
                             !manual.LibraryTrackId.HasValue
            ? []
            : DurableProviderRouteSelector.Select(
                sourceIdentity,
                providerIdentities ?? [],
                providerPriority ?? [],
                manual);
        var hasManualProviderRoute = providerRoutes.FirstOrDefault()?.IsManual == true;
        var hasManualLibraryRoute = manual?.Decision == ManualOverrideDecision.Pin &&
                                    manual.LibraryTrackId.HasValue &&
                                    (playableLibraryTrackIds == null ||
                                     playableLibraryTrackIds.Contains(manual.LibraryTrackId.Value));
        var hasEligibleManualPin = hasManualProviderRoute || hasManualLibraryRoute;
        var state = manual?.Decision switch
        {
            ManualOverrideDecision.Pin when hasEligibleManualPin => TrackMatchState.Pinned,
            ManualOverrideDecision.Reject when rejected => TrackMatchState.Rejected,
            _ when decision?.State == TrackMatchState.Accepted &&
                   decision.Confidence < decision.Threshold => TrackMatchState.Unresolved,
            _ => decision?.State ?? TrackMatchState.Unresolved
        };
        var libraryTrackId = hasManualLibraryRoute
            ? manual!.LibraryTrackId
            : hasManualProviderRoute
                ? null
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

        return new TrackClassification(state, libraryTrackId, providerRoutes);
    }
}
