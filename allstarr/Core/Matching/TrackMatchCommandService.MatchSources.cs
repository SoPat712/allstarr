using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Identity;
using allstarr.Core.Operations;
using allstarr.Core.Playlists;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Core.Settings;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Matching;

public sealed partial class TrackMatchCommandService
{
    public async Task<IReadOnlyList<AutomatedSourceMatchResult>> MatchSourceTracksAsync(
        IReadOnlyCollection<SourceTrackSeed> sourceTracks,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        var tracks = sourceTracks
            .Where(item =>
                !string.IsNullOrWhiteSpace(item.ProviderId) &&
                !string.IsNullOrWhiteSpace(item.ExternalId))
            .GroupBy(
                item => SourceKey(item.ProviderId, item.ExternalId),
                StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.Ordinal);
        if (tracks.Count == 0) return [];

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var providerIds = tracks.Values
            .Select(item => item.ProviderId.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var externalIds = tracks.Values
            .Select(item => item.ExternalId.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var identities = (await db.ProviderTrackIdentities
                .Where(item =>
                    providerIds.Contains(item.ProviderId) &&
                    externalIds.Contains(item.ExternalId) &&
                    item.ResourceKind == ProviderResourceKind.Track)
                .ToListAsync(cancellationToken))
            .Where(item => tracks.ContainsKey(SourceKey(item.ProviderId, item.ExternalId)))
            .ToArray();
        if (identities.Length == 0) return [];

        var identityById = identities.ToDictionary(item => item.Id);
        var identityIds = identityById.Keys.ToArray();
        var snapshots = (await db.ExternalMetadataSnapshots
                .Where(item =>
                    item.ProviderTrackIdentityId.HasValue &&
                    identityIds.Contains(item.ProviderTrackIdentityId.Value))
                .OrderByDescending(item => item.RetrievedAt)
                .ToListAsync(cancellationToken))
            .GroupBy(item => new
            {
                item.ProviderId,
                item.ExternalIdHash,

                item.OwnerUserId
            })
            .Select(group => group.First())
            .ToArray();
        if (snapshots.Length == 0) return [];

        var snapshotIds = snapshots.Select(item => item.Id).ToArray();
        var activeOverrides = new Dictionary<Guid, ManualTrackOverrideRecord?>();
        foreach (var group in snapshots.GroupBy(item => item.OwnerUserId))
        {
            var records = await ManualTrackOverrides.LoadAsync(
                db, group.Key, group.ToArray(), cancellationToken);
            foreach (var item in ManualTrackOverrides.Index(group, records, group.Key))
                activeOverrides[item.Key] = item.Value.Effective;
        }
        var ownerLibraries = new Dictionary<Guid, LibraryTrackRecord[]>();
        var ownerProviderOrders = new Dictionary<Guid, IReadOnlyList<string>>();
        var ownerPolicies = new Dictionary<Guid, EffectiveProviderPolicySnapshot?>();
        foreach (var owner in snapshots.Select(item => item.OwnerUserId).Distinct())
        {
            ownerPolicies[owner] = effectivePolicies == null ? null :
                await effectivePolicies.ResolveForUserAsync(owner, cancellationToken);
            var access = await libraryAccess.ResolveUserAsync(owner, cancellationToken);
            ownerLibraries[owner] = await LibraryTrackAccess.Query(db, access).ToArrayAsync(cancellationToken);
            ownerProviderOrders[owner] = providerGateway == null
                ? activeOverrides.Values.Where(item => item?.OwnerUserId == owner || item?.OwnerUserId == null)
                    .Select(item => item?.TargetProviderId).OfType<string>().Distinct().ToArray()
                : access.Context?.Actor is { } viewer
                    ? await providerGateway.GetPlayableProviderOrderAsync(viewer, cancellationToken) : [];
        }
        var latestDecisions = await LatestDecisions(db.TrackMatches
                .Where(item => snapshotIds.Contains(item.ExternalSnapshotId)))
            .ToDictionaryAsync(item => item.ExternalSnapshotId, cancellationToken);
        var recordingIds = identities.Select(item => item.CanonicalRecordingId)
            .Concat(latestDecisions.Values.Where(item => item.CanonicalRecordingId.HasValue)
                .Select(item => item.CanonicalRecordingId!.Value))
            .Distinct().ToArray();
        var providerIdentities = await db.ProviderTrackIdentities.AsNoTracking()
            .Where(item => recordingIds.Contains(item.CanonicalRecordingId) &&
                           item.ResourceKind == ProviderResourceKind.Track &&
                           item.Scope == ProviderIdentityScope.Catalog &&
                           (item.Verification == ProviderIdentityVerification.Verified ||
                            item.Verification == ProviderIdentityVerification.Pinned))
            .ToArrayAsync(cancellationToken);
        var providerOrder = providerIdentities.Select(item => item.ProviderId).Distinct().ToArray();
        var localRecordingIds = ownerLibraries.Values.SelectMany(item => item)
            .Where(item => item.CanonicalRecordingId.HasValue).Select(item => item.CanonicalRecordingId!.Value)
            .Distinct().ToArray();
        var localExplicitness = await db.CanonicalRecordings.AsNoTracking()
            .Where(item => localRecordingIds.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, item => item.IsExplicit, cancellationToken);
        var scopedLibraries = snapshots.Select(item => new { item.OwnerUserId, item.BackendInstanceId })
            .Distinct()
            .ToDictionary(item => (item.OwnerUserId, item.BackendInstanceId), item =>
            {
                var local = ownerLibraries[item.OwnerUserId].Where(track =>
                    track.BackendInstanceId == item.BackendInstanceId).ToArray();
                return (
                    Tracks: local,
                    ById: local.ToDictionary(track => track.Id),
                    PlayableIds: local.Select(track => track.Id).ToHashSet(),
                    Libraries: local.Select(track => track.BackendLibraryId).ToHashSet(StringComparer.Ordinal),
                    Candidates: decisionEngine.PrepareCandidates(local.Select(track => ToLocalCandidate(track) with
                    {
                        IsExplicit = track.CanonicalRecordingId is { } recordingId
                            ? localExplicitness.GetValueOrDefault(recordingId) : null
                    })));
            });

        var results = new List<AutomatedSourceMatchResult>(snapshots.Length);
        var aliases = await LoadCrossLinksAsync(db, identities.Select(item => item.CanonicalRecordingId), cancellationToken);
        var musicBrainzIsrcs = await LoadMusicBrainzIsrcsAsync(db,
            snapshots.Select(item => ReadMetadata(item.PayloadJson).MusicBrainzRecordingId), cancellationToken);
        var now = DateTimeOffset.UtcNow;
        foreach (var snapshot in snapshots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var identity = identityById[snapshot.ProviderTrackIdentityId!.Value];
            var seed = tracks[SourceKey(identity.ProviderId, identity.ExternalId)];
            latestDecisions.TryGetValue(snapshot.Id, out var latest);
            activeOverrides.TryGetValue(snapshot.Id, out var manual);
            var library = scopedLibraries[(snapshot.OwnerUserId, snapshot.BackendInstanceId)];
            var cleanOnly = ownerPolicies[snapshot.OwnerUserId]?.Preferences.ExplicitFilter == "CleanOnly";
            var metadata = ReadMetadata(snapshot.PayloadJson);
            if (manual?.Decision == ManualOverrideDecision.Pin ||
                manual?.Decision == ManualOverrideDecision.Reject && !manual.LibraryTrackId.HasValue)
            {
                if (manual.Decision == ManualOverrideDecision.Pin && (cleanOnly || metadata.IsExplicit == false))
                {
                    var explicitness = manual.LibraryTrackId is { } pinnedLocalId
                        ? await StoredLocalExplicitnessAsync(db, pinnedLocalId, cancellationToken)
                        : !string.IsNullOrWhiteSpace(manual.TargetProviderId) && !string.IsNullOrWhiteSpace(manual.TargetExternalId)
                            ? await StoredProviderExplicitnessAsync(db, snapshot, manual.TargetProviderId, manual.TargetExternalId, cancellationToken)
                            : null;
                    if (cleanOnly && explicitness != false || metadata.IsExplicit == false && explicitness == true)
                    {
                        results.Add(ToAutomatedResult(seed, TrackMatchReviewState.Unresolved, null, 0));
                        continue;
                    }
                }
                var classification = TrackClassifier.Classify(
                    manual,
                    latest,
                    identity,
                    identities,
                    ownerProviderOrders[snapshot.OwnerUserId],
                    playableLibraryTrackIds: library.PlayableIds, providerPolicy: ownerPolicies[snapshot.OwnerUserId]);
                var protectedLocal = classification.LibraryTrackId is { } protectedId
                    ? library.ById.GetValueOrDefault(protectedId)
                    : null;
                results.Add(ToAutomatedResult(
                    seed,
                    Enum.Parse<TrackMatchReviewState>(classification.State.ToString(), true),
                    protectedLocal,
                    latest?.Confidence ?? 0));
                continue;
            }

            if (!await AutomaticSourceAsync(snapshot.OwnerUserId, identity.ProviderId, cancellationToken))
            {
                results.Add(ToAutomatedResult(seed, TrackMatchReviewState.Unresolved, null, 0));
                continue;
            }
            var candidates = library.Candidates;
            var libraryIndexRevision = candidates.Revision;
            var scope = new TrackMatchScope(

                snapshot.OwnerUserId,
                snapshot.BackendInstanceId,

                snapshot.ProviderAccountId,
                2,
                snapshot.SnapshotVersion,
                library.Libraries);
            var source = new ExternalTrackMatchSnapshot(
                snapshot.Id.ToString("N"),
                identity.ProviderId,
                identity.ExternalId,
                seed.Title,
                seed.Artist,
                seed.Album,
                null,
                seed.DurationMilliseconds is > 0 ? seed.DurationMilliseconds : null,
                seed.Isrc,
                metadata.MusicBrainzRecordingId,
                metadata.IsExplicit)
            {
                DurationProvenance = metadata.DurationProvenance,
                MusicBrainzRecordingIsrcs = musicBrainzIsrcs.GetValueOrDefault(metadata.MusicBrainzRecordingId ?? ""),
                ExactProviderAliases = FilterAutomaticCrossLinks(
                    aliases.GetValueOrDefault(identity.CanonicalRecordingId),
                    ownerPolicies[snapshot.OwnerUserId])
            };
            var rejectedOverride =
                manual?.Decision == ManualOverrideDecision.Reject &&
                manual.LibraryTrackId.HasValue &&
                library.ById.ContainsKey(manual.LibraryTrackId.Value)
                    ? new ScopedTrackMatchOverride(

                        snapshot.OwnerUserId,

                        source.ProviderId,
                        source.ExternalId,
                        null,
                        new HashSet<Guid> { manual.LibraryTrackId.Value })
                    : null;
            var effectiveEngine = await DecisionEngineAsync(cancellationToken, snapshot.OwnerUserId);
            var decision = effectiveEngine.Decide(
                scope, source, candidates, rejectedOverride);
            var selected = decision.SelectedLibraryTrackId is { } selectedId
                ? library.ById[selectedId]
                : null;
            if (selected != null && !selected.CanonicalRecordingId.HasValue)
            {
                selected.CanonicalRecordingId = identity.CanonicalRecordingId;
                selected.UpdatedAt = now;
            }

            var input = MatchDecisionInput.FromDecision(
                snapshot.Id,
                identity.CanonicalRecordingId,
                decision,
                (latest?.DecisionVersion ?? 0) + 1,
                snapshot.SnapshotVersion,
                libraryIndexRevision,
                "automatic-provider-neutral-v2");
            if (latest != null)
            {
                var providerEvidence = StoredProviderEvidence(latest.CandidateResultsJson);
                var providerRoutes = DurableProviderRouteSelector.Select(identity, providerIdentities, providerOrder,
                    canonicalRecordingId: latest.CanonicalRecordingId, providerPolicy: ownerPolicies[snapshot.OwnerUserId]);
                var fallbackScore = providerEvidence.Select(item => item.Deserialize<TrackMatchCandidateScore>())
                    .OfType<TrackMatchCandidateScore>()
                    .Where(item => TrackMatchDecisionEngine.IsAcceptedExternalCandidate(item, decision.AcceptThreshold) &&
                        providerRoutes.Any(route => TrackMatchSignals.TryGetProviderTrackId(item.ProviderTrackIds, route.ProviderId, out var id) &&
                            id == route.ExternalId))
                    .OrderByDescending(item => item.Confidence).FirstOrDefault();
                var keepProviderDecision = decision.State != TrackMatchReviewState.Accepted &&
                    latest.State == TrackMatchState.Accepted && (!latest.LibraryTrackId.HasValue || fallbackScore != null) &&
                    latest.Confidence >= latest.Threshold &&
                    providerRoutes.Count > 0 &&
                    (!cleanOnly || providerRoutes.All(route =>
                        StoredProviderExplicitness(providerEvidence, route.ProviderId, route.ExternalId) == false)) &&
                    (source.IsExplicit != false || providerRoutes.All(route =>
                        StoredProviderExplicitness(providerEvidence, route.ProviderId, route.ExternalId) != true));
                if (keepProviderDecision)
                {
                    selected = null;
                    input = input with
                    {
                        LibraryTrackId = null,
                        CanonicalRecordingId = latest.CanonicalRecordingId,
                        State = latest.State,
                        Confidence = latest.LibraryTrackId.HasValue ? fallbackScore!.Confidence : latest.Confidence,
                        Threshold = latest.Threshold,
                        MatcherVersion = latest.LibraryTrackId.HasValue ? TrackMatchDecisionEngine.AlgorithmVersion : latest.MatcherVersion,
                        PolicyVersion = latest.LibraryTrackId.HasValue ? input.PolicyVersion : latest.PolicyVersion,
                        ReasonsJson = latest.LibraryTrackId.HasValue ? JsonSerializer.Serialize(fallbackScore!.Reasons) : latest.ReasonsJson,
                        WarningsJson = latest.LibraryTrackId.HasValue ? JsonSerializer.Serialize(fallbackScore!.Warnings) : latest.WarningsJson
                    };
                }
                if (providerEvidence.Length > 0)
                {
                    var localEvidence = decision.Candidates.Select(item => JsonSerializer.SerializeToElement(item));
                    input = input with
                    {
                        CandidateResultsJson = JsonSerializer.Serialize(keepProviderDecision
                            ? providerEvidence.Concat(localEvidence)
                            : localEvidence.Concat(providerEvidence))
                    };
                }
            }
            var unchanged = latest != null && MatchesImmutableDecision(latest, input);
            if (!unchanged)
            {
                db.TrackMatches.Add(ToRecord(
                    input, snapshot.OwnerUserId,
                    correlationId, now));
            }

            results.Add(ToAutomatedResult(seed,
                Enum.Parse<TrackMatchReviewState>(input.State.ToString(), true), selected, input.Confidence));
        }

        var changed = db.ChangeTracker.Entries<TrackMatchRecord>().Where(item => item.State == EntityState.Added)
            .Select(item => item.Entity).ToArray();
        await db.SaveChangesAsync(cancellationToken);
        if (events != null)
            foreach (var item in changed) await events.MatchAsync(item.ExternalSnapshotId, item.OwnerUserId, automatic: true);
        if (crossLinks != null)
            foreach (var snapshot in snapshots)
                await crossLinks.EnqueueAsync(snapshot.OwnerUserId, snapshot.ProviderTrackIdentityId!.Value,
                    correlationId, cancellationToken);
        return results;
    }

    private static string SourceKey(string providerId, string externalId) =>
        $"{providerId.Trim().ToLowerInvariant()}:{externalId.Trim()}";

    private static JsonElement[] StoredProviderEvidence(string candidatesJson)
    {
        using var candidates = JsonDocument.Parse(candidatesJson);
        if (candidates.RootElement.ValueKind != JsonValueKind.Array) return [];
        return candidates.RootElement.EnumerateArray().Where(candidate =>
        {
            if (candidate.ValueKind != JsonValueKind.Object) return false;
            if (candidate.TryGetProperty("IsLocal", out var isLocal) && isLocal.ValueKind == JsonValueKind.False)
                return true;
            return candidate.TryGetProperty("ProviderTrackIds", out var providerIds) &&
                   providerIds.ValueKind == JsonValueKind.Object && providerIds.EnumerateObject().Any() &&
                   (!candidate.TryGetProperty("LibraryTrackId", out var libraryId) ||
                    libraryId.ValueKind == JsonValueKind.Null ||
                    libraryId.ValueKind == JsonValueKind.String && libraryId.GetString() == Guid.Empty.ToString());
        }).Select(candidate => candidate.Clone()).ToArray();
    }

    private static IQueryable<TrackMatchRecord> LatestDecisions(
        IQueryable<TrackMatchRecord> decisions)
    {
        var versions = decisions
            .GroupBy(item => item.ExternalSnapshotId)
            .Select(group => new
            {
                ExternalSnapshotId = group.Key,
                DecisionVersion = group.Max(item => item.DecisionVersion)
            });
        return from decision in decisions
               join version in versions
                   on new { decision.ExternalSnapshotId, decision.DecisionVersion }
                   equals new { version.ExternalSnapshotId, version.DecisionVersion }
               select decision;
    }

    private static LocalTrackMatchCandidate ToLocalCandidate(LibraryTrackRecord item) =>
        LocalCopyPreference.ToCandidate(item);

    private static AutomatedSourceMatchResult ToAutomatedResult(
        SourceTrackSeed source,
        TrackMatchReviewState state,
        LibraryTrackRecord? local,
        double confidence) => new(
        source.ProviderId.Trim().ToLowerInvariant(),
        source.ExternalId.Trim(),
        state,
        local?.BackendItemId,
        local?.Title,
        local?.Artist,
        local?.Album,
        local?.DurationMilliseconds is > 0 ? local.DurationMilliseconds : null,
        local?.Isrc,
        confidence);
}
