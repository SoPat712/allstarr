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
    public async Task<IReadOnlyList<IReadOnlyList<SongPlaylistMembership>>> ReadAsync(IReadOnlyList<SongPlaylistIdentity> songs,
        Guid ownerUserId, CancellationToken cancellationToken = default)
    {
        if (songs.Count == 0) return [];
        var inputs = songs.Select(song => song with
        {
            ProviderId = string.IsNullOrWhiteSpace(song.ProviderId) ? null : AppleMusicClient.NormalizeProviderId(song.ProviderId),
            ExternalId = string.IsNullOrWhiteSpace(song.ExternalId) ? null : song.ExternalId.Trim(),
            Protocol = string.IsNullOrWhiteSpace(song.Protocol) ? null : song.Protocol.Trim().ToLowerInvariant(),
            BackendInstanceId = string.IsNullOrWhiteSpace(song.BackendInstanceId) ? null : song.BackendInstanceId.Trim(),
            BackendItemId = string.IsNullOrWhiteSpace(song.BackendItemId) ? null : song.BackendItemId.Trim()
        }).ToArray();
        foreach (var song in inputs)
        {
            if ((song.ProviderId != null || song.ExternalId != null) && (song.ProviderId == null || song.ExternalId == null))
                throw new ArgumentException("Both the source and song id are required.");
            if ((song.Protocol != null || song.BackendInstanceId != null || song.BackendItemId != null) &&
                (song.Protocol == null || song.BackendInstanceId == null || song.BackendItemId == null))
                throw new ArgumentException("The protocol, backend and song id are required together.");
            if (song.CanonicalRecordingId == null && song.ProviderId == null && song.Protocol == null)
                throw new ArgumentException("A song identity is required.");
        }
        var hashes = inputs.Select(song => song.ExternalId == null ? [] : CanonicalCatalogKeys.StoredKeys(song.ExternalId)).ToArray();
        var sourceKeys = inputs.SelectMany((song, index) => hashes[index].Select(hash => song.ProviderId + ":" + hash)).Distinct().ToArray();
        var providers = inputs.Select(song => song.ProviderId).OfType<string>().Distinct().ToArray();
        var externalHashes = hashes.SelectMany(keys => keys).Distinct().ToArray();
        var protocols = inputs.Select(song => song.Protocol).OfType<string>().Distinct().ToArray();
        var backends = inputs.Select(song => song.BackendInstanceId).OfType<string>().Distinct().ToArray();
        var items = inputs.Select(song => song.BackendItemId).OfType<string>().Distinct().ToArray();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var identities = db.ProviderTrackIdentities.AsNoTracking().Where(identity =>
            identity.ResourceKind == ProviderResourceKind.Track &&
            (identity.Verification == ProviderIdentityVerification.Verified || identity.Verification == ProviderIdentityVerification.Pinned) &&
            (identity.Scope == ProviderIdentityScope.Catalog && identity.ProviderAccountId == null ||
             identity.Scope == ProviderIdentityScope.Account && db.ProviderAccounts.Any(account =>
                 account.Id == identity.ProviderAccountId && account.OwnerUserId == ownerUserId)));
        var candidates = await identities.Where(identity => EF.Parameter(providers).Contains(identity.ProviderId) && EF.Parameter(externalHashes).Contains(identity.ExternalIdHash))
            .Select(identity => new
            {
                Provider = (string?)identity.ProviderId,
                Hash = (string?)identity.ExternalIdHash,
                Protocol = (string?)null,
                Backend = (string?)null,
                Item = (string?)null,
                Recording = (Guid?)identity.CanonicalRecordingId,
                Track = (Guid?)null
            })
            .Concat(db.LibraryTracks.AsNoTracking().Where(track => track.OwnerUserId == ownerUserId &&
                    EF.Parameter(protocols).Contains(track.Protocol) && EF.Parameter(backends).Contains(track.BackendInstanceId) && EF.Parameter(items).Contains(track.BackendItemId))
                .Select(track => new
                {
                    Provider = (string?)null,
                    Hash = (string?)null,
                    Protocol = (string?)track.Protocol,
                    Backend = (string?)track.BackendInstanceId,
                    Item = (string?)track.BackendItemId,
                    Recording = track.CanonicalRecordingId,
                    Track = (Guid?)track.Id
                })).ToArrayAsync(cancellationToken);
        var selected = inputs.Select((song, index) => candidates.Where(candidate => candidate.Track.HasValue
            ? candidate.Protocol == song.Protocol && candidate.Backend == song.BackendInstanceId && candidate.Item == song.BackendItemId
            : candidate.Provider == song.ProviderId && hashes[index].Contains(candidate.Hash!)).ToArray()).ToArray();
        var recordings = inputs.Select((song, index) => selected[index].Select(candidate => candidate.Recording)
            .Append(song.CanonicalRecordingId).OfType<Guid>().ToHashSet()).ToArray();
        var canonicalIds = recordings.SelectMany(ids => ids).Distinct().ToArray();
        var trackIds = selected.SelectMany(rows => rows).Select(row => row.Track).OfType<Guid>().Distinct().ToArray();
        var routes = await identities.Where(identity => EF.Parameter(canonicalIds).Contains(identity.CanonicalRecordingId))
            .Select(identity => new { identity.CanonicalRecordingId, identity.ProviderId, identity.ExternalIdHash, identity.ProviderAccountId, identity.Scope })
            .ToArrayAsync(cancellationToken);
        var catalogKeys = sourceKeys.Concat(routes.Where(route => route.Scope == ProviderIdentityScope.Catalog)
            .Select(route => route.ProviderId + ":" + route.ExternalIdHash)).Distinct().ToArray();
        var accountKeys = routes.Where(route => route.Scope == ProviderIdentityScope.Account && route.ProviderAccountId.HasValue)
            .Select(route => route.ProviderId + ":" + route.ExternalIdHash + ":" + route.ProviderAccountId!.Value.ToString("D").ToLowerInvariant()).Distinct().ToArray();
        var entries = await (
            from link in db.PlaylistLinks.AsNoTracking()
            join snapshot in db.PlaylistSourceSnapshots.AsNoTracking() on link.Id equals snapshot.PlaylistLinkId
            join entry in db.PlaylistSourceEntries.AsNoTracking() on snapshot.Id equals entry.PlaylistSourceSnapshotId
            join source in db.ExternalMetadataSnapshots.AsNoTracking() on entry.ExternalMetadataSnapshotId equals source.Id
            join match in db.TrackMatches.AsNoTracking().Where(match => match.OwnerUserId == ownerUserId &&
                (match.State == TrackMatchState.Accepted || match.State == TrackMatchState.Pinned)) on entry.PublishedTrackMatchId equals match.Id into matches
            from match in matches.DefaultIfEmpty()
            join track in db.LibraryTracks.AsNoTracking().Where(track => track.OwnerUserId == ownerUserId) on match.LibraryTrackId equals track.Id into tracks
            from track in tracks.DefaultIfEmpty()
            where link.OwnerUserId == ownerUserId && snapshot.OwnerUserId == ownerUserId && source.OwnerUserId == ownerUserId && snapshot.PublishedAt.HasValue &&
                !db.PlaylistSourceSnapshots.Any(newer => newer.PlaylistLinkId == link.Id && newer.OwnerUserId == ownerUserId &&
                    newer.PublishedAt.HasValue && newer.SnapshotVersion > snapshot.SnapshotVersion) &&
                (EF.Parameter(catalogKeys).Contains(source.ProviderId + ":" + source.ExternalIdHash) ||
                 source.ProviderAccountId.HasValue && EF.Parameter(accountKeys).Contains(source.ProviderId + ":" + source.ExternalIdHash + ":" + source.ProviderAccountId!.Value.ToString().ToLower()) ||
                 match != null && match.CanonicalRecordingId.HasValue && EF.Parameter(canonicalIds).Contains(match.CanonicalRecordingId.Value) ||
                 track != null && (EF.Parameter(trackIds).Contains(track.Id) || track.CanonicalRecordingId.HasValue && EF.Parameter(canonicalIds).Contains(track.CanonicalRecordingId.Value)))
            orderby snapshot.Name, link.Id
            select new
            {
                link.Id,
                snapshot.Name,
                source.ProviderId,
                source.ExternalIdHash,
                source.ProviderAccountId,
                MatchRecording = match == null ? null : match.CanonicalRecordingId,
                TrackRecording = track == null ? null : track.CanonicalRecordingId,
                TrackId = track == null ? (Guid?)null : track.Id
            })
            .ToArrayAsync(cancellationToken);
        return inputs.Select((song, index) =>
        {
            var ownRoutes = routes.Where(route => recordings[index].Contains(route.CanonicalRecordingId)).ToArray();
            var catalog = hashes[index].Select(hash => song.ProviderId + ":" + hash)
                .Concat(ownRoutes.Where(route => route.Scope == ProviderIdentityScope.Catalog).Select(route => route.ProviderId + ":" + route.ExternalIdHash)).ToHashSet(StringComparer.Ordinal);
            var accounts = ownRoutes.Where(route => route.Scope == ProviderIdentityScope.Account && route.ProviderAccountId.HasValue)
                .Select(route => route.ProviderId + ":" + route.ExternalIdHash + ":" + route.ProviderAccountId!.Value.ToString("D").ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
            var ownTracks = selected[index].Select(candidate => candidate.Track).OfType<Guid>().ToHashSet();
            return (IReadOnlyList<SongPlaylistMembership>)entries.Where(entry =>
                    catalog.Contains(entry.ProviderId + ":" + entry.ExternalIdHash) ||
                    entry.ProviderAccountId.HasValue && accounts.Contains(entry.ProviderId + ":" + entry.ExternalIdHash + ":" + entry.ProviderAccountId.Value.ToString("D").ToLowerInvariant()) ||
                    entry.MatchRecording.HasValue && recordings[index].Contains(entry.MatchRecording.Value) ||
                    entry.TrackRecording.HasValue && recordings[index].Contains(entry.TrackRecording.Value) || entry.TrackId.HasValue && ownTracks.Contains(entry.TrackId.Value))
                .Select(entry => new SongPlaylistMembership(entry.Id, entry.Name)).DistinctBy(entry => entry.Id).ToArray();
        }).ToArray();
    }

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
