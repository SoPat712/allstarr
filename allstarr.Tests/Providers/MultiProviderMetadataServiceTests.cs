using allstarr.Services.Common;

namespace allstarr.Tests;

public sealed class MultiProviderMetadataServiceTests
{
    [Fact]
    public async Task Timed_out_work_drains_before_the_concurrency_slot_is_reused()
    {
        using var gate = new SemaphoreSlim(1, 1);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = RunAsync(async _ =>
        {
            firstStarted.SetResult();
            await releaseFirst.Task;
            return 1;
        });
        await firstStarted.Task;
        await Task.Delay(100);
        var second = RunAsync(_ =>
        {
            secondStarted.SetResult();
            return Task.FromResult(2);
        });
        await Task.Delay(50);

        Assert.False(first.IsCompleted);
        Assert.False(secondStarted.Task.IsCompleted);

        releaseFirst.SetResult();
        await Assert.ThrowsAsync<TimeoutException>(() => first);
        Assert.Equal(2, await second);

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
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await Assert.ThrowsAsync<TimeoutException>(() =>
            MultiProviderMetadataService.RunTimedAsync(
                async token =>
                {
                    using var registration = token.Register(cancelled.SetResult);
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return 1;
                },
                TimeSpan.FromMilliseconds(25),
                CancellationToken.None));

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }
}
