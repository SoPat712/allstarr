using allstarr.Services.Common;

namespace allstarr.Tests;

public sealed class ApplicationCacheRequestCoalescerTests
{
    [Fact]
    public async Task ConcurrentRequests_RunOneFetch()
    {
        var metrics = new ApplicationCacheActivityMetrics();
        var coalescer = new ApplicationCacheRequestCoalescer(metrics);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        async Task<string> Fetch()
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            await release.Task;
            return "page";
        }

        var requests = Enumerable.Range(0, 8)
            .Select(_ => coalescer.RunAsync("playlist:discovery:fixture", Fetch, CancellationToken.None))
            .ToArray();
        await started.Task;
        release.SetResult();

        Assert.All(await Task.WhenAll(requests), value => Assert.Equal("page", value));
        Assert.Equal(1, calls);
        Assert.Equal(7, metrics.Snapshot().CoalescedRequests);
    }
}
