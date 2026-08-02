using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using allstarr.Core.Matching;

namespace allstarr.Core.Playlists;

public enum PlaylistPlanMode
{
    Virtual,
    Reconcile,
    Recreate
}

public enum PlaylistPreviewEntryStatus
{
    Included,
    Duplicate,
    Unresolved,
    Suggested,
    Ambiguous,
    Rejected,
    BelowAcceptanceThreshold,
    MissingLocalItem,
    WrongBackend,
    StaleDecision
}

public static class PlaylistMaterializationOutcomeCodes
{
    public const string IncludedNativeBackendItem = "included_native_backend_item";
    public const string SkippedExternalOnlyForBackend = "skipped_external_only_for_backend";
    public const string SkippedUnresolved = "skipped_unresolved";
    public const string SkippedRejected = "skipped_rejected";
    public const string SkippedDuplicate = "skipped_duplicate";
    public const string SkippedWrongBackendOrLibrary = "skipped_wrong_backend_or_library";
    public const string SkippedStaleRevision = "skipped_stale_revision";
}

public sealed record PlaylistSourceIdentity(
    string ProviderId,
    Guid ProviderAccountId,
    string ExternalIdHash,
    string SourceRevision,
    int SnapshotVersion,
    string? ExternalId = null);

public sealed record PlaylistSourceMetadata(
    string? Title = null,
    IReadOnlyList<string>? Artists = null,
    string? Album = null,
    long? DurationMilliseconds = null,
    string? DurationProvenance = null,
    string? Isrc = null,
    bool? IsExplicit = null,
    string? ArtworkUrl = null,
    Guid? CanonicalRecordingId = null);

public sealed record PlaylistResolvedRoute(
    TrackRouteKind Kind,
    Guid? LibraryTrackId = null,
    string? BackendItemId = null,
    string? BackendInstanceId = null,
    string? Protocol = null,
    string? LibraryScopeId = null,
    string? ProviderId = null,
    string? ExternalId = null,
    Guid? CanonicalRecordingId = null);

public sealed record ImmutablePlaylistSourceEntry(
    Guid SourceEntryId,
    int SourcePosition,
    Guid ExternalSnapshotId,
    string SourceTrackReference,
    PlaylistSourceIdentity? SourceIdentity = null,
    PlaylistSourceMetadata? Metadata = null);

public sealed record ImmutablePlaylistSourceSnapshot
{
    public ImmutablePlaylistSourceSnapshot(
        Guid snapshotId,
        Guid playlistLinkId,
        string sourceRevision,
        string name,
        IEnumerable<ImmutablePlaylistSourceEntry> entries,
        string? description = null,
        string? artworkReference = null)
    {
        if (snapshotId == Guid.Empty) throw new ArgumentException("A snapshot ID is required.", nameof(snapshotId));
        if (playlistLinkId == Guid.Empty) throw new ArgumentException("A playlist link ID is required.", nameof(playlistLinkId));
        if (string.IsNullOrWhiteSpace(sourceRevision)) throw new ArgumentException("A source revision is required.", nameof(sourceRevision));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A playlist name is required.", nameof(name));
        var copiedEntries = entries?.ToArray() ?? throw new ArgumentNullException(nameof(entries));
        if (copiedEntries.Any(entry => entry.SourceEntryId == Guid.Empty ||
                                       entry.ExternalSnapshotId == Guid.Empty ||
                                       entry.SourcePosition < 0 ||
                                       string.IsNullOrWhiteSpace(entry.SourceTrackReference)))
            throw new ArgumentException("Every source entry requires stable IDs, a non-negative position, and a track reference.", nameof(entries));
        if (!copiedEntries.Select(entry => entry.SourcePosition).SequenceEqual(copiedEntries.Select(entry => entry.SourcePosition).Order()))
            throw new ArgumentException("Source entries must already be in source order.", nameof(entries));
        if (copiedEntries.Select(entry => entry.SourcePosition).Distinct().Count() != copiedEntries.Length ||
            copiedEntries.Select(entry => entry.SourceEntryId).Distinct().Count() != copiedEntries.Length)
            throw new ArgumentException("Source entry IDs and positions must be unique.", nameof(entries));

        SnapshotId = snapshotId;
        PlaylistLinkId = playlistLinkId;
        SourceRevision = sourceRevision.Trim();
        Name = name.Trim();
        Description = description;
        ArtworkReference = ValidateArtworkReference(artworkReference);
        Entries = copiedEntries;
    }

    public Guid SnapshotId { get; }
    public Guid PlaylistLinkId { get; }
    public string SourceRevision { get; }
    public string Name { get; }
    public string? Description { get; }
    public string? ArtworkReference { get; }
    public IReadOnlyList<ImmutablePlaylistSourceEntry> Entries { get; }

