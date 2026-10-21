using allstarr.Core.Capabilities;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Matching;

public sealed partial class TrackIdentityService
{
    public async Task<string?> GetStoredMetadataAsync(ProviderActorContext actor, ProviderExternalResourceId resource,
        CancellationToken cancellationToken = default)
    {
        EnsureStorageReady();
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await ValidateActorAsync(db, actor, cancellationToken);
        var kind = DisplayResourceKind(resource);
        var hash = CanonicalCatalogKeys.Hash(resource.Value);
        var userId = actor.EffectiveUserId;
        var snapshots = await db.ExternalMetadataSnapshots.AsNoTracking().Where(item =>
            item.ProviderId == resource.ProviderId && item.ResourceKind == kind && item.ExternalIdHash == hash &&
            (item.OwnerUserId == userId || userId == null && item.ProviderAccountId == null) &&
            (item.ProviderAccountId == null || db.ProviderAccounts.Any(account => account.Id == item.ProviderAccountId &&
                account.Enabled && (account.OwnerUserId == null || account.OwnerUserId == userId))))
            .ToArrayAsync(cancellationToken);
        return snapshots.OrderByDescending(item => item.RetrievedAt).ThenByDescending(item => item.SnapshotVersion)
            .ThenBy(item => item.Id).FirstOrDefault()?.PayloadJson;
    }

    private static string DisplayResourceKind(ProviderExternalResourceId resource) => resource.ResourceKind switch
    {
        ProviderResourceKind.Album => "album",
        ProviderResourceKind.Artist => "artist",
        _ => throw new ArgumentException("Stored resource metadata requires an album or artist.", nameof(resource))
    };
}
