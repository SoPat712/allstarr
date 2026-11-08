using allstarr.Core.Capabilities;
using allstarr.Core.Matching;
using allstarr.Core.Providers.AppleMusicKit;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Playlists;

public sealed record SongPlaylistIdentity(
    Guid? CanonicalRecordingId = null,
    string? ProviderId = null,
    string? ExternalId = null,
    string? Protocol = null,
    string? BackendInstanceId = null,
    string? BackendItemId = null);

public sealed record SongPlaylistMembership(Guid Id, string Name);

public sealed class SongPlaylistMembershipReader(IDbContextFactory<AllstarrDbContext> factory)
{
    public async Task<IReadOnlyList<SongPlaylistMembership>> ReadAsync(Guid ownerUserId,
        SongPlaylistIdentity song, CancellationToken cancellationToken = default)
    {
        var providerId = string.IsNullOrWhiteSpace(song.ProviderId) ? null : AppleMusicClient.NormalizeProviderId(song.ProviderId);
        var externalId = string.IsNullOrWhiteSpace(song.ExternalId) ? null : song.ExternalId.Trim();
        var protocol = string.IsNullOrWhiteSpace(song.Protocol) ? null : song.Protocol.Trim().ToLowerInvariant();
        var backend = string.IsNullOrWhiteSpace(song.BackendInstanceId) ? null : song.BackendInstanceId.Trim();
        var backendItem = string.IsNullOrWhiteSpace(song.BackendItemId) ? null : song.BackendItemId.Trim();
        var hasProvider = providerId != null && externalId != null;
        var hasBackend = protocol != null && backend != null && backendItem != null;
        if ((providerId != null || externalId != null) && !hasProvider)
            throw new ArgumentException("Both the source and song id are required.");
        if ((protocol != null || backend != null || backendItem != null) && !hasBackend)
            throw new ArgumentException("The protocol, backend and song id are required together.");
        if (song.CanonicalRecordingId is null && !hasProvider && !hasBackend)
            throw new ArgumentException("A song identity is required.");

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var recordingIds = new HashSet<Guid>();
        if (song.CanonicalRecordingId is { } recordingId) recordingIds.Add(recordingId);
        var externalHashes = hasProvider ? CanonicalCatalogKeys.StoredKeys(externalId!) : [];
        var identities = db.ProviderTrackIdentities.AsNoTracking().Where(identity =>
            identity.ResourceKind == ProviderResourceKind.Track &&
            (identity.Verification == ProviderIdentityVerification.Verified || identity.Verification == ProviderIdentityVerification.Pinned) &&
            (identity.Scope == ProviderIdentityScope.Catalog && identity.ProviderAccountId == null ||
             identity.Scope == ProviderIdentityScope.Account && db.ProviderAccounts.Any(account =>
                 account.Id == identity.ProviderAccountId && account.OwnerUserId == ownerUserId)));
        Guid[] backendTrackIds = [];
        if (hasProvider || hasBackend)
        {
            var candidates = await identities
                .Where(identity => hasProvider && identity.ProviderId == providerId && EF.Parameter(externalHashes).Contains(identity.ExternalIdHash))
                .Select(identity => new { RecordingId = (Guid?)identity.CanonicalRecordingId, LibraryTrackId = (Guid?)null })
                .Concat(db.LibraryTracks.AsNoTracking().Where(track => hasBackend &&
                        track.OwnerUserId == ownerUserId && track.Protocol == protocol &&
                        track.BackendInstanceId == backend && track.BackendItemId == backendItem)
                    .Select(track => new { RecordingId = track.CanonicalRecordingId, LibraryTrackId = (Guid?)track.Id }))
                .ToArrayAsync(cancellationToken);
            backendTrackIds = candidates.Where(candidate => candidate.LibraryTrackId.HasValue)
                .Select(candidate => candidate.LibraryTrackId!.Value).Distinct().ToArray();
            recordingIds.UnionWith(candidates.Where(candidate => candidate.RecordingId.HasValue)
                .Select(candidate => candidate.RecordingId!.Value));
        }

        var canonicalIds = recordingIds.ToArray();
        string[] catalogRouteKeys = [];
        string[] accountRouteKeys = [];
        if (canonicalIds.Length > 0)
        {
            var routes = await identities.Where(identity => EF.Parameter(canonicalIds).Contains(identity.CanonicalRecordingId))
                .Select(identity => new { identity.ProviderId, identity.ExternalIdHash, identity.ProviderAccountId, identity.Scope })
                .ToArrayAsync(cancellationToken);
            catalogRouteKeys = routes.Where(route => route.Scope == ProviderIdentityScope.Catalog)
                .Select(route => route.ProviderId + ":" + route.ExternalIdHash).Distinct(StringComparer.Ordinal).ToArray();
            accountRouteKeys = routes.Where(route => route.Scope == ProviderIdentityScope.Account && route.ProviderAccountId.HasValue)
                .Select(route => route.ProviderId + ":" + route.ExternalIdHash + ":" + route.ProviderAccountId!.Value.ToString("D").ToLowerInvariant())
                .Distinct(StringComparer.Ordinal).ToArray();
        }
        var relatedTracks = db.LibraryTracks.AsNoTracking().Where(track =>
            track.OwnerUserId == ownerUserId &&
            (EF.Parameter(backendTrackIds).Contains(track.Id) ||
             track.CanonicalRecordingId.HasValue && EF.Parameter(canonicalIds).Contains(track.CanonicalRecordingId.Value)));
        var matchingEntries =
            from entry in db.PlaylistSourceEntries.AsNoTracking()
            join source in db.ExternalMetadataSnapshots.AsNoTracking() on entry.ExternalMetadataSnapshotId equals source.Id
            where source.OwnerUserId == ownerUserId &&
                (hasProvider && source.ProviderId == providerId && EF.Parameter(externalHashes).Contains(source.ExternalIdHash) ||
                 EF.Parameter(catalogRouteKeys).Contains(source.ProviderId + ":" + source.ExternalIdHash) ||
                 source.ProviderAccountId.HasValue && EF.Parameter(accountRouteKeys).Contains(
                     source.ProviderId + ":" + source.ExternalIdHash + ":" + source.ProviderAccountId.Value.ToString().ToLower()) ||
                 db.TrackMatches.Any(match => match.Id == entry.PublishedTrackMatchId && match.OwnerUserId == ownerUserId &&
                     (match.State == TrackMatchState.Accepted || match.State == TrackMatchState.Pinned) &&
                     (match.CanonicalRecordingId.HasValue && EF.Parameter(canonicalIds).Contains(match.CanonicalRecordingId.Value) ||
                      relatedTracks.Any(track => track.Id == match.LibraryTrackId))))
            select entry.PlaylistSourceSnapshotId;

        return await (
            from link in db.PlaylistLinks.AsNoTracking()
            join snapshot in db.PlaylistSourceSnapshots.AsNoTracking() on link.Id equals snapshot.PlaylistLinkId
            where link.OwnerUserId == ownerUserId && snapshot.OwnerUserId == ownerUserId && snapshot.PublishedAt.HasValue &&
                !db.PlaylistSourceSnapshots.Any(newer => newer.PlaylistLinkId == link.Id &&
                    newer.OwnerUserId == ownerUserId && newer.PublishedAt.HasValue && newer.SnapshotVersion > snapshot.SnapshotVersion) &&
                matchingEntries.Contains(snapshot.Id)
            orderby snapshot.Name, link.Id
            select new SongPlaylistMembership(link.Id, snapshot.Name)).ToArrayAsync(cancellationToken);
    }

