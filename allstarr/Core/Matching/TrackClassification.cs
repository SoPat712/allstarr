using System.Collections.Concurrent;
using allstarr.Core.Storage;
using allstarr.Core.Settings;
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

// The listener's library arranged once for projecting many songs, so each song doesn't rescan all of it.
public sealed class TrackRouteLibrary(IEnumerable<LibraryTrackRecord> tracks)
{
    private readonly Dictionary<string, Backend> backends = tracks
        .GroupBy(item => item.BackendInstanceId, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => new Backend(group), StringComparer.Ordinal);
    private static readonly Backend None = new([]);

    internal Backend For(string backendInstanceId) => backends.GetValueOrDefault(backendInstanceId) ?? None;

    internal sealed class Backend
    {
        public Backend(IEnumerable<LibraryTrackRecord> tracks)
        {
            ById = tracks.ToDictionary(item => item.Id);
            Ids = ById.Keys.ToHashSet();
            LibraryIds = ById.Values.Select(item => item.BackendLibraryId).ToHashSet(StringComparer.Ordinal);
            Candidates = new TrackMatchDecisionEngine().PrepareCandidates(ById.Values.Select(LocalCopyPreference.ToCandidate));
            FirstByRecording = ById.Values.Where(item => item.CanonicalRecordingId.HasValue)
                .GroupBy(item => item.CanonicalRecordingId!.Value)
                .ToDictionary(group => group.Key, group => group
                    .OrderByDescending(item => LocalCopyPreference.FormatRank(Path.GetExtension(item.FilePath)))
                    .ThenByDescending(item => item.SourceModifiedAt)
                    .ThenBy(item => item.BackendLibraryId, StringComparer.Ordinal)
                    .ThenBy(item => item.BackendItemId, StringComparer.Ordinal)
                    .ThenBy(item => item.Id)
                    .First());
        }

        public Dictionary<Guid, LibraryTrackRecord> ById { get; }
        public HashSet<Guid> Ids { get; }
        public HashSet<string> LibraryIds { get; }
        public TrackMatchCandidateSet Candidates { get; }
        public Dictionary<Guid, LibraryTrackRecord> FirstByRecording { get; }
    }
}

public static class TrackRouteProjector
{
    // Deciding whether the library holds a song means scoring it against the library, the slow part of projecting
    // thousands of songs. The answer holds until the song, its identity, the library or the listener's policy changes.
    private readonly record struct LocalCopyKey(Guid SnapshotId, int SnapshotVersion, Guid? IdentityId, long IdentityRevision,
        long LibraryRevision, double Window, bool CleanOnly);
    private const int MaximumLocalCopies = 200_000;
    private static readonly ConcurrentDictionary<LocalCopyKey, Guid?> LocalCopies = new();

    public static TrackRouteProjection Project(
        ExternalMetadataSnapshotRecord snapshot,
        TrackMatchRecord? decision,
        ManualTrackOverrideRecord? manual,
        ProviderTrackIdentityRecord? sourceIdentity,
        IEnumerable<LibraryTrackRecord> accessibleLibraryTracks,
        IEnumerable<ProviderTrackIdentityRecord> providerIdentities,
        IReadOnlyCollection<string>? providerPriority = null,
        EffectiveProviderPolicySnapshot? providerPolicy = null, bool preferLocalCopies = true) =>
        Project(snapshot, decision, manual, sourceIdentity,
            new TrackRouteLibrary(accessibleLibraryTracks.Where(item => item.BackendInstanceId == snapshot.BackendInstanceId)),
            providerIdentities, providerPriority, providerPolicy, preferLocalCopies);