    private static string? ValidateArtworkReference(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        if (value.Length > 500 || value.Contains('?') || value.Contains('#') ||
            Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ||
            value.Contains("token=", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("signature=", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Artwork must use a stable reference key, not a signed or expiring URL.", nameof(value));
        return value;
    }
}

public sealed record PersistedPlaylistMatchDecision(
    Guid SourceEntryId,
    Guid ExternalSnapshotId,
    TrackMatchReviewState State,
    Guid? LibraryTrackId,
    string? BackendItemId,
    string? BackendInstanceId,
    double Confidence,
    double AcceptanceThreshold,
    int DecisionVersion,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<string> Warnings,
    PlaylistResolvedRoute? Route = null);

public sealed record PlaylistPlanningTarget(
    string Protocol,
    string BackendInstanceId,
    string? BackendPlaylistId,
    string? ExpectedRevision = null,
    string? ExpectedFingerprint = null);

public sealed record PlaylistPlanningRules
{
    public PlaylistPlanningRules(
        string ruleVersion,
        long runGeneration,
        bool preserveManualEntries,
        bool mirrorStaleSyncOwnedEntries,
        IEnumerable<string>? syncOwnedMembershipIds = null,
        bool syncName = true,
        bool syncDescription = true,
        bool syncArtwork = true)
    {
        if (string.IsNullOrWhiteSpace(ruleVersion)) throw new ArgumentException("A rule version is required.", nameof(ruleVersion));
        if (runGeneration <= 0) throw new ArgumentOutOfRangeException(nameof(runGeneration));
        RuleVersion = ruleVersion.Trim();
        RunGeneration = runGeneration;
        PreserveManualEntries = preserveManualEntries;
        MirrorStaleSyncOwnedEntries = mirrorStaleSyncOwnedEntries;
        SyncOwnedMembershipIds = (syncOwnedMembershipIds ?? []).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).ToArray();
        SyncName = syncName;
        SyncDescription = syncDescription;
        SyncArtwork = syncArtwork;
    }

    public string RuleVersion { get; }
    public long RunGeneration { get; }
    public bool PreserveManualEntries { get; }
    public bool MirrorStaleSyncOwnedEntries { get; }
    public IReadOnlyList<string> SyncOwnedMembershipIds { get; }
    public bool SyncName { get; }
    public bool SyncDescription { get; }
    public bool SyncArtwork { get; }
}

public sealed record PlaylistPreviewEntry(
    Guid SourceEntryId,
    int SourcePosition,
    string SourceTrackReference,
    PlaylistPreviewEntryStatus Status,
    Guid? LibraryTrackId,
    string? BackendItemId,
    int? TargetPosition,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<string> Warnings,
    Guid? DuplicateOfSourceEntryId = null,
    PlaylistResolvedRoute? ResolvedRoute = null,
    bool TargetEligible = false,
    string OutcomeCode = PlaylistMaterializationOutcomeCodes.SkippedUnresolved)
{
    public PlaylistSourceIdentity? SourceIdentity { get; init; }
    public PlaylistSourceMetadata? SourceMetadata { get; init; }
}

public sealed record PlannedPlaylistMetadata(
    string? Name,
    string? Description,
    string? ArtworkReference);

public sealed record PlaylistMaterializationPlan(
    PlaylistPlanMode Mode,
    Guid PlaylistLinkId,
    Guid SourceSnapshotId,
    string SourceRevision,
    bool SourceSnapshotIsStale,
    PlaylistPlanningTarget Target,
    PlaylistPlanningRules Rules,
    PlannedPlaylistMetadata Metadata,
    string IdempotencyKey,
    IReadOnlyList<string> OrderedBackendItemIds,
    IReadOnlyList<PlaylistPreviewEntry> Entries)
{
    public bool HasSkips => Entries.Any(entry => entry.Status != PlaylistPreviewEntryStatus.Included);
    public bool RequiresBackendWrite => Mode != PlaylistPlanMode.Virtual;
}

public sealed class PlaylistMaterializationPlanner
{
    public PlaylistMaterializationPlan Plan(
        PlaylistPlanMode mode,
        ImmutablePlaylistSourceSnapshot source,
        IEnumerable<PersistedPlaylistMatchDecision> persistedDecisions,
        PlaylistPlanningTarget target,
        PlaylistPlanningRules rules,
        Guid? latestKnownSourceSnapshotId = null)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(persistedDecisions);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(rules);
        ValidateTarget(target);

        var decisionLookup = persistedDecisions.ToLookup(decision => decision.SourceEntryId);
        if (decisionLookup.Any(group => group.Count() > 1))
            throw new ArgumentException("A source entry may have only one persisted match decision.", nameof(persistedDecisions));

        var includedByBackendItem = new Dictionary<string, PlaylistPreviewEntry>(StringComparer.Ordinal);
        var preview = new List<PlaylistPreviewEntry>(source.Entries.Count);
        var orderedItems = new List<string>();
        foreach (var entry in source.Entries)
        {
            var decision = decisionLookup[entry.SourceEntryId].SingleOrDefault();
            var assessed = Assess(entry, decision, target, orderedItems.Count);
            if (assessed.Status == PlaylistPreviewEntryStatus.Included)
            {
                if (includedByBackendItem.TryGetValue(assessed.BackendItemId!, out var original))
                {
                    assessed = assessed with
                    {
                        Status = PlaylistPreviewEntryStatus.Duplicate,
                        TargetPosition = original.TargetPosition,
                        Reasons = assessed.Reasons.Concat(["duplicate_local_backend_item_first_source_entry_kept"]).ToArray(),
                        DuplicateOfSourceEntryId = original.SourceEntryId,
                        TargetEligible = false,
                        OutcomeCode = PlaylistMaterializationOutcomeCodes.SkippedDuplicate
                    };
                }
                else
                {
                    includedByBackendItem.Add(assessed.BackendItemId!, assessed);
                    orderedItems.Add(assessed.BackendItemId!);
                }
            }
            preview.Add(assessed);
        }

        var stale = latestKnownSourceSnapshotId.HasValue &&
                    latestKnownSourceSnapshotId.Value != source.SnapshotId;
        return new(
            mode,
            source.PlaylistLinkId,
            source.SnapshotId,
            source.SourceRevision,
            stale,
            target,
            rules,
            new(
                rules.SyncName ? source.Name : null,
                rules.SyncDescription ? source.Description : null,
                rules.SyncArtwork ? source.ArtworkReference : null),
            ComputeIdempotencyKey(source, target, rules),
            orderedItems,
            preview);
    }

    public static string ComputeIdempotencyKey(
        ImmutablePlaylistSourceSnapshot source,
        PlaylistPlanningTarget target,
        PlaylistPlanningRules rules)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(rules);
        var components = new[]
        {
            source.PlaylistLinkId.ToString("N"),
            target.Protocol,
            target.BackendInstanceId,
            source.SnapshotId.ToString("N"),
            rules.RuleVersion,
            rules.RunGeneration.ToString(CultureInfo.InvariantCulture)
        };
        var canonical = string.Concat(components.Select(value => $"{Encoding.UTF8.GetByteCount(value)}:{value}"));
        return $"playlist-materialize:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant()}";
    }

