using System.Text.Json;
using allstarr.Core.Jobs;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace allstarr.Tests;

public sealed class DurableJobHistoryMigrationTests
{
    private const string Before = "20261015045010_RecordingLyricsSourceChoices";

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(true, false, false, true)]
    [InlineData(false, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(true, false, true, false)]
    [Trait("Lane", "ReleaseCritical")]
    public async Task UpgradeFoldsCompletedAttemptsAndPreservesInterruptedWorkAndRollback(bool completeBeforeRollback, bool cancelBeforeRollback, bool missingCompletedOutcome, bool historyExpired)
    {
        await using var database = await SqliteTestDatabase.CreateAsync(useTemplate: false);
        await using var db = new AllstarrDbContext(database.Options);
        await db.GetService<IMigrator>().MigrateAsync(Before);
        var now = new DateTimeOffset(2026, 10, 15, 1, 0, 0, TimeSpan.Zero);
        var owner = Guid.CreateVersion7();
        db.Users.Add(new()
        {
            Id = owner,
            BackendType = "jellyfin",
            BackendInstanceId = "fixture",
            BackendPrincipalId = "listener",
            DisplayName = "Listener",
            Enabled = true,
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync();
        var completedJob = Guid.CreateVersion7();
        var runningJob = Guid.CreateVersion7();
        var oldAudit = Guid.CreateVersion7();
        var fingerprint = new string('a', 64);
        var policy = """{"Version":2,"AuthorizationRule":"initiator_only"}""";
        var empty = "{}";
        foreach (var (id, state, attempts, failures) in new[]
                 { (completedJob, "Succeeded", 2, 1), (runningJob, "Running", 1, 0) })
        {
            var scope = "user:" + owner.ToString("N");
            var correlation = id.ToString("N");
            var completedAt = state == "Succeeded" ? (long?)now.AddSeconds(2).UtcTicks : null;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO durable_jobs
                (Id, ScopeKey, OwnerUserId, PolicySnapshotJson, RequestFingerprint, CorrelationId,
                 Type, PayloadJson, IdempotencyKey, State, Priority, AttemptCount, FailureCount,
                 DeferralCount, MaxAttempts, MaxDeferrals, AvailableAt, LeaseOwner, LeaseExpiresAt,
                 StartedAt, CompletedAt, CreatedAt, UpdatedAt, Revision)
                VALUES ({id}, {scope}, {owner}, {policy},
                        {fingerprint}, {correlation}, 'fixture.work', {empty}, {correlation}, {state}, 0,
                        {attempts}, {failures}, 0, 3, 20, {now.UtcTicks}, 'old-process', {now.AddMinutes(10).UtcTicks},
                        {now.UtcTicks}, {completedAt}, {now.UtcTicks}, {now.UtcTicks}, 7);
                """);
        }
        foreach (var (jobId, number, outcome) in new[]
                 { (completedJob, 1, "retry_scheduled"), (completedJob, 2, "succeeded"), (runningJob, 1, (string?)null) })
        {
            var id = Guid.CreateVersion7();
            var started = now.AddSeconds(number - 1);
            var ended = outcome == null ? (long?)null : now.AddSeconds(number).UtcTicks;
            var savedOutcome = missingCompletedOutcome && outcome == "succeeded" ? null : outcome;
            var errorCode = outcome == "retry_scheduled" ? "source_unavailable" : null;
            var errorMessage = outcome == "retry_scheduled" ? "The source was unavailable." : null;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO job_attempts (Id, JobId, AttemptNumber, WorkerId, StartedAt, CompletedAt, Outcome, ErrorCode, ErrorMessage)
                VALUES ({id}, {jobId}, {number}, 'old-process', {started.UtcTicks}, {ended}, {savedOutcome},
                        {errorCode},
                        {errorMessage});
                """);
        }
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO audit_events (Id, ActorUserId, Category, Action, Outcome, CorrelationId, DetailsJson, CreatedAt)
            VALUES ({oldAudit}, {owner}, 'fixture', 'preserve', 'success', 'unrelated', {empty}, {now.UtcTicks});
            """);
        db.ChangeTracker.Clear();
        await db.Database.MigrateAsync();
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS Value FROM sqlite_master WHERE type = 'table' AND name = 'job_attempts'").SingleAsync());
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS Value FROM pragma_table_info('durable_jobs') WHERE name IN ('LeaseOwner', 'LeaseExpiresAt')").SingleAsync());
        Assert.True(await db.AuditEvents.AnyAsync(item => item.Id == oldAudit));
        var history = await db.AuditEvents.Where(item => item.Category == "job-attempt").ToArrayAsync();
        Assert.Equal(2, history.Length);
        Assert.All(history, item => Assert.Equal(owner, item.ActorUserId));
        var retry = Assert.Single(history, item => item.Outcome == "retry_scheduled");
        using (var details = JsonDocument.Parse(retry.DetailsJson))
        {
            Assert.Equal(completedJob, details.RootElement.GetProperty("jobId").GetGuid());
            Assert.Equal(1, details.RootElement.GetProperty("attemptNumber").GetInt32());
            Assert.Equal(now.UtcTicks, details.RootElement.GetProperty("startedAtTicks").GetInt64());
            Assert.Equal(now.AddSeconds(1).UtcTicks, details.RootElement.GetProperty("completedAtTicks").GetInt64());
            Assert.Equal("source_unavailable", details.RootElement.GetProperty("errorCode").GetString());
        }
        var completed = await db.Jobs.AsNoTracking().SingleAsync(item => item.Id == completedJob);
        Assert.Equal(missingCompletedOutcome ? "interrupted" : "succeeded", completed.LastAttemptOutcome);
        Assert.NotNull(completed.LastAttemptCompletedAt);
        var factory = new TestDbContextFactory(database.Options);
        var options = new DurableJobOptions();
        var queue = new DurableJobQueue(factory, options, new JobPayloadPolicy(options), new SystemPlatformClock());
        Assert.Equal(1, await queue.RecoverInterruptedAsync());
        var claim = Assert.IsType<DurableJobClaim>(await queue.ClaimNextAsync("new-process"));
        Assert.Equal(runningJob, claim.JobId);
        Assert.Equal(2, claim.AttemptNumber);
        if (cancelBeforeRollback)
            Assert.True(await queue.RequestCancellationAsync(runningJob, owner));
        if (completeBeforeRollback)
            await queue.CompleteAsync(claim, DurableJobCompletion.Success());
        db.ChangeTracker.Clear();
        var saved = await db.Jobs.SingleAsync(item => item.Id == runningJob);
        Assert.Equal(completeBeforeRollback ? DurableJobState.Succeeded : DurableJobState.Running, saved.State);
        Assert.Equal(2, saved.AttemptCount);
        Assert.Equal(0, saved.FailureCount);
        Assert.NotNull(saved.LastAttemptStartedAt);
        Assert.Equal(completeBeforeRollback ? "succeeded" : "running", saved.LastAttemptOutcome);
        Assert.Equal(completeBeforeRollback ? 4 : 3, await db.AuditEvents.CountAsync(item => item.Category == "job-attempt"));

        if (historyExpired)
            await db.AuditEvents.Where(item => item.Category == "job-attempt").ExecuteDeleteAsync();
        await db.GetService<IMigrator>().MigrateAsync(Before);
        Assert.Equal(historyExpired ? 2 : 4, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM job_attempts").SingleAsync());
        Assert.Equal(2, await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM durable_jobs WHERE PayloadJson = {empty}").SingleAsync());
        await db.Database.MigrateAsync();
        db.ChangeTracker.Clear();
        Assert.Equal(historyExpired ? 2 : 4, await db.AuditEvents.CountAsync(item => item.Category == "job-attempt"));
        Assert.True(await db.AuditEvents.AnyAsync(item => item.Id == oldAudit));
        Assert.Equal(2, await db.Jobs.CountAsync());
        saved = await db.Jobs.SingleAsync(item => item.Id == runningJob);
        Assert.Equal(completeBeforeRollback ? DurableJobState.Succeeded : cancelBeforeRollback ? DurableJobState.Cancelled : DurableJobState.Pending, saved.State);
        Assert.Equal(0, saved.FailureCount);
        Assert.Equal(completeBeforeRollback ? "succeeded" : cancelBeforeRollback ? "cancelled" : "interrupted", saved.LastAttemptOutcome);
        Assert.Equal(2, saved.AttemptCount);
    }
    private sealed class TestDbContextFactory(DbContextOptions<AllstarrDbContext> options)
        : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
        public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AllstarrDbContext(options));
    }

}
