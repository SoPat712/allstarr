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
    int? DurationMilliseconds,
    TrackMatchState? MatchState,
    string? BackendItemId);

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
    public int LocalCount => Entries.Count(item => item.BackendItemId != null);
    public int MissingCount => Entries.Count - LocalCount;
    public long? DurationMilliseconds => Entries.All(item => item.DurationMilliseconds.HasValue)
        ? Entries.Sum(item => (long)item.DurationMilliseconds!.Value)
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
            .Where(item => item.TenantId == tenantId && item.Name.ToLower() == normalizedName);
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
        var matches = await database.TrackMatches.AsNoTracking()
            .Where(item => externalIds.Contains(item.ExternalSnapshotId))
            .ToListAsync(cancellationToken);
        var latestMatches = matches
            .GroupBy(item => item.ExternalSnapshotId)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(item => item.DecisionVersion).First());
        var libraryIds = latestMatches.Values
            .Where(item => item.LibraryTrackId.HasValue)
            .Select(item => item.LibraryTrackId!.Value)
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
                    latestMatches,
                    library))
                .ToArray());
    }

    private static DurablePlaylistEntryProjection ProjectEntry(
        PlaylistSourceEntryRecord entry,
        ExternalMetadataSnapshotRecord external,
        IReadOnlyDictionary<Guid, ProviderTrackIdentityRecord> identities,
        IReadOnlyDictionary<Guid, TrackMatchRecord> matches,
        IReadOnlyDictionary<Guid, LibraryTrackRecord> library)
    {
        matches.TryGetValue(external.Id, out var match);
        var backendItemId = match?.State is TrackMatchState.Accepted or TrackMatchState.Pinned &&
                            match.LibraryTrackId is { } libraryId &&
                            library.TryGetValue(libraryId, out var local)
            ? local.BackendItemId
            : null;
        var metadata = ReadMetadata(external.PayloadJson);
        var externalId = external.ProviderTrackIdentityId is { } identityId &&
                         identities.TryGetValue(identityId, out var identity)
            ? identity.ExternalId
            : external.ExternalIdHash;
        return new(
            entry.SourcePosition,
            external.Id,
            externalId,
            metadata.Title,
            metadata.Artists,
            metadata.Album,
            metadata.Isrc,
            metadata.DurationMilliseconds,
            match?.State,
            backendItemId);
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
        var duration = root.TryGetProperty("durationSeconds", out var durationValue) &&
                       durationValue.TryGetDouble(out var seconds)
            ? (int?)Math.Round(seconds * 1000d)
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
        int? DurationMilliseconds);
}
