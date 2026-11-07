using System.Text.Json;
using allstarr.Core.Jobs;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed partial class DurableJobQueueTests
{
    [Fact]
    public async Task HistoryContinuation_UpdatesOneJobAndProgressSnapshotWithoutAttemptEvents()
    {
        var queued = await _queue.EnqueueAsync(new DurableJobEnqueueRequest<object>(
            "listening-history.resolve", "history-continuation", new { }, _userId, MaxDeferrals: 1));
        Guid? progressId = null;
        for (var completed = 1; completed <= 3; completed++)
        {
            var claim = Assert.IsType<DurableJobClaim>(await _queue.ClaimNextAsync("history-progress"));
            Assert.Equal(queued.JobId, claim.JobId);
            Assert.True(await _queue.ReportProgressAsync(claim,
                new("history-resolution", "Saved past songs.", completed, 4)));
            await _queue.CompleteAsync(claim, DurableJobCompletion.Continue(TimeSpan.FromMinutes(1)));

            await using var db = await _factory.CreateDbContextAsync();
            var job = Assert.Single(await db.Jobs.ToArrayAsync());
            Assert.Equal(DurableJobState.RetryScheduled, job.State);
            Assert.Equal("deferred", job.LastAttemptOutcome);
            Assert.Equal(0, job.DeferralCount);
            Assert.Equal(0, job.FailureCount);
            Assert.Null(job.LastErrorCode);
            Assert.Null(job.LastErrorMessage);
            Assert.Empty(await db.AuditEvents.Where(item => item.Category == "job-attempt").ToArrayAsync());
            var snapshot = Assert.Single(await db.AuditEvents.Where(item => item.Category == "job-progress").ToArrayAsync());
            Assert.Equal("history-resolution-current", snapshot.Action);
            progressId ??= snapshot.Id;
            Assert.Equal(progressId, snapshot.Id);
            using var details = JsonDocument.Parse(snapshot.DetailsJson);
            Assert.Equal(completed, details.RootElement.GetProperty("completed").GetInt32());
            Assert.Equal(4, details.RootElement.GetProperty("total").GetInt32());
            Assert.True(details.RootElement.GetProperty("currentSnapshot").GetBoolean());
            _clock.Advance(TimeSpan.FromMinutes(1));
        }

        var final = Assert.IsType<DurableJobClaim>(await _queue.ClaimNextAsync("history-progress"));
        await _queue.CompleteAsync(final, DurableJobCompletion.Success());
        await using var finished = await _factory.CreateDbContextAsync();
        Assert.Equal(DurableJobState.Succeeded, (await finished.Jobs.SingleAsync()).State);
        Assert.Single(await finished.AuditEvents.Where(item => item.Category == "job-attempt").ToArrayAsync());
    }

    [Fact]
    public async Task BlockedHistoryDeferrals_GrowExistingBackoffAndHonorLongerSourceDelay()
    {
        await _queue.EnqueueAsync(new DurableJobEnqueueRequest<object>(
            "listening-history.resolve", "history-blocked", new { }, _userId, MaxDeferrals: 4));
        foreach (var seconds in new[] { 2, 4, 8 })
        {
            var claim = Assert.IsType<DurableJobClaim>(await _queue.ClaimNextAsync("history-blocked"));
            await _queue.CompleteAsync(claim, DurableJobCompletion.Defer(
                "history_resolution_blocked", "Waiting for a music source.", useBackoff: true));
            await using var db = await _factory.CreateDbContextAsync();
            var job = await db.Jobs.SingleAsync();
            Assert.Equal(TimeSpan.FromSeconds(seconds), job.AvailableAt - _clock.UtcNow);
            Assert.Equal(0, job.FailureCount);
            Assert.Equal("history_resolution_blocked", job.LastErrorCode);
            _clock.Advance(TimeSpan.FromSeconds(seconds));
        }

        var limited = Assert.IsType<DurableJobClaim>(await _queue.ClaimNextAsync("history-blocked"));
        await _queue.CompleteAsync(limited, DurableJobCompletion.Defer(
            "history_resolution_blocked", "Waiting for a music source.", TimeSpan.FromMinutes(10), useBackoff: true));
        await using (var db = await _factory.CreateDbContextAsync())
            Assert.Equal(_clock.UtcNow.AddMinutes(10), (await db.Jobs.SingleAsync()).AvailableAt);

        _clock.Advance(TimeSpan.FromMinutes(10));
        var exhausted = Assert.IsType<DurableJobClaim>(await _queue.ClaimNextAsync("history-blocked"));
        await _queue.CompleteAsync(exhausted, DurableJobCompletion.Defer(
            "history_resolution_blocked", "Waiting for a music source.", useBackoff: true));
        await using var finished = await _factory.CreateDbContextAsync();
        var stopped = await finished.Jobs.SingleAsync();
        Assert.Equal(DurableJobState.Failed, stopped.State);
        Assert.Equal("deferral_limit_exceeded", stopped.LastErrorCode);
        Assert.Equal(0, stopped.FailureCount);
    }

    [Fact]
    public async Task ExistingHistoryJob_UsesConfiguredBlockedBudgetAfterProgressDeferralsAreSeparated()
    {
        _options.DefaultMaxDeferrals = 2;
        await _queue.EnqueueAsync(new DurableJobEnqueueRequest<object>(
            "listening-history.resolve", "history-legacy-budget", new { }, _userId, MaxDeferrals: 10000));
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var claim = Assert.IsType<DurableJobClaim>(await _queue.ClaimNextAsync("history-blocked"));
            await _queue.CompleteAsync(claim, DurableJobCompletion.Defer(
                "history_resolution_blocked", "Waiting for a music source.", useBackoff: true));
            _clock.Advance(TimeSpan.FromMinutes(1));
        }
        await using var db = await _factory.CreateDbContextAsync();
        var job = await db.Jobs.SingleAsync();
        Assert.Equal(2, job.MaxDeferrals);
        Assert.Equal(DurableJobState.Failed, job.State);
        Assert.Equal("deferral_limit_exceeded", job.LastErrorCode);
    }

    [Fact]
    public async Task HistoryProgress_LeavesEarlierAuditEventsUnchanged()
    {
        await _queue.EnqueueAsync(new DurableJobEnqueueRequest<object>(
            "listening-history.resolve", "history-previous-progress", new { }, _userId));
        var claim = Assert.IsType<DurableJobClaim>(await _queue.ClaimNextAsync("history-progress"));
        var previousId = Guid.CreateVersion7();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.AuditEvents.Add(new()
            {
                Id = previousId,
                ActorUserId = _userId,
                Category = "job-progress",
                Action = "history-resolution",
                Outcome = "running",
                CorrelationId = claim.CorrelationId,
                SubjectType = "job",
                SubjectId = claim.JobId.ToString("D"),
                DetailsJson = "{\"completed\":1}",
                CreatedAt = _clock.UtcNow.AddMinutes(-1)
            });
            await db.SaveChangesAsync();
        }
        await _queue.ReportProgressAsync(claim, new("history-resolution", "Saved past songs.", 2, 3));
        await _queue.ReportProgressAsync(claim, new("history-resolution", "Saved past songs.", 3, 3));
        await using var finished = await _factory.CreateDbContextAsync();
        var previous = await finished.AuditEvents.SingleAsync(item => item.Id == previousId);
        Assert.Equal("history-resolution", previous.Action);
        Assert.Equal("{\"completed\":1}", previous.DetailsJson);
        Assert.Equal(_clock.UtcNow.AddMinutes(-1), previous.CreatedAt);
        Assert.Equal(2, await finished.AuditEvents.CountAsync(item => item.Category == "job-progress"));
    }
}
