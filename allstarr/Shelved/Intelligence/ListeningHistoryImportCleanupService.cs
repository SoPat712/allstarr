using allstarr.Core.Jobs;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Intelligence;

public sealed class ListeningHistoryImportCleanupService(
    IDbContextFactory<AllstarrDbContext> factory,
    ListeningHistoryImportArtifactStore artifacts,
    DurableJobQueue jobs,
    ListeningHistoryRetentionSweeper historyRetention,
    DurableStorageState storageState,
    IPlatformClock clock,
    ILogger<ListeningHistoryImportCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (storageState.GetSnapshot().Readiness == DurableStorageReadiness.Ready)
                    await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    "Listening-history cleanup failed ({ExceptionType})",
                    exception.GetType().Name);
            }
            if (!await timer.WaitForNextTickAsync(stoppingToken)) return;
        }
    }

    internal async Task SweepAsync(CancellationToken cancellationToken)
    {
        await historyRetention.SweepAsync(cancellationToken);
        var now = clock.UtcNow;
        await using var readDb = await factory.CreateDbContextAsync(cancellationToken);
        var expired = await readDb.ListeningHistoryImports.AsNoTracking().Where(item =>
                item.ExpiresAt <= now && item.State != ListeningHistoryImportState.Completed &&
                item.State != ListeningHistoryImportState.Expired)
            .OrderBy(item => item.ExpiresAt).Take(100)
            .Select(item => new { item.Id, item.TenantId, item.JobId })
            .ToListAsync(cancellationToken);
        foreach (var item in expired.Where(item => item.JobId != null))
            await jobs.RequestCancellationAsync(item.JobId!.Value, item.TenantId, cancellationToken);
        if (expired.Count == 0) return;

        var ids = expired.Select(item => item.Id).ToArray();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var records = await db.ListeningHistoryImports.Where(item => ids.Contains(item.Id)).ToListAsync(cancellationToken);
        var jobIds = records.Select(item => item.JobId).OfType<Guid>().ToArray();
        var jobStates = jobIds.Length == 0
            ? []
            : await db.Jobs.AsNoTracking().Where(item => jobIds.Contains(item.Id))
                .ToDictionaryAsync(item => item.Id, item => item.State, cancellationToken);
        foreach (var record in records)
        {
            if (record.JobId is { } jobId && jobStates.TryGetValue(jobId, out var state) &&
                state is not (DurableJobState.Succeeded or DurableJobState.Failed or DurableJobState.Cancelled))
                continue;
            try
            {
                artifacts.Delete(record.Id);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(
                    "Listening-history artifact cleanup failed for import {ImportId} ({ExceptionType})",
                    record.Id,
                    exception.GetType().Name);
                continue;
            }
            record.State = ListeningHistoryImportState.Expired;
            record.CompletedAt ??= now;
            record.UpdatedAt = now;
            record.Revision++;
        }
        await db.SaveChangesAsync(cancellationToken);
    }
}
