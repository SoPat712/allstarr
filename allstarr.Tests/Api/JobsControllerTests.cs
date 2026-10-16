using System.Text.Json;
using allstarr.Controllers;
using allstarr.Core.Jobs;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed partial class JobsControllerTests : IAsyncLifetime
{
    private readonly Guid _userId = Guid.CreateVersion7();
    private readonly Guid _otherUserId = Guid.CreateVersion7();
    private SqliteTestDatabase _database = null!;
    private TestDbContextFactory _factory = null!;
    private DurableJobQueue _queue = null!;
    private Guid _ownJobId;
    private Guid _otherJobId;

    public async Task InitializeAsync()
    {
        _database = await SqliteTestDatabase.CreateAsync();
        _factory = new TestDbContextFactory(_database.Options);
        await using var context = await _factory.CreateDbContextAsync();
        context.Users.AddRange(User(_userId), User(_otherUserId));
        await context.SaveChangesAsync();
        var jobOptions = new DurableJobOptions();
        _queue = new DurableJobQueue(
            _factory,
            jobOptions,
            new JobPayloadPolicy(jobOptions),
            new SystemPlatformClock());
        _ownJobId = (await Enqueue(_userId, "own-job")).JobId;
        _otherJobId = (await Enqueue(_otherUserId, "other-job")).JobId;
    }

    [Fact]
    public async Task UserList_ReturnsOnlyOwnedJobs()
    {
        var controller = Controller(Session(_userId));

        var result = Assert.IsType<OkObjectResult>(await controller.List());

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
        var jobs = json.RootElement.GetProperty("jobs");
        Assert.Equal(1, jobs.GetArrayLength());
        Assert.Equal(_ownJobId, jobs[0].GetProperty("Id").GetGuid());
        Assert.True(jobs[0].TryGetProperty("DeferralCount", out _));
    }

    [Fact]
    public async Task UserCannotReadOrCancelAnotherUsersJob()
    {
        var controller = Controller(Session(_userId));

        Assert.IsType<NotFoundResult>(await controller.Get(_otherJobId));
        Assert.IsType<NotFoundResult>(await controller.Cancel(_otherJobId));
        await using var context = await _factory.CreateDbContextAsync();
        Assert.Null((await context.Jobs.SingleAsync(item => item.Id == _otherJobId)).CancellationRequestedAt);
    }

    [Fact]
    public async Task UserCanCancelOwnPendingJob()
    {
        var controller = Controller(Session(_userId));

        Assert.IsType<AcceptedResult>(await controller.Cancel(_ownJobId));

        await using var context = await _factory.CreateDbContextAsync();
        var job = await context.Jobs.SingleAsync(item => item.Id == _ownJobId);
        Assert.Equal(DurableJobState.Cancelled, job.State);
        Assert.NotNull(job.CancellationRequestedAt);
    }

    [Fact]
    public async Task AdministratorList_DefaultsToOwnJobs()
    {
        var result = Assert.IsType<OkObjectResult>(await Controller(Session(_userId, true)).List());

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
        Assert.Equal(_ownJobId, Assert.Single(json.RootElement.GetProperty("jobs").EnumerateArray()).GetProperty("Id").GetGuid());
    }

    [Fact]
    public async Task Progress_WithCollidingCorrelation_RemainsOwnedInListAndDetail()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var own = await db.Jobs.SingleAsync(item => item.Id == _ownJobId);
            var other = await db.Jobs.SingleAsync(item => item.Id == _otherJobId);
            other.CorrelationId = own.CorrelationId;
            foreach (var owner in new[] { _userId, _otherUserId })
                db.AuditEvents.Add(new AuditEventRecord
                {
                    Id = Guid.CreateVersion7(),
                    ActorUserId = owner,
                    Category = "job-progress",
                    Action = owner == _userId ? "own-progress" : "other-progress",
                    Outcome = "running",
                    CorrelationId = own.CorrelationId,
                    DetailsJson = "{}",
                    CreatedAt = DateTimeOffset.UtcNow
                });
            await db.SaveChangesAsync();
        }
        var controller = Controller(Session(_userId));
        foreach (var result in new[] { await controller.List(), await controller.Get(_ownJobId) })
        {
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(result).Value));
            var progress = Assert.Single(json.RootElement.GetProperty("progress").EnumerateArray());
            Assert.Equal("own-progress", progress.GetProperty("Action").GetString());
        }
    }

    [Fact]
    public async Task CompletedHistoryUsesExactSubjectAndOwnerAndLatestStateSurvivesRetention()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var own = await db.Jobs.SingleAsync(item => item.Id == _ownJobId);
            var other = await db.Jobs.SingleAsync(item => item.Id == _otherJobId);
            other.CorrelationId = own.CorrelationId;
            await db.SaveChangesAsync();
        }
        for (var index = 0; index < 2; index++)
            await _queue.CompleteAsync((await _queue.ClaimNextAsync("fixture"))!, DurableJobCompletion.Success());
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var own = await db.AuditEvents.SingleAsync(item => item.SubjectId == _ownJobId.ToString("D"));
            db.AuditEvents.AddRange(
                new AuditEventRecord
                {
                    Id = Guid.CreateVersion7(),
                    ActorUserId = _otherUserId,
                    Category = own.Category,
                    Action = own.Action,
                    Outcome = "wrong-owner",
                    CorrelationId = own.CorrelationId,
                    SubjectType = own.SubjectType,
                    SubjectId = own.SubjectId,
                    DetailsJson = own.DetailsJson,
                    CreatedAt = own.CreatedAt
                },
                new AuditEventRecord
                {
                    Id = Guid.CreateVersion7(),
                    ActorUserId = _userId,
                    Category = own.Category,
                    Action = own.Action,
                    Outcome = "malformed",
                    CorrelationId = own.CorrelationId,
                    SubjectType = own.SubjectType,
                    SubjectId = own.SubjectId,
                    DetailsJson = "invalid-json",
                    CreatedAt = own.CreatedAt
                });
            await db.SaveChangesAsync();
        }
        foreach (var admin in new[] { false, true })
        {
            var result = Assert.IsType<OkObjectResult>(await Controller(Session(_userId, admin)).Get(_ownJobId));
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
            var attempt = Assert.Single(json.RootElement.GetProperty("attempts").EnumerateArray());
            Assert.Equal(1, attempt.GetProperty("AttemptNumber").GetInt32());
            Assert.Equal("succeeded", attempt.GetProperty("Outcome").GetString());
            Assert.Equal("succeeded", json.RootElement.GetProperty("job").GetProperty("LastAttemptOutcome").GetString());
        }
        await using (var db = await _factory.CreateDbContextAsync())
            await db.AuditEvents.ExecuteDeleteAsync();
        var retained = Assert.IsType<OkObjectResult>(await Controller(Session(_userId)).Get(_ownJobId));
        using var latest = JsonDocument.Parse(JsonSerializer.Serialize(retained.Value));
        Assert.Empty(latest.RootElement.GetProperty("attempts").EnumerateArray());
        Assert.Equal("succeeded", latest.RootElement.GetProperty("job").GetProperty("LastAttemptOutcome").GetString());
        Assert.Equal(JsonValueKind.String, latest.RootElement.GetProperty("job").GetProperty("LastAttemptCompletedAt").ValueKind);
    }

    private JobsController Controller(AdminAuthSession session)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = session;
        return new JobsController(_factory, _queue)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };
    }

    private AdminAuthSession Session(Guid userId, bool admin = false) => new()
    {
        SessionId = Guid.NewGuid().ToString("N"),
        UserId = userId.ToString(),
        UserName = "fixture",
        IsAdministrator = admin,
        AllstarrUserId = userId,
        JellyfinAccessToken = "protected",
        ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
        LastSeenUtc = DateTime.UtcNow
    };

    private UserRecord User(Guid id) => new()
    {
        Id = id,
        DisplayName = id.ToString("N"),
        Enabled = true,
        BackendType = "jellyfin",
        BackendInstanceId = "fixture",
        BackendPrincipalId = id.ToString("N"),
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private Task<DurableJobEnqueueResult> Enqueue(Guid ownerId, string key) =>
        _queue.EnqueueAsync(new DurableJobEnqueueRequest<object>(
            "fixture",
            key,
            new { itemId = key },
            ownerId));

    public async Task DisposeAsync() => await _database.DisposeAsync();

    private sealed class TestDbContextFactory(DbContextOptions<AllstarrDbContext> options)
        : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);

        public Task<AllstarrDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(new AllstarrDbContext(options));
    }
}