    public static string SongKey(string providerId, string externalIdHash) =>
        $"{providerId.ToLowerInvariant()}:{externalIdHash}";

    // Keyed by provider and external id so a refreshed snapshot keeps playlists recorded on the one it replaced.
    public async Task<IReadOnlyDictionary<string, string[]>> ReadNamesAsync(Guid ownerUserId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var rows = await (
            from link in db.PlaylistLinks.AsNoTracking()
            join snapshot in db.PlaylistSourceSnapshots.AsNoTracking() on link.Id equals snapshot.PlaylistLinkId
            join entry in db.PlaylistSourceEntries.AsNoTracking() on snapshot.Id equals entry.PlaylistSourceSnapshotId
            join song in db.ExternalMetadataSnapshots.AsNoTracking() on entry.ExternalMetadataSnapshotId equals song.Id
            where link.OwnerUserId == ownerUserId && snapshot.OwnerUserId == ownerUserId && song.OwnerUserId == ownerUserId &&
                song.ResourceKind == "track" && snapshot.PublishedAt.HasValue &&
                !db.PlaylistSourceSnapshots.Any(newer => newer.PlaylistLinkId == link.Id &&
                    newer.OwnerUserId == ownerUserId && newer.PublishedAt.HasValue && newer.SnapshotVersion > snapshot.SnapshotVersion)
            select new { link.Id, link.CreatedAt, link.SourceProviderId, snapshot.Name, song.ProviderId, song.ExternalIdHash })
            .ToArrayAsync(cancellationToken);
        return rows.OrderBy(row => row.CreatedAt).ThenBy(row => row.Id)
            .GroupBy(row => SongKey(row.ProviderId, row.ExternalIdHash), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group
                .Select(row => PlaylistVirtualizationService.DisplayName(row.SourceProviderId, row.Name))
                .Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
    }
}