    public static TrackRouteProjection Project(
        ExternalMetadataSnapshotRecord snapshot,
        TrackMatchRecord? decision,
        ManualTrackOverrideRecord? manual,
        ProviderTrackIdentityRecord? sourceIdentity,
        TrackRouteLibrary library,
        IEnumerable<ProviderTrackIdentityRecord> providerIdentities,
        IReadOnlyCollection<string>? providerPriority = null,
        EffectiveProviderPolicySnapshot? providerPolicy = null, bool preferLocalCopies = true)
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
        var scopedLibrary = library.For(snapshot.BackendInstanceId);
        var classification = TrackClassifier.Classify(
            manual,
            decision,
            sourceIdentity,
            identities,
            providerOrder,
            scopedLibrary.Ids, providerPolicy, snapshot.ProviderId);
        scopedLibrary.ById.TryGetValue(classification.LibraryTrackId ?? Guid.Empty, out var local);
        ExternalTrackMatchSnapshot? matchingSource = null;
        var sourceRead = false;
        ExternalTrackMatchSnapshot? MatchingSource()
        {
            if (!sourceRead) { matchingSource = TrackSnapshotMetadata.MatchingSource(snapshot, sourceIdentity); sourceRead = true; }
            return matchingSource;
        }
        if (preferLocalCopies && local == null && manual == null && decision?.State is not (TrackMatchState.Pinned or TrackMatchState.Rejected) &&
            snapshot.OwnerUserId != Guid.Empty &&
            providerPolicy?.IsAutomatic(sourceIdentity?.ProviderId ?? snapshot.ProviderId) != false)
        {
            var window = providerPolicy?.LocalPreferenceWindow ?? .07;
            var cleanOnly = providerPolicy?.Preferences.ExplicitFilter == "CleanOnly";
            var key = new LocalCopyKey(snapshot.Id, snapshot.SnapshotVersion, sourceIdentity?.Id, sourceIdentity?.Revision ?? 0,
                scopedLibrary.Candidates.Revision, window, cleanOnly);
            if (!LocalCopies.TryGetValue(key, out var selected))
            {
                selected = null;
                if (MatchingSource() is { } source)
                {
                    var scope = new TrackMatchScope(snapshot.OwnerUserId, snapshot.BackendInstanceId, null, 2,
                        Math.Max(1, snapshot.SnapshotVersion), scopedLibrary.LibraryIds);
                    var engine = new TrackMatchDecisionEngine(new TrackMatchPolicy
                    {
                        LocalPriorityWindow = window,
                        ExplicitContent = cleanOnly
                            ? allstarr.Core.Capabilities.ProviderExplicitContentPolicy.CleanOnly
                            : allstarr.Core.Capabilities.ProviderExplicitContentPolicy.Allow
                    });
                    var localDecision = engine.Decide(scope, source, scopedLibrary.Candidates);
                    if (localDecision.State == TrackMatchReviewState.Accepted) selected = localDecision.SelectedLibraryTrackId;
                }
                if (LocalCopies.Count >= MaximumLocalCopies) LocalCopies.Clear();
                LocalCopies[key] = selected;
            }
            if (selected is { } selectedId && scopedLibrary.ById.TryGetValue(selectedId, out var chosen))
            {
                local = chosen;
                classification = classification with { State = TrackMatchState.Accepted, LibraryTrackId = selectedId };
            }
        }
        canonicalId ??= local?.CanonicalRecordingId;
        if (preferLocalCopies && local == null && manual == null &&
            decision?.State is not (TrackMatchState.Pinned or TrackMatchState.Rejected) &&
            classification.PrimaryProviderRoute?.IsManual != true &&
            classification.ReviewState is TrackMatchState.Accepted or TrackMatchState.Pinned &&
            canonicalId.HasValue)
        {
            local = scopedLibrary.FirstByRecording.GetValueOrDefault(canonicalId.Value);
            if (local != null &&
                (MatchingSource() is { } known && !LocalCopyPreference.KnownRecordingMatches(known, LocalCopyPreference.ToCandidate(local)) ||
                 providerPolicy?.Preferences.ExplicitFilter == "CleanOnly" &&
                 TrackMatchSignals.Explicitness(LocalCopyPreference.ToCandidate(local)) != false)) local = null;
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
        IReadOnlySet<Guid>? playableLibraryTrackIds = null,
        EffectiveProviderPolicySnapshot? providerPolicy = null,
        string? sourceProviderId = null)
    {
        var automaticSource = (sourceIdentity?.ProviderId ?? sourceProviderId) is not { } sourceProvider ||
                              providerPolicy?.IsAutomatic(sourceProvider) != false;
        var rejected = TrackMatchOverridePolicy.IsEffectiveRejection(manual, decision);
        var providerRoutes = manual?.Decision == ManualOverrideDecision.Reject &&
                             !manual.LibraryTrackId.HasValue
            ? []
            : DurableProviderRouteSelector.Select(
                sourceIdentity,
                providerIdentities ?? [],
                providerPriority ?? [],
                manual,
                decision?.CanonicalRecordingId, providerPolicy);
        if (!automaticSource) providerRoutes = providerRoutes.Where(item => item.IsManual).ToArray();
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
            _ when !automaticSource => TrackMatchState.Unresolved,
            _ when decision?.State == TrackMatchState.Accepted &&
                   decision.Confidence < decision.Threshold => TrackMatchState.Unresolved,
            _ => decision?.State ?? TrackMatchState.Unresolved
        };
        var libraryTrackId = hasManualLibraryRoute
            ? manual!.LibraryTrackId
            : hasManualProviderRoute
                ? null
                : rejected || !automaticSource
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
