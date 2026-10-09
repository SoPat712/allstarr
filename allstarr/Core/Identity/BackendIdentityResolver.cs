using allstarr.Core.Operations;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Identity;

public sealed record BackendIdentityDescriptor(
    string BackendType,
    string PrincipalId,
    string? DisplayName = null,
    bool? IsAdministrator = null,
    string? BackendInstanceId = null);

public sealed record AllstarrPrincipal(
    Guid UserId,
    string BackendType,
    string BackendInstanceId,
    string BackendPrincipalId,
    string DisplayName,
    bool IsAdministrator);

public sealed class BackendIdentityResolver(
    IDbContextFactory<AllstarrDbContext> contextFactory,
    DurableStorageState storageState,
    IdentityOptions options,
    IPlatformClock clock)
{
    public const string HttpContextPrincipalItemKey = "allstarr.principal";

    public async Task<AllstarrPrincipal?> ResolveAsync(
        BackendIdentityDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        var backendType = descriptor.BackendType?.Trim().ToLowerInvariant();
        var instanceId = (descriptor.BackendInstanceId ?? options.BackendInstanceId).Trim();
        var principalId = descriptor.PrincipalId?.Trim();
        if (string.IsNullOrWhiteSpace(backendType) || backendType.Length > 32 ||
            string.IsNullOrWhiteSpace(instanceId) || instanceId.Length > 200 ||
            string.IsNullOrWhiteSpace(principalId) || principalId.Length > 300)
            throw new ArgumentException("A valid backend type, instance and principal are required.", nameof(descriptor));
        if (storageState.GetSnapshot().Readiness != DurableStorageReadiness.Ready) return null;

        var now = clock.UtcNow;
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var users = db.Users.Where(item => item.BackendType == backendType &&
            item.BackendInstanceId == instanceId && item.BackendPrincipalId == principalId);
        var user = await users.SingleOrDefaultAsync(cancellationToken);
        if (user == null)
        {
            user = new UserRecord
            {
                Id = Guid.CreateVersion7(),
                BackendType = backendType,
                BackendInstanceId = instanceId,
                BackendPrincipalId = principalId,
                DisplayName = string.IsNullOrWhiteSpace(descriptor.DisplayName) ? principalId : descriptor.DisplayName.Trim(),
                IsAdmin = descriptor.IsAdministrator ?? false,
                Enabled = true,
                CreatedAt = now,
                UpdatedAt = now,
                LastSeenAt = now
            };
            db.Users.Add(user);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return ToPrincipal(user);
            }
            catch (DbUpdateException exception) when (DbErrors.IsUniqueViolation(exception))
            {
                db.ChangeTracker.Clear();
                user = await users.SingleAsync(cancellationToken);
            }
        }

        if (!user.Enabled) throw new UnauthorizedAccessException("The Allstarr user is disabled.");
        if (!string.IsNullOrWhiteSpace(descriptor.DisplayName)) user.DisplayName = descriptor.DisplayName.Trim();
        if (descriptor.IsAdministrator is { } isAdministrator) user.IsAdmin = isAdministrator;
        user.LastSeenAt = now;
        user.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return ToPrincipal(user);
    }

    private static AllstarrPrincipal ToPrincipal(UserRecord user) => new(
        user.Id, user.BackendType, user.BackendInstanceId, user.BackendPrincipalId,
        user.DisplayName, user.IsAdmin);
}
