using System.Text.Json;
using allstarr.Core.Health;
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
        var titles = await db.PlaylistSourceSnapshots.AsNoTracking().Where(x => ids.Contains(x.PlaylistLinkId) && x.PublishedAt != null && db.PlaylistLinks.Any(link => link.Id == x.PlaylistLinkId && link.OwnerUserId == x.OwnerUserId))
            .GroupBy(x => new { x.OwnerUserId, x.PlaylistLinkId }).Select(group => group.OrderByDescending(x => x.SnapshotVersion).First())
            .ToDictionaryAsync(x => (x.OwnerUserId, x.PlaylistLinkId), x => x.Name, cancellationToken);
        var runs = await db.PlaylistSyncRuns.AsNoTracking().Where(x => ids.Contains(x.PlaylistLinkId) && db.PlaylistLinks.Any(link => link.Id == x.PlaylistLinkId && link.OwnerUserId == x.OwnerUserId))
            .GroupBy(x => new { x.OwnerUserId, x.PlaylistLinkId }).Select(group => group.OrderByDescending(x => x.StartedAt).ThenByDescending(x => x.Id).First())
            .ToDictionaryAsync(x => (x.OwnerUserId, x.PlaylistLinkId), cancellationToken);
        var items = new List<object>();
        foreach (var entry in scheduled)
        {
            var schedule = entry.Schedule;
            var link = entry.Link;
            runs.TryGetValue((link.OwnerUserId, link.Id), out var run);
            var enabled = schedule.Enabled && link.Enabled && link.ImportMode == PlaylistImportMode.Linked;
            items.Add(new
            {
                id = schedule.Id.ToString("D"),
                kind = "playlist",
                name = "Sync " + (SafeOperationalText.Sanitize(titles.GetValueOrDefault((link.OwnerUserId, link.Id)), 200) ?? "playlist"),
                ownerName = ActivityPresentation.UserName(names.GetValueOrDefault(link.OwnerUserId)),
                enabled,
                state = !enabled ? "Paused" : run != null && run.CompletedAt == null ? "Running" : "Scheduled",
                cadence = CronCadence(schedule.CronExpression, schedule.TimeZoneId),
                lastRunAt = run?.CompletedAt ?? run?.StartedAt,
                nextRunAt = enabled ? schedule.NextRunAt : null,
                runPlaylistLinkId = link.Enabled && link.ImportMode == PlaylistImportMode.Linked ? (Guid?)link.Id : null,
                detail = "Uses this playlist's saved update rules."
            });
        }
        if (session.IsAdministrator)
        {
            var serviceSchedules = await db.JobSchedules.AsNoTracking()
                .Where(item => item.JobType == HealthSmokeService.JobType && (everyone || item.OwnerUserId == session.AllstarrUserId) ||
                    everyone && item.JobType == DurableBackupService.JobType && item.Id == DurableBackupService.ScheduleId && item.OwnerUserId == null)
                .OrderBy(item => item.NextRunAt).ThenBy(item => item.Id).Take(10).ToArrayAsync(cancellationToken);
            var serviceTypes = serviceSchedules.Select(item => item.JobType).Distinct().ToArray();
            var serviceOwners = serviceSchedules.Select(item => item.OwnerUserId).Distinct().ToArray();
            var recent = serviceTypes.Length == 0 ? [] : await db.Jobs.AsNoTracking()
                .Where(item => serviceTypes.Contains(item.Type) && serviceOwners.Contains(item.OwnerUserId))
                .OrderByDescending(item => item.UpdatedAt).ThenByDescending(item => item.Id).Take(250).ToArrayAsync(cancellationToken);
            var serviceNames = await db.Users.AsNoTracking().Where(item => serviceOwners.Contains(item.Id))
                .ToDictionaryAsync(item => item.Id, item => item.DisplayName, cancellationToken);
            var latest = serviceSchedules.Select(schedule => new
            {
                Schedule = schedule,
                Job = recent.FirstOrDefault(job => job.OwnerUserId == schedule.OwnerUserId && job.Type == schedule.JobType &&
                    (schedule.JobType == DurableBackupService.JobType || BelongsToSchedule(job.PayloadJson, schedule.Id)))
            }).ToArray();
            var displays = await ActivityJobPresentation.ResolveAsync(db,
                latest.Where(item => item.Job != null).Select(item => item.Job!).ToArray(), cancellationToken, administrator: true);
            foreach (var entry in latest)
            {
                var schedule = entry.Schedule;
                var health = schedule.JobType == HealthSmokeService.JobType;
                var configuration = health ? HealthSmokeService.Parse<HealthSmokeSchedule>(schedule.PayloadTemplateJson) : null;
                var display = entry.Job == null ? null : displays.GetValueOrDefault(entry.Job.Id);
                var state = !schedule.Enabled ? "Paused" : entry.Job?.State switch
                {
                    DurableJobState.Running => "Running",
                    DurableJobState.Pending => "Waiting",
                    DurableJobState.RetryScheduled => "Retrying",
                    DurableJobState.Failed => "Needs attention",
                    _ => display?.ActivityState == "not_checked" ? "Not checked" : "Scheduled"
                };
                items.Add(new
                {
                    id = schedule.Id.ToString("D"),
                    kind = health ? "health" : "backup",
                    name = health ? "Health checks" : "Database and keyring backups",
                    ownerName = schedule.OwnerUserId.HasValue ? ActivityPresentation.UserName(serviceNames.GetValueOrDefault(schedule.OwnerUserId.Value)) : "Household",
                    enabled = schedule.Enabled,
                    state,
                    cadence = health && configuration is { IntervalMinutes: >= 1 and <= 1440 }
                        ? Interval(configuration.IntervalMinutes) : CronCadence(schedule.CronExpression, schedule.TimeZoneId),
                    lastRunAt = entry.Job?.CompletedAt ?? entry.Job?.StartedAt,
                    nextRunAt = schedule.Enabled ? schedule.NextRunAt : null,
                    detail = display?.Summary ?? "No recorded runs yet.",
                    actionHref = health ? "#/settings/health" : "#/settings/backups",
                    actionLabel = health ? "Open Health" : "Open Backups"
                });
            }
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
    private static bool BelongsToSchedule(string json, Guid scheduleId)
    {
        if (json.Length > 32768) return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("scheduleId", out var value) && value.ValueKind == JsonValueKind.String &&
                Guid.TryParse(value.GetString(), out var id) && id == scheduleId;
        }
        catch (JsonException) { return false; }
    }

    private static string Interval(int minutes) => minutes switch
    {
        60 => "Hourly",
        1440 => "Daily",
        _ when minutes % 60 == 0 => $"Every {minutes / 60} hours",
        _ => $"Every {minutes} minutes"
    };

    private static string CronCadence(string expression, string zone)
    {
        var fields = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var timeZone = SafeOperationalText.Sanitize(zone, 100) ?? "server time";
        if (fields is [var minute, var hour, "*", "*", "*"] &&
            int.TryParse(minute, out var m) && m is >= 0 and < 60 && int.TryParse(hour, out var h) && h is >= 0 and < 24)
            return $"Daily at {h:00}:{m:00} · {timeZone}";
        if (fields is ["0", "*", "*", "*", "*"]) return "Hourly";
        return $"Custom schedule · {timeZone}";
    }

}
