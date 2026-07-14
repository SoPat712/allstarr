using System.Data;
using System.Text.Json;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using Cronos;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Jobs;

public sealed record PlaylistSyncScheduledPayload(
    Guid ScheduleId,
    Guid PlaylistLinkId,
    DateTimeOffset ScheduledFor,
    long ScheduleRevision,
    string RetryPolicyReference,
    string CancellationPolicyReference);

public sealed record DurableScheduleTickResult(int Claimed, int Enqueued, int SkippedOverlap, int SkippedMisfire);

public sealed class DurableScheduleEngine
{
    public const string PlaylistSyncJobType = "playlist.materialize";
    private readonly IDbContextFactory<AllstarrDbContext> _contextFactory;
    private readonly DurableJobQueue _queue;
    private readonly IPlatformClock _clock;

    public DurableScheduleEngine(
        IDbContextFactory<AllstarrDbContext> contextFactory,
        DurableJobQueue queue,
        IPlatformClock clock)
    {
        _contextFactory = contextFactory;
        _queue = queue;
        _clock = clock;
    }

    public static DateTimeOffset? GetNextOccurrence(string expression, string timeZoneId, DateTimeOffset after)
    {
        var cron = ParseCron(expression);
        var zone = ResolveTimeZone(timeZoneId);
        return cron.GetNextOccurrence(after.UtcDateTime, zone, inclusive: false);
    }

    public static void Validate(string expression, string timeZoneId)
    {
        _ = ParseCron(expression);
        _ = ResolveTimeZone(timeZoneId);
    }

    public async Task<DurableScheduleTickResult> TickAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        await using var scan = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var dueIds = await scan.JobSchedules.AsNoTracking()
            .Where(item => item.Enabled && item.NextRunAt != null && item.NextRunAt <= now)
            .OrderBy(item => item.NextRunAt).ThenBy(item => item.Id)
            .Select(item => item.Id).ToListAsync(cancellationToken);

        var claimed = 0;
        var enqueued = 0;
        var overlap = 0;
        var misfire = 0;
        foreach (var id in dueIds)
        {
            var outcome = await ProcessDueAsync(id, now, cancellationToken);
            claimed += outcome.claimed;
            enqueued += outcome.enqueued;
            overlap += outcome.overlap;
            misfire += outcome.misfire;
        }
        return new DurableScheduleTickResult(claimed, enqueued, overlap, misfire);
    }

    private async Task<(int claimed, int enqueued, int overlap, int misfire)> ProcessDueAsync(
        Guid scheduleId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            var schedule = await context.JobSchedules.SingleOrDefaultAsync(item => item.Id == scheduleId, cancellationToken);
            if (schedule is not { Enabled: true, NextRunAt: not null } || schedule.NextRunAt > now)
            {
                await transaction.CommitAsync(cancellationToken);
                return default;
            }

            Validate(schedule.CronExpression, schedule.TimeZoneId);
            ValidateRetryPolicy(schedule.RetryPolicyJson);
            var dueAt = schedule.NextRunAt.Value;
            var following = GetNextOccurrence(schedule.CronExpression, schedule.TimeZoneId, dueAt)
                ?? throw new InvalidOperationException("The schedule has no future occurrence.");
            var isMisfire = following <= now;
            while (following <= now)
            {
                following = GetNextOccurrence(schedule.CronExpression, schedule.TimeZoneId, following)
                    ?? throw new InvalidOperationException("The schedule has no future occurrence.");
            }

            var link = await context.PlaylistLinks.SingleOrDefaultAsync(
                item => item.TenantId == schedule.TenantId && item.ScheduleId == schedule.Id,
                cancellationToken);
            if (link == null || schedule.JobType.Trim().ToLowerInvariant() != PlaylistSyncJobType)
            {
                throw new InvalidOperationException("Enabled playlist schedules must reference exactly one scoped playlist link.");
            }

            var shouldEnqueue = !(isMisfire && schedule.MisfirePolicy == ScheduleMisfirePolicy.Skip);
            var skippedOverlap = false;
            var idempotencyPrefix = $"schedule:{schedule.Id:N}:";
            if (shouldEnqueue && schedule.OverlapPolicy == ScheduleOverlapPolicy.Skip)
            {
                skippedOverlap = await context.Jobs.AnyAsync(item =>
                    item.TenantId == schedule.TenantId && item.OwnerUserId == schedule.OwnerUserId &&
                    item.Type == PlaylistSyncJobType && item.IdempotencyKey.StartsWith(idempotencyPrefix) &&
                    (item.State == DurableJobState.Pending || item.State == DurableJobState.RetryScheduled || item.State == DurableJobState.Running),
                    cancellationToken);
                shouldEnqueue = !skippedOverlap;
            }

            if (shouldEnqueue)
            {
                var retryReference = $"job-schedule:{schedule.Id:N}:revision:{schedule.Revision}";
                var cancellationReference = $"durable-job:schedule:{schedule.Id:N}";
                var request = new DurableJobEnqueueRequest<PlaylistSyncScheduledPayload>(
                    PlaylistSyncJobType,
                    $"{idempotencyPrefix}{dueAt.UtcTicks}",
                    new PlaylistSyncScheduledPayload(
                        schedule.Id,
                        link.Id,
                        dueAt,
                        schedule.Revision,
                        retryReference,
                        cancellationReference),
                    schedule.TenantId,
                    schedule.OwnerUserId,
                    ProviderAccountId: link.ProviderAccountId,
                    LibraryScopeId: schedule.LibraryScopeId,
                    Capability: "playlist",
                    CorrelationId: $"schedule-{schedule.Id:N}-{dueAt.UtcTicks}");
                await _queue.EnqueueInExistingTransactionAsync(context, request, cancellationToken);
            }

            schedule.NextRunAt = following;
            schedule.UpdatedAt = now;
            schedule.Revision++;
            try
            {
                await context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return (1, shouldEnqueue ? 1 : 0, skippedOverlap ? 1 : 0,
                    isMisfire && schedule.MisfirePolicy == ScheduleMisfirePolicy.Skip ? 1 : 0);
            }
            catch (DbUpdateConcurrencyException) when (attempt < 2)
            {
                await transaction.RollbackAsync(cancellationToken);
            }
        }
        return default;
    }

    private static CronExpression ParseCron(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression)) throw new ArgumentException("Cron expression is required.", nameof(expression));
        try { return CronExpression.Parse(expression.Trim(), CronFormat.Standard); }
        catch (CronFormatException exception) { throw new ArgumentException("Cron expression is invalid.", nameof(expression), exception); }
    }

    private static TimeZoneInfo ResolveTimeZone(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Time zone ID is required.", nameof(id));
        try { return TimeZoneInfo.FindSystemTimeZoneById(id.Trim()); }
        catch (TimeZoneNotFoundException exception) { throw new ArgumentException("Time zone ID is invalid.", nameof(id), exception); }
        catch (InvalidTimeZoneException exception) { throw new ArgumentException("Time zone data is invalid.", nameof(id), exception); }
    }

    private static void ValidateRetryPolicy(string json)
    {
        try { using var document = JsonDocument.Parse(json); if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException(); }
        catch (JsonException exception) { throw new InvalidOperationException("Retry policy must be a JSON object.", exception); }
    }
}

public sealed class DurableScheduleWorker(
    DurableScheduleEngine engine,
    DurableJobOptions options,
    ILogger<DurableScheduleWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Max(1000, options.PollIntervalMilliseconds)));
        do
        {
            try { await engine.TickAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError(exception, "Durable schedule tick failed."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
