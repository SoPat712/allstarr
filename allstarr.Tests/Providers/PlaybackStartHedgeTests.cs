using allstarr.Core.Music;

namespace allstarr.Tests;

public sealed class PlaybackStartHedgeTests
{
    [Theory]
    [InlineData("FastStart", 2)]
    [InlineData("Balanced", 2)]
    [InlineData("BestQuality", 6)]
    public async Task StartsOnlyOneSourceUntilTheHedgeDelayAndImmediatelyCancelsTheLoser(string mode, int seconds)
    {
        var clock = new ManualTimeProvider();
        var started = 0;
        var abandoned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<Result> First(CancellationToken cancellation, CancellationToken abandon)
        {
            using var registration = abandon.Register(() => abandoned.TrySetResult());
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
            return new(true);
        }
        Task<Result> Second(CancellationToken _, CancellationToken __)
        {
            Interlocked.Increment(ref started);
            return Task.FromResult(new Result(true));
        }
        var opening = PlaybackStartHedge.FirstReadyAsync(First, Second, item => item.Ready, item => item.Dispose(),
            PlaybackStartPolicy.HedgeDelay(mode), clock, CancellationToken.None);
        await clock.WaitForTimersAsync(TimeSpan.FromSeconds(seconds));
        clock.Advance(TimeSpan.FromSeconds(seconds - 1));
        Assert.Equal(0, Volatile.Read(ref started));
        Assert.False(opening.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        using var winner = await opening.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, Volatile.Read(ref started));
        await abandoned.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task FastPrimaryNeverStartsAnotherFetchAndLateLoserResponseIsDisposed()
    {
        var secondCalls = 0;
        var winner = new Result(true);
        var ready = await PlaybackStartHedge.FirstReadyAsync((_, _) => Task.FromResult(winner), (_, _) =>
        {
            secondCalls++;
            return Task.FromResult(new Result(true));
        }, item => item.Ready, item => item.Dispose(), TimeSpan.FromSeconds(2), TimeProvider.System, default);
        Assert.Same(winner, ready);
        Assert.Equal(0, secondCalls);

        var clock = new ManualTimeProvider();
        var late = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
        var opening = PlaybackStartHedge.FirstReadyAsync((_, _) => late.Task, (_, _) => Task.FromResult(winner),
            item => item.Ready, item => item.Dispose(), TimeSpan.FromSeconds(2), clock, default);
        await clock.WaitForTimersAsync(TimeSpan.FromSeconds(2));
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Same(winner, await opening.WaitAsync(TimeSpan.FromSeconds(5)));
        var discarded = new Result(true);
        late.SetResult(discarded);
        await discarded.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(winner.Disposed.Task.IsCompleted);
        winner.Dispose();
    }

    [Fact]
    public async Task RequestCancellationStopsBothAttemptsWithoutMarkingAnOrdinaryAbortAsAbandoned()
    {
        using var cancellation = new CancellationTokenSource();
        var clock = new ManualTimeProvider();
        var cancellations = 0;
        var abandons = 0;
        var starts = 0;
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<Result> Open(CancellationToken token, CancellationToken abandon)
        {
            using var aborted = token.Register(() => Interlocked.Increment(ref cancellations));
            using var discarded = abandon.Register(() => Interlocked.Increment(ref abandons));
            if (Interlocked.Increment(ref starts) == 2) secondStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new(true);
        }
        var opening = PlaybackStartHedge.FirstReadyAsync(Open, Open, item => item.Ready, item => item.Dispose(),
            TimeSpan.FromSeconds(2), clock, cancellation.Token);
        await clock.WaitForTimersAsync(TimeSpan.FromSeconds(2));
        clock.Advance(TimeSpan.FromSeconds(2));
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening);
        Assert.Equal(0, Volatile.Read(ref abandons));
        Assert.Equal(2, Volatile.Read(ref cancellations));
    }

    private sealed class Result(bool ready) : IDisposable
    {
        public bool Ready { get; } = ready;
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Dispose() => Disposed.TrySetResult();
    }
}
