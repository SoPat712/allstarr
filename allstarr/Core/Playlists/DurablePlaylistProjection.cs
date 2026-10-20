using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Core.Settings;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Playlists;

internal sealed record PlaylistRetentionPublication(Guid SnapshotId,
    IReadOnlyDictionary<Guid, Guid?>? Decisions = null, IReadOnlyCollection<Guid>? RematchedExternalIds = null);

public sealed record DurablePlaylistEntryProjection(
    int Position,
    Guid ExternalSnapshotId,
    string ExternalId,
    string Title,
    IReadOnlyList<string> Artists,
    string? Album,
    string? Isrc,
    long? DurationMilliseconds,
    string? DurationProvenance,
    DateTimeOffset? DurationRetrievedAt,
    TrackMatchState? MatchState,
    string? BackendItemId,
    string RouteKind,
    string? RouteProviderId,
    IReadOnlyList<DurableProviderRoute> ProviderRoutes)
{
    internal Guid? CanonicalRecordingId { get; init; }
}

public sealed record DurablePlaylistSourceEntryProjection(
    int Position,
    Guid ExternalSnapshotId,
    PlaylistSourceIdentity Identity,
    PlaylistSourceMetadata Metadata);

public sealed record DurablePlaylistReconciliation(
    int ProviderAdvertisedRows,
    int RawRows,
    int MappedRows,
    int PersistedSourceRows,
    int PublishedRows,
    int Accepted,
    int Tentative,
    int Rejected,
    int Unresolved,
    int PlayableRoutes,
    int MaterializedTargetRows,
    int ProtocolVisibleRows,
    IReadOnlyList<int> AddedPositions,
    IReadOnlyList<int> RemovedPositions,
    IReadOnlyList<int> MovedPositions,
    IReadOnlyList<int> DuplicatedPositions,
    IReadOnlyList<int> ChangedPositions);

