using System.Data;
using allstarr.Core.Jobs;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Music;

/// <summary>Owns the household schedule and manual admission for backend lyrics refreshes.</summary>
public sealed class LyricsRefreshSchedule(
    IDbContextFactory<AllstarrDbContext> contextFactory,
    DurableJobQueue jobs,
    IPlatformClock clock)
{
    public static readonly Guid ScheduleId = Guid.Parse("6ebd2c0a-4db9-4e6a-8d3d-0b20c2bc7442");

    private const string CronExpression = "0 8 * * *";
    private const string TimeZoneId = "UTC";

    public async Task EnsureAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.UtcNow;
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var schedule = await db.JobSchedules.SingleOrDefaultAsync(item => item.Id == ScheduleId, cancellationToken);
        var next = schedule?.NextRunAt;
        var changed = false;
        if (schedule == null)
        {
            schedule = new JobScheduleRecord
            {
                Id = ScheduleId,
                JobType = LyricsRefreshService.Type,
                OwnerUserId = null,
                CreatedAt = now,
                Revision = 1
            };
            db.JobSchedules.Add(schedule);
            changed = true;
        }
        else if (schedule.JobType != LyricsRefreshService.Type || schedule.OwnerUserId != null)
        {
            throw new InvalidOperationException("The lyrics refresh schedule is unavailable.");
        }

        if (!schedule.Enabled || schedule.CronExpression != CronExpression || schedule.TimeZoneId != TimeZoneId ||
            schedule.OverlapPolicy != ScheduleOverlapPolicy.Skip || schedule.MisfirePolicy != ScheduleMisfirePolicy.RunOnce ||
            schedule.RetryPolicyJson != "{}" || schedule.PayloadTemplateJson != "{}" || schedule.NextRunAt == null)
        {
            next = schedule.Enabled && schedule.CronExpression == CronExpression && schedule.TimeZoneId == TimeZoneId &&
                schedule.NextRunAt != null
                    ? schedule.NextRunAt
                    : DurableScheduleEngine.GetNextOccurrence(CronExpression, TimeZoneId, now)
                      ?? throw new InvalidOperationException("The lyrics refresh schedule has no future occurrence.");
            schedule.Enabled = true;
            schedule.CronExpression = CronExpression;
            schedule.TimeZoneId = TimeZoneId;
            schedule.OverlapPolicy = ScheduleOverlapPolicy.Skip;
            schedule.MisfirePolicy = ScheduleMisfirePolicy.RunOnce;
            schedule.RetryPolicyJson = "{}";
            schedule.PayloadTemplateJson = "{}";
            schedule.NextRunAt = next;
            schedule.UpdatedAt = now;
            if (!changed) schedule.Revision++;
            changed = true;
        }

        if (changed) await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public Task<DurableJobEnqueueResult> RunAsync(CancellationToken cancellationToken = default) =>
        jobs.EnqueueAsync(new DurableJobEnqueueRequest<LyricsRefreshRequest>(
            LyricsRefreshService.Type,
            $"manual:lyrics-refresh:{Guid.NewGuid():N}",
            new LyricsRefreshRequest(),
            OwnerUserId: null,
            MaxDeferrals: 10000,
            CorrelationId: "lyrics-refresh-manual"), cancellationToken);
}
