using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Playlists;

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
    IReadOnlyList<DurableProviderRoute> ProviderRoutes);

public sealed record DurablePlaylistProjection(
    Guid LinkId,
    Guid SnapshotId,
    int SnapshotVersion,
    string Name,
    string SourceProviderId,
    string SourcePlaylistId,
    Guid ProviderAccountId,
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
    public string? Description { get; init; }
    public Guid? RunId { get; init; }
    public long? Generation { get; init; }
    public int MaterializedCount { get; init; }
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

public sealed class DurablePlaylistProjectionReader(
    IDbContextFactory<AllstarrDbContext> factory,
    IProtocolProviderGateway? providerGateway = null)
{
    public async Task<DurablePlaylistProjection?> ReadByNameAsync(
        Guid tenantId,
        Guid? ownerUserId,
        string name,
        CancellationToken cancellationToken = default)
    {
        await using var database = await factory.CreateDbContextAsync(cancellationToken);
        var normalizedName = name.Trim().ToLowerInvariant();
        var snapshots = database.PlaylistSourceSnapshots.AsNoTracking()
            .Where(item => item.TenantId == tenantId &&
                           item.Name.ToLower() == normalizedName &&
                           item.PublishedAt.HasValue);
        if (ownerUserId.HasValue)
            snapshots = snapshots.Where(item => item.OwnerUserId == ownerUserId.Value);
        var snapshot = await snapshots
            .OrderByDescending(item => item.SnapshotVersion)
            .ThenByDescending(item => item.RetrievedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (snapshot == null) return null;

        return await ProjectAsync(database, snapshot, tenantId, cancellationToken);
    }

    public async Task<DurablePlaylistProjection?> ReadByLinkIdAsync(
        Guid tenantId,
        Guid? ownerUserId,
        Guid playlistLinkId,
        CancellationToken cancellationToken = default)
    {
        await using var database = await factory.CreateDbContextAsync(cancellationToken);
        var snapshots = database.PlaylistSourceSnapshots.AsNoTracking()
            .Where(item => item.TenantId == tenantId &&
                           item.PlaylistLinkId == playlistLinkId &&
                           item.PublishedAt.HasValue);
        if (ownerUserId.HasValue)
            snapshots = snapshots.Where(item => item.OwnerUserId == ownerUserId.Value);
        var snapshot = await snapshots
            .OrderByDescending(item => item.SnapshotVersion)
            .ThenByDescending(item => item.RetrievedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (snapshot == null) return null;

        return await ProjectAsync(database, snapshot, tenantId, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<Guid, DurablePlaylistProjection>> ReadByLinkIdsAsync(
        Guid tenantId,
        Guid? ownerUserId,
        IReadOnlyCollection<Guid> playlistLinkIds,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<Guid, DurablePlaylistProjection>();
        // ponytail: project sequentially; bulk-load only if measured list latency requires it.
        foreach (var playlistLinkId in playlistLinkIds)
        {
            var projection = await ReadByLinkIdAsync(
                tenantId, ownerUserId, playlistLinkId, cancellationToken);
            if (projection != null) result[playlistLinkId] = projection;
        }
        return result;
    }

    private async Task<DurablePlaylistProjection> ProjectAsync(
        AllstarrDbContext database,
        PlaylistSourceSnapshotRecord snapshot,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var ownerUserId = snapshot.OwnerUserId;
        var link = await database.PlaylistLinks.AsNoTracking()
            .SingleAsync(item => item.Id == snapshot.PlaylistLinkId, cancellationToken);
        var entries = await database.PlaylistSourceEntries.AsNoTracking()
            .Where(item => item.PlaylistSourceSnapshotId == snapshot.Id)
            .OrderBy(item => item.SourcePosition)
            .ToListAsync(cancellationToken);
        var externalIds = entries.Select(item => item.ExternalMetadataSnapshotId).ToArray();
        var external = await database.ExternalMetadataSnapshots.AsNoTracking()
            .Where(item => externalIds.Contains(item.Id))
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
            .Where(item => item.TenantId == tenantId &&
                           (identityIds.Contains(item.Id) ||
                            externalHashes.Contains(item.ExternalIdHash)))
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        var canonicalIds = sourceIdentities.Values
            .Select(item => item.CanonicalRecordingId)
            .Distinct()
            .ToArray();
        var identities = await database.ProviderTrackIdentities.AsNoTracking()
            .Where(item => item.TenantId == tenantId &&
                           canonicalIds.Contains(item.CanonicalRecordingId) &&
                           item.ResourceKind == ProviderResourceKind.Track &&
                           (item.Verification == ProviderIdentityVerification.Verified ||
                            item.Verification == ProviderIdentityVerification.Pinned))
            .ToListAsync(cancellationToken);
        var providerOrder = providerGateway == null
            ? identities.Select(item => item.ProviderId)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray()
            : (providerGateway.GetProviderOrder(ProviderCapabilityKind.Streaming) ?? [])
                .Concat(providerGateway.GetProviderOrder(ProviderCapabilityKind.Download) ?? [])
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        var publishedMatchIds = entries
            .Where(item => item.PublishedTrackMatchId.HasValue)
            .Select(item => item.PublishedTrackMatchId!.Value)
            .Distinct()
            .ToArray();
        var publishedMatches = await database.TrackMatches.AsNoTracking()
            .Where(item => publishedMatchIds.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        var overrides = await database.ManualTrackOverrides.AsNoTracking()
            .Where(item => externalIds.Contains(item.ExternalSnapshotId) && item.RevokedAt == null)
            .ToDictionaryAsync(item => item.ExternalSnapshotId, cancellationToken);
        var libraryIds = publishedMatches.Values
            .Where(item => item.LibraryTrackId.HasValue)
            .Select(item => item.LibraryTrackId!.Value)
            .Concat(overrides.Values.Where(item => item.LibraryTrackId.HasValue)
                .Select(item => item.LibraryTrackId!.Value))
            .Distinct()
            .ToArray();
        var library = await database.LibraryTracks.AsNoTracking()
            .Where(item => libraryIds.Contains(item.Id) &&
                           item.TenantId == tenantId &&
                           item.OwnerUserId == ownerUserId &&
                           item.LibraryScopeId == link.LibraryScopeId &&
                           item.BackendInstanceId == link.TargetBackendInstanceId &&
                           (link.TargetProtocol == "jellyfin"
                               ? item.Protocol == "jellyfin"
                               : item.Protocol == "subsonic" ||
                                 item.Protocol == "opensubsonic" ||
                                 item.Protocol == "navidrome"))
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        var playableLibraryTrackIds = library.Keys.ToHashSet();
        var run = await database.PlaylistSyncRuns.AsNoTracking()
            .Where(item => item.PlaylistLinkId == link.Id &&
                           item.PlaylistSourceSnapshotId == snapshot.Id &&
                           item.State != PlaylistSyncState.Pending &&
                           item.State != PlaylistSyncState.Running)
            .OrderByDescending(item => item.Generation)
            .FirstOrDefaultAsync(cancellationToken);
        var lastSuccessfulSyncAt = await database.PlaylistSyncRuns.AsNoTracking()
            .Where(item => item.PlaylistLinkId == link.Id &&
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
            entries.Select(entry => ProjectEntry(
                    entry,
                    external[entry.ExternalMetadataSnapshotId],
                    sourceIdentities,
                    identities,
                    providerOrder,
                    entry.PublishedTrackMatchId is { } matchId
                        ? publishedMatches.GetValueOrDefault(matchId)
                        : null,
                    overrides,
                    library,
                    playableLibraryTrackIds))
                .ToArray(),
            run?.PlannedTargetTrackCount,
            run?.PlannedTargetDurationMilliseconds,
            run?.VerifiedTargetTrackCount,
            run?.VerifiedTargetDurationMilliseconds,
            run?.VerificationCode,
            run?.VerifiedAt)
        {
            Description = snapshot.Description,
            RunId = run?.Id,
            Generation = run?.Generation,
            MaterializedCount = materializedCount
        };
    }

    private static DurablePlaylistEntryProjection ProjectEntry(
        PlaylistSourceEntryRecord entry,
        ExternalMetadataSnapshotRecord external,
        IReadOnlyDictionary<Guid, ProviderTrackIdentityRecord> sourceIdentities,
        IReadOnlyList<ProviderTrackIdentityRecord> identities,
        IReadOnlyList<string> providerOrder,
        TrackMatchRecord? match,
        IReadOnlyDictionary<Guid, ManualTrackOverrideRecord> overrides,
        IReadOnlyDictionary<Guid, LibraryTrackRecord> library,
        IReadOnlySet<Guid> playableLibraryTrackIds)
    {
        overrides.TryGetValue(external.Id, out var manual);
        ProviderTrackIdentityRecord? identity = null;
        if (external.ProviderTrackIdentityId is { } identityId)
            sourceIdentities.TryGetValue(identityId, out identity);
        identity ??= sourceIdentities.Values.FirstOrDefault(item =>
            item.ProviderId.Equals(external.ProviderId, StringComparison.OrdinalIgnoreCase) &&
            item.ExternalIdHash == external.ExternalIdHash);
        var classification = TrackClassifier.Classify(
            manual,
            match,
            identity,
            identities,
            providerOrder,
            playableLibraryTrackIds);
        LibraryTrackRecord? local = null;
        if (classification.LibraryTrackId.HasValue)
            library.TryGetValue(classification.LibraryTrackId.Value, out local);
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
            classification.ProviderRoutes);
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

    private sealed record EntryMetadata(
        string Title,
        IReadOnlyList<string> Artists,
        string? Album,
        string? Isrc,
        long? DurationMilliseconds);
}
