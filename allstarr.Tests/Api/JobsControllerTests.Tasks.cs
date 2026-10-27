using System.Text.Json;
using allstarr.Controllers;
using allstarr.Core.Jobs;
using allstarr.Core.Storage;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed partial class JobsControllerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActivityTasks_SchedulesAreScopedAndRetainActualNextRun(bool administrator)
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            foreach (var owner in new[] { _userId, _otherUserId })
            {
                var schedule = Guid.CreateVersion7();
                var account = Guid.CreateVersion7();
                var now = DateTimeOffset.UtcNow;
                db.ProviderAccounts.Add(new() { Id = account, OwnerUserId = owner, ProviderId = "spotify", Enabled = true, DisplayName = "Source", CreatedAt = now, UpdatedAt = now });
                db.JobSchedules.Add(new()
                {
                    Id = schedule,
                    OwnerUserId = owner,
                    JobType = DurableScheduleEngine.PlaylistSyncJobType,
                    CronExpression = "0 2 * * *",
                    TimeZoneId = "UTC",
                    NextRunAt = now.AddDays(1),
                    Enabled = true,
                    PayloadTemplateJson = "{\"token\":\"fixture-task-private\"}",
                    CreatedAt = now,
                    UpdatedAt = now
                });
                db.PlaylistLinks.Add(new()
                {
                    Id = Guid.CreateVersion7(),
                    OwnerUserId = owner,
                    ProviderAccountId = account,
                    ScheduleId = schedule,
                    SourceProviderId = "spotify",
                    SourcePlaylistId = "fixture-playlist",
                    SourcePlaylistIdHash = new string('b', 64),
                    TargetProtocol = "jellyfin",
                    TargetBackendInstanceId = "fixture",
                    RuleVersion = "rules",
                    PolicyVersion = "policy",
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }
            await db.SaveChangesAsync();
        }
        var background = new allstarr.Core.Operations.BackgroundTaskActivity(_factory, TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<allstarr.Core.Operations.BackgroundTaskActivity>.Instance);
        background.Register("retention", "Retention", TimeSpan.FromHours(6));
        await background.CompletedAsync("retention", true, default);
        var controller = new ActivityTasksController(_factory, background) { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };
        controller.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = Session(_userId, administrator);
        using var json = Json(await controller.List());
        var tasks = json.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(1, tasks.Count(x => x.GetProperty("kind").GetString() == "playlist"));
        foreach (var task in tasks.Where(x => x.GetProperty("kind").GetString() == "playlist"))
        {
            Assert.True(task.GetProperty("nextRunAt").GetDateTimeOffset() > DateTimeOffset.UtcNow);
            Assert.Equal(JsonValueKind.String, task.GetProperty("runPlaylistLinkId").ValueKind);
        }
        Assert.Equal(0, tasks.Count(x => x.GetProperty("kind").GetString() == "maintenance"));
        if (administrator)
        {
            using var household = Json(await controller.List(scope: "everyone"));
            Assert.Equal(3, household.RootElement.GetProperty("items").GetArrayLength());
        }
        else Assert.IsType<ForbidResult>(await controller.List(scope: "everyone"));
        Assert.IsType<BadRequestObjectResult>(await controller.List(scope: "invalid"));
        Assert.DoesNotContain("fixture-task-private", json.RootElement.ToString());
    }

    [Fact]
    public async Task ActivityTasks_IncludesRealHealthAndBackupSchedulesWithoutExposingTheirPayloads()
    {
        var healthId = Guid.CreateVersion7();
        var backupId = Guid.Parse("93e9bb8f-f2af-4c60-bc7c-d622f20f7611");
        var next = new DateTimeOffset(2026, 10, 28, 2, 0, 0, TimeSpan.Zero);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.JobSchedules.AddRange(
                new()
                {
                    Id = healthId,
                    OwnerUserId = _userId,
                    JobType = "health.smoke",
                    Enabled = true,
                    CronExpression = "* * * * *",
                    TimeZoneId = "UTC",
                    NextRunAt = next,
                    PayloadTemplateJson = "{\"version\":2,\"intervalMinutes\":60,\"credentialReferenceId\":\"00000000-0000-0000-0000-000000000000\"}"
                },
                new()
                {
                    Id = backupId,
                    JobType = "storage.backup",
                    Enabled = true,
                    CronExpression = "0 2 * * *",
                    TimeZoneId = "UTC",
                    NextRunAt = next.AddHours(1),
                    PayloadTemplateJson = "{\"Version\":1,\"Root\":\"fixture-private-backup-path\"}"
                });
            var job = await db.Jobs.SingleAsync(item => item.Id == _ownJobId);
            job.Type = "health.smoke";
            job.PayloadJson = JsonSerializer.Serialize(new { scheduleId = healthId });
            job.State = DurableJobState.Running;
            job.StartedAt = next.AddHours(-1);
            job.UpdatedAt = next.AddHours(-1);
            await db.SaveChangesAsync();
        }
        var controller = new ActivityTasksController(_factory) { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };
        controller.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = Session(_userId, true);
        using var mine = Json(await controller.List());
        var own = Assert.Single(mine.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("health", own.GetProperty("kind").GetString());
        Assert.Equal(next, own.GetProperty("nextRunAt").GetDateTimeOffset());
        Assert.Equal("Running", own.GetProperty("state").GetString());
        Assert.Equal("Hourly", own.GetProperty("cadence").GetString());
        Assert.Equal("#/settings/health", own.GetProperty("actionHref").GetString());
        using var household = Json(await controller.List(scope: "everyone"));
        Assert.Equal(2, household.RootElement.GetProperty("items").GetArrayLength());
        var backup = Assert.Single(household.RootElement.GetProperty("items").EnumerateArray(), item => item.GetProperty("kind").GetString() == "backup");
        Assert.Equal("#/settings/backups", backup.GetProperty("actionHref").GetString());
        Assert.Equal(next.AddHours(1), backup.GetProperty("nextRunAt").GetDateTimeOffset());
        Assert.DoesNotContain("fixture-private-backup-path", household.RootElement.ToString());
        controller.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = Session(_userId);
        using var listener = Json(await controller.List());
        Assert.Empty(listener.RootElement.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task PlaylistTaskUsesOnlyItsOwnersSnapshotAndLastRun()
    {
        var linkId = Guid.CreateVersion7();
        var scheduleId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.JobSchedules.Add(new() { Id = scheduleId, OwnerUserId = _userId, JobType = DurableScheduleEngine.PlaylistSyncJobType, Enabled = true, CronExpression = "0 2 * * *", TimeZoneId = "UTC", NextRunAt = now.AddDays(1) });
            var link = Link(linkId, _userId); link.ScheduleId = scheduleId; db.PlaylistLinks.Add(link);
            var own = Snapshot(Guid.CreateVersion7(), linkId, _userId, "Owner mix");
            var foreign = Snapshot(Guid.CreateVersion7(), linkId, _otherUserId, "Foreign mix"); foreign.SnapshotVersion = 2;
            db.PlaylistSourceSnapshots.AddRange(own, foreign);
            db.PlaylistSyncRuns.Add(SyncRun(Guid.CreateVersion7(), linkId, own.Id, _userId, _ownJobId, now.AddHours(-1)));
            var wrong = SyncRun(Guid.CreateVersion7(), linkId, foreign.Id, _otherUserId, _otherJobId, now);
            wrong.CompletedAt = null; wrong.State = PlaylistSyncState.Running; db.PlaylistSyncRuns.Add(wrong);
            await db.SaveChangesAsync();
        }
        var controller = new ActivityTasksController(_factory) { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };
        controller.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = Session(_userId, true);
        foreach (var scope in new[] { "mine", "everyone" })
        {
            using var result = Json(await controller.List(scope: scope));
            var task = Assert.Single(result.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal("Sync Owner mix", task.GetProperty("name").GetString());
            Assert.Equal("Scheduled", task.GetProperty("state").GetString());
            Assert.Equal(now.AddHours(-1), task.GetProperty("lastRunAt").GetDateTimeOffset());
            Assert.DoesNotContain("Foreign mix", task.ToString());
        }
    }

    [Fact]
    public async Task ActivityTasks_RequiresLinkedAuthentication()
    {
        var controller = new ActivityTasksController(_factory) { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };
        Assert.IsType<UnauthorizedObjectResult>(await controller.List());
        var unlinked = new AdminAuthSession { SessionId = "unlinked", UserId = "unlinked", UserName = "Unlinked", IsAdministrator = false, JellyfinAccessToken = "protected", ExpiresAtUtc = DateTime.UtcNow.AddHours(1), LastSeenUtc = DateTime.UtcNow };
        controller.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = unlinked;
        Assert.IsType<ForbidResult>(await controller.List());
    }
}
