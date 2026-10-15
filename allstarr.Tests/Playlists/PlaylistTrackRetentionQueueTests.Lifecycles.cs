using System.Text.Json;
using allstarr.Core.Jobs;
using allstarr.Core.Matching;
using allstarr.Core.Playlists;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed partial class PlaylistTrackRetentionQueueTests
{
    [Fact]
    public async Task NewPublishedSnapshotQueuesFreshWorkWithTheSameLastingInterestKey()
    {
        var link = Link(PlaylistTrackRetention.KeepAll);
        var first = RetentionPlan(link);
        var queue = RetentionQueue();
        Assert.Equal(1, await queue.EnqueueAsync(link, first, "first", default));
        var claim = Assert.IsType<DurableJobClaim>(await _jobs.ClaimNextAsync("first", [PlaylistTrackRetentionJobHandler.JobTypeName]));
        await _jobs.CompleteAsync(claim, DurableJobCompletion.Success());
        var second = RetentionPlan(link);
        Assert.NotEqual(first.SourceSnapshotId, second.SourceSnapshotId);
        Assert.Equal(1, await queue.EnqueueAsync(link, second, "second", default));
        Assert.Equal(0, await queue.EnqueueAsync(link, second, "second-repeat", default));
        await AssertSeparateLifecycleJobsAsync(first.SourceSnapshotId, second.SourceSnapshotId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReenabledKeepAllQueuesFreshWorkForTheSamePublishedSnapshot(bool cancelled)
    {
        var link = Link(PlaylistTrackRetention.KeepAll);
        var plan = RetentionPlan(link);
        var queue = RetentionQueue();
        Assert.Equal(1, await queue.EnqueueAsync(link, plan, "enabled", default));
        var claim = Assert.IsType<DurableJobClaim>(await _jobs.ClaimNextAsync("enabled", [PlaylistTrackRetentionJobHandler.JobTypeName]));
        await _jobs.CompleteAsync(claim, cancelled ? DurableJobCompletion.Cancelled() : DurableJobCompletion.Success());
        link.TrackRetention = PlaylistTrackRetention.OnDemand;
        link.Revision++;
        Assert.Equal(0, await queue.EnqueueAsync(link, plan, "disabled", default));
        link.TrackRetention = PlaylistTrackRetention.KeepAll;
        link.Revision++;
        Assert.Equal(1, await queue.EnqueueAsync(link, plan, "reenabled", default));
        Assert.Equal(0, await queue.EnqueueAsync(link, plan, "reenabled-repeat", default));
        await AssertSeparateLifecycleJobsAsync(plan.SourceSnapshotId, plan.SourceSnapshotId);
    }

    private PlaylistMaterializationPlan RetentionPlan(PlaylistLinkRecord link) =>
        Plan(link, Entry(Guid.CreateVersion7(), 0,
            new PlaylistResolvedRoute(TrackRouteKind.External, ProviderId: "spotify", ExternalId: "same-track")));

    private async Task AssertSeparateLifecycleJobsAsync(Guid firstSnapshot, Guid secondSnapshot)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var jobs = await db.Jobs.OrderBy(item => item.Id).ToArrayAsync();
        Assert.Equal(2, jobs.Length);
        Assert.NotEqual(jobs[0].IdempotencyKey, jobs[1].IdempotencyKey);
        var first = JsonSerializer.Deserialize<PlaylistTrackRetentionJobPayload>(jobs[0].PayloadJson)!;
        var second = JsonSerializer.Deserialize<PlaylistTrackRetentionJobPayload>(jobs[1].PayloadJson)!;
        Assert.Equal(firstSnapshot, first.SourceSnapshotId);
        Assert.Equal(secondSnapshot, second.SourceSnapshotId);
        Assert.Equal(first.RetentionKey, second.RetentionKey);
        Assert.Equal(DurableJobState.Pending, jobs[1].State);
        Assert.NotEqual(DurableJobState.Pending, jobs[0].State);
    }
}
