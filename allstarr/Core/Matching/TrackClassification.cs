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
        IEnumerable<LibraryTrackRecord> libraryTracks,
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
            .Select(ExternalTrackPlaybackPolicy.Normalize)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var scopedLibrary = libraryTracks
            .Where(item =>
                item.TenantId == snapshot.TenantId &&
                item.OwnerUserId == snapshot.OwnerUserId &&
                item.LibraryScopeId == snapshot.LibraryScopeId &&
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
            classification.ReviewState is TrackMatchState.Accepted or TrackMatchState.Pinned &&
            canonicalId.HasValue)
        {
            local = scopedLibrary
                .Where(item => item.CanonicalRecordingId == canonicalId.Value)
                .OrderBy(item => item.BackendItemId, StringComparer.Ordinal)
                .FirstOrDefault();
        }
        return new(classification, canonicalId, local, identities);
    }
}

public static class ManualTrackAuthorityPolicy
{
    public const string ProviderVerificationMethod = "manual-review";
    public const string ReleasedProviderVerificationMethod = "manual-released";
    public const string ReplacedProviderVerificationMethod = "manual-replaced";
    public const string RematchPolicyVersion = "manual-authority-rematch-v1";

    public static bool IsProviderAuthority(ProviderTrackIdentityRecord identity) =>
        identity.Verification == ProviderIdentityVerification.Pinned &&
        identity.VerificationMethod == ProviderVerificationMethod;
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
