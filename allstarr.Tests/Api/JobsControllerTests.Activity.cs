using System.Text.Json;
using allstarr.Core.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed partial class JobsControllerTests
{
    [Fact]
    public async Task ActivityQueueAndHistory_AreDisjointAndOwnerScoped()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var own = await db.Jobs.SingleAsync(x => x.Id == _ownJobId);
            own.State = DurableJobState.Succeeded;
            own.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }
        var listener = Controller(Session(_userId));
        using var queue = Json(await listener.List(view: "queue"));
        Assert.Empty(queue.RootElement.GetProperty("jobs").EnumerateArray());
        using var history = Json(await listener.List(view: "history"));
        Assert.Equal(_ownJobId, Assert.Single(history.RootElement.GetProperty("jobs").EnumerateArray()).GetProperty("Id").GetGuid());
        using var household = Json(await Controller(Session(_userId, true)).List(view: "queue"));
        Assert.Equal(_otherJobId, Assert.Single(household.RootElement.GetProperty("jobs").EnumerateArray()).GetProperty("Id").GetGuid());
    }

    [Fact]
    public async Task ActivityDeferredFilter_DistinguishesWaitsFromFailures()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var job = await db.Jobs.SingleAsync(x => x.Id == _ownJobId);
            job.State = DurableJobState.RetryScheduled;
            job.LastAttemptOutcome = "deferred";
            job.DeferralCount = 1;
            await db.SaveChangesAsync();
        }
        using var result = Json(await Controller(Session(_userId)).List(state: "Deferred", view: "queue"));
        var row = Assert.Single(result.RootElement.GetProperty("jobs").EnumerateArray());
        Assert.Equal("Deferred", row.GetProperty("state").GetString());
    }

    [Theory]
    [InlineData("42", null)]
    [InlineData(null, "unknown")]
    public async Task ActivityRejectsUnknownFilters(string? state, string? view) =>
        Assert.IsType<BadRequestObjectResult>(await Controller(Session(_userId)).List(state: state, view: view));

    [Fact]
    public async Task ActivityPaging_UsesStableTimestampAndIdTiesWithoutDuplicates()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            foreach (var job in await db.Jobs.ToListAsync())
            {
                job.State = DurableJobState.Succeeded;
                job.CompletedAt = DateTimeOffset.UtcNow.Date;
            }
            await db.SaveChangesAsync();
        }
        var controller = Controller(Session(_userId, true));
        using var first = Json(await controller.List(view: "history", limit: 1));
        Assert.True(first.RootElement.GetProperty("hasMore").GetBoolean());
        var time = first.RootElement.GetProperty("nextCursor").GetDateTimeOffset();
        var id = first.RootElement.GetProperty("nextCursorId").GetGuid();
        using var second = Json(await controller.List(view: "history", limit: 1, before: time, beforeId: id));
        var next = Assert.Single(second.RootElement.GetProperty("jobs").EnumerateArray()).GetProperty("Id").GetGuid();
        Assert.NotEqual(id, next);
        Assert.False(second.RootElement.GetProperty("hasMore").GetBoolean());
    }

    [Fact]
    public async Task ActivityReads_RedactLegacyErrorsAndRawProgressFields()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var job = await db.Jobs.SingleAsync(x => x.Id == _ownJobId);
            job.LastErrorMessage = "token=fixture-private-value";
            db.AuditEvents.Add(new()
            {
                Id = Guid.CreateVersion7(),
                ActorUserId = _userId,
                Category = "job-progress",
                Action = "copy",
                SubjectType = "job",
                SubjectId = job.Id.ToString("D"),
                CorrelationId = job.CorrelationId,
                Outcome = "running",
                DetailsJson = "{\"completed\":1,\"total\":2,\"message\":\"token=fixture-private-value\",\"rawPayload\":\"fixture-raw-private\"}",
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }
        foreach (var value in new[] { await Controller(Session(_userId)).List(), await Controller(Session(_userId)).Get(_ownJobId) })
        {
            var json = JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(value).Value);
            Assert.DoesNotContain("fixture-private-value", json);
            Assert.DoesNotContain("fixture-raw-private", json);
            Assert.Contains("completed", json);
        }
    }

    [Fact]
    public async Task ActivityAttemptHistory_PagesRetainedAttemptsWithoutRepeatingThem()
    {
        var now = DateTimeOffset.UtcNow;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            for (var i = 1; i <= 3; i++) db.AuditEvents.Add(new()
            {
                Id = Guid.CreateVersion7(),
                ActorUserId = _userId,
                Category = "job-attempt",
                SubjectType = "job",
                SubjectId = _ownJobId.ToString("D"),
                Action = "attempt-completed",
                Outcome = "failed",
                CreatedAt = now.AddSeconds(i),
                DetailsJson = JsonSerializer.Serialize(new { jobId = _ownJobId, attemptNumber = i, startedAtTicks = now.UtcTicks })
            });
            await db.SaveChangesAsync();
        }
        var controller = Controller(Session(_userId));
        using var first = Json(await controller.Get(_ownJobId, limit: 1));
        Assert.Equal(3, Assert.Single(first.RootElement.GetProperty("attempts").EnumerateArray()).GetProperty("AttemptNumber").GetInt32());
        using var second = Json(await controller.Get(_ownJobId, limit: 1,
            before: first.RootElement.GetProperty("nextCursor").GetDateTimeOffset(), beforeId: first.RootElement.GetProperty("nextCursorId").GetGuid()));
        Assert.Equal(2, Assert.Single(second.RootElement.GetProperty("attempts").EnumerateArray()).GetProperty("AttemptNumber").GetInt32());
    }

    private static JsonDocument Json(IActionResult value) =>
        JsonDocument.Parse(JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(value).Value));
}