    private static PlaylistPreviewEntry Assess(
        ImmutablePlaylistSourceEntry entry,
        PersistedPlaylistMatchDecision? decision,
        PlaylistPlanningTarget target,
        int targetPosition)
    {
        if (decision == null)
            return Skip(entry, PlaylistPreviewEntryStatus.Unresolved, "no_persisted_match_decision");
        if (decision.ExternalSnapshotId != entry.ExternalSnapshotId)
            return Skip(entry, PlaylistPreviewEntryStatus.StaleDecision, "match_decision_external_snapshot_mismatch", decision);
        if (decision.State == TrackMatchReviewState.Accepted && decision.Confidence < decision.AcceptanceThreshold)
            return Skip(entry, PlaylistPreviewEntryStatus.BelowAcceptanceThreshold, "accepted_match_below_persisted_threshold", decision);
        if (decision.State == TrackMatchReviewState.Rejected)
            return Skip(entry, PlaylistPreviewEntryStatus.Rejected, "match_state_rejected", decision);
        if (decision.State is not (TrackMatchReviewState.Accepted or TrackMatchReviewState.Pinned))
            return Skip(entry, Map(decision.State), $"match_state_{decision.State.ToString().ToLowerInvariant()}", decision);

        var route = decision.Route ?? InferRoute(decision);
        if (route.Kind == TrackRouteKind.External)
            return Skip(entry, PlaylistPreviewEntryStatus.MissingLocalItem,
                "accepted_match_has_external_only_route_for_backend", decision, route,
                PlaylistMaterializationOutcomeCodes.SkippedExternalOnlyForBackend);
        if (route.Kind != TrackRouteKind.Local)
        {
            var status = route.LibraryTrackId.HasValue || decision.LibraryTrackId.HasValue
                ? PlaylistPreviewEntryStatus.WrongBackend
                : PlaylistPreviewEntryStatus.Unresolved;
            return Skip(entry, status,
                status == PlaylistPreviewEntryStatus.WrongBackend
                    ? "local_item_is_not_in_selected_backend_library"
                    : "accepted_match_has_no_resolved_route",
                decision, route,
                status == PlaylistPreviewEntryStatus.WrongBackend
                    ? PlaylistMaterializationOutcomeCodes.SkippedWrongBackendOrLibrary
                    : PlaylistMaterializationOutcomeCodes.SkippedUnresolved);
        }
        if (route.LibraryTrackId == null || string.IsNullOrWhiteSpace(route.BackendItemId))
            return Skip(entry, PlaylistPreviewEntryStatus.WrongBackend, "accepted_match_has_no_local_backend_item", decision, route,
                PlaylistMaterializationOutcomeCodes.SkippedWrongBackendOrLibrary);
        if (!target.BackendInstanceId.Equals(route.BackendInstanceId, StringComparison.Ordinal))
            return Skip(entry, PlaylistPreviewEntryStatus.WrongBackend, "local_item_belongs_to_different_backend_instance_or_library", decision, route,
                PlaylistMaterializationOutcomeCodes.SkippedWrongBackendOrLibrary);

        return new PlaylistPreviewEntry(
            entry.SourceEntryId,
            entry.SourcePosition,
            entry.SourceTrackReference,
            PlaylistPreviewEntryStatus.Included,
            route.LibraryTrackId,
            route.BackendItemId,
            targetPosition,
            decision.Reasons,
            decision.Warnings,
            ResolvedRoute: route,
            TargetEligible: true,
            OutcomeCode: PlaylistMaterializationOutcomeCodes.IncludedNativeBackendItem)
        {
            SourceIdentity = entry.SourceIdentity,
            SourceMetadata = entry.Metadata
        };
    }

