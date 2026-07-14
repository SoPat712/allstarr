using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Providers.Spotify;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Intelligence;

public interface IScopedRecommendationAccountAccessor
{
    Task<bool> HasAccountAsync(IntelligenceScope scope, string providerId, CancellationToken cancellationToken);
    Task<T> UseAsync<T>(IntelligenceScope scope, string providerId,
        Func<JsonElement, CancellationToken, Task<T>> operation, CancellationToken cancellationToken);
}

public sealed class ScopedRecommendationAccountAccessor(IDbContextFactory<AllstarrDbContext> factory,
    IProviderAccountSecretAccessor secrets) : IScopedRecommendationAccountAccessor
{
    public async Task<bool> HasAccountAsync(IntelligenceScope scope, string providerId, CancellationToken cancellationToken)
    {
        IntelligencePolicyService.ValidateScope(scope); await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.ProviderAccounts.AsNoTracking().AnyAsync(item => item.Enabled && item.SecretReferenceId != null && item.ProviderId == providerId && item.TenantId == scope.TenantId &&
            (item.Scope == ProviderAccountScope.User && item.OwnerUserId == scope.OwnerUserId || item.Scope == ProviderAccountScope.Library && item.LibraryScopeId == scope.LibraryScopeId), cancellationToken);
    }
    public async Task<T> UseAsync<T>(IntelligenceScope scope, string providerId,
        Func<JsonElement, CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        IntelligencePolicyService.ValidateScope(scope);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var accounts = await db.ProviderAccounts.AsNoTracking().Where(item => item.Enabled && item.ProviderId == providerId &&
            item.TenantId == scope.TenantId && (item.Scope == ProviderAccountScope.User && item.OwnerUserId == scope.OwnerUserId ||
            item.Scope == ProviderAccountScope.Library && item.LibraryScopeId == scope.LibraryScopeId)).ToListAsync(cancellationToken);
        var account = accounts.OrderBy(item => item.Scope == ProviderAccountScope.User ? 0 : 1).ThenBy(item => item.Id).FirstOrDefault()
            ?? throw new NotSupportedException("No exact-scope recommendation account is configured.");
        var context = new ProviderAccountContext(account.Id, account.ProviderId, account.Scope, account.Revision,
            account.Enabled, account.TenantId, account.OwnerUserId, account.LibraryScopeId,
            "recommendation-account", account.SecretReferenceId);
        return await secrets.UseAsync(context, async (bytes) =>
        {
            using var document = JsonDocument.Parse(bytes);
            return await operation(document.RootElement, cancellationToken);
        }, cancellationToken);
    }
}
