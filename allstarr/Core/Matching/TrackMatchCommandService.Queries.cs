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
    public async Task<TrackMatchDetailData> GetDetailAsync(
        TrackMatchActor actor,
        string providerId,
        string externalId,
        string? backendItemId = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        providerId = providerId.Trim().ToLowerInvariant();
        externalId = externalId.Trim();

        var sourceIdentities = await db.ProviderTrackIdentities.AsNoTracking()
            .Where(item => item.ProviderId == providerId &&
                           item.ExternalId == externalId)
            .OrderBy(item => item.CreatedAt)
            .ToListAsync(cancellationToken);
        var canonicalIds = sourceIdentities
            .Select(item => item.CanonicalRecordingId)
            .Distinct()
            .ToArray();
        var identities = canonicalIds.Length == 0
            ? sourceIdentities
            : await db.ProviderTrackIdentities.AsNoTracking()
                .Where(item => canonicalIds.Contains(item.CanonicalRecordingId))
                .OrderBy(item => item.ProviderId)
                .ThenBy(item => item.CreatedAt)
                .ToListAsync(cancellationToken);

        var localQuery = await AccessibleTracksAsync(db, actor, cancellationToken);
        var localTracks = await localQuery
            .Where(item =>
                item.CanonicalRecordingId.HasValue &&
                canonicalIds.Contains(item.CanonicalRecordingId.Value) ||
                backendItemId != null && item.BackendItemId == backendItemId)
            .OrderByDescending(item => item.UpdatedAt)
            .ToListAsync(cancellationToken);

        var identityIds = identities.Select(item => item.Id).Distinct().ToArray();
        var snapshotQuery = db.ExternalMetadataSnapshots.AsNoTracking()
            .Where(item => item.ProviderTrackIdentityId.HasValue &&
                           identityIds.Contains(item.ProviderTrackIdentityId.Value));
        if (!actor.IsAdministrator)
            snapshotQuery = snapshotQuery.Where(item => item.OwnerUserId == actor.UserId);
        var snapshots = await snapshotQuery.OrderByDescending(item => item.RetrievedAt)
            .ToListAsync(cancellationToken);
        var snapshotIds = snapshots.Select(item => item.Id).ToArray();
        var decisions = snapshotIds.Length == 0
            ? []
            : await db.TrackMatches.AsNoTracking()
                .Where(item => snapshotIds.Contains(item.ExternalSnapshotId))
                .OrderByDescending(item => item.DecidedAt)
                .ToListAsync(cancellationToken);
        var overrides = await ManualTrackOverrides.LoadAsync(
            db, actor.UserId, snapshots, cancellationToken, includeRevoked: true);

        var externalIds = identities.Select(item => item.ExternalId)
            .Append(externalId)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct()
            .ToArray();
        var artifactQuery = db.ProviderDownloadArtifacts.AsNoTracking()
            .Where(item => externalIds.Contains(item.ProviderArtifactId));
        if (!actor.IsAdministrator)
            artifactQuery = artifactQuery.Where(item =>
                item.OwnerUserId == null || item.OwnerUserId == actor.UserId);
        var artifacts = await artifactQuery.OrderByDescending(item => item.CreatedAt)
            .Take(50)
            .ToListAsync(cancellationToken);

        return new(identities, localTracks, snapshots, decisions, overrides, artifacts);
    }

    public async Task<TrackMatchReviewData> GetReviewDataAsync(
        TrackMatchActor actor,
        string? backendLibraryId = null,
        string? search = null,
        Guid? externalSnapshotId = null,
        int scanLimit = 5000,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var snapshotsQuery = db.ExternalMetadataSnapshots.AsNoTracking()
            ;
        if (!actor.IsAdministrator)
            snapshotsQuery = snapshotsQuery.Where(item => item.OwnerUserId == actor.UserId);
        if (externalSnapshotId.HasValue)
            snapshotsQuery = snapshotsQuery.Where(item => item.Id == externalSnapshotId.Value);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim().Replace("%", "\\%").Replace("_", "\\_")}%";
            snapshotsQuery = snapshotsQuery.Where(item =>
                EF.Functions.Like(item.ProviderId, pattern, "\\") ||
                EF.Functions.Like(item.PayloadJson, pattern, "\\"));
        }

        var snapshots = await snapshotsQuery.OrderByDescending(item => item.RetrievedAt)
            .Take(Math.Clamp(scanLimit, 1, 10000))
            .ToListAsync(cancellationToken);
        var snapshotIds = snapshots.Select(item => item.Id).ToArray();
        var decisionQuery = db.TrackMatches.AsNoTracking()
            .Where(item => snapshotIds.Contains(item.ExternalSnapshotId));
        var latestVersions = decisionQuery
            .GroupBy(item => item.ExternalSnapshotId)
            .Select(group => new
            {
                ExternalSnapshotId = group.Key,
                DecisionVersion = group.Max(item => item.DecisionVersion)
            });
        var decisions = await decisionQuery.Join(
                latestVersions,
                item => new { item.ExternalSnapshotId, item.DecisionVersion },
                latest => new { latest.ExternalSnapshotId, latest.DecisionVersion },
                (item, _) => item)
            .ToListAsync(cancellationToken);
        var overrides = await ManualTrackOverrides.LoadAsync(
            db, actor.UserId, snapshots, cancellationToken);
        var libraryIds = decisions.Where(item => item.LibraryTrackId.HasValue)
            .Select(item => item.LibraryTrackId!.Value)
            .Concat(overrides.Where(item => item.LibraryTrackId.HasValue)
                .Select(item => item.LibraryTrackId!.Value))
            .Distinct()
            .ToArray();
        var sourceIdentityIds = snapshots
            .Where(item => item.ProviderTrackIdentityId.HasValue)
            .Select(item => item.ProviderTrackIdentityId!.Value)
            .Distinct()
            .ToArray();
        var sourceExternalIdHashes = snapshots
            .Select(item => item.ExternalIdHash)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var sourceIdentities = await db.ProviderTrackIdentities.AsNoTracking()
            .Where(item => item.ResourceKind == ProviderResourceKind.Track &&
                           (sourceIdentityIds.Contains(item.Id) ||
                            sourceExternalIdHashes.Contains(item.ExternalIdHash)))
            .ToListAsync(cancellationToken);
        var canonicalIds = decisions.Where(item => item.CanonicalRecordingId.HasValue)
            .Select(item => item.CanonicalRecordingId!.Value)
            .Concat(sourceIdentities.Select(item => item.CanonicalRecordingId))
            .Distinct()
            .ToArray();
        var libraryQuery = (await AccessibleTracksAsync(db, actor, cancellationToken))
            .Where(item => libraryIds.Contains(item.Id) ||
                           item.CanonicalRecordingId.HasValue &&
                           canonicalIds.Contains(item.CanonicalRecordingId.Value));
        if (!string.IsNullOrWhiteSpace(backendLibraryId))
            libraryQuery = libraryQuery.Where(item => item.BackendLibraryId == backendLibraryId.Trim());
        var library = await libraryQuery.ToListAsync(cancellationToken);
        var identities = await db.ProviderTrackIdentities.AsNoTracking()
            .Where(item => canonicalIds.Contains(item.CanonicalRecordingId))
            .OrderBy(item => item.ProviderId)
            .ThenBy(item => item.ExternalId)
            .ToListAsync(cancellationToken);
        return new(snapshots, decisions, overrides, library, identities);
    }

    public async Task<IReadOnlyList<LibraryTrackRecord>> SearchLocalTracksAsync(
        TrackMatchActor actor,
        string query,
        string? backendLibraryId = null,
        int limit = 20,
        ExternalTrackMatchSnapshot? source = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        limit = Math.Clamp(limit, 1, 50);
        var patterns = query.Split((char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(term => $"%{term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%")
            .ToArray();
        var tracks = await AccessibleTracksAsync(db, actor, cancellationToken);
        if (!string.IsNullOrWhiteSpace(backendLibraryId))
            tracks = tracks.Where(item => item.BackendLibraryId == backendLibraryId.Trim());
        IReadOnlyList<LibraryTrackRecord> indexed = source == null
            ? []
            : await tracks.ToListAsync(cancellationToken);
        var effectiveEngine = source == null
            ? decisionEngine
            : await DecisionEngineAsync(cancellationToken);
        HashSet<Guid> automatic = source == null
            ? []
            : effectiveEngine.PrepareCandidates(indexed.Select(ToLocalCandidate))
                .Select(source)
                .Select(item => item.LibraryTrackId)
                .ToHashSet();
        foreach (var pattern in patterns)
            tracks = tracks.Where(item =>
                EF.Functions.Like(item.Title, pattern, "\\") ||
                EF.Functions.Like(item.Artist, pattern, "\\") ||
                item.Album != null && EF.Functions.Like(item.Album, pattern, "\\"));
        var searched = await tracks
            .OrderBy(item => item.Artist)
            .ThenBy(item => item.Title)
            .Take(limit)
            .ToListAsync(cancellationToken);
        if (automatic.Count == 0) return searched;
        var indexedById = indexed.ToDictionary(item => item.Id);
        var selected = effectiveEngine.ScoreCandidates(
                source!,
                indexed.Where(item => automatic.Contains(item.Id)).Select(ToLocalCandidate))
            .Select(item => indexedById[item.LibraryTrackId]);
        return selected.Concat(searched).DistinctBy(item => item.Id).Take(limit).ToArray();
    }

    public async Task<TrackMatchActivityData> GetActivityDataAsync(
        TrackMatchActor actor,
        DateTimeOffset? before = null,
        Guid? beforeId = null,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var matches = await db.TrackMatches.AsNoTracking()
            .Where(item => (actor.IsAdministrator || item.OwnerUserId == actor.UserId) &&
                           (!before.HasValue ||
                            item.DecidedAt < before.Value ||
                            item.DecidedAt == before.Value &&
                            beforeId.HasValue &&
                            item.Id.CompareTo(beforeId.Value) < 0))
            .OrderByDescending(item => item.DecidedAt)
            .ThenByDescending(item => item.Id)
            .Take(Math.Clamp(limit, 1, 500))
            .ToListAsync(cancellationToken);
        var snapshotIds = matches.Select(item => item.ExternalSnapshotId).Distinct().ToArray();
        var snapshots = snapshotIds.Length == 0
            ? []
            : await db.ExternalMetadataSnapshots.AsNoTracking()
                .Where(item => (actor.IsAdministrator || item.OwnerUserId == actor.UserId) && snapshotIds.Contains(item.Id))
                .ToListAsync(cancellationToken);
        var identityIds = snapshots
            .Where(item => item.ProviderTrackIdentityId.HasValue)
            .Select(item => item.ProviderTrackIdentityId!.Value)
            .Distinct()
            .ToArray();
        var externalHashes = snapshots.Select(item => item.ExternalIdHash)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var visibleIdentities = db.ProviderTrackIdentities.AsNoTracking().Where(item =>
            (actor.IsAdministrator || item.ProviderAccountId == null ||
                db.ProviderAccounts.Any(account => account.Id == item.ProviderAccountId &&
                    (account.OwnerUserId == actor.UserId || account.OwnerUserId == null))));
        var sourceIdentities = identityIds.Length == 0 && externalHashes.Length == 0
            ? []
            : await visibleIdentities
                .Where(item => (identityIds.Contains(item.Id) ||
                                externalHashes.Contains(item.ExternalIdHash)))
                .ToListAsync(cancellationToken);
        var canonicalIds = matches.Where(item => item.CanonicalRecordingId.HasValue)
            .Select(item => item.CanonicalRecordingId!.Value)
            .Concat(sourceIdentities.Select(item => item.CanonicalRecordingId))
            .Distinct()
            .ToArray();
        var identities = canonicalIds.Length == 0
            ? sourceIdentities
            : await visibleIdentities
                .Where(item => canonicalIds.Contains(item.CanonicalRecordingId))
                .ToListAsync(cancellationToken);
        var libraryIds = matches
            .Where(item => item.LibraryTrackId.HasValue)
            .Select(item => item.LibraryTrackId!.Value)
            .Distinct()
            .ToArray();
        var libraryTracks = libraryIds.Length == 0 && canonicalIds.Length == 0
            ? []
            : await (await AccessibleTracksAsync(db, actor, cancellationToken))
                .Where(item => libraryIds.Contains(item.Id) ||
                               item.CanonicalRecordingId.HasValue &&
                               canonicalIds.Contains(item.CanonicalRecordingId.Value))
                .ToListAsync(cancellationToken);
        return new(matches, snapshots, identities, libraryTracks);
    }

    public async Task<TrackMatchResolutionData> GetResolutionDataAsync(
        TrackMatchActor actor,
        Guid ownerUserId,
        IReadOnlyCollection<Guid> externalSnapshotIds,
        CancellationToken cancellationToken = default)
    {
        var snapshotIds = externalSnapshotIds.Distinct().ToArray();
        if (snapshotIds.Length == 0)
            return new([], [], [], []);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var snapshots = await db.ExternalMetadataSnapshots.AsNoTracking()
            .Where(item => item.OwnerUserId == ownerUserId &&
                           snapshotIds.Contains(item.Id))
            .ToListAsync(cancellationToken);
        var ownedSnapshotIds = snapshots.Select(item => item.Id).ToArray();
        var identityIds = snapshots
            .Where(item => item.ProviderTrackIdentityId.HasValue)
            .Select(item => item.ProviderTrackIdentityId!.Value)
            .Distinct()
            .ToArray();
        var sourceIdentities = await db.ProviderTrackIdentities.AsNoTracking()
            .Where(item => identityIds.Contains(item.Id) &&
                           (item.Verification == ProviderIdentityVerification.Verified ||
                            item.Verification == ProviderIdentityVerification.Pinned))
            .ToListAsync(cancellationToken);
        var canonicalIds = sourceIdentities
            .Select(item => item.CanonicalRecordingId)
            .Distinct()
            .ToArray();
        var identities = canonicalIds.Length == 0
            ? sourceIdentities
            : await db.ProviderTrackIdentities.AsNoTracking()
                .Where(item => canonicalIds.Contains(item.CanonicalRecordingId) &&
                               item.ResourceKind == ProviderResourceKind.Track &&
                               (item.Verification == ProviderIdentityVerification.Verified ||
                                item.Verification == ProviderIdentityVerification.Pinned))
                .ToListAsync(cancellationToken);
        var overrides = await ManualTrackOverrides.LoadAsync(
            db, actor.UserId, snapshots, cancellationToken);
        var decisions = await LatestDecisions(db.TrackMatches.AsNoTracking()
                .Where(item => item.OwnerUserId == ownerUserId &&
                                   ownedSnapshotIds.Contains(item.ExternalSnapshotId)))
            .ToArrayAsync(cancellationToken);
        return new(snapshots, identities, overrides, decisions);
    }
}
