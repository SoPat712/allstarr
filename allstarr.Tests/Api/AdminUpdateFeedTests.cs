using System.Text;
using System.Text.Json;
using allstarr.Controllers;
using allstarr.Core.Storage;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed class AdminUpdateFeedTests : IAsyncLifetime
{
    private SqliteTestDatabase database = null!;
    private TestFactory factory = null!;
    private Guid userId;
    private Guid otherUserId;
    private DateTimeOffset startedAt;

    public async Task InitializeAsync()
    {
        database = await SqliteTestDatabase.CreateAsync();
        factory = new TestFactory(database.Options);
        await using var context = await factory.CreateDbContextAsync();

        userId = Guid.CreateVersion7();
        otherUserId = Guid.CreateVersion7();
        startedAt = DateTimeOffset.UtcNow;
        context.Users.AddRange(
            User(userId, "Owner"),
            User(otherUserId, "Other"));

        var ownJob = Job(userId, "own", startedAt.AddSeconds(1));
        var otherJob = Job(otherUserId, "other", startedAt.AddSeconds(2));
        var householdJob = Job(null, "household", startedAt.AddSeconds(3));
        context.Jobs.AddRange(ownJob, otherJob, householdJob);
        context.AuditEvents.AddRange(
            Audit(userId, "own-audit", "own", startedAt.AddSeconds(4), """{"secret":"never-stream"}"""),
            Audit(otherUserId, "other-audit", "other", startedAt.AddSeconds(5)),
            Audit(null, "job-audit", "own", startedAt.AddSeconds(6)),
            Audit(null, "household-audit", "household", startedAt.AddSeconds(7)));
        await context.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await database.DisposeAsync();

    [Fact]
    public async Task ReadAsync_FiltersUserAndNeverProjectsRawPayloads()
    {
        var events = await Feed().ReadAsync(
            new AdminUpdateScope(userId, false),
            BeforeSeed(),
            100,
            CancellationToken.None);

        Assert.Contains(events, item => item.Resource == "job" && item.CorrelationId == "own");
        Assert.Contains(events, item => item.Resource == "audit" && item.Action == "own-audit");
        Assert.DoesNotContain(events, item => item.Action == "job-audit");
        Assert.DoesNotContain(events, item => item.CorrelationId is "other" or "household");
        Assert.DoesNotContain(events, item => item.Resource == "outbox");
        var json = JsonSerializer.Serialize(events);
        Assert.DoesNotContain("never-stream", json, StringComparison.Ordinal);
        Assert.DoesNotContain("PayloadJson", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DetailsJson", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadAsync_AdminSeesPersonalAndHouseholdJobsAndAuditsWithoutOutboxEvents()
    {
        var events = await Feed().ReadAsync(
            new AdminUpdateScope(userId, true),
            BeforeSeed(),
            100,
            CancellationToken.None);

        Assert.Contains(events, item => item.Resource == "job" && item.CorrelationId == "own");
        Assert.Contains(events, item => item.CorrelationId == "other");
        Assert.Contains(events, item => item.Resource == "audit" && item.Action == "own-audit");
        Assert.Contains(events, item => item.Resource == "audit" && item.Action == "other-audit");
        Assert.DoesNotContain(events, item => item.Resource == "outbox");
        Assert.Contains(events, item => item.CorrelationId == "household");
        var json = JsonSerializer.Serialize(events);
        Assert.Contains(events, item => item.Resource == "audit" && item.Action == "household-audit");
        Assert.DoesNotContain("never-stream", json, StringComparison.Ordinal);
        Assert.DoesNotContain("PayloadJson", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DetailsJson", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadAsync_LastEventCursorDeduplicatesAndKeepsLaterEvents()
    {
        var feed = Feed();
        var first = await feed.ReadAsync(
            new AdminUpdateScope(userId, true),
            BeforeSeed(),
            2,
            CancellationToken.None);
        Assert.Equal(2, first.Count);
        Assert.True(AdminUpdateCursor.TryParse(first[^1].EventId, out var cursor));

        var remaining = await feed.ReadAsync(
            new AdminUpdateScope(userId, true),
            cursor,
            100,
            CancellationToken.None);

        Assert.DoesNotContain(remaining, item => first.Any(previous => previous.EventId == item.EventId));
        Assert.NotEmpty(remaining);
    }

    [Fact]
    public async Task ReadAsync_SameTimestampPublishesHigherRevisionOnce()
    {
        var feed = Feed();
        var initial = await feed.ReadAsync(
            new AdminUpdateScope(userId, false),
            BeforeSeed(),
            100,
            CancellationToken.None);
        var jobEvent = Assert.Single(initial, item => item.Resource == "job");
        Assert.True(AdminUpdateCursor.TryParse(jobEvent.EventId, out var cursor));

        await using (var context = await factory.CreateDbContextAsync())
        {
            var job = await context.Jobs.SingleAsync(item => item.Id == jobEvent.ResourceId);
            job.Revision++;
            await context.SaveChangesAsync();
        }

        var updates = await feed.ReadAsync(
            new AdminUpdateScope(userId, false),
            cursor,
            100,
            CancellationToken.None);

        var revised = Assert.Single(
            updates,
            item => item.ResourceId == jobEvent.ResourceId && item.Revision == jobEvent.Revision + 1);
        Assert.NotEqual(jobEvent.EventId, revised.EventId);
    }

    [Fact]
    public async Task Stream_WritesStatusAndRecoverableSafeUpdates()
    {
        var sessions = new AdminAuthSessionService(new MemoryAdminAuthSessionStore(),
            new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AdminAuthSessionService>.Instance,
            contextFactory: factory, identityOptions: new() { BackendInstanceId = "fixture" });
        var session = await sessions.CreateSessionAsync(userId.ToString("N"), "Owner", false, "never-stream-session-token", null,
            allstarrUserId: userId);
        var controller = new AdminUpdatesController(Feed(), sessions);
        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = new MemoryStream();
        httpContext.Request.Headers["Last-Event-ID"] = BeforeSeed().ToString();
        httpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = session;
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        await controller.Stream(cancellation.Token);

        httpContext.Response.Body.Position = 0;
        var body = await new StreamReader(httpContext.Response.Body, Encoding.UTF8).ReadToEndAsync();
        Assert.Contains("event: stream-status", body, StringComparison.Ordinal);
        Assert.Contains("\"recovered\":true", body, StringComparison.Ordinal);
        Assert.Contains("event: update", body, StringComparison.Ordinal);
        Assert.Contains("id: ", body, StringComparison.Ordinal);
        Assert.DoesNotContain("never-stream", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CorrelationCollision_DoesNotGrantAnotherActorsAudit()
    {
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.AuditEvents.Add(Audit(otherUserId, "private-other", "own", startedAt.AddSeconds(8)));
            await db.SaveChangesAsync();
        }
        var events = await Feed().ReadAsync(new(userId, false), BeforeSeed(), 100, default);
        Assert.DoesNotContain(events, item => item.Action == "private-other");
        Assert.Empty(await Feed().ReadAsync(new(null, false), BeforeSeed(), 100, default));
    }

    [Fact]
    public async Task Stream_StopsAfterSessionRevocation()
    {
        await using var auth = await AdminAuthSessionTestSupport.CreateLinkedAsync();
        var sessions = auth.Service;
        var session = await auth.CreateSessionAsync("owner", "Owner", false, "fixture", null);
        var http = new DefaultHttpContext();
        http.Response.Body = new MemoryStream();
        http.Items[AdminAuthSessionService.HttpContextSessionItemKey] = session;
        var controller = new AdminUpdatesController(Feed(), sessions)
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var stream = controller.Stream(timeout.Token);
        await sessions.RemoveSessionAsync(session.SessionId);
        await stream.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(timeout.IsCancellationRequested);
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad")]
    [InlineData("0:99:00000000000000000000000000000000:0")]
    public void Cursor_RejectsMalformedValues(string value) =>
        Assert.False(AdminUpdateCursor.TryParse(value, out _));

    private AdminUpdateFeed Feed() => new(factory);

    private AdminUpdateCursor BeforeSeed() =>
        new(startedAt.AddMinutes(-1), 0, Guid.Empty, 0);

    private static UserRecord User(Guid id, string name) => new()
    {
        Id = id,
        DisplayName = name,
        Enabled = true,
        BackendType = "jellyfin",
        BackendInstanceId = "fixture",
        BackendPrincipalId = id.ToString("N"),
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private static DurableJobRecord Job(Guid? owner, string correlation, DateTimeOffset at) => new()
    {
        Id = Guid.CreateVersion7(),
        OwnerUserId = owner,
        ScopeKey = $"user:{owner:N}",
        RequestFingerprint = new string('a', 64),
        CorrelationId = correlation,
        Type = "test",
        IdempotencyKey = correlation,
        State = DurableJobState.Running,
        MaxAttempts = 3,
        MaxDeferrals = 3,
        AvailableAt = at,
        CreatedAt = at,
        UpdatedAt = at,
        Revision = 1
    };

    private static AuditEventRecord Audit(
        Guid? actor,
        string action,
        string correlation,
        DateTimeOffset at,
        string details = "{}") => new()
        {
            Id = Guid.CreateVersion7(),
            ActorUserId = actor,
            Category = "test",
            Action = action,
            Outcome = "ok",
            CorrelationId = correlation,
            DetailsJson = details,
            CreatedAt = at
        };

    private sealed class TestFactory(DbContextOptions<AllstarrDbContext> options)
        : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);

        public Task<AllstarrDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
