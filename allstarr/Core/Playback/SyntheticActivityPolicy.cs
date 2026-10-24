using allstarr.Core.Intelligence;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Playback;

public static class SyntheticActivityPolicy
{
    public static bool IsProviderItem(string? itemId) =>
        itemId?.StartsWith("ext-", StringComparison.OrdinalIgnoreCase) == true ||
        itemId?.StartsWith("external:", StringComparison.OrdinalIgnoreCase) == true;

    public static async Task<bool> IsExcludedAsync(HttpContext http, string? itemId)
    {
        var context = http.GetProtocolExecutionContext();
        return context != null && await IsExcludedAsync(
            http.RequestServices.GetService<IDbContextFactory<AllstarrDbContext>>(), context, itemId, http.RequestAborted);
    }

    public static async Task<bool> IsExcludedAsync(IDbContextFactory<AllstarrDbContext>? factory,
        ProtocolExecutionContext context, string? itemId, CancellationToken token)
    {
        if (!IsProviderItem(itemId)) return false;
        if (context.Principal?.IsSynthetic == true) return true;
        if (factory == null || context.Actor?.EffectiveUserId is not { } owner) return false;
        await using var db = await factory.CreateDbContextAsync(token);
        var protocol = context.Protocol.ToString().ToLowerInvariant();
        return await db.Users.AsNoTracking().AnyAsync(user => user.Id == owner && user.IsSynthetic &&
            user.BackendType == protocol &&
            user.BackendInstanceId == context.BackendInstanceId &&
            user.BackendPrincipalId == context.VerifiedBackendPrincipalId, token);
    }

    public static async Task<bool> IsExcludedAsync(IDbContextFactory<AllstarrDbContext> factory,
        PlaybackSignalPayload payload, CancellationToken token)
    {
        if (!IsProviderItem(payload.ItemId)) return false;
        await using var db = await factory.CreateDbContextAsync(token);
        return await IsExcludedAsync(db, payload.Scope, payload.ItemId, token);
    }

    public static Task<bool> IsExcludedAsync(AllstarrDbContext db, IntelligenceScope scope, string? itemId,
        CancellationToken token) => !IsProviderItem(itemId) ? Task.FromResult(false) :
        db.Users.AsNoTracking().AnyAsync(user => user.Id == scope.OwnerUserId && user.IsSynthetic &&
            user.BackendType == scope.Protocol && user.BackendInstanceId == scope.BackendInstanceId, token);

    public static async Task<bool> IsSyntheticPrincipalAsync(IDbContextFactory<AllstarrDbContext> factory,
        string protocol, string backendInstanceId, string principalId, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        return await db.Users.AsNoTracking().AnyAsync(user => user.IsSynthetic &&
            user.BackendType == protocol && user.BackendInstanceId == backendInstanceId &&
            user.BackendPrincipalId == principalId, token);
    }
}
