using allstarr.Core.Storage;

namespace allstarr.Tests;

public sealed class KeyedAsyncLockTests
{
    [Fact]
    public async Task SameKeySerializesWhileDifferentKeysProceed()
    {
        var locks = new KeyedAsyncLock();
        await using var first = await locks.AcquireAsync("first", CancellationToken.None);
        var waiting = locks.AcquireAsync("first", CancellationToken.None);
        Assert.False(waiting.IsCompleted);
        await using var other = await locks.AcquireAsync("other", CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(waiting.IsCompleted);

        await first.DisposeAsync();
        await using var second = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        var third = locks.AcquireAsync("first", CancellationToken.None);
        await first.DisposeAsync();
        Assert.False(third.IsCompleted);
        await second.DisposeAsync();
        await using var last = await third.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CancellingWaitDoesNotReleaseCurrentOwnerOrBlockNextWaiter()
    {
        var locks = new KeyedAsyncLock();
        await using var owner = await locks.AcquireAsync("key", CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var cancelled = locks.AcquireAsync("key", cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

        var next = locks.AcquireAsync("key", CancellationToken.None);
        Assert.False(next.IsCompleted);
        await owner.DisposeAsync();
        await using var acquired = await next.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
