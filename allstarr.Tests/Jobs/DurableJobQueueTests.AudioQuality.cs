using allstarr.Core.Downloads;
using allstarr.Core.Jobs;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed partial class DurableJobQueueTests
{
    [Fact]
    public async Task QualityChecksAreDurableLowPriorityAndNeverClaimedTogether()
    {
        var first = await _queue.EnqueueAsync(AudioQualityCheckJobHandler.Request(Guid.NewGuid(), _userId));
        var second = await _queue.EnqueueAsync(AudioQualityCheckJobHandler.Request(Guid.NewGuid(), _userId));
        var foreground = await _queue.EnqueueAsync(new DurableJobEnqueueRequest<object>(
            "favorite.download", "foreground", new { }, _userId));
        var foregroundClaim = await _queue.ClaimNextAsync("fixture");
        Assert.Equal(foreground.JobId, foregroundClaim!.JobId);
        var claim = await _queue.ClaimNextAsync("fixture", [AudioQualityCheckJobHandler.Type]);
        Assert.Contains(claim!.JobId, new[] { first.JobId, second.JobId });
        Assert.Null(await _queue.ClaimNextAsync("other-worker", [AudioQualityCheckJobHandler.Type]));
        await using var db = await _factory.CreateDbContextAsync();
        var saved = await db.Jobs.SingleAsync(item => item.Id == first.JobId);
        Assert.True(saved.Priority < 0);
        Assert.DoesNotContain("Path", saved.PayloadJson);
    }
}
