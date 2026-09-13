using allstarr.Core.Storage;

namespace allstarr.Core.Identity;

public static class ProviderAccountOwnership
{
    public static IQueryable<ProviderAccountRecord> OwnedBy(
        this IQueryable<ProviderAccountRecord> accounts, Guid? tenantId, Guid? userId) =>
        accounts.Where(account => tenantId.HasValue && userId.HasValue &&
            (account.Scope == ProviderAccountScope.User && account.TenantId == tenantId &&
             account.OwnerUserId == userId ||
             account.Scope == ProviderAccountScope.Global && account.TenantId == null &&
             account.CreatedByUserId == userId));
}
