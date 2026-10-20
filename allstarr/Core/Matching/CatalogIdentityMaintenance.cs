using allstarr.Core.Capabilities;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Matching;

public sealed class CatalogIdentityMaintenance(
    IDbContextFactory<AllstarrDbContext> factory,
    IPlatformClock clock)
{
    internal const string Category = "catalog-identity-maintenance";
    internal const string CompletionAction = "duplicates.complete";

    public async Task<int> RepairBatchAsync(int batchSize = 100, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(batchSize, 100);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var active = db.ProviderTrackIdentities.Where(item =>
            item.ResourceKind == ProviderResourceKind.Track &&
            (item.Verification == ProviderIdentityVerification.Verified || item.Verification == ProviderIdentityVerification.Pinned) &&
            item.VerificationMethod != CatalogIdentityPolicy.SupersededVerificationMethod &&
            item.VerificationMethod != ManualTrackAuthorityPolicy.ReleasedProviderVerificationMethod &&
            item.VerificationMethod != ManualTrackAuthorityPolicy.ReplacedProviderVerificationMethod);
        var groups = await active.GroupBy(item => new
        {
            item.CanonicalRecordingId,
            item.ProviderId,
            item.CatalogNamespace,
            item.Scope,
            item.ProviderAccountId
        })
            .Where(group => group.Count() > 1 && group.Any(item => item.Verification == ProviderIdentityVerification.Verified))
            .Select(group => group.Key)
            .OrderBy(item => item.CanonicalRecordingId).ThenBy(item => item.ProviderId)
            .ThenBy(item => item.CatalogNamespace).ThenBy(item => item.Scope).ThenBy(item => item.ProviderAccountId)
            .Take(batchSize).ToArrayAsync(cancellationToken);
        var repaired = 0;
        foreach (var group in groups)
        {
            var members = active.Where(item => item.CanonicalRecordingId == group.CanonicalRecordingId &&
                item.ProviderId == group.ProviderId && item.CatalogNamespace == group.CatalogNamespace &&
                item.Scope == group.Scope && item.ProviderAccountId == group.ProviderAccountId);
            var retained = await members.OrderByDescending(item => item.VerificationMethod != "automatic-suggestion")
                .ThenByDescending(item => item.Verification == ProviderIdentityVerification.Pinned)
                .ThenByDescending(item => item.DecisionVersion).ThenByDescending(item => item.VerifiedAt)
                .ThenBy(item => item.Id).FirstAsync(cancellationToken);
            var duplicates = await members.Where(item => item.Id != retained.Id &&
                    item.Verification == ProviderIdentityVerification.Verified)
                .OrderBy(item => item.Id).Take(batchSize - repaired).ToArrayAsync(cancellationToken);
            foreach (var duplicate in duplicates) CatalogIdentityPolicy.Supersede(duplicate, clock.UtcNow);
            repaired += duplicates.Length;
            if (repaired == batchSize) break;
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return repaired;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await using (var db = await factory.CreateDbContextAsync(cancellationToken))
        {
            if (await db.AuditEvents.AnyAsync(item => item.Category == Category && item.Action == CompletionAction,
                    cancellationToken)) return;
        }
        while (await RepairBatchAsync(cancellationToken: cancellationToken) > 0)
            await Task.Yield();
        await using var completed = await factory.CreateDbContextAsync(cancellationToken);
        completed.AuditEvents.Add(new AuditEventRecord
        {
            Id = Guid.CreateVersion7(),
            Category = Category,
            Action = CompletionAction,
            Outcome = "completed",
            CorrelationId = Category,
            CreatedAt = clock.UtcNow
        });
        await completed.SaveChangesAsync(cancellationToken);
    }
}

public sealed class CatalogIdentityMaintenanceService(
    CatalogIdentityMaintenance maintenance,
    DurableStorageState storage,
    ILogger<CatalogIdentityMaintenanceService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (storage.GetSnapshot().Readiness != DurableStorageReadiness.Ready)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                continue;
            }
            try
            {
                await maintenance.RunAsync(stoppingToken);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning("Catalog identity cleanup will retry ({FailureKind})", exception.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }
}
