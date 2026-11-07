using System.Text.Json;
using allstarr.Controllers;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Jobs;
using allstarr.Core.Matching;
using allstarr.Core.Operations;
using allstarr.Core.Playlists;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Services.Admin;
using allstarr.Models.Settings;
using allstarr.Services.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace allstarr.Tests;

public sealed partial class AdminUiActivityTests
{
    [Fact]
    public async Task HomeActivity_PagesPastRoutineFloodsAndCollapsesHealthWithoutChangingHistory()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new Factory(database.Options);
        var owner = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        Guid visibleId;
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Users.Add(new() { Id = owner, DisplayName = "Listener", BackendType = "jellyfin", BackendInstanceId = "fixture", BackendPrincipalId = owner.ToString("N") });
            for (var index = 0; index < 600; index++)
                db.AuditEvents.Add(new()
                {
                    Id = Guid.CreateVersion7(),
                    ActorUserId = owner,
                    Category = "history",
                    Action = "history-resolution",
                    Outcome = "running",
                    CreatedAt = now
                });
            for (var index = 0; index < 12; index++)
                db.AuditEvents.Add(new()
                {
                    Id = Guid.CreateVersion7(),
                    ActorUserId = owner,
                    Category = "health-check",
                    Action = "completed",
                    Outcome = "success",
                    CreatedAt = now.AddMinutes(-index - 1),
                    DetailsJson = JsonSerializer.Serialize(new { message = index == 11 ? "1 failed" : "4 passed, 3 not checked" })
                });
            var visible = HistoryJob(owner, "favorite.process", DurableJobState.Succeeded, "succeeded", now.AddHours(-1));
            visibleId = visible.Id;
            db.Jobs.Add(visible);
            await db.SaveChangesAsync();
        }
        var matches = new TrackMatchCommandService(factory, new TrackMatchDecisionEngine(), new ProviderAccountResolver(factory), new SystemPlatformClock(), Mock.Of<IBackendLibraryAccessResolver>());
        var controller = Controller(factory, matches, owner);
        var home = Assert.IsType<AdminUiActivityPage>(await controller.ReadHomeActivityAsync());
        Assert.Equal(3, home.Items.Length);
        Assert.Contains(home.Items, item => item.Id == visibleId.ToString("N"));
        Assert.Equal(2, home.Items.Count(item => item.Label == "Health check"));
        Assert.Equal(now.AddMinutes(-1), home.Items.First().OccurredAt);
        Assert.All(home.Items, item => Assert.False(item.IsRoutine));

        await using var services = new ServiceCollection().AddSingleton<Microsoft.EntityFrameworkCore.IDbContextFactory<AllstarrDbContext>>(factory)
            .AddSingleton(new DurablePlaylistProjectionReader(factory, null!)).BuildServiceProvider();
        var configuration = new ConfigurationBuilder().Build();
        var status = new ProviderStatusManager(configuration, null!, NullLogger<ProviderStatusManager>.Instance,
            Options.Create(new SpotifyApiSettings()), Options.Create(new AppleDownloadSettings()),
            Options.Create(new DeezerSettings()), Options.Create(new QobuzSettings()));
        var overview = new AdminUiController(configuration,
            Options.Create(new SpotifyApiSettings()), Options.Create(new DeezerSettings()), Options.Create(new QobuzSettings()),
            Options.Create(new AppleDownloadSettings()), Options.Create(new MusicBrainzSettings()), null!, status,
            new ProviderAccountOptions(), matches)
        { ControllerContext = new() { HttpContext = new DefaultHttpContext { RequestServices = services } } };
        overview.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = new AdminAuthSession
        {
            SessionId = "fixture",
            UserId = owner.ToString("N"),
            UserName = "Listener",
            AllstarrUserId = owner,
            IsAdministrator = false,
            JellyfinAccessToken = "fixture",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        };
        using var response = JsonDocument.Parse(JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(await overview.GetHome()).Value));
        Assert.Equal(3, response.RootElement.GetProperty("activity").GetProperty("items").GetArrayLength());

        var history = new List<AdminUiActivityItem>();
        AdminUiActivityPage page;
        DateTimeOffset? before = null;
        Guid? beforeId = null;
        do
        {
            page = Assert.IsType<AdminUiActivityPage>(Assert.IsType<OkObjectResult>(
                await controller.GetDashboardActivity(100, before, beforeId)).Value);
            history.AddRange(page.Items);
            before = page.NextCursor;
            beforeId = page.NextCursorId == null ? null : Guid.Parse(page.NextCursorId);
        } while (page.HasMore && page.Items.Length > 0);
        Assert.Equal(613, history.Count);
        Assert.Equal(600, history.Count(item => item.IsRoutine));
        Assert.Equal(12, history.Count(item => item.Label == "Health check"));
    }

    [Fact]
    public async Task HomeActivity_CurrentProgressSnapshotsCannotHideAnOlderNotableAudit()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new Factory(database.Options);
        var owner = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        var notableId = Guid.CreateVersion7();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Users.Add(new() { Id = owner, DisplayName = "Listener", BackendType = "jellyfin", BackendInstanceId = "fixture", BackendPrincipalId = owner.ToString("N") });
            for (var index = 0; index < 600; index++)
            {
                var job = HistoryJob(owner, "listening-history.resolve", DurableJobState.Succeeded, "succeeded", now);
                db.Jobs.Add(job);
                db.AuditEvents.Add(new()
                {
                    Id = Guid.CreateVersion7(),
                    ActorUserId = owner,
                    Category = "job-progress",
                    Action = "history-resolution-current",
                    Outcome = "running",
                    CreatedAt = now.AddSeconds(-1),
                    SubjectType = "job",
                    SubjectId = job.Id.ToString("D")
                });
            }
            db.AuditEvents.Add(new()
            {
                Id = notableId,
                ActorUserId = owner,
                Category = "playlist",
                Action = "refreshed",
                Outcome = "success",
                CreatedAt = now.AddHours(-1)
            });
            await db.SaveChangesAsync();
        }
        var matches = new TrackMatchCommandService(factory, new TrackMatchDecisionEngine(), new ProviderAccountResolver(factory), new SystemPlatformClock(), Mock.Of<IBackendLibraryAccessResolver>());
        var home = Assert.IsType<AdminUiActivityPage>(await Controller(factory, matches, owner).ReadHomeActivityAsync());
        Assert.Equal(notableId.ToString("N"), Assert.Single(home.Items).Id);
    }

    [Fact]
    public async Task HomeActivity_ReturnsHeadroomAfterFilteringAndKeepsProblems()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new Factory(database.Options);
        var owner = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Users.Add(new() { Id = owner, DisplayName = "Listener", BackendType = "jellyfin", BackendInstanceId = "fixture", BackendPrincipalId = owner.ToString("N") });
            for (var index = 0; index < 30; index++)
                db.Jobs.Add(HistoryJob(owner, index == 0 ? "listening-history.resolve" : "favorite.process",
                    index == 0 ? DurableJobState.Failed : DurableJobState.Succeeded,
                    index == 0 ? "failed" : "succeeded", now.AddMinutes(-index)));
            await db.SaveChangesAsync();
        }
        var matches = new TrackMatchCommandService(factory, new TrackMatchDecisionEngine(), new ProviderAccountResolver(factory), new SystemPlatformClock(), Mock.Of<IBackendLibraryAccessResolver>());
        var home = Assert.IsType<AdminUiActivityPage>(await Controller(factory, matches, owner).ReadHomeActivityAsync());
        Assert.Equal(24, home.Items.Length);
        Assert.Equal("failed", home.Items[0].State);
        Assert.True(home.HasMore);
    }

    [Fact]
    public async Task Activity_HistoryResolutionUsesOneUpdatingRowAndRetainsEarlierEvents()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new Factory(database.Options);
        var owner = Guid.CreateVersion7();
        Guid jobId;
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Users.Add(new() { Id = owner, DisplayName = "Listener", BackendType = "jellyfin", BackendInstanceId = "fixture", BackendPrincipalId = owner.ToString("N") });
            var job = HistoryJob(owner, "listening-history.resolve", DurableJobState.RetryScheduled, "deferred", DateTimeOffset.UtcNow);
            job.PayloadJson = JsonSerializer.Serialize(new { Cursor = 20, Total = 100 });
            jobId = job.Id;
            db.Jobs.Add(job);
            foreach (var action in new[] { "history-resolution", "history-resolution-current" })
                db.AuditEvents.Add(new()
                {
                    Id = Guid.CreateVersion7(),
                    ActorUserId = owner,
                    Category = "job-progress",
                    Action = action,
                    Outcome = "running",
                    SubjectType = "job",
                    SubjectId = job.Id.ToString("D"),
                    CreatedAt = job.UpdatedAt,
                    DetailsJson = "{}"
                });
            await db.SaveChangesAsync();
        }
        var matches = new TrackMatchCommandService(factory, new TrackMatchDecisionEngine(), new ProviderAccountResolver(factory), new SystemPlatformClock(), Mock.Of<IBackendLibraryAccessResolver>());
        var controller = Controller(factory, matches, owner);
        var page = Assert.IsType<AdminUiActivityPage>(Assert.IsType<OkObjectResult>(await controller.GetDashboardActivity()).Value);
        Assert.Equal(2, page.Items.Length);
        var row = Assert.Single(page.Items, item => item.Id == jobId.ToString("N"));
        Assert.Equal("History resolution", row.Label);
        Assert.Equal("20 of 100 songs checked; 80 remaining.", row.Detail);
        Assert.True(row.IsRoutine);
        Assert.Contains(page.Items, item => item.Action == "history-resolution");
        await using (var db = await factory.CreateDbContextAsync())
        {
            var job = await db.Jobs.FindAsync(jobId);
            job!.PayloadJson = JsonSerializer.Serialize(new { Cursor = 40, Total = 100 });
            await db.SaveChangesAsync();
        }
        page = Assert.IsType<AdminUiActivityPage>(Assert.IsType<OkObjectResult>(await controller.GetDashboardActivity()).Value);
        Assert.Equal("40 of 100 songs checked; 60 remaining.", Assert.Single(page.Items, item => item.Id == jobId.ToString("N")).Detail);
    }

    [Theory]
    [InlineData("Background task", "succeeded", "info", true)]
    [InlineData("History Resolution", "running", "info", true)]
    [InlineData("Record a listen", "succeeded", "info", true)]
    [InlineData("Update playlist", "waiting", "info", true)]
    [InlineData("Background task", "deferred", "warning", false)]
    [InlineData("Background task", "RetryScheduled", "error", false)]
    [InlineData("Background task", "failed", "info", false)]
    [InlineData("Background task", "unhealthy", "info", false)]
    [InlineData("Health check", "passed", "info", false)]
    public void HomeActivity_RoutineFlagPreservesProblems(string label, string state, string severity, bool expected)
    {
        var item = new AdminUiActivityItem("one", "job", "System", label, state, "Result", DateTimeOffset.UtcNow, Severity: severity);
        Assert.Equal(expected, item.IsRoutine);
    }
}
