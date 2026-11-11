using System.Collections.Concurrent;
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
    bool IsAdministrator,
    bool IsSynthetic = false);

public sealed class BackendIdentityResolver(
    IDbContextFactory<AllstarrDbContext> contextFactory,
    DurableStorageState storageState,
    IdentityOptions options,
    IPlatformClock clock,
    ILogger<BackendIdentityResolver>? logger = null,
    IHostApplicationLifetime? lifetime = null)
{
    public const string HttpContextPrincipalItemKey = "allstarr.principal";
    private readonly ConcurrentDictionary<BackendIdentityDescriptor, PendingResolution> _pending = new();

    private sealed class PendingResolution
    {
        public readonly TaskCompletionSource<AllstarrPrincipal?> Existing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly Lazy<Task<AllstarrPrincipal?>> Work;

        public PendingResolution(Func<PendingResolution, Task<AllstarrPrincipal?>> resolve) =>
            Work = new(() => Task.Run(() => resolve(this)));
    }

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

        var key = descriptor with { BackendType = backendType, BackendInstanceId = instanceId, PrincipalId = principalId };
        var pending = _pending.GetOrAdd(key, _ => new PendingResolution(item => PersistAsync(item)));
        var work = pending.Work.Value;
        var existing = await pending.Existing.Task.WaitAsync(cancellationToken);
        if (existing == null) return await work.WaitAsync(cancellationToken);
        try
        {
            return await work.WaitAsync(TimeSpan.FromMilliseconds(200), cancellationToken);
        }
        catch (TimeoutException)
        {
            // The write owns its context and continues independently of the request.
            return existing;
        }

        async Task<AllstarrPrincipal?> PersistAsync(PendingResolution item)
        {
            var token = lifetime?.ApplicationStopping ?? CancellationToken.None;
            try
            {
                while (true)
                {
                    try
                    {
                        return await ResolveAndSaveAsync(key, item, token);
                    }
                    catch (Exception error) when (DbErrors.IsTransientConflict(error) &&
                        item.Existing.Task.IsCompletedSuccessfully && item.Existing.Task.Result != null && !token.IsCancellationRequested)
                    {
                        logger?.LogDebug("Identity refresh deferred while storage is busy");
                        await Task.Delay(250, token);
                    }
                }
            }
            catch (Exception error)
            {
                if (item.Existing.TrySetException(error)) return null;
                if (await item.Existing.Task is not { } known) throw;
                logger?.LogWarning("Identity refresh could not be persisted ({ErrorType})", error.GetType().Name);
                return known;
            }
            finally
            {
                _pending.TryRemove(new KeyValuePair<BackendIdentityDescriptor, PendingResolution>(key, item));
            }
        }
    }

    private async Task<AllstarrPrincipal?> ResolveAndSaveAsync(BackendIdentityDescriptor descriptor,
        PendingResolution pending, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await db.Database.OpenConnectionAsync(cancellationToken);
        var users = db.Users.Where(item => item.BackendType == descriptor.BackendType &&
            item.BackendInstanceId == descriptor.BackendInstanceId && item.BackendPrincipalId == descriptor.PrincipalId);
        var user = await users.SingleOrDefaultAsync(cancellationToken);
        if (user == null)
        {
            user = new UserRecord
            {
                Id = Guid.CreateVersion7(),
                BackendType = descriptor.BackendType,
                BackendInstanceId = descriptor.BackendInstanceId!,
                BackendPrincipalId = descriptor.PrincipalId,
                DisplayName = string.IsNullOrWhiteSpace(descriptor.DisplayName) ? descriptor.PrincipalId : descriptor.DisplayName.Trim(),
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
                pending.Existing.TrySetResult(null);
                return ToPrincipal(user);
            }
            catch (DbUpdateException exception) when (DbErrors.IsUniqueViolation(exception))
            {
                db.ChangeTracker.Clear();
                user = await users.SingleAsync(cancellationToken);
            }
        }

        if (!user.Enabled) throw new UnauthorizedAccessException("The Allstarr user is disabled.");
        pending.Existing.TrySetResult(ToPrincipal(user) with
        {
            DisplayName = string.IsNullOrWhiteSpace(descriptor.DisplayName) ? user.DisplayName : descriptor.DisplayName.Trim(),
            IsAdministrator = descriptor.IsAdministrator ?? user.IsAdmin
        });
        db.Database.SetCommandTimeout(1);
        await db.Database.ExecuteSqlRawAsync("PRAGMA busy_timeout=100;", cancellationToken);
        if (!string.IsNullOrWhiteSpace(descriptor.DisplayName)) user.DisplayName = descriptor.DisplayName.Trim();
        if (descriptor.IsAdministrator is { } isAdministrator) user.IsAdmin = isAdministrator;
        user.LastSeenAt = now;
        user.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return ToPrincipal(user);
    }

    private static AllstarrPrincipal ToPrincipal(UserRecord user) => new(
        user.Id, user.BackendType, user.BackendInstanceId, user.BackendPrincipalId,
        user.DisplayName, user.IsAdmin, user.IsSynthetic);
}
