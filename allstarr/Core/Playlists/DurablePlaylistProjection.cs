using System.Text.Json;
using allstarr.Core.Matching;
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
    string? RouteProviderId);

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
    IReadOnlyList<DurablePlaylistEntryProjection> Entries)
{
    public int TotalCount => Entries.Count;
    public int LocalCount => Entries.Count(item => item.BackendItemId != null);
    public int ExternalCount => Entries.Count(item => item.RouteKind == "external");
    public int MissingCount => Entries.Count(item => item.RouteKind == "unmatched");
    public int UnknownDurationCount => Entries.Count(item => !item.DurationMilliseconds.HasValue);
    public long? DurationMilliseconds => Entries.Any(item => item.DurationMilliseconds.HasValue)
        ? Entries.Sum(item => item.DurationMilliseconds ?? 0)
        : null;
}

public sealed class DurablePlaylistProjectionReader(
    IDbContextFactory<AllstarrDbContext> factory)
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
        var identities = await database.ProviderTrackIdentities.AsNoTracking()
            .Where(item => identityIds.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, cancellationToken);
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
            .Where(item => libraryIds.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        var run = await database.PlaylistSyncRuns.AsNoTracking()
            .Where(item => item.PlaylistLinkId == link.Id &&
                           item.PlaylistSourceSnapshotId == snapshot.Id)
            .OrderByDescending(item => item.Generation)
            .FirstOrDefaultAsync(cancellationToken);

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
            run?.CompletedAt,
            run?.State,
            entries.Select(entry => ProjectEntry(
                    entry,
                    external[entry.ExternalMetadataSnapshotId],
                    identities,
                    entry.PublishedTrackMatchId is { } matchId
                        ? publishedMatches.GetValueOrDefault(matchId)
                        : null,
                    overrides,
                    library))
                .ToArray());
    }

    private static DurablePlaylistEntryProjection ProjectEntry(
        PlaylistSourceEntryRecord entry,
        ExternalMetadataSnapshotRecord external,
        IReadOnlyDictionary<Guid, ProviderTrackIdentityRecord> identities,
        TrackMatchRecord? match,
        IReadOnlyDictionary<Guid, ManualTrackOverrideRecord> overrides,
        IReadOnlyDictionary<Guid, LibraryTrackRecord> library)
    {
        overrides.TryGetValue(external.Id, out var manual);
        var rejected = TrackMatchOverridePolicy.IsEffectiveRejection(manual, match);
        var libraryId = manual?.Decision == ManualOverrideDecision.Pin
            ? manual.LibraryTrackId
            : rejected ? null : match?.LibraryTrackId;
        LibraryTrackRecord? local = null;
        if (libraryId.HasValue)
            library.TryGetValue(libraryId.Value, out local);
        var backendItemId = local?.BackendItemId;
        var metadata = ReadMetadata(external.PayloadJson);
        ProviderTrackIdentityRecord? identity = null;
        if (external.ProviderTrackIdentityId is { } identityId)
            identities.TryGetValue(identityId, out identity);
        var hasExternal = !rejected &&
                          identity?.Verification is ProviderIdentityVerification.Verified or
                              ProviderIdentityVerification.Pinned;
        var externalId = hasExternal
            ? identity!.ExternalId
            : external.ExternalIdHash;
        var routeKind = local != null ? "local" : hasExternal ? "external" : "unmatched";
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
            manual?.Decision == ManualOverrideDecision.Pin
                ? TrackMatchState.Pinned
                : rejected ? TrackMatchState.Rejected : match?.State,
            backendItemId,
            routeKind,
            routeKind == "local" ? local!.Protocol : hasExternal ? identity!.ProviderId : null);
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
