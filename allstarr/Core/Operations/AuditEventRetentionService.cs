using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Operations;

/// <summary>
/// Keeps the operator-facing event log useful without allowing high-volume
/// playback and matching activity to grow the operational database forever.
/// Referenced audit events are retained because they are part of another
/// durable record's evidence chain.
/// </summary>
public sealed class AuditEventRetentionService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<AuditEventRetentionService> logger) : BackgroundService
{
    private readonly TimeSpan retention = TimeSpan.FromDays(Math.Clamp(
        configuration.GetValue("Operations:EventLog:RetentionDays", 30), 1, 3650));
    private readonly TimeSpan interval = TimeSpan.FromMinutes(Math.Clamp(
        configuration.GetValue("Operations:EventLog:CleanupIntervalMinutes", 360), 5, 10080));
    private readonly int batchSize = Math.Clamp(
        configuration.GetValue("Operations:EventLog:CleanupBatchSize", 1000), 100, 10000);
    private readonly int maximumRows = Math.Clamp(
        configuration.GetValue("Operations:EventLog:MaximumRows", 250000), 1000, 5000000);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await PruneNowAsync(stoppingToken);

        using var timer = new PeriodicTimer(interval, timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await PruneNowAsync(stoppingToken);
        }
    }

    public async Task PruneNowAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var deleted = 0;
            var cutoff = timeProvider.GetUtcNow() - retention;

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
                        .OrderBy(item => item.CreatedAt)
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
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Event-log retention failed; cleanup will retry at the next interval.");
        }
    }

    private static IQueryable<AuditEventRecord> Deletable(AllstarrDbContext database) =>
        database.AuditEvents.Where(audit =>
            !database.LegacyEnvImports.Any(importRecord => importRecord.AuditEventId == audit.Id));
}
