using allstarr.Core.Operations;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Identity;

public sealed record BackendIdentityDescriptor(
    string BackendType,
    string PrincipalId,
    string? DisplayName = null,
    bool IsAdministrator = false,
    string? BackendInstanceId = null);

public sealed record AllstarrPrincipal(
    Guid TenantId,
    Guid UserId,
    string BackendType,
    string BackendInstanceId,
    string BackendPrincipalId,
    string DisplayName,
    bool IsAdministrator);

public sealed class BackendIdentityResolver
{
    public const string HttpContextPrincipalItemKey = "allstarr.principal";

    private readonly IDbContextFactory<AllstarrDbContext> _contextFactory;
    private readonly DurableStorageState _storageState;
    private readonly IdentityOptions _options;
    private readonly IPlatformClock _clock;

    public BackendIdentityResolver(
        IDbContextFactory<AllstarrDbContext> contextFactory,
        DurableStorageState storageState,
        IdentityOptions options,
        IPlatformClock clock)
    {
        _contextFactory = contextFactory;
        _storageState = storageState;
        _options = options;
        _clock = clock;
    }

    public async Task<AllstarrPrincipal?> ResolveAsync(
        BackendIdentityDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        ValidateDescriptor(descriptor);
        if (_storageState.GetSnapshot().Readiness != DurableStorageReadiness.Ready)
        {
            return null;
        }

        var mode = _options.ParseMode();
        var backendType = descriptor.BackendType.Trim().ToLowerInvariant();
        var instanceId = (descriptor.BackendInstanceId ?? _options.BackendInstanceId).Trim();
        var principalId = descriptor.PrincipalId.Trim();
        var now = _clock.UtcNow;

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var identity = await context.BackendIdentities.SingleOrDefaultAsync(
            item => item.BackendType == backendType &&
                    item.BackendInstanceId == instanceId &&
                    item.PrincipalId == principalId,
            cancellationToken);
        if (identity != null)
        {
            var user = await context.Users.SingleAsync(item => item.Id == identity.UserId, cancellationToken);
            if (user.Status != PlatformUserStatus.Active)
            {
                throw new UnauthorizedAccessException("The mapped Allstarr user is disabled.");
            }

            identity.LastSeenAt = now;
            if (!string.IsNullOrWhiteSpace(descriptor.DisplayName))
            {
                identity.DisplayName = descriptor.DisplayName.Trim();
                user.DisplayName = descriptor.DisplayName.Trim();
                user.UpdatedAt = now;
            }

            await context.SaveChangesAsync(cancellationToken);
            return ToPrincipal(identity, user, descriptor.IsAdministrator);
        }

        if (mode == MultiUserMode.Strict)
        {
            return null;
        }

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var tenant = await EnsureDefaultTenantAsync(context, cancellationToken);
        PlatformUserRecord newUser;
        if (mode == MultiUserMode.SingleUser)
        {
            var userId = _options.GetSingleUserId();
            newUser = await context.Users.SingleOrDefaultAsync(item => item.Id == userId, cancellationToken)
                      ?? new PlatformUserRecord
                      {
                          Id = userId,
                          TenantId = tenant.Id,
                          DisplayName = descriptor.DisplayName?.Trim() ?? "Allstarr user",
                          Status = PlatformUserStatus.Active,
                          CreatedAt = now,
                          UpdatedAt = now
                      };
            if (context.Entry(newUser).State == EntityState.Detached)
            {
                context.Users.Add(newUser);
            }
        }
        else
        {
            newUser = new PlatformUserRecord
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenant.Id,
                DisplayName = descriptor.DisplayName?.Trim() ?? principalId,
                Status = PlatformUserStatus.Active,
                CreatedAt = now,
                UpdatedAt = now
            };
            context.Users.Add(newUser);
        }

        identity = new BackendIdentityRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            UserId = newUser.Id,
            BackendType = backendType,
            BackendInstanceId = instanceId,
            PrincipalId = principalId,
            DisplayName = descriptor.DisplayName?.Trim(),
            CreatedAt = now,
            LastSeenAt = now
        };
        context.BackendIdentities.Add(identity);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            context.ChangeTracker.Clear();
            identity = await context.BackendIdentities.SingleAsync(
                item => item.BackendType == backendType &&
                        item.BackendInstanceId == instanceId &&
                        item.PrincipalId == principalId,
                cancellationToken);
            newUser = await context.Users.SingleAsync(item => item.Id == identity.UserId, cancellationToken);
        }

        return ToPrincipal(identity, newUser, descriptor.IsAdministrator);
    }

    internal async Task<TenantRecord> EnsureDefaultTenantAsync(
        AllstarrDbContext context,
        CancellationToken cancellationToken)
    {
        var tenantId = _options.GetDefaultTenantId();
        var tenant = await context.Tenants.SingleOrDefaultAsync(item => item.Id == tenantId, cancellationToken);
        if (tenant != null)
        {
            return tenant;
        }

        tenant = new TenantRecord
        {
            Id = tenantId,
            Slug = _options.DefaultTenantSlug.Trim(),
            Name = _options.DefaultTenantName.Trim(),
            CreatedAt = _clock.UtcNow
        };
        context.Tenants.Add(tenant);
        return tenant;
    }

    private static void ValidateDescriptor(BackendIdentityDescriptor descriptor)
    {
        if (string.IsNullOrWhiteSpace(descriptor.BackendType) ||
            string.IsNullOrWhiteSpace(descriptor.PrincipalId))
        {
            throw new ArgumentException("Backend type and principal ID are required.", nameof(descriptor));
        }

        if (descriptor.BackendType.Length > 32 || descriptor.PrincipalId.Length > 300)
        {
            throw new ArgumentException("Backend identity fields exceed their supported length.", nameof(descriptor));
        }
    }

    private static AllstarrPrincipal ToPrincipal(
        BackendIdentityRecord identity,
        PlatformUserRecord user,
        bool isAdministrator) => new(
        identity.TenantId,
        identity.UserId,
        identity.BackendType,
        identity.BackendInstanceId,
        identity.PrincipalId,
        user.DisplayName,
        isAdministrator);
}