public sealed record DurablePlaylistProjection(
    Guid LinkId,
    Guid SnapshotId,
    int SnapshotVersion,
    string Name,
    string SourceProviderId,
    string SourcePlaylistId,
    Guid? ProviderAccountId,
    string TargetProtocol,
    string? TargetPlaylistId,
    string? ArtworkReferenceKey,
    DateTimeOffset RetrievedAt,
    DateTimeOffset? CompletedAt,
    PlaylistSyncState? SyncState,
    IReadOnlyList<DurablePlaylistEntryProjection> Entries,
    int? PlannedTargetTrackCount = null,
    long? PlannedTargetDurationMilliseconds = null,
    int? VerifiedTargetTrackCount = null,
    long? VerifiedTargetDurationMilliseconds = null,
    string? VerificationCode = null,
    DateTimeOffset? VerifiedAt = null)
{
    public string? SyncFailureCode { get; init; }
    public string? Description { get; init; }
    public DateTimeOffset? LastMatchedAt { get; init; }
    public Guid? RunId { get; init; }
    public long? Generation { get; init; }
    public int MaterializedCount { get; init; }
    public PlaylistProjectionMode ProjectionMode { get; init; } = PlaylistProjectionMode.Resolved;
    public int LatestSourceSnapshotVersion { get; init; }
    public IReadOnlyList<DurablePlaylistSourceEntryProjection> SourceEntries { get; init; } = [];
    public bool HasNewerSourceGeneration => LatestSourceSnapshotVersion > SnapshotVersion;
    public DurablePlaylistReconciliation? Reconciliation { get; init; }
    public int TotalCount => Entries.Count;
    public int LocalCount => Entries.Count(item => item.RouteKind == "local");
    public int ExternalCount => Entries.Count(item => item.RouteKind == "external");
    public int MissingCount => TotalCount - LocalCount - ExternalCount;
    public int MatchedCount => Entries.Count(item =>
        item.MatchState is TrackMatchState.Accepted or TrackMatchState.Pinned);
    public int ReviewCount => Entries.Count(item =>
        item.MatchState is TrackMatchState.Suggested or TrackMatchState.Ambiguous);
    public int RejectedCount => Entries.Count(item => item.MatchState == TrackMatchState.Rejected);
    public int PlayableCount => LocalCount + ExternalCount;
    public IReadOnlyDictionary<string, int> RouteCounts => Entries
        .GroupBy(item => item.RouteProviderId ??
            (item.RouteKind == "local" ? TargetProtocol :
                item.RouteKind == "unmatched" ? "unresolved" : item.RouteKind),
            StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
    public int UnknownDurationCount => Entries.Count(item => !item.DurationMilliseconds.HasValue);
    public long? DurationMilliseconds => Entries.Any(item => item.DurationMilliseconds.HasValue)
        ? Entries.Sum(item => item.DurationMilliseconds ?? 0)
        : null;
}

public static class PlaylistProjectionSelector
{
    public static IReadOnlyList<T>? Select<T>(
        PlaylistProjectionMode mode,
        IReadOnlyList<T> source,
        IReadOnlyList<T> resolved,
        IReadOnlyList<T>? target = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(resolved);
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (source.Count != resolved.Count)
            throw new ArgumentException("Source and resolved projections must contain one row per source entry.");
        return mode switch
        {
            PlaylistProjectionMode.Source => source,
            PlaylistProjectionMode.Resolved => resolved,
            PlaylistProjectionMode.Target => target,
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
    }
}

public sealed class DurablePlaylistProjectionReader(
    IDbContextFactory<AllstarrDbContext> factory,
    IBackendLibraryAccessResolver libraryAccess,
    IProtocolProviderGateway? providerGateway = null,
    IEffectiveProviderPolicyResolver? effectivePolicies = null)
{
    public async Task<DurablePlaylistProjection?> ReadByNameAsync(
        Guid? ownerUserId,
        string name,
        CancellationToken cancellationToken = default,
        Guid? viewerUserId = null)
    {
        await using var database = await factory.CreateDbContextAsync(cancellationToken);
        var normalizedName = name.Trim().ToLowerInvariant();
        var snapshots = database.PlaylistSourceSnapshots.AsNoTracking()
            .Where(item => item.Name.ToLower() == normalizedName &&
                           item.PublishedAt.HasValue);
        if (ownerUserId.HasValue)
            snapshots = snapshots.Where(item => item.OwnerUserId == ownerUserId.Value);
        var snapshot = await snapshots
            .OrderByDescending(item => item.SnapshotVersion)
            .ThenByDescending(item => item.RetrievedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (snapshot == null) return null;

        return await ProjectAsync(database, snapshot, viewerUserId ?? ownerUserId ?? snapshot.OwnerUserId, cancellationToken);
    }

    public async Task<DurablePlaylistProjection?> ReadByLinkIdAsync(
        Guid? ownerUserId,
        Guid playlistLinkId,
        CancellationToken cancellationToken = default,
        Guid? viewerUserId = null)
    {
        await using var database = await factory.CreateDbContextAsync(cancellationToken);
        var snapshots = database.PlaylistSourceSnapshots.AsNoTracking()
            .Where(item => item.PlaylistLinkId == playlistLinkId &&
                           item.PublishedAt.HasValue);
        if (ownerUserId.HasValue)
            snapshots = snapshots.Where(item => item.OwnerUserId == ownerUserId.Value);
        var snapshot = await snapshots
            .OrderByDescending(item => item.SnapshotVersion)
            .ThenByDescending(item => item.RetrievedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (snapshot == null) return null;

        return await ProjectAsync(database, snapshot, viewerUserId ?? ownerUserId ?? snapshot.OwnerUserId, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<Guid, DurablePlaylistProjection>> ReadByLinkIdsAsync(
        Guid? ownerUserId,
        IReadOnlyCollection<Guid> playlistLinkIds,
        CancellationToken cancellationToken = default,
        Guid? viewerUserId = null)
    {
        var result = new Dictionary<Guid, DurablePlaylistProjection>();
        // ponytail: project sequentially; bulk-load only if measured list latency requires it.
        foreach (var playlistLinkId in playlistLinkIds)
        {
            var projection = await ReadByLinkIdAsync(
                ownerUserId, playlistLinkId, cancellationToken, viewerUserId);
            if (projection != null) result[playlistLinkId] = projection;
        }
        return result;
    }

    internal Task<BackendLibraryAccessContext> ResolveRetentionAccessAsync(Guid ownerUserId,
        CancellationToken cancellationToken) => libraryAccess.ResolveUserAsync(ownerUserId, cancellationToken);

    internal async Task<DurablePlaylistProjection?> ReadForRetentionAsync(Guid ownerUserId, Guid playlistLinkId,
        CancellationToken cancellationToken)
    {
        var access = await ResolveRetentionAccessAsync(ownerUserId, cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await ReadForRetentionAsync(db, ownerUserId, playlistLinkId, access, cancellationToken);
    }

    internal async Task<DurablePlaylistProjection?> ReadForRetentionAsync(
        AllstarrDbContext database, Guid ownerUserId, Guid playlistLinkId,
        BackendLibraryAccessContext access, CancellationToken cancellationToken, PlaylistRetentionPublication? publication = null)
    {
        var snapshot = await RetentionSnapshotAsync(database, ownerUserId, playlistLinkId, publication, cancellationToken);
        if (snapshot == null || publication != null && snapshot.Id != publication.SnapshotId) return null;
        var sourceIds = await database.PlaylistSourceEntries.AsNoTracking()
            .Where(item => item.PlaylistSourceSnapshotId == snapshot.Id)
            .Select(item => item.Id).ToArrayAsync(cancellationToken);
        var entries = await new DurablePlaylistProjectionReader(factory, libraryAccess)
            .ReadRetentionEntriesAsync(database, ownerUserId, playlistLinkId, snapshot.Id,
                sourceIds, access, cancellationToken, publication);
        if (entries.Count != sourceIds.Length)
            throw new UnauthorizedAccessException("The retained playlist projection is no longer fully available.");
        var link = await database.PlaylistLinks.AsNoTracking().SingleAsync(item =>
            item.Id == playlistLinkId && item.OwnerUserId == ownerUserId, cancellationToken);
        return new(link.Id, snapshot.Id, snapshot.SnapshotVersion, snapshot.Name,
            link.SourceProviderId, link.SourcePlaylistId, link.ProviderAccountId, link.TargetProtocol,
            link.TargetPlaylistId, null, snapshot.RetrievedAt, null, null,
            entries.Values.OrderBy(item => item.Position).ToArray());
    }

    /// <summary>
    /// Reads one retained source row without materializing the complete playlist.
    /// The batch overload below is the shared bounded loader for retention workers.
    /// </summary>
    internal async Task<DurablePlaylistEntryProjection?> ReadRetentionEntryAsync(
        AllstarrDbContext database,
        Guid ownerUserId,
        Guid playlistLinkId,
        Guid expectedCurrentSnapshotId,
        Guid sourceEntryId,
        BackendLibraryAccessContext access,
        CancellationToken cancellationToken)
    {
        var entries = await ReadRetentionEntriesAsync(database, ownerUserId, playlistLinkId,
            expectedCurrentSnapshotId, [sourceEntryId], access, cancellationToken);
        return entries.GetValueOrDefault(sourceEntryId);
    }

    /// <summary>
    /// Loads only the requested source entries and their route inputs. It deliberately
    /// omits full-list reconciliation, sync history, and UI source projections.
    /// </summary>
    internal async Task<IReadOnlyDictionary<Guid, DurablePlaylistEntryProjection>> ReadRetentionEntriesAsync(
        AllstarrDbContext database,
        Guid ownerUserId,
        Guid playlistLinkId,
        Guid expectedCurrentSnapshotId,
        IReadOnlyCollection<Guid> sourceEntryIds,
        BackendLibraryAccessContext access,
        CancellationToken cancellationToken, PlaylistRetentionPublication? publication = null)
    {
        if (sourceEntryIds.Count == 0) return new Dictionary<Guid, DurablePlaylistEntryProjection>();

        var snapshot = await RetentionSnapshotAsync(database, ownerUserId, playlistLinkId, publication, cancellationToken);
        if (snapshot == null || snapshot.Id != expectedCurrentSnapshotId)
            return new Dictionary<Guid, DurablePlaylistEntryProjection>();

        var link = await database.PlaylistLinks.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == playlistLinkId && item.OwnerUserId == ownerUserId &&
                item.ProviderAccountId == snapshot.ProviderAccountId, cancellationToken);
        var user = await database.Users.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == ownerUserId && item.Enabled, cancellationToken);
        var principal = access.Context?.Principal;
        if (link == null || user == null ||
            user.BackendType != link.TargetProtocol ||
            user.BackendInstanceId != link.TargetBackendInstanceId ||
            principal != null &&
            (principal.UserId != ownerUserId ||
             principal.BackendType != link.TargetProtocol ||
             principal.BackendInstanceId != link.TargetBackendInstanceId ||
             principal.BackendPrincipalId != user.BackendPrincipalId))
            return new Dictionary<Guid, DurablePlaylistEntryProjection>();

        var requestedIds = sourceEntryIds.Distinct().ToArray();
        var requestedEntries = await database.PlaylistSourceEntries.AsNoTracking()
            .Where(item => EF.Parameter(requestedIds).Contains(item.Id) && item.PlaylistSourceSnapshotId == snapshot.Id)
            .OrderBy(item => item.SourcePosition)
            .ToListAsync(cancellationToken);
        if (requestedEntries.Count == 0)
            return new Dictionary<Guid, DurablePlaylistEntryProjection>();

        if (publication?.Decisions is { } decisions)
        {
            if (decisions.Count != requestedEntries.Count || requestedEntries.Any(entry =>
                    !decisions.TryGetValue(entry.Id, out var id) || id == null))
                throw new InvalidOperationException("The publication requires one decision per source entry.");
            foreach (var entry in requestedEntries) entry.PublishedTrackMatchId = decisions[entry.Id];
        }
        else if (publication?.RematchedExternalIds is { } rematched)
        {
            var latest = await LoadLatestRematchDecisionsAsync(database, ownerUserId, rematched, cancellationToken);
            if (latest.Count != rematched.Distinct().Count() ||
                requestedEntries.Select(entry => entry.ExternalMetadataSnapshotId).Intersect(rematched).Count() != latest.Count)
                throw new InvalidOperationException("The rematched publication requires one decision per source entry.");
            foreach (var entry in requestedEntries)
                if (latest.TryGetValue(entry.ExternalMetadataSnapshotId, out var decision)) entry.PublishedTrackMatchId = decision.Id;
        }

        var externalIds = requestedEntries.Select(item => item.ExternalMetadataSnapshotId).Distinct().ToArray();
        var external = await database.ExternalMetadataSnapshots.AsNoTracking()
            .Where(item => EF.Parameter(externalIds).Contains(item.Id) && item.OwnerUserId == ownerUserId &&
                           item.ProviderAccountId == link.ProviderAccountId &&
                           item.Protocol == link.TargetProtocol &&
                           item.BackendInstanceId == link.TargetBackendInstanceId &&
                           item.BackendPrincipalId == user.BackendPrincipalId &&
                           item.ResourceKind == "track")
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        var rows = requestedEntries
            .Where(item => external.ContainsKey(item.ExternalMetadataSnapshotId))
            .Select(item => (Entry: item, External: external[item.ExternalMetadataSnapshotId]))
            .ToArray();
        if (rows.Length == 0)
            return new Dictionary<Guid, DurablePlaylistEntryProjection>();

        var identityIds = rows.Where(item => item.External.ProviderTrackIdentityId.HasValue)
            .Select(item => item.External.ProviderTrackIdentityId!.Value).Distinct().ToArray();
        var externalHashes = rows.Select(item => item.External.ExternalIdHash)
            .Distinct(StringComparer.Ordinal).ToArray();
        var sourceCandidates = await database.ProviderTrackIdentities.AsNoTracking()
            .Where(item => item.ResourceKind == ProviderResourceKind.Track &&
                           (EF.Parameter(identityIds).Contains(item.Id) || EF.Parameter(externalHashes).Contains(item.ExternalIdHash)))
            .ToListAsync(cancellationToken);
        var sourceIdentityIndex = BuildRetentionSourceIdentityIndex(sourceCandidates);
        var scopedSourceIdentities = external.Values
            .Select(item => (item.Id, Identity: SelectRetentionSourceIdentity(item, sourceIdentityIndex)))
            .Where(item => item.Identity != null)
            .ToDictionary(item => item.Id, item => item.Identity!);

        var externalValues = external.Values.ToArray();
        var overrideRecords = await ManualTrackOverrides.LoadAsync(
            database, ownerUserId, externalValues, cancellationToken);
        var overrides = ManualTrackOverrides.Index(externalValues, overrideRecords, ownerUserId);

        var matchIds = rows.Where(item => item.Entry.PublishedTrackMatchId.HasValue)
            .Select(item => item.Entry.PublishedTrackMatchId!.Value).Distinct().ToArray();
        var matches = matchIds.Length == 0
            ? new Dictionary<Guid, TrackMatchRecord>()
            : await database.TrackMatches.AsNoTracking()
                .Where(item => EF.Parameter(matchIds).Contains(item.Id) && item.OwnerUserId == ownerUserId)
                .ToDictionaryAsync(item => item.Id, cancellationToken);
        var canonicalIds = scopedSourceIdentities.Values.Select(item => item.CanonicalRecordingId)
            .Concat(matches.Values.Where(item => item.CanonicalRecordingId.HasValue)
                .Select(item => item.CanonicalRecordingId!.Value))
            .Distinct().ToArray();
        var identities = canonicalIds.Length == 0
            ? []
            : await database.ProviderTrackIdentities.AsNoTracking()
                .Where(item => EF.Parameter(canonicalIds).Contains(item.CanonicalRecordingId) &&
                               item.ResourceKind == ProviderResourceKind.Track &&
                               (item.Verification == ProviderIdentityVerification.Verified ||
                                item.Verification == ProviderIdentityVerification.Pinned))
                .ToListAsync(cancellationToken);
        var providerOrder = await ResolveProviderOrderAsync(
            link.SourceProviderId, identities, overrides.Values, ownerUserId, access, cancellationToken);
        var providerPolicy = effectivePolicies == null ? null :
            await effectivePolicies.ResolveForUserAsync(ownerUserId, cancellationToken);
        var libraryIds = matches.Values.Where(item => item.LibraryTrackId.HasValue)
            .Select(item => item.LibraryTrackId!.Value)
            .Concat(overrides.Values.Select(item => item.Effective?.LibraryTrackId)
                .OfType<Guid>()).Distinct().ToArray();
        var matchedCanonicalIds = matches.Values.Where(item => item.CanonicalRecordingId.HasValue)
            .Select(item => item.CanonicalRecordingId!.Value)
            .Concat(canonicalIds).Distinct().ToArray();
        var library = await LibraryTrackAccess.Query(database, access)
            .Where(item => (EF.Parameter(libraryIds).Contains(item.Id) ||
                           item.CanonicalRecordingId.HasValue && EF.Parameter(matchedCanonicalIds).Contains(item.CanonicalRecordingId.Value)) &&
                           item.BackendInstanceId == link.TargetBackendInstanceId &&
                           (link.TargetProtocol == "jellyfin"
                               ? item.Protocol == "jellyfin"
                               : item.Protocol == "subsonic" || item.Protocol == "opensubsonic" ||
                                 item.Protocol == "navidrome"))
            .ToDictionaryAsync(item => item.Id, cancellationToken);

        var identitiesByCanonical = identities.GroupBy(item => item.CanonicalRecordingId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<ProviderTrackIdentityRecord>)group.ToArray());
        var libraryByCanonical = library.Values.Where(item => item.CanonicalRecordingId.HasValue)
            .GroupBy(item => item.CanonicalRecordingId!.Value)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var projected = new Dictionary<Guid, DurablePlaylistEntryProjection>(rows.Length);
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var match = row.Entry.PublishedTrackMatchId is { } matchId
                ? matches.GetValueOrDefault(matchId)
                : null;
            var layers = overrides.GetValueOrDefault(row.External.Id) ?? new(null, null);
            var sourceIdentity = scopedSourceIdentities.GetValueOrDefault(row.External.Id);
            var effectiveCanonicalId = match?.CanonicalRecordingId ?? sourceIdentity?.CanonicalRecordingId;
            var entryIdentities = effectiveCanonicalId is { } canonicalId
                ? identitiesByCanonical.GetValueOrDefault(canonicalId) ?? []
                : Array.Empty<ProviderTrackIdentityRecord>();
            var entryLibrary = new Dictionary<Guid, LibraryTrackRecord>();
            if (match?.LibraryTrackId is { } matchLibraryId && library.TryGetValue(matchLibraryId, out var matchLibrary))
                entryLibrary[matchLibrary.Id] = matchLibrary;
            if (layers.Effective?.LibraryTrackId is { } manualLibraryId && library.TryGetValue(manualLibraryId, out var manualLibrary))
                entryLibrary[manualLibrary.Id] = manualLibrary;
            if (sourceIdentity?.CanonicalRecordingId is { } sourceCanonical &&
                libraryByCanonical.TryGetValue(sourceCanonical, out var sourceLibraries))
                foreach (var item in sourceLibraries) entryLibrary[item.Id] = item;
            if (match?.CanonicalRecordingId is { } matchCanonical &&
                libraryByCanonical.TryGetValue(matchCanonical, out var matchLibraries))
                foreach (var item in matchLibraries) entryLibrary[item.Id] = item;
            var sourceMap = sourceIdentity == null
                ? new Dictionary<Guid, ProviderTrackIdentityRecord>()
                : new Dictionary<Guid, ProviderTrackIdentityRecord> { [row.External.Id] = sourceIdentity };
            projected[row.Entry.Id] = ProjectEntry(row.Entry, row.External, sourceMap,
                entryIdentities, providerOrder, match,
                new Dictionary<Guid, ManualTrackOverrideLayers> { [row.External.Id] = layers }, entryLibrary, providerPolicy);
        }
        return projected;
    }

    private static Task<PlaylistSourceSnapshotRecord?> RetentionSnapshotAsync(AllstarrDbContext database,
        Guid ownerUserId, Guid playlistLinkId, PlaylistRetentionPublication? publication, CancellationToken cancellationToken)
    {
        var query = database.PlaylistSourceSnapshots.AsNoTracking().Where(item =>
            item.PlaylistLinkId == playlistLinkId && item.OwnerUserId == ownerUserId);
        var candidateId = publication?.SnapshotId;
        return query.Where(item => item.PublishedAt != null || item.Id == candidateId)
            .OrderByDescending(item => item.SnapshotVersion).ThenByDescending(item => item.RetrievedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    internal static async Task<Dictionary<Guid, TrackMatchRecord>> LoadLatestRematchDecisionsAsync(
        AllstarrDbContext database, Guid ownerUserId, IReadOnlyCollection<Guid> externalSnapshotIds,
        CancellationToken cancellationToken)
    {
        var externalIds = externalSnapshotIds.Distinct().ToArray();
        var decisions = database.TrackMatches.AsNoTracking().Where(item =>
            item.OwnerUserId == ownerUserId && EF.Parameter(externalIds).Contains(item.ExternalSnapshotId));
        var versions = decisions.GroupBy(item => item.ExternalSnapshotId).Select(group => new
        {
            ExternalSnapshotId = group.Key,
            DecisionVersion = group.Max(item => item.DecisionVersion)
        });
        return await (from decision in decisions
                      join version in versions
                          on new { decision.ExternalSnapshotId, decision.DecisionVersion }
                          equals new { version.ExternalSnapshotId, version.DecisionVersion }
                      select decision).ToDictionaryAsync(item => item.ExternalSnapshotId, cancellationToken);
    }

    private sealed record RetentionSourceIdentityIndex(
        IReadOnlyDictionary<Guid, ProviderTrackIdentityRecord> ById,
        IReadOnlyDictionary<(string ProviderId, Guid ProviderAccountId, string ExternalIdHash), ProviderTrackIdentityRecord> Accounts,
        IReadOnlyDictionary<(string ProviderId, string ExternalIdHash), ProviderTrackIdentityRecord> Catalog);

    private static RetentionSourceIdentityIndex BuildRetentionSourceIdentityIndex(
        IEnumerable<ProviderTrackIdentityRecord> identities)
    {
        var materialized = identities.ToArray();
        var eligible = materialized.Where(identity =>
            (identity.Scope is ProviderIdentityScope.Catalog or ProviderIdentityScope.Account) &&
            (identity.Verification is ProviderIdentityVerification.Verified or ProviderIdentityVerification.Pinned));
        static ProviderTrackIdentityRecord SelectBest(IEnumerable<ProviderTrackIdentityRecord> candidates) => candidates
            .OrderByDescending(identity => identity.Verification == ProviderIdentityVerification.Pinned)
            .ThenByDescending(identity => identity.VerifiedAt)
            .ThenBy(identity => identity.Id)
            .First();

        var accounts = eligible
            .Where(identity => identity.Scope == ProviderIdentityScope.Account && identity.ProviderAccountId.HasValue)
            .GroupBy(identity => (
                ProviderId: identity.ProviderId.ToLowerInvariant(),
                ProviderAccountId: identity.ProviderAccountId!.Value,
                identity.ExternalIdHash))
            .ToDictionary(group => group.Key, group => SelectBest(group));
        var catalog = eligible
            .Where(identity => identity.Scope == ProviderIdentityScope.Catalog && !identity.ProviderAccountId.HasValue)
            .GroupBy(identity => (ProviderId: identity.ProviderId.ToLowerInvariant(), identity.ExternalIdHash))
            .ToDictionary(group => group.Key, group => SelectBest(group));
        return new(materialized.ToDictionary(identity => identity.Id), accounts, catalog);
    }

    private static ProviderTrackIdentityRecord? SelectRetentionSourceIdentity(
        ExternalMetadataSnapshotRecord external,
        RetentionSourceIdentityIndex index)
    {
        if (external.ProviderTrackIdentityId is { } linkedId &&
            index.ById.TryGetValue(linkedId, out var linked) &&
            MatchesSourceIdentity(external, linked))
            return linked;

        if (external.ProviderAccountId is { } accountId &&
            index.Accounts.TryGetValue((external.ProviderId.ToLowerInvariant(), accountId, external.ExternalIdHash),
                out var account) &&
            MatchesSourceIdentity(external, account))
            return account;

        return index.Catalog.TryGetValue((external.ProviderId.ToLowerInvariant(), external.ExternalIdHash),
            out var catalog) && MatchesSourceIdentity(external, catalog) ? catalog : null;
    }

    private async Task<IReadOnlyList<string>> ResolveProviderOrderAsync(
        string sourceProviderId,
        IEnumerable<ProviderTrackIdentityRecord> identities,
        IEnumerable<ManualTrackOverrideLayers> overrides,
        Guid viewerUserId,
        BackendLibraryAccessContext access,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> fallback = new[] { sourceProviderId }
            .Concat(identities.Select(item => item.ProviderId))
            .Concat(overrides.Select(item => item.Effective?.TargetProviderId).OfType<string>())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (providerGateway == null && effectivePolicies != null)
        {
            var policy = await effectivePolicies.ResolveForUserAsync(viewerUserId, cancellationToken);
            fallback = policy.ApplyProviderAvailability(ProviderCapabilityKind.Streaming, fallback);
        }
        return providerGateway == null
            ? fallback
            : access.Context?.Actor is { } actor
                ? await providerGateway.GetPlayableProviderOrderAsync(actor, cancellationToken)
                : [];
    }

    private async Task<DurablePlaylistProjection> ProjectAsync(
        AllstarrDbContext database,
        PlaylistSourceSnapshotRecord snapshot,
        Guid viewerUserId,
        CancellationToken cancellationToken,
        BackendLibraryAccessContext? retainedAccess = null)
    {
        var ownerUserId = snapshot.OwnerUserId;
        var link = await database.PlaylistLinks.AsNoTracking()
            .SingleAsync(item => item.Id == snapshot.PlaylistLinkId &&
                                 item.OwnerUserId == ownerUserId &&
                                 item.ProviderAccountId == snapshot.ProviderAccountId,
                cancellationToken);
        var entries = await database.PlaylistSourceEntries.AsNoTracking()
            .Where(item => item.PlaylistSourceSnapshotId == snapshot.Id)
            .OrderBy(item => item.SourcePosition)
            .ToListAsync(cancellationToken);
        var externalIds = entries.Select(item => item.ExternalMetadataSnapshotId).ToArray();
        var external = await database.ExternalMetadataSnapshots.AsNoTracking()
            .Where(item => externalIds.Contains(item.Id) &&
                           item.OwnerUserId == ownerUserId &&
                           item.ProviderAccountId == link.ProviderAccountId &&
                           item.BackendInstanceId == link.TargetBackendInstanceId)
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        var identityIds = external.Values
            .Where(item => item.ProviderTrackIdentityId.HasValue)
            .Select(item => item.ProviderTrackIdentityId!.Value)
            .ToArray();
        var externalHashes = external.Values
            .Select(item => item.ExternalIdHash)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var sourceIdentities = await database.ProviderTrackIdentities.AsNoTracking()
            .Where(item => item.ResourceKind == ProviderResourceKind.Track &&
                           (identityIds.Contains(item.Id) ||
                            externalHashes.Contains(item.ExternalIdHash)))
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        var linkedSourceIdentities = external.Values
            .Select(item =>
            {
                if (item.ProviderTrackIdentityId is not { } identityId ||
                    !sourceIdentities.TryGetValue(identityId, out var identity) ||
                    !MatchesSourceIdentity(item, identity))
                    return (item.Id, Identity: (ProviderTrackIdentityRecord?)null);
                return (item.Id, Identity: identity);
            })
            .Where(item => item.Identity != null)
            .ToDictionary(item => item.Id, item => item.Identity!);
        var sourceIdentityIndex = BuildRetentionSourceIdentityIndex(sourceIdentities.Values);
        var scopedSourceIdentities = external.Values
            .Select(item =>
            {
                if (linkedSourceIdentities.TryGetValue(item.Id, out var linked))
                    return (item.Id, Identity: linked);
                return (item.Id, Identity: SelectRetentionSourceIdentity(item, sourceIdentityIndex));
            })
            .Where(item => item.Identity != null)
            .ToDictionary(item => item.Id, item => item.Identity!);
        var canonicalIds = scopedSourceIdentities.Values
            .Select(item => item.CanonicalRecordingId)
            .Distinct()
            .ToArray();
        var identities = await database.ProviderTrackIdentities.AsNoTracking()
            .Where(item => canonicalIds.Contains(item.CanonicalRecordingId) &&
                           item.ResourceKind == ProviderResourceKind.Track &&
                           (item.Verification == ProviderIdentityVerification.Verified ||
                            item.Verification == ProviderIdentityVerification.Pinned))
            .ToListAsync(cancellationToken);
        var overrideRecords = await ManualTrackOverrides.LoadAsync(
            database, viewerUserId, external.Values.ToArray(), cancellationToken);
        var overrides = ManualTrackOverrides.Index(external.Values, overrideRecords, viewerUserId);
        var access = retainedAccess ?? await libraryAccess.ResolveUserAsync(viewerUserId, cancellationToken);
        var publishedMatchIds = entries
            .Where(item => item.PublishedTrackMatchId.HasValue)
            .Select(item => item.PublishedTrackMatchId!.Value)
            .Distinct()
            .ToArray();
        var publishedMatches = await database.TrackMatches.AsNoTracking()
            .Where(item => publishedMatchIds.Contains(item.Id) &&
                           item.OwnerUserId == ownerUserId)
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        var matchedCanonicalIdsForIdentities = publishedMatches.Values
            .Where(item => item.CanonicalRecordingId.HasValue)
            .Select(item => item.CanonicalRecordingId!.Value)
            .Except(canonicalIds)
            .ToArray();
        if (matchedCanonicalIdsForIdentities.Length != 0)
        {
            var matchedIdentities = await database.ProviderTrackIdentities.AsNoTracking()
                .Where(item => matchedCanonicalIdsForIdentities.Contains(item.CanonicalRecordingId) &&
                               item.ResourceKind == ProviderResourceKind.Track &&
                               (item.Verification == ProviderIdentityVerification.Verified ||
                                item.Verification == ProviderIdentityVerification.Pinned))
                .ToListAsync(cancellationToken);
            identities = identities.Concat(matchedIdentities).ToList();
        }
        var providerOrder = await ResolveProviderOrderAsync(
            link.SourceProviderId, identities, overrides.Values, viewerUserId, access, cancellationToken);
        var providerPolicy = effectivePolicies == null ? null :
            await effectivePolicies.ResolveForUserAsync(viewerUserId, cancellationToken);
        var libraryIds = publishedMatches.Values
            .Where(item => item.LibraryTrackId.HasValue)
            .Select(item => item.LibraryTrackId!.Value)
            .Concat(overrides.Values.Select(item => item.Effective)
                .Where(item => item?.LibraryTrackId.HasValue == true)
                .Select(item => item!.LibraryTrackId!.Value))
            .Distinct()
            .ToArray();
        var matchedCanonicalIds = publishedMatches.Values.Where(item => item.CanonicalRecordingId.HasValue)
            .Select(item => item.CanonicalRecordingId!.Value).Concat(canonicalIds).Distinct().ToArray();
        var library = await LibraryTrackAccess.Query(database, access)
            .Where(item => (libraryIds.Contains(item.Id) ||
                           item.CanonicalRecordingId.HasValue && matchedCanonicalIds.Contains(item.CanonicalRecordingId.Value)) &&
                           item.BackendInstanceId == link.TargetBackendInstanceId &&
                           (link.TargetProtocol == "jellyfin"
                               ? item.Protocol == "jellyfin"
                               : item.Protocol == "subsonic" ||
                                 item.Protocol == "opensubsonic" ||
                                 item.Protocol == "navidrome"))
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        var run = await database.PlaylistSyncRuns.AsNoTracking()
            .Where(item => item.PlaylistLinkId == link.Id &&
                           item.OwnerUserId == ownerUserId &&
                           item.PlaylistSourceSnapshotId == snapshot.Id &&
                           item.State != PlaylistSyncState.Pending &&
                           item.State != PlaylistSyncState.Running)
            .OrderByDescending(item => item.Generation)
            .FirstOrDefaultAsync(cancellationToken);
        var lastSuccessfulSyncAt = await database.PlaylistSyncRuns.AsNoTracking()
            .Where(item => item.PlaylistLinkId == link.Id &&
                           item.OwnerUserId == ownerUserId &&
                           (item.State == PlaylistSyncState.Succeeded ||
                            item.State == PlaylistSyncState.PartiallySucceeded))
            .OrderByDescending(item => item.CompletedAt)
            .Select(item => item.CompletedAt)
            .FirstOrDefaultAsync(cancellationToken);
        var materializedCount = run == null
            ? 0
            : await database.PlaylistSyncEntryResults.AsNoTracking()
                .Where(item => item.PlaylistSyncRunId == run.Id &&
                               (item.Outcome == PlaylistEntryOutcome.Reused ||
                                item.Outcome == PlaylistEntryOutcome.Added ||
                                item.Outcome == PlaylistEntryOutcome.Reordered))
                .Select(item => item.PlaylistSourceEntryId)
                .Distinct()
                .CountAsync(cancellationToken);
        var latestSourceSnapshotVersion = await database.PlaylistSourceSnapshots.AsNoTracking()
            .Where(item => item.PlaylistLinkId == link.Id && item.OwnerUserId == ownerUserId)
            .MaxAsync(item => (int?)item.SnapshotVersion, cancellationToken)
            ?? snapshot.SnapshotVersion;
        var previousSnapshot = await database.PlaylistSourceSnapshots.AsNoTracking()
            .Where(item => item.PlaylistLinkId == link.Id &&
                           item.OwnerUserId == ownerUserId &&
                           item.PublishedAt.HasValue &&
                           item.ProviderAccountId == snapshot.ProviderAccountId &&
                           item.SnapshotVersion < snapshot.SnapshotVersion)
            .OrderByDescending(item => item.SnapshotVersion)
            .FirstOrDefaultAsync(cancellationToken);
        var previousEntries = previousSnapshot == null
            ? []
            : await database.PlaylistSourceEntries.AsNoTracking()
                .Where(item => item.PlaylistSourceSnapshotId == previousSnapshot.Id)
                .OrderBy(item => item.SourcePosition)
                .ToListAsync(cancellationToken);
        var previousExternalIds = previousEntries
            .Select(item => item.ExternalMetadataSnapshotId)
            .Distinct()
            .ToArray();
        var previousExternal = previousExternalIds.Length == 0
            ? new Dictionary<Guid, ExternalMetadataSnapshotRecord>()
            : await database.ExternalMetadataSnapshots.AsNoTracking()
                .Where(item => previousExternalIds.Contains(item.Id) &&
                               item.OwnerUserId == ownerUserId &&
                               item.ProviderAccountId == link.ProviderAccountId &&
                               item.BackendInstanceId == link.TargetBackendInstanceId)
                .ToDictionaryAsync(item => item.Id, cancellationToken);
        var sourceEntries = entries.Select(entry =>
            ProjectSourceEntry(
                entry,
                external[entry.ExternalMetadataSnapshotId],
                linkedSourceIdentities))
            .ToArray();
        var projectedEntries = entries.Select(entry => ProjectEntry(
                entry,
                external[entry.ExternalMetadataSnapshotId],
                scopedSourceIdentities,
                identities,
                providerOrder,
                entry.PublishedTrackMatchId is { } matchId
                    ? publishedMatches.GetValueOrDefault(matchId)
                    : null,
                overrides,
                library, providerPolicy))
            .ToArray();

        return new(
            link.Id,
            snapshot.Id,
            snapshot.SnapshotVersion,
            snapshot.Name,
            link.SourceProviderId,
            link.SourcePlaylistId,
            link.ProviderAccountId,
            link.TargetProtocol,
            link.TargetPlaylistId,
            snapshot.ArtworkReferenceKey,
            snapshot.RetrievedAt,
            lastSuccessfulSyncAt,
            run?.State,
            projectedEntries,
            run?.PlannedTargetTrackCount,
            run?.PlannedTargetDurationMilliseconds,
            run?.VerifiedTargetTrackCount,
            run?.VerifiedTargetDurationMilliseconds,
            run?.VerificationCode,
            run?.VerifiedAt)
        {
            SyncFailureCode = run?.ConflictCode,
            Description = snapshot.Description,
            LastMatchedAt = publishedMatches.Count == 0
                ? null
                : publishedMatches.Values.Max(item => item.DecidedAt),
            RunId = run?.Id,
            Generation = run?.Generation,
            MaterializedCount = materializedCount,
            ProjectionMode = link.ProjectionMode,
            LatestSourceSnapshotVersion = latestSourceSnapshotVersion,
            SourceEntries = sourceEntries,
            Reconciliation = BuildReconciliation(
                entries,
                external,
                previousEntries,
                previousExternal,
                projectedEntries,
                materializedCount)
        };
    }

    private static DurablePlaylistReconciliation BuildReconciliation(
        IReadOnlyList<PlaylistSourceEntryRecord> entries,
        IReadOnlyDictionary<Guid, ExternalMetadataSnapshotRecord> external,
        IReadOnlyList<PlaylistSourceEntryRecord> previousEntries,
        IReadOnlyDictionary<Guid, ExternalMetadataSnapshotRecord> previousExternal,
        IReadOnlyList<DurablePlaylistEntryProjection> projected,
        int materializedCount)
    {
        var currentRows = entries.Select(item => (
            item.SourcePosition,
            External: external[item.ExternalMetadataSnapshotId],
            Metadata: ReadMetadata(external[item.ExternalMetadataSnapshotId].PayloadJson))).ToArray();
        var priorRows = previousEntries.Select(item => (
            item.SourcePosition,
            External: previousExternal[item.ExternalMetadataSnapshotId],
            Metadata: ReadMetadata(previousExternal[item.ExternalMetadataSnapshotId].PayloadJson))).ToArray();
        var added = new List<int>();
        var removed = new List<int>();
        var moved = new List<int>();
        var changed = new List<int>();
        foreach (var hash in currentRows.Select(item => item.External.ExternalIdHash)
                     .Concat(priorRows.Select(item => item.External.ExternalIdHash))
                     .Distinct(StringComparer.Ordinal))
        {
            var currentTracks = currentRows
                .Where(item => item.External.ExternalIdHash == hash)
                .OrderBy(item => item.SourcePosition)
                .ToArray();
            var priorTracks = priorRows
                .Where(item => item.External.ExternalIdHash == hash)
                .OrderBy(item => item.SourcePosition)
                .ToArray();
            var paired = Math.Min(currentTracks.Length, priorTracks.Length);
            var pairs = currentTracks.Take(paired).Zip(priorTracks.Take(paired)).ToArray();
            moved.AddRange(pairs
                .Where(pair => pair.First.SourcePosition != pair.Second.SourcePosition)
                .Select(pair => pair.First.SourcePosition));
            changed.AddRange(pairs
                .Where(pair => !SameMetadata(pair.First.Metadata, pair.Second.Metadata))
                .Select(pair => pair.First.SourcePosition));
            added.AddRange(currentTracks.Skip(paired).Select(item => item.SourcePosition));
            removed.AddRange(priorTracks.Skip(paired).Select(item => item.SourcePosition));
        }
        var duplicated = currentRows
            .GroupBy(item => item.External.ExternalIdHash, StringComparer.Ordinal)
            .SelectMany(group => group.OrderBy(item => item.SourcePosition).Skip(1))
            .Select(item => item.SourcePosition)
            .Order()
            .ToArray();
        var accepted = projected.Count(item =>
            item.MatchState is TrackMatchState.Accepted or TrackMatchState.Pinned);
        var tentative = projected.Count(item =>
            item.MatchState is TrackMatchState.Suggested or TrackMatchState.Ambiguous);
        var rejected = projected.Count(item => item.MatchState == TrackMatchState.Rejected);
        var unresolved = projected.Count - accepted - tentative - rejected;
        var playable = projected.Count(item => item.RouteKind != "unmatched");
        return new(
            entries.Count,
            entries.Count,
            entries.Count,
            entries.Count,
            entries.Count,
            accepted,
            tentative,
            rejected,
            unresolved,
            playable,
            materializedCount,
            projected.Count,
            added.Order().ToArray(),
            removed.Order().ToArray(),
            moved.Distinct().Order().ToArray(),
            duplicated,
            changed.Order().ToArray());
    }

    private static bool SameMetadata(EntryMetadata current, EntryMetadata prior) =>
        current.Title == prior.Title &&
        current.Artists.SequenceEqual(prior.Artists, StringComparer.Ordinal) &&
        current.Album == prior.Album &&
        current.Isrc == prior.Isrc &&
        current.DurationMilliseconds == prior.DurationMilliseconds;

    private static bool MatchesSourceIdentity(
        ExternalMetadataSnapshotRecord external,
        ProviderTrackIdentityRecord identity) =>
        identity.ProviderId.Equals(external.ProviderId, StringComparison.OrdinalIgnoreCase) &&
        (identity.ProviderAccountId == external.ProviderAccountId ||
         identity.Scope == ProviderIdentityScope.Catalog && !identity.ProviderAccountId.HasValue) &&
        identity.ResourceKind == ProviderResourceKind.Track &&
        identity.Scope is ProviderIdentityScope.Catalog or ProviderIdentityScope.Account &&
        identity.Verification is ProviderIdentityVerification.Verified or ProviderIdentityVerification.Pinned &&
        identity.ExternalIdHash == external.ExternalIdHash;

    private static DurablePlaylistSourceEntryProjection ProjectSourceEntry(
        PlaylistSourceEntryRecord entry,
        ExternalMetadataSnapshotRecord external,
        IReadOnlyDictionary<Guid, ProviderTrackIdentityRecord> linkedSourceIdentities)
    {
        var sourceExternalId = linkedSourceIdentities.GetValueOrDefault(external.Id)?.ExternalId;
        if (string.IsNullOrWhiteSpace(sourceExternalId) ||
            sourceExternalId.Equals(external.ExternalIdHash, StringComparison.Ordinal))
            sourceExternalId = null;
        return new(
            entry.SourcePosition,
            external.Id,
            new PlaylistSourceIdentity(
                external.ProviderId,
                external.ProviderAccountId,
                external.ExternalIdHash,
                external.ProviderRevision,
                external.SnapshotVersion,
                sourceExternalId),
            ReadSourceMetadata(external.PayloadJson));
    }

    private static DurablePlaylistEntryProjection ProjectEntry(
        PlaylistSourceEntryRecord entry,
        ExternalMetadataSnapshotRecord external,
        IReadOnlyDictionary<Guid, ProviderTrackIdentityRecord> sourceIdentities,
        IReadOnlyList<ProviderTrackIdentityRecord> identities,
        IReadOnlyList<string> providerOrder,
        TrackMatchRecord? match,
        IReadOnlyDictionary<Guid, ManualTrackOverrideLayers> overrides,
        IReadOnlyDictionary<Guid, LibraryTrackRecord> library,
        EffectiveProviderPolicySnapshot? providerPolicy = null)
    {
        var manual = overrides.GetValueOrDefault(external.Id)?.Effective;
        var identity = sourceIdentities.GetValueOrDefault(external.Id);
        var route = TrackRouteProjector.Project(
            external, match, manual, identity, library.Values, identities, providerOrder, providerPolicy);
        var classification = route.Classification;
        var local = route.LibraryTrack;
        var backendItemId = local?.BackendItemId;
        var metadata = ReadMetadata(external.PayloadJson);
        var primaryRoute = classification.PrimaryProviderRoute;
        var hasExternal = classification.RouteKind == TrackRouteKind.External;
        var externalId = hasExternal
            ? primaryRoute!.ExternalId
            : external.ExternalIdHash;
        var routeKind = classification.RouteKind switch
        {
            TrackRouteKind.Local => "local",
            TrackRouteKind.External => "external",
            _ => "unmatched"
        };
        return new(
            entry.SourcePosition,
            external.Id,
            externalId,
            metadata.Title,
            metadata.Artists,
            metadata.Album,
            metadata.Isrc,
            local?.DurationMilliseconds ?? metadata.DurationMilliseconds,
            local?.DurationMilliseconds.HasValue == true
                ? local.DurationProvenance ?? local.Protocol
                : metadata.DurationMilliseconds.HasValue ? external.ProviderId : null,
            local?.DurationMilliseconds.HasValue == true
                ? local.DurationRetrievedAt ?? local.IndexedAt
                : metadata.DurationMilliseconds.HasValue ? external.RetrievedAt : null,
            classification.ReviewState,
            backendItemId,
            routeKind,
            routeKind == "local" ? local!.Protocol : primaryRoute?.ProviderId,
            classification.ProviderRoutes)
        { CanonicalRecordingId = route.CanonicalRecordingId };
    }

    private static EntryMetadata ReadMetadata(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        var artists = root.TryGetProperty("Artists", out var artistsValue) &&
                      artistsValue.ValueKind == JsonValueKind.Array
            ? artistsValue.EnumerateArray()
                .Select(item => item.GetString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!)
                .ToArray()
            : [];
        var duration = root.TryGetProperty("DurationMilliseconds", out var durationValue) &&
                       durationValue.ValueKind == JsonValueKind.Number &&
                       durationValue.TryGetInt64(out var milliseconds)
            ? milliseconds
            : root.TryGetProperty("durationMilliseconds", out durationValue) &&
              durationValue.ValueKind == JsonValueKind.Number &&
              durationValue.TryGetInt64(out milliseconds)
                ? milliseconds
                : root.TryGetProperty("durationSeconds", out durationValue) &&
                  durationValue.ValueKind == JsonValueKind.Number &&
                  durationValue.TryGetDouble(out var seconds)
                    ? (long?)Math.Round(seconds * 1000d)
                    : null;
        return new(
            root.TryGetProperty("Title", out var title) ? title.GetString() ?? "Unknown" : "Unknown",
            artists,
            root.TryGetProperty("Album", out var album) ? album.GetString() : null,
            root.TryGetProperty("Isrc", out var isrc) ? isrc.GetString() : null,
            duration);
    }

    private static readonly JsonSerializerOptions SourceMetadataJson = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static PlaylistSourceMetadata ReadSourceMetadata(string payload)
    {
        try
        {
            return JsonSerializer.Deserialize<PlaylistSourceMetadata>(payload, SourceMetadataJson) ?? new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    private sealed record EntryMetadata(
        string Title,
        IReadOnlyList<string> Artists,
        string? Album,
        string? Isrc,
        long? DurationMilliseconds);
}
