using allstarr.Core.Matching;
using allstarr.Core.Playback;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Operations;

/// <summary>
/// Keeps the operator-facing event log useful without allowing high-volume
/// playback and matching activity to grow the operational database forever.
/// Referenced audit events are retained because they are part of another
/// durable record's evidence chain. Finished playback jobs expire on the same
/// schedule: every progress report is its own job, and listening history keeps
/// its own duplicate protection once the job is gone.
/// </summary>
public sealed class AuditEventRetentionService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<AuditEventRetentionService> logger, BackgroundTaskActivity? taskActivity = null) : BackgroundService
{
    private readonly TimeSpan retention = TimeSpan.FromDays(Math.Clamp(
        configuration.GetValue("Operations:EventLog:RetentionDays", 30), 1, 3650));
    private readonly TimeSpan interval = TimeSpan.FromMinutes(Math.Clamp(
        configuration.GetValue("Operations:EventLog:CleanupIntervalMinutes", 360), 5, 10080));
    private readonly int batchSize = Math.Clamp(
        configuration.GetValue("Operations:EventLog:CleanupBatchSize", 1000), 100, 10000);
    private readonly int maximumRows = Math.Clamp(
        configuration.GetValue("Operations:EventLog:MaximumRows", 250000), 1000, 5000000);
    // Job attempt history is high-volume; it expires sooner and is trimmed first so it
    // cannot crowd administrator and security events out of the shared row limit.
    // Per-file storage verification follows the same short-lived detail policy.
    private readonly TimeSpan jobHistoryRetention = TimeSpan.FromDays(Math.Clamp(
        configuration.GetValue("Operations:EventLog:JobHistoryRetentionDays", 7), 1, 3650));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        taskActivity?.Register("event-retention", "Activity retention", interval);
        await PruneNowAsync(stoppingToken);

        using var timer = new PeriodicTimer(interval, timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await PruneNowAsync(stoppingToken);
        }
    }

    public async Task PruneNowAsync(CancellationToken cancellationToken = default)
    {
        taskActivity?.Register("event-retention", "Activity retention", interval);
        taskActivity?.Started("event-retention");
        try
        {
            var now = timeProvider.GetUtcNow();
            var cutoff = now - retention;
            var deleted = await PruneJobHistoryAsync(
                now - (jobHistoryRetention < retention ? jobHistoryRetention : retention), cancellationToken);

            while (!cancellationToken.IsCancellationRequested)
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var database = scope.ServiceProvider.GetRequiredService<AllstarrDbContext>();
                var candidates = await Deletable(database)
                    .Where(item => item.CreatedAt < cutoff)
                    .OrderBy(item => item.CreatedAt)
                    .ThenBy(item => item.Id)
                    .Take(batchSize)
                    .ToListAsync(cancellationToken);

                if (candidates.Count == 0)
                {
                    break;
                }

                database.AuditEvents.RemoveRange(candidates);
                await database.SaveChangesAsync(cancellationToken);
                deleted += candidates.Count;

                if (candidates.Count < batchSize)
                {
                    break;
                }
            }

            await using (var scope = scopeFactory.CreateAsyncScope())
            {
                var database = scope.ServiceProvider.GetRequiredService<AllstarrDbContext>();
                var overflow = Math.Max(0, await database.AuditEvents.CountAsync(cancellationToken) - maximumRows);

                while (overflow > 0 && !cancellationToken.IsCancellationRequested)
                {
                    var take = Math.Min(batchSize, overflow);
                    var candidates = await Deletable(database)
                        .OrderBy(item => item.Category == JobHistoryCategory || item.Category == "managed-storage" ? 0 : 1)
                        .ThenBy(item => item.CreatedAt)
                        .ThenBy(item => item.Id)
                        .Take(take)
                        .ToListAsync(cancellationToken);

                    if (candidates.Count == 0)
                    {
                        break;
                    }

                    database.AuditEvents.RemoveRange(candidates);
                    await database.SaveChangesAsync(cancellationToken);
                    database.ChangeTracker.Clear();
                    deleted += candidates.Count;
                    overflow -= candidates.Count;
                }
            }

            if (deleted > 0)
            {
                logger.LogInformation(
                    "Pruned {Count} unreferenced event-log records older than {Cutoff} or beyond the {MaximumRows} row limit.",
                    deleted,
                    cutoff,
                    maximumRows);
            }

            var deletedJobs = await PrunePlaybackJobsAsync(cutoff, cancellationToken);
            if (deletedJobs > 0)
            {
                logger.LogInformation(
                    "Pruned {Count} finished playback jobs older than {Cutoff}.",
                    deletedJobs,
                    cutoff);
            }
            if (taskActivity != null) await taskActivity.CompletedAsync("event-retention", true, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Event-log retention failed; cleanup will retry at the next interval.");
            if (taskActivity != null) await taskActivity.CompletedAsync("event-retention", false, cancellationToken);
        }
    }

    private const string JobHistoryCategory = "job-attempt";

    private async Task<int> PruneJobHistoryAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        var deleted = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<AllstarrDbContext>();
            var candidates = await Deletable(database)
                .Where(item => (item.Category == JobHistoryCategory || item.Category == "managed-storage") && item.CreatedAt < cutoff)
                .OrderBy(item => item.CreatedAt)
                .ThenBy(item => item.Id)
                .Take(batchSize)
                .ToListAsync(cancellationToken);
            if (candidates.Count == 0) break;

            database.AuditEvents.RemoveRange(candidates);
            await database.SaveChangesAsync(cancellationToken);
            deleted += candidates.Count;
            if (candidates.Count < batchSize) break;
        }
        return deleted;
    }

    private async Task<int> PrunePlaybackJobsAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        var deleted = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<AllstarrDbContext>();
            var ids = await database.Jobs.AsNoTracking()
                .Where(item => item.Type == PlaybackSignalPipeline.JobType &&
                               (item.State == DurableJobState.Succeeded || item.State == DurableJobState.Cancelled) &&
                               item.CompletedAt < cutoff)
                .OrderBy(item => item.CompletedAt)
                .Select(item => item.Id)
                .Take(batchSize)
                .ToListAsync(cancellationToken);
            if (ids.Count == 0) break;

            deleted += await database.Jobs.Where(item => ids.Contains(item.Id)).ExecuteDeleteAsync(cancellationToken);
            if (ids.Count < batchSize) break;
        }
        return deleted;
    }

    private static IQueryable<AuditEventRecord> Deletable(AllstarrDbContext database) =>
        database.AuditEvents.Where(audit =>
            (audit.Category != CatalogIdentityMaintenance.Category || audit.Action != CatalogIdentityMaintenance.CompletionAction) &&
            !database.LegacyEnvImports.Any(importRecord => importRecord.AuditEventId == audit.Id));
}
