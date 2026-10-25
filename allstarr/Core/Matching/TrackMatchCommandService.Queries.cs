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
using allstarr.Services.Common;
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
        var visibleAccountIds = actor.IsAdministrator
            ? null
            : await VisibleProviderAccountIdsAsync(db, actor.UserId, cancellationToken);

        var sourceIdentities = await VisibleProviderIdentitiesAsync(
                db,
                actor,
                db.ProviderTrackIdentities.AsNoTracking()
                    .Where(item => item.ProviderId == providerId &&
                                   item.ExternalId == externalId)
                    .OrderBy(item => item.CreatedAt),
                visibleAccountIds,
                cancellationToken);
        var canonicalIds = sourceIdentities
            .Select(item => item.CanonicalRecordingId)
            .Distinct()
            .ToArray();
        var identities = canonicalIds.Length == 0
            ? sourceIdentities
            : await VisibleProviderIdentitiesAsync(
                db,
                actor,
                db.ProviderTrackIdentities.AsNoTracking()
                    .Where(item => canonicalIds.Contains(item.CanonicalRecordingId))
                    .OrderBy(item => item.ProviderId)
                    .ThenBy(item => item.CreatedAt),
                visibleAccountIds,
                cancellationToken);

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
        var visibleAccountIds = actor.IsAdministrator
            ? null
            : await VisibleProviderAccountIdsAsync(db, actor.UserId, cancellationToken);
        var snapshotsQuery = db.ExternalMetadataSnapshots.AsNoTracking()
            ;
        if (!actor.IsAdministrator)
            snapshotsQuery = snapshotsQuery.Where(item => item.OwnerUserId == actor.UserId);
        if (externalSnapshotId.HasValue)
            snapshotsQuery = snapshotsQuery.Where(item => item.Id == externalSnapshotId.Value);
        var orderedSnapshots = snapshotsQuery.OrderByDescending(item => item.RetrievedAt);
        var limit = Math.Clamp(scanLimit, 1, 10000);
        List<ExternalMetadataSnapshotRecord> snapshots;
        if (string.IsNullOrWhiteSpace(search))
            snapshots = await orderedSnapshots.Take(limit).ToListAsync(cancellationToken);
        else
        {
            var normalizedSearch = TrackMatchReviewSearch.Normalize(search);
            snapshots = [];
            if (normalizedSearch.Length > 0)
                await foreach (var snapshot in orderedSnapshots.AsAsyncEnumerable().WithCancellation(cancellationToken))
                {
                    if (!TrackMatchReviewSearch.Matches(snapshot.ProviderId, snapshot.PayloadJson, normalizedSearch))
                        continue;
                    snapshots.Add(snapshot);
                    if (snapshots.Count == limit) break;
                }
        }
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
        var sourceIdentities = await VisibleProviderIdentitiesAsync(
            db,
            actor,
            db.ProviderTrackIdentities.AsNoTracking()
                .Where(item => item.ResourceKind == ProviderResourceKind.Track &&
                               (sourceIdentityIds.Contains(item.Id) ||
                                sourceExternalIdHashes.Contains(item.ExternalIdHash))),
            visibleAccountIds,
            cancellationToken);
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
        var identities = await VisibleProviderIdentitiesAsync(
            db,
            actor,
            db.ProviderTrackIdentities.AsNoTracking()
                .Where(item => canonicalIds.Contains(item.CanonicalRecordingId))
                .OrderBy(item => item.ProviderId)
                .ThenBy(item => item.ExternalId),
            visibleAccountIds,
            cancellationToken);
        return new(snapshots, decisions, overrides, library, identities);
    }

    private async Task<IReadOnlyList<ProviderTrackIdentityRecord>> VisibleProviderIdentitiesAsync(
        AllstarrDbContext db,
        TrackMatchActor actor,
        IQueryable<ProviderTrackIdentityRecord> query,
        IReadOnlySet<Guid>? visibleAccountIds,
        CancellationToken cancellationToken)
    {
        if (actor.IsAdministrator)
            return await query.ToListAsync(cancellationToken);
        if (visibleAccountIds == null || visibleAccountIds.Count == 0)
            return await query.Where(item => item.Scope == ProviderIdentityScope.Catalog && item.ProviderAccountId == null)
                .ToListAsync(cancellationToken);

        return await query.Where(item =>
                item.Scope == ProviderIdentityScope.Catalog && item.ProviderAccountId == null ||
                item.Scope == ProviderIdentityScope.Account && item.ProviderAccountId.HasValue &&
                visibleAccountIds.Contains(item.ProviderAccountId.Value) &&
                db.ProviderAccounts.Any(account => account.Id == item.ProviderAccountId.Value &&
                    account.ProviderId == item.ProviderId))
            .ToListAsync(cancellationToken);
    }

    private static async Task<IReadOnlySet<Guid>> VisibleProviderAccountIdsAsync(
        AllstarrDbContext db,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var accounts = await db.ProviderAccounts.AsNoTracking()
            .Where(item => item.Enabled &&
                (item.OwnerUserId == userId || item.OwnerUserId == null) &&
                (!item.SecretReferenceId.HasValue || db.SecretReferences.Any(secret =>
                    secret.Id == item.SecretReferenceId && secret.RevokedAt == null)))
            .ToListAsync(cancellationToken);
        accounts.RemoveAll(ProviderAccountSettings.IsRemoved);
        return accounts.Select(item => item.Id).ToHashSet();
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
        var access = await libraryAccess.ResolveUserAsync(actor.UserId, cancellationToken);
        var tracks = LibraryTrackAccess.Query(db, access);
        if (!string.IsNullOrWhiteSpace(backendLibraryId))
            tracks = tracks.Where(item => item.BackendLibraryId == backendLibraryId.Trim());
        var indexed = await tracks.ToListAsync(cancellationToken);
        var title = MatchLibrarySearch.QueryTitle(query, source);
        var searchSource = source == null
            ? new ExternalTrackMatchSnapshot("search", "local", "search", title, "Unknown", null, null, null, null, null, null)
            : source with { Title = title };
        var variants = MatchLibrarySearch.QueryVariants(query, source);
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var found = indexed.Where(item =>
                terms.All(term => $"{item.Title} {item.Artist} {item.Album}".Contains(term, StringComparison.OrdinalIgnoreCase)) ||
                variants.Any(term => MatchLibrarySearch.SimilarTitle(term, item.Title)) ||
                source?.Isrc != null && FuzzyMatcher.IdentifiersEqual(source.Isrc, item.Isrc) &&
                MatchLibrarySearch.SimilarTitle(source.Title, title))
            .ToList();
        if (librarySearch != null && libraryIndex != null && access.Context != null && access.Access.Succeeded)
        {
            var libraries = access.Access.LibraryIds.Where(id => string.IsNullOrWhiteSpace(backendLibraryId) || id == backendLibraryId.Trim()).ToArray();
            var gathered = await librarySearch.GatherAsync(access.Context, libraries, query, source, cancellationToken);
            var inputs = gathered.Where(item => libraries.Contains(item.BackendLibraryId, StringComparer.Ordinal))
                .Select(item => (Input: item, Candidate: new LocalTrackMatchCandidate(Guid.CreateVersion7(), actor.UserId,
                    access.Context.BackendInstanceId, item.BackendLibraryId, item.BackendItemId, null,
                    item.Title, item.Artist, item.Album, item.AlbumArtist, item.DurationMilliseconds, item.Isrc, item.MusicBrainzRecordingId, null)))
                .ToArray();
            var effectiveEngine = await DecisionEngineAsync(cancellationToken);
            var byId = inputs.ToDictionary(item => item.Candidate.LibraryTrackId, item => item.Input);
            var ranked = effectiveEngine.ScoreCandidates(searchSource, inputs.Select(item => item.Candidate));
            foreach (var score in ranked.Take(limit))
            {
                var input = byId[score.LibraryTrackId];
                var existing = indexed.FirstOrDefault(item => item.BackendItemId == input.BackendItemId);
                if (existing != null)
                {
                    found.Add(existing);
                    continue;
                }
                var added = await libraryIndex.UpsertAsync(access.Context, input, cancellationToken);
                var record = await db.LibraryTracks.AsNoTracking().SingleAsync(item => item.Id == added.Id, cancellationToken);
                found.Add(record);
            }
        }
        var selected = found.DistinctBy(item => item.BackendItemId, StringComparer.Ordinal).ToDictionary(item => item.Id);
        var engine = await DecisionEngineAsync(cancellationToken);
        return engine.ScoreCandidates(searchSource, selected.Values.Select(ToLocalCandidate))
            .Take(limit).Select(item => selected[item.LibraryTrackId]).ToArray();
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
        var externalHashes = snapshots.Select(item => item.ExternalIdHash).Distinct().ToArray();
        var sourceCandidates = await db.ProviderTrackIdentities.AsNoTracking()
            .Where(item => item.ResourceKind == ProviderResourceKind.Track &&
                           (identityIds.Contains(item.Id) || externalHashes.Contains(item.ExternalIdHash)) &&
                           (item.Verification == ProviderIdentityVerification.Verified ||
                            item.Verification == ProviderIdentityVerification.Pinned))
            .ToListAsync(cancellationToken);
        var accountScopes = snapshots.Where(item => item.ProviderAccountId.HasValue)
            .Select(item => (item.ProviderId.ToLowerInvariant(), item.ProviderAccountId, item.ExternalIdHash))
            .ToHashSet();
        var catalogScopes = snapshots.Select(item => (item.ProviderId.ToLowerInvariant(), item.ExternalIdHash))
            .ToHashSet();
        var sourceIdentities = sourceCandidates.Where(identity =>
                identity.Scope == ProviderIdentityScope.Account && identity.ProviderAccountId.HasValue &&
                accountScopes.Contains((identity.ProviderId.ToLowerInvariant(), identity.ProviderAccountId, identity.ExternalIdHash)) ||
                identity.Scope == ProviderIdentityScope.Catalog && !identity.ProviderAccountId.HasValue &&
                catalogScopes.Contains((identity.ProviderId.ToLowerInvariant(), identity.ExternalIdHash)))
            .ToArray();
        var decisions = await LatestDecisions(db.TrackMatches.AsNoTracking()
                .Where(item => item.OwnerUserId == ownerUserId &&
                               ownedSnapshotIds.Contains(item.ExternalSnapshotId)))
            .ToArrayAsync(cancellationToken);
        var canonicalIds = sourceIdentities
            .Select(item => item.CanonicalRecordingId)
            .Concat(decisions.Where(item => item.CanonicalRecordingId.HasValue)
                .Select(item => item.CanonicalRecordingId!.Value))
            .Distinct()
            .ToArray();
        var identities = canonicalIds.Length == 0
            ? sourceIdentities.ToList()
            : await db.ProviderTrackIdentities.AsNoTracking()
                .Where(item => canonicalIds.Contains(item.CanonicalRecordingId) &&
                               item.ResourceKind == ProviderResourceKind.Track &&
                               (item.Verification == ProviderIdentityVerification.Verified ||
                                item.Verification == ProviderIdentityVerification.Pinned))
                .ToListAsync(cancellationToken);
        var overrides = await ManualTrackOverrides.LoadAsync(
            db, actor.UserId, snapshots, cancellationToken);
        return new(snapshots, identities, overrides, decisions);
    }
}
