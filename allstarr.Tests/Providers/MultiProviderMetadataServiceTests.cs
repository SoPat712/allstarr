using allstarr.Services.Common;

namespace allstarr.Tests;

public sealed class MultiProviderMetadataServiceTests
{
    [Fact]
    public async Task Timed_out_work_drains_before_the_concurrency_slot_is_reused()
    {
        var synchronizationTimeout = TimeSpan.FromSeconds(2);
        using var gate = new SemaphoreSlim(1, 1);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = RunAsync(async token =>
        {
            firstStarted.SetResult();
            using var registration = token.Register(firstCancellationObserved.SetResult);
            await releaseFirst.Task;
            return 1;
        });
        try
        {
            await firstStarted.Task.WaitAsync(synchronizationTimeout);
            await firstCancellationObserved.Task.WaitAsync(synchronizationTimeout);
            var second = RunAsync(_ =>
            {
                secondStarted.SetResult();
                return Task.FromResult(2);
            });

            Assert.False(first.IsCompleted);
            Assert.False(secondStarted.Task.IsCompleted);

            releaseFirst.TrySetResult();
            await Assert.ThrowsAsync<TimeoutException>(() => first);
            Assert.Equal(2, await second);
        }
        finally
        {
            releaseFirst.TrySetResult();
        }

        async Task<int> RunAsync(Func<CancellationToken, Task<int>> operation)
        {
            await gate.WaitAsync();
            try
            {
                return await MultiProviderMetadataService.RunTimedAsync(
                    operation, TimeSpan.FromMilliseconds(50), CancellationToken.None);
            }
            finally
            {
                gate.Release();
            }
        }
    }

    [Fact]
    public async Task Provider_deadline_is_linked_to_the_underlying_operation()
    {
        var cancellationObserved = false;

        await Assert.ThrowsAsync<TimeoutException>(() =>
            MultiProviderMetadataService.RunTimedAsync(
                async token =>
                {
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, token);
                        return 1;
                    }
                    finally
                    {
                        cancellationObserved = token.IsCancellationRequested;
                    }
                },
                TimeSpan.FromMilliseconds(25),
                CancellationToken.None));

        Assert.True(cancellationObserved);
    }
}
