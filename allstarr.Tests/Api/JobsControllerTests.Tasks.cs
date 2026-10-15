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
        Assert.Equal(administrator ? 2 : 1, tasks.Count(x => x.GetProperty("kind").GetString() == "playlist"));
        foreach (var task in tasks.Where(x => x.GetProperty("kind").GetString() == "playlist"))
        {
            Assert.True(task.GetProperty("nextRunAt").GetDateTimeOffset() > DateTimeOffset.UtcNow);
            Assert.Equal(JsonValueKind.String, task.GetProperty("runPlaylistLinkId").ValueKind);
        }
        Assert.Equal(administrator ? 1 : 0, tasks.Count(x => x.GetProperty("kind").GetString() == "maintenance"));
        Assert.DoesNotContain("fixture-task-private", json.RootElement.ToString());
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
