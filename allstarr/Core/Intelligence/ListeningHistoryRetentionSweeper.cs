using allstarr.Core.Operations;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Intelligence;

public sealed class ListeningHistoryRetentionSweeper(
    IDbContextFactory<AllstarrDbContext> factory,
    IPlatformClock clock)
{
    internal async Task SweepAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.UtcNow;
        await using var readDb = await factory.CreateDbContextAsync(cancellationToken);
        var policies = await readDb.IntelligencePolicies.AsNoTracking()
            .Select(item => new
            {
                item.TenantId,
                item.OwnerUserId,
                item.Protocol,
                item.BackendInstanceId,
                item.LibraryScopeId,
                item.RetentionDays
            }).ToListAsync(cancellationToken);

        // ponytail: keep one transaction per exact scope; batch only if sweep metrics make this material.
        foreach (var policy in policies)
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var history = db.ListeningEvents.Where(item =>
                item.TenantId == policy.TenantId && item.OwnerUserId == policy.OwnerUserId &&
                item.Protocol == policy.Protocol && item.BackendInstanceId == policy.BackendInstanceId &&
                item.LibraryScopeId == policy.LibraryScopeId);

            var abandonedBefore = now.AddHours(-8);
            await history.Where(item => item.State == ListeningEventState.Playing &&
                    (item.StartedAt ?? item.UpdatedAt) < abandonedBefore)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.State, ListeningEventState.Abandoned)
                    .SetProperty(item => item.Revision, item => item.Revision + 1), cancellationToken);

            var expiredBefore = now.AddDays(-Math.Clamp(policy.RetentionDays, 1, 3650));
            await db.ListeningSignals.Where(item =>
                    item.TenantId == policy.TenantId && item.OwnerUserId == policy.OwnerUserId &&
                    item.Protocol == policy.Protocol && item.BackendInstanceId == policy.BackendInstanceId &&
                    item.LibraryScopeId == policy.LibraryScopeId && item.ExpiresAt <= now)
                .ExecuteDeleteAsync(cancellationToken);
            await db.ListeningProfiles.Where(item =>
                    item.TenantId == policy.TenantId && item.OwnerUserId == policy.OwnerUserId &&
                    item.Protocol == policy.Protocol && item.BackendInstanceId == policy.BackendInstanceId &&
                    item.LibraryScopeId == policy.LibraryScopeId && item.CreatedAt < expiredBefore)
                .ExecuteDeleteAsync(cancellationToken);
            var expired = history.Where(item =>
                (item.ListenedAt ?? item.StartedAt ?? item.UpdatedAt) < expiredBefore);
            var occurrenceKeys = expired.Select(item => item.OccurrenceKey);
            await db.PlaybackDeliveryCheckpoints.Where(item =>
                    item.TenantId == policy.TenantId && item.OwnerUserId == policy.OwnerUserId &&
                    item.OccurrenceKey != null && occurrenceKeys.Contains(item.OccurrenceKey))
                .ExecuteDeleteAsync(cancellationToken);
            await expired.ExecuteDeleteAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
    }
}
