using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Matching;

public sealed record ManualTrackOverrideLayers(
    ManualTrackOverrideRecord? Personal,
    ManualTrackOverrideRecord? Household)
{
    public ManualTrackOverrideRecord? Effective => Personal ?? Household;
    public string EffectiveScope => Personal != null ? "personal" : Household != null ? "household" : "automatic";
    public IEnumerable<ManualTrackOverrideRecord> All =>
        new[] { Personal, Household }.OfType<ManualTrackOverrideRecord>();
}

internal static class ManualTrackOverrides
{
    public static async Task<IReadOnlyList<ManualTrackOverrideRecord>> LoadAsync(
        AllstarrDbContext db, Guid tenantId, Guid userId,
        IReadOnlyCollection<ExternalMetadataSnapshotRecord> snapshots, CancellationToken cancellationToken,
        bool includeRevoked = false)
    {
        if (snapshots.Count == 0) return [];
        var hashes = snapshots.Select(item => item.ExternalIdHash).Distinct().ToArray();
        var providers = snapshots.Select(item => item.ProviderId).Distinct().ToArray();
        var records = await db.ManualTrackOverrides.AsNoTracking().Where(item =>
                item.TenantId == tenantId && (includeRevoked || item.RevokedAt == null) &&
                (item.OwnerUserId == userId || item.OwnerUserId == null) &&
                providers.Contains(item.SourceProviderId) && hashes.Contains(item.SourceExternalIdHash))
            .ToArrayAsync(cancellationToken);
        var requested = snapshots.Select(item => (item.ProviderId, item.ExternalIdHash)).ToHashSet();
        return records.Where(item => requested.Contains((item.SourceProviderId, item.SourceExternalIdHash))).ToArray();
    }

    public static IQueryable<ManualTrackOverrideRecord> ForSource(
        AllstarrDbContext db, ExternalMetadataSnapshotRecord snapshot) =>
        db.ManualTrackOverrides.Where(item => item.TenantId == snapshot.TenantId &&
            item.SourceProviderId == snapshot.ProviderId && item.SourceExternalIdHash == snapshot.ExternalIdHash);

    public static async Task<ManualTrackOverrideLayers> ReadAsync(
        AllstarrDbContext db, ExternalMetadataSnapshotRecord snapshot, Guid userId,
        CancellationToken cancellationToken)
    {
        var records = await ForSource(db, snapshot).AsNoTracking().Where(item =>
            item.RevokedAt == null && (item.OwnerUserId == userId || item.OwnerUserId == null))
            .ToArrayAsync(cancellationToken);
        return Select(records, userId);
    }

    public static IReadOnlyDictionary<Guid, ManualTrackOverrideLayers> Index(
        IEnumerable<ExternalMetadataSnapshotRecord> snapshots,
        IEnumerable<ManualTrackOverrideRecord> records, Guid userId)
    {
        var bySource = records.Where(item => item.RevokedAt == null &&
                (item.OwnerUserId == userId || item.OwnerUserId == null))
            .GroupBy(item => (item.SourceProviderId, item.SourceExternalIdHash))
            .ToDictionary(group => group.Key, group => Select(group, userId));
        return snapshots.ToDictionary(item => item.Id,
            item => bySource.GetValueOrDefault((item.ProviderId, item.ExternalIdHash)) ?? new(null, null));
    }

    public static Task<HashSet<Guid>> ProtectedSnapshotIdsAsync(
        AllstarrDbContext db, Guid tenantId, IReadOnlyCollection<Guid> snapshotIds,
        CancellationToken cancellationToken) => db.ExternalMetadataSnapshots.AsNoTracking()
        .Where(snapshot => snapshot.TenantId == tenantId && snapshotIds.Contains(snapshot.Id) &&
            db.ManualTrackOverrides.Any(authority => authority.TenantId == tenantId &&
                authority.SourceProviderId == snapshot.ProviderId && authority.SourceExternalIdHash == snapshot.ExternalIdHash &&
                authority.RevokedAt == null && (authority.OwnerUserId == snapshot.OwnerUserId || authority.OwnerUserId == null)))
        .Select(snapshot => snapshot.Id).ToHashSetAsync(cancellationToken);

    private static ManualTrackOverrideLayers Select(IEnumerable<ManualTrackOverrideRecord> records, Guid userId)
    {
        var materialized = records.ToArray();
        return new(materialized.SingleOrDefault(item => item.OwnerUserId == userId),
            materialized.SingleOrDefault(item => item.OwnerUserId == null));
    }
}
