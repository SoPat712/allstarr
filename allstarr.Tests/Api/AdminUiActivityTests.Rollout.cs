using System.Text.Json;
using allstarr.Controllers;
using allstarr.Core.Identity;
using allstarr.Core.Jobs;
using allstarr.Core.Matching;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Tests;

public sealed partial class AdminUiActivityTests
{
    [Theory]
    [InlineData(DurableJobState.Running, null, "running")]
    [InlineData(DurableJobState.RetryScheduled, "deferred", "running")]
    [InlineData(DurableJobState.Succeeded, "succeeded", "succeeded")]
    public async Task HomeActivity_RolloutUsesOneProgressOrSummaryRowAndKeepsFailures(
        DurableJobState state, string? outcome, string progressState)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new Factory(database.Options);
        var owner = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Users.Add(new() { Id = owner, DisplayName = "Listener", BackendType = "jellyfin", BackendInstanceId = "fixture", BackendPrincipalId = owner.ToString("N") });
            var job = HistoryJob(owner, "track-match.rematch", state, outcome, now);
            job.LastErrorCode = outcome == "deferred" ? "track_rematch_batch_pending" : null;
            db.Jobs.Add(job);
            if (outcome != null) DurableJobHistory.Append(db, job, now);
            db.AuditEvents.Add(new()
            {
                Id = Guid.CreateVersion7(),
                ActorUserId = owner,
                Category = "matching-rollout",
                Action = "checking-matches",
                Outcome = progressState,
                SubjectType = "job",
                SubjectId = job.Id.ToString("D"),
                CreatedAt = now,
                DetailsJson = JsonSerializer.Serialize(new { message = "20 of 100 · Now plays from your library: 12 songs" })
            });
            for (var index = 0; index < 30; index++)
                db.AuditEvents.Add(new()
                {
                    Id = Guid.CreateVersion7(),
                    ActorUserId = owner,
                    Category = "job-progress",
                    Action = "track.rematch",
                    Outcome = "running",
                    SubjectType = "job",
                    SubjectId = job.Id.ToString("D"),
                    CreatedAt = now,
                    DetailsJson = "{}"
                });
            db.AuditEvents.Add(new()
            {
                Id = Guid.CreateVersion7(),
                ActorUserId = owner,
                Category = "track-rematch",
                Action = "automatic.review",
                Outcome = "retry_exception",
                CreatedAt = now.AddSeconds(-1)
            });
            var failed = HistoryJob(owner, "track-match.rematch", DurableJobState.Failed, "failed", now.AddSeconds(-2));
            failed.LastErrorCode = "track_rematch_rows_failed";
            db.Jobs.Add(failed);
            db.AuditEvents.Add(new()
            {
                Id = Guid.CreateVersion7(),
                ActorUserId = owner,
                Category = "matching-rollout",
                Action = "checking-matches",
                Outcome = "running",
                SubjectType = "job",
                SubjectId = failed.Id.ToString("D"),
                CreatedAt = now.AddSeconds(-3),
                DetailsJson = JsonSerializer.Serialize(new { message = "Checking stopped" })
            });
            var recording = Guid.CreateVersion7();
            db.CanonicalRecordings.Add(new() { Id = recording, CreatedByUserId = owner });
            for (var index = 0; index < 30; index++)
            {
                var snapshot = new ExternalMetadataSnapshotRecord
                {
                    Id = Guid.CreateVersion7(),
                    OwnerUserId = owner,
                    ProviderId = "fixture",
                    ResourceKind = "track",
                    BackendInstanceId = "fixture",
                    BackendPrincipalId = owner.ToString("N"),
                    Protocol = "jellyfin",
                    ExternalIdHash = index.ToString("x64"),
                    PayloadSha256 = new string('d', 64),
                    PayloadJson = "{}",
                    RetrievedAt = now,
                    SnapshotVersion = 1
                };
                db.ExternalMetadataSnapshots.Add(snapshot);
                db.TrackMatches.Add(new()
                {
                    Id = Guid.CreateVersion7(),
                    OwnerUserId = owner,
                    ExternalSnapshotId = snapshot.Id,
                    State = TrackMatchState.Accepted,
                    CanonicalRecordingId = recording,
                    PolicyVersion = TrackRematchAllService.RolloutPolicyVersion,
                    DecidedAt = now,
                    DecisionVersion = 1,
                    SourceSnapshotVersion = 1
                });
            }
            await db.SaveChangesAsync();
        }
        var matches = new TrackMatchCommandService(factory, new TrackMatchDecisionEngine(), new ProviderAccountResolver(factory), new SystemPlatformClock(), HomeLibraryAccess().Object);
        var controller = Controller(factory, matches, owner);
        var home = Assert.IsType<AdminUiActivityPage>(await controller.ReadHomeActivityAsync());
        Assert.Equal(3, home.Items.Length);
        var progress = Assert.Single(home.Items, item => item.Action == "checking-matches");
        Assert.Equal("Checking matches", progress.Label);
        Assert.Equal("Matching", progress.Source);
        Assert.Equal(progressState, progress.State);
        Assert.Equal("20 of 100 · Now plays from your library: 12 songs", progress.Detail);
        Assert.Contains(home.Items, item => item.State == "failed" && !item.IsRoutine);
        Assert.Contains(home.Items, item => item.State == "retry_exception" && !item.IsRoutine);
        var history = Assert.IsType<AdminUiActivityPage>(Assert.IsType<OkObjectResult>(await controller.GetDashboardActivity(100)).Value);
        Assert.All(history.Items.Where(item => item.Label == "Track matched"), item => Assert.True(item.IsRoutine));
        Assert.Contains(history.Items, item => item.Label == "Track matched");
        Assert.All(history.Items.Where(item => item.Label == "Track Rematch"), item => Assert.True(item.IsRoutine));
        Assert.Contains(history.Items, item => item.Label == "Track Rematch");
    }

    [Theory]
    [InlineData("retry_scheduled")]
    [InlineData("retryscheduled")]
    public async Task Activity_RematchCurrentRetryAppearsOnceWithCanonicalState(string outcome)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new Factory(database.Options);
        var owner = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Users.Add(new() { Id = owner, DisplayName = "Listener", BackendType = "jellyfin", BackendInstanceId = "fixture", BackendPrincipalId = owner.ToString("N") });
            var job = HistoryJob(owner, "track-match.rematch", DurableJobState.RetryScheduled, outcome, now);
            job.LastErrorCode = "track_rematch_rows_failed";
            db.Jobs.Add(job);
            DurableJobHistory.Append(db, job, now);
            await db.SaveChangesAsync();
        }
        var matches = new TrackMatchCommandService(factory, new TrackMatchDecisionEngine(), new ProviderAccountResolver(factory), new SystemPlatformClock(), HomeLibraryAccess().Object);
        var controller = Controller(factory, matches, owner);
        foreach (var page in new[] { await controller.ReadHomeActivityAsync(),
            Assert.IsType<AdminUiActivityPage>(Assert.IsType<OkObjectResult>(await controller.GetDashboardActivity()).Value) })
        {
            var row = Assert.Single(page!.Items);
            Assert.Equal("retry_scheduled", row.State);
            Assert.False(row.IsRoutine);
        }
    }

    [Theory]
    [InlineData(TrackMatchState.Accepted, true)]
    [InlineData(TrackMatchState.Unresolved, false)]
    [InlineData(TrackMatchState.Rejected, false)]
    public void Activity_AutomaticRematchDecisionsPreserveProblems(TrackMatchState state, bool routine)
    {
        var match = new TrackMatchRecord
        {
            Id = Guid.NewGuid(),
            OwnerUserId = Guid.NewGuid(),
            State = state,
            PolicyVersion = TrackRematchAllService.RolloutPolicyVersion
        };
        Assert.Equal(routine, AdminUiController.MatchActivityItem(match, null, null, [], []).IsRoutine);
        match.PolicyVersion = "manual";
        Assert.False(AdminUiController.MatchActivityItem(match, null, null, [], []).IsRoutine);
        match.PolicyVersion = TrackRematchAllService.ForcePolicyVersion;
        Assert.False(AdminUiController.MatchActivityItem(match, null, null, [], []).IsRoutine);
    }
}
