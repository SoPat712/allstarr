using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Identity;

public sealed record ProviderAccountResolutionRequest(
    AllstarrPrincipal Principal,
    string ProviderId,
    string Capability,
    Guid? RequestedAccountId = null,
    IReadOnlyCollection<ProviderAccountScope>? AllowedScopes = null,
    bool AllowSharedAccount = true);

public sealed record ResolvedProviderAccount(ProviderAccountRecord Account, string Reason);

public sealed class ProviderAccountResolver(IDbContextFactory<AllstarrDbContext> contextFactory)
{
    public async Task<ResolvedProviderAccount?> ResolveAsync(
        ProviderAccountResolutionRequest request,
        CancellationToken cancellationToken = default) =>
        (await ResolveCandidatesAsync(request, cancellationToken)).FirstOrDefault();

    public async Task<IReadOnlyList<ResolvedProviderAccount>> ResolveCandidatesAsync(
        ProviderAccountResolutionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.ProviderId) || string.IsNullOrWhiteSpace(request.Capability))
            throw new ArgumentException("Provider and capability are required.", nameof(request));

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await context.Users.AsNoTracking().AnyAsync(item => item.Id == request.Principal.UserId && item.Enabled,
                cancellationToken))
            throw new UnauthorizedAccessException("The requesting user is unavailable.");
        var providerId = request.ProviderId.Trim().ToLowerInvariant();
        var allowPersonal = request.AllowedScopes == null || request.AllowedScopes.Contains(ProviderAccountScope.Personal);
        var allowShared = request.AllowSharedAccount &&
            (request.AllowedScopes == null || request.AllowedScopes.Contains(ProviderAccountScope.Shared));
        var accounts = await context.ProviderAccounts.AsNoTracking()
            .Where(item => item.Enabled && item.ProviderId == providerId &&
                (allowPersonal && item.OwnerUserId == request.Principal.UserId ||
                 allowShared && item.OwnerUserId == null))
            .Where(item => !item.SecretReferenceId.HasValue || context.SecretReferences.Any(secret =>
                secret.Id == item.SecretReferenceId && secret.RevokedAt == null))
            .OrderByDescending(item => item.OwnerUserId.HasValue)
            .ThenBy(item => item.CreatedAt)
            .ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);

        if (request.RequestedAccountId.HasValue)
        {
            var requested = accounts.SingleOrDefault(item => item.Id == request.RequestedAccountId.Value)
                ?? throw new UnauthorizedAccessException("The requested provider account is unavailable to this user.");
            return [new(requested, "explicit_account")];
        }

        return accounts.Select(account => new ResolvedProviderAccount(account,
            account.OwnerUserId.HasValue ? "personal_account" : "shared_account")).ToArray();
    }
}