    private static PlaylistPreviewEntry Skip(
        ImmutablePlaylistSourceEntry entry,
        PlaylistPreviewEntryStatus status,
        string reason,
        PersistedPlaylistMatchDecision? decision = null,
        PlaylistResolvedRoute? route = null,
        string? outcomeCode = null)
    {
        route ??= decision?.Route;
        var code = outcomeCode ?? OutcomeCodeFor(status);
        return new PlaylistPreviewEntry(
            entry.SourceEntryId,
            entry.SourcePosition,
            entry.SourceTrackReference,
            status,
            route?.LibraryTrackId ?? decision?.LibraryTrackId,
            route?.BackendItemId ?? decision?.BackendItemId,
            null,
            (decision?.Reasons ?? []).Concat([reason]).Distinct(StringComparer.Ordinal).ToArray(),
            decision?.Warnings ?? [],
            ResolvedRoute: route,
            TargetEligible: false,
            OutcomeCode: code)
        {
            SourceIdentity = entry.SourceIdentity,
            SourceMetadata = entry.Metadata
        };
    }

    private static PlaylistResolvedRoute InferRoute(PersistedPlaylistMatchDecision decision)
    {
        if (!string.IsNullOrWhiteSpace(decision.BackendItemId) || decision.LibraryTrackId.HasValue)
            return new(TrackRouteKind.Local, decision.LibraryTrackId, decision.BackendItemId,
                decision.BackendInstanceId);
        return decision.Route ?? new(TrackRouteKind.Unresolved);
    }

    private static string OutcomeCodeFor(PlaylistPreviewEntryStatus status) => status switch
    {
        PlaylistPreviewEntryStatus.Included => PlaylistMaterializationOutcomeCodes.IncludedNativeBackendItem,
        PlaylistPreviewEntryStatus.Duplicate => PlaylistMaterializationOutcomeCodes.SkippedDuplicate,
        PlaylistPreviewEntryStatus.Rejected => PlaylistMaterializationOutcomeCodes.SkippedRejected,
        PlaylistPreviewEntryStatus.WrongBackend => PlaylistMaterializationOutcomeCodes.SkippedWrongBackendOrLibrary,
        PlaylistPreviewEntryStatus.MissingLocalItem => PlaylistMaterializationOutcomeCodes.SkippedWrongBackendOrLibrary,
        PlaylistPreviewEntryStatus.StaleDecision => PlaylistMaterializationOutcomeCodes.SkippedStaleRevision,
        _ => PlaylistMaterializationOutcomeCodes.SkippedUnresolved
    };

    private static PlaylistPreviewEntryStatus Map(TrackMatchReviewState state) => state switch
    {
        TrackMatchReviewState.Suggested => PlaylistPreviewEntryStatus.Suggested,
        TrackMatchReviewState.Ambiguous => PlaylistPreviewEntryStatus.Ambiguous,
        TrackMatchReviewState.Rejected => PlaylistPreviewEntryStatus.Rejected,
        _ => PlaylistPreviewEntryStatus.Unresolved
    };

    private static void ValidateTarget(PlaylistPlanningTarget target)
    {
        if (string.IsNullOrWhiteSpace(target.Protocol)) throw new ArgumentException("A target protocol is required.", nameof(target));
        if (string.IsNullOrWhiteSpace(target.BackendInstanceId)) throw new ArgumentException("A target backend instance is required.", nameof(target));
    }
}
