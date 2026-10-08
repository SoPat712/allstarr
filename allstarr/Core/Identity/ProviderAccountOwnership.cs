using allstarr.Core.Storage;

namespace allstarr.Core.Identity;

public static class ProviderAccountOwnership
{
    public static IQueryable<ProviderAccountRecord> OwnedBy(
        this IQueryable<ProviderAccountRecord> accounts, Guid? tenantId, Guid? userId) =>
        accounts.Where(account => tenantId.HasValue && userId.HasValue &&
            account.TenantId == tenantId && account.OwnerUserId == userId);

    public static IQueryable<ProviderAccountRecord> AvailableTo(
        this IQueryable<ProviderAccountRecord> accounts, Guid? tenantId, Guid? userId) =>
        accounts.Where(account => tenantId.HasValue && userId.HasValue &&
            (account.TenantId == tenantId && account.OwnerUserId == userId ||
             account.TenantId == null && account.OwnerUserId == null));
}
