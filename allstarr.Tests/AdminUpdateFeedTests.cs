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
    private PostgresTestDatabase database = null!;
    private TestFactory factory = null!;
    private Guid tenantId;
    private Guid otherTenantId;
    private Guid userId;
    private Guid otherUserId;
    private DateTimeOffset startedAt;

    public async Task InitializeAsync()
    {
        database = await PostgresTestDatabase.CreateAsync();
        factory = new TestFactory(database.Options);
        await using var context = await factory.CreateDbContextAsync();
        await context.Database.MigrateAsync();

        tenantId = Guid.CreateVersion7();
        otherTenantId = Guid.CreateVersion7();
        userId = Guid.CreateVersion7();
        otherUserId = Guid.CreateVersion7();
        startedAt = DateTimeOffset.UtcNow;
        context.Tenants.AddRange(
            new TenantRecord { Id = tenantId, Slug = "one", Name = "One", CreatedAt = startedAt },
            new TenantRecord { Id = otherTenantId, Slug = "two", Name = "Two", CreatedAt = startedAt });
        context.Users.AddRange(
            User(userId, tenantId, "Owner"),
            User(otherUserId, tenantId, "Other"),
            User(Guid.CreateVersion7(), otherTenantId, "Elsewhere"));

        var ownJob = Job(tenantId, userId, "own", startedAt.AddSeconds(1));
        var otherJob = Job(tenantId, otherUserId, "other", startedAt.AddSeconds(2));
        var foreignJob = Job(otherTenantId, null, "foreign", startedAt.AddSeconds(3));
        context.Jobs.AddRange(ownJob, otherJob, foreignJob);
        context.AuditEvents.AddRange(
            Audit(tenantId, userId, "own-audit", "own", startedAt.AddSeconds(4), """{"secret":"never-stream"}"""),
            Audit(tenantId, otherUserId, "other-audit", "other", startedAt.AddSeconds(5)),
            Audit(tenantId, null, "job-audit", "own", startedAt.AddSeconds(6)),
            Audit(otherTenantId, null, "foreign-audit", "foreign", startedAt.AddSeconds(7)));
        context.OutboxMessages.AddRange(
            Outbox(tenantId, "tenant-message", startedAt.AddSeconds(8), """{"token":"never-stream"}"""),
            Outbox(otherTenantId, "foreign-message", startedAt.AddSeconds(9), """{"token":"foreign"}"""));
        await context.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await database.DisposeAsync();

    [Fact]
    public async Task ReadAsync_FiltersUserAndNeverProjectsRawPayloads()
    {
        var events = await Feed().ReadAsync(
            new AdminUpdateScope(tenantId, userId, false),
            BeforeSeed(),
            100,
            CancellationToken.None);

        Assert.Contains(events, item => item.Resource == "job" && item.CorrelationId == "own");
        Assert.Contains(events, item => item.Resource == "audit" && item.Action == "own-audit");
        Assert.Contains(events, item => item.Resource == "audit" && item.Action == "job-audit" && item.JobId.HasValue);
        Assert.DoesNotContain(events, item => item.CorrelationId is "other" or "foreign");
        Assert.DoesNotContain(events, item => item.Resource == "outbox");
        var json = JsonSerializer.Serialize(events);
        Assert.DoesNotContain("never-stream", json, StringComparison.Ordinal);
        Assert.DoesNotContain("PayloadJson", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DetailsJson", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadAsync_AdminSeesTenantOutboxButNotOtherTenant()
    {
        var events = await Feed().ReadAsync(
            new AdminUpdateScope(tenantId, userId, true),
            BeforeSeed(),
            100,
            CancellationToken.None);

        Assert.Contains(events, item => item.Resource == "outbox" &&
            JsonSerializer.Serialize(item.Data).Contains("tenant-message", StringComparison.Ordinal));
        Assert.Contains(events, item => item.CorrelationId == "other");
        Assert.DoesNotContain(events, item => item.CorrelationId == "foreign");
        Assert.DoesNotContain(
            JsonSerializer.Serialize(events),
            "foreign-message",
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsync_LastEventCursorDeduplicatesAndKeepsLaterEvents()
    {
        var feed = Feed();
        var first = await feed.ReadAsync(
            new AdminUpdateScope(tenantId, userId, true),
            BeforeSeed(),
            2,
            CancellationToken.None);
        Assert.Equal(2, first.Count);
        Assert.True(AdminUpdateCursor.TryParse(first[^1].EventId, out var cursor));

        var remaining = await feed.ReadAsync(
            new AdminUpdateScope(tenantId, userId, true),
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
            new AdminUpdateScope(tenantId, userId, false),
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
            new AdminUpdateScope(tenantId, userId, false),
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
        var controller = new AdminUpdatesController(Feed());
        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = new MemoryStream();
        httpContext.Request.Headers["Last-Event-ID"] = BeforeSeed().ToString();
        httpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = new AdminAuthSession
        {
            SessionId = "session",
            UserId = "backend-user",
            UserName = "Owner",
            IsAdministrator = false,
            TenantId = tenantId,
            AllstarrUserId = userId,
            JellyfinAccessToken = "never-stream-session-token",
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(5)
        };
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await controller.Stream(cancellation.Token);

        httpContext.Response.Body.Position = 0;
        var body = await new StreamReader(httpContext.Response.Body, Encoding.UTF8).ReadToEndAsync();
        Assert.Contains("event: stream-status", body, StringComparison.Ordinal);
        Assert.Contains("\"recovered\":true", body, StringComparison.Ordinal);
        Assert.Contains("event: update", body, StringComparison.Ordinal);
        Assert.Contains("id: ", body, StringComparison.Ordinal);
        Assert.DoesNotContain("never-stream", body, StringComparison.Ordinal);
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

    private static PlatformUserRecord User(Guid id, Guid tenant, string name) => new()
    {
        Id = id,
        TenantId = tenant,
        DisplayName = name,
        Status = PlatformUserStatus.Active,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private static DurableJobRecord Job(Guid tenant, Guid? owner, string correlation, DateTimeOffset at) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenant,
        OwnerUserId = owner,
        ScopeKey = $"{tenant:N}:{owner:N}",
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
        Guid tenant,
        Guid? actor,
        string action,
        string correlation,
        DateTimeOffset at,
        string details = "{}") => new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant,
            ActorUserId = actor,
            Category = "test",
            Action = action,
            Outcome = "ok",
            CorrelationId = correlation,
            DetailsJson = details,
            CreatedAt = at
        };

    private static OutboxMessageRecord Outbox(
        Guid tenant,
        string type,
        DateTimeOffset at,
        string payload) => new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant,
            Type = type,
            PayloadJson = payload,
            State = OutboxMessageState.Pending,
            AvailableAt = at,
            MaxAttempts = 3,
            CreatedAt = at,
            UpdatedAt = at,
            Revision = 1
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
