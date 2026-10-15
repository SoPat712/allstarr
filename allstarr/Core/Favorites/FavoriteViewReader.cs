using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Favorites;

public sealed class FavoriteViewReader(IDbContextFactory<AllstarrDbContext> factory)
{
    public async Task<IReadOnlyList<FavoriteViewItem>> ReadAsync(ProtocolExecutionContext execution,
        CancellationToken cancellationToken = default)
    {
        if (execution.Principal is not { } principal || execution.Actor?.EffectiveUserId is not { } userId) return [];
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if (!await db.Users.AnyAsync(item => item.Id == userId && item.Enabled &&
            item.BackendType == principal.BackendType && item.BackendInstanceId == execution.BackendInstanceId &&
            item.BackendPrincipalId == execution.VerifiedBackendPrincipalId, cancellationToken)) return [];
        var rows = await (from state in db.Set<FavoriteStateRecord>().AsNoTracking()
                          join accepted in db.Set<FavoriteEventRecord>() on state.LastEventId equals accepted.Id
                          where state.OwnerUserId == userId && state.Protocol == principal.BackendType &&
                                state.BackendInstanceId == execution.BackendInstanceId && state.IsFavorite &&
                                accepted.OwnerUserId == userId && accepted.Protocol == state.Protocol &&
                                accepted.BackendInstanceId == state.BackendInstanceId && accepted.ItemId == state.ItemId &&
                                accepted.BackendPrincipalId == execution.VerifiedBackendPrincipalId &&
                                accepted.Operation == FavoriteOperation.Favorite
                          orderby accepted.CreatedAt descending, state.Id
                          select new { state.ItemId, state.DisplayMetadataJson, accepted.CreatedAt })
            .ToArrayAsync(cancellationToken);
        var result = new List<FavoriteViewItem>();
        foreach (var row in rows)
        {
            if (FavoriteDisplaySnapshot.Resource(row.ItemId) is not { } resource) continue;
            result.Add(new(row.ItemId, resource, row.CreatedAt, FavoriteDisplaySnapshot.Read(row.ItemId, row.DisplayMetadataJson)
                ?? FavoriteDisplaySnapshot.Placeholder(row.ItemId, resource.ResourceKind)));
        }
        return result;
    }
}
