using allstarr.Core.Jobs;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using allstarr.Filters;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin/activity/tasks")]
[ServiceFilter(typeof(AdminPortFilter))]
public sealed class ActivityTasksController(IDbContextFactory<AllstarrDbContext> factory,
    BackgroundTaskActivity? backgroundTasks = null) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken = default, [FromQuery] string scope = "mine")
    {
        if (HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] is not AdminAuthSession session)
            return Unauthorized(new { error = "Authentication required" });
        if (session.AllstarrUserId == null) return Forbid();
        if (scope is not ("mine" or "everyone")) return BadRequest(new { error = "Unknown activity scope" });
        if (scope == "everyone" && !session.IsAdministrator) return Forbid();
        var everyone = session.IsAdministrator && scope == "everyone";
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var scheduled = await (from schedule in db.JobSchedules.AsNoTracking()
                               join link in db.PlaylistLinks.AsNoTracking() on schedule.Id equals link.ScheduleId
                               where schedule.JobType == DurableScheduleEngine.PlaylistSyncJobType &&
                                   link.OwnerUserId == schedule.OwnerUserId &&
                                   (everyone || schedule.OwnerUserId == session.AllstarrUserId)
                               orderby schedule.NextRunAt, schedule.Id
                               select new { Schedule = schedule, Link = link }).Take(501).ToArrayAsync(cancellationToken);
        var hasMore = scheduled.Length > 500;
        scheduled = scheduled.Take(500).ToArray();
        var ids = scheduled.Select(x => x.Link.Id).ToArray();
        var owners = scheduled.Select(x => x.Schedule.OwnerUserId).Distinct().ToArray();
        var names = await db.Users.AsNoTracking().Where(x => owners.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.DisplayName, cancellationToken);
        var titles = await db.PlaylistSourceSnapshots.AsNoTracking().Where(x => ids.Contains(x.PlaylistLinkId) && x.PublishedAt != null)
            .GroupBy(x => x.PlaylistLinkId).Select(group => group.OrderByDescending(x => x.SnapshotVersion).First())
            .ToDictionaryAsync(x => x.PlaylistLinkId, x => x.Name, cancellationToken);
        var runs = await db.PlaylistSyncRuns.AsNoTracking().Where(x => ids.Contains(x.PlaylistLinkId))
            .GroupBy(x => x.PlaylistLinkId).Select(group => group.OrderByDescending(x => x.StartedAt).ThenByDescending(x => x.Id).First())
            .ToDictionaryAsync(x => x.PlaylistLinkId, cancellationToken);
        var items = new List<object>();
        foreach (var entry in scheduled)
        {
            var schedule = entry.Schedule;
            var link = entry.Link;
            runs.TryGetValue(link.Id, out var run);
            var enabled = schedule.Enabled && link.Enabled && link.ImportMode == PlaylistImportMode.Linked;
            items.Add(new
            {
                id = schedule.Id.ToString("D"),
                kind = "playlist",
                name = "Sync " + (SafeOperationalText.Sanitize(titles.GetValueOrDefault(link.Id), 200) ?? "playlist"),
                ownerName = ActivityPresentation.UserName(names.GetValueOrDefault(link.OwnerUserId)),
                enabled,
                state = !enabled ? "Paused" : run != null && run.CompletedAt == null ? "Running" : "Scheduled",
                cadence = $"{schedule.CronExpression} · {schedule.TimeZoneId}",
                lastRunAt = run?.CompletedAt ?? run?.StartedAt,
                nextRunAt = enabled ? schedule.NextRunAt : null,
                runPlaylistLinkId = link.Enabled && link.ImportMode == PlaylistImportMode.Linked ? (Guid?)link.Id : null,
                detail = "Uses this playlist's saved update rules."
            });
        }
        if (everyone)
        {
            if (backgroundTasks != null)
                foreach (var task in await backgroundTasks.ReadAsync(cancellationToken))
                    items.Add(new
                    {
                        id = task.Id,
                        kind = "maintenance",
                        name = task.Name,
                        enabled = true,
                        state = task.State,
                        cadence = task.CadenceSeconds >= 3600 ? $"Every {task.CadenceSeconds / 3600:g} hours" : $"Every {task.CadenceSeconds / 60:g} minutes",
                        lastRunAt = task.LastRunAt,
                        nextRunAt = task.NextRunAt,
                        detail = "Runs automatically. The next check is approximate."
                    });
            var cleanup = db.Jobs.AsNoTracking().Where(x => x.Type == "recording.kept-cleanup");
            if (await cleanup.AnyAsync(cancellationToken))
                items.Add(new
                {
                    id = "kept-cleanup",
                    kind = "maintenance",
                    name = "Unused kept copy cleanup",
                    enabled = true,
                    state = "Automatic",
                    cadence = "After the 24-hour grace period",
                    lastRunAt = await cleanup.MaxAsync(x => x.CompletedAt, cancellationToken),
                    nextRunAt = await cleanup.Where(x => x.State == DurableJobState.Pending || x.State == DurableJobState.RetryScheduled)
                        .MinAsync(x => (DateTimeOffset?)x.AvailableAt, cancellationToken),
                    detail = "Only unreferenced, verified owned files are eligible. Individual cleanups appear in Queue."
                });
        }
        return Ok(new { items, hasMore });
    }
}
