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
        var starts = 0;
        var stopped = new[]
        {
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var abandonment = new CancellationToken[2];
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<Result> Open(int attempt, CancellationToken token, CancellationToken abandon)
        {
            abandonment[attempt] = abandon;
            Interlocked.Increment(ref starts);
            if (attempt == 1) secondStarted.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException error) when (token.IsCancellationRequested)
            {
                Assert.Equal(token, error.CancellationToken);
                stopped[attempt].TrySetResult();
                throw;
            }
            return new(true);
        }
        var opening = PlaybackStartHedge.FirstReadyAsync((token, abandon) => Open(0, token, abandon),
            (token, abandon) => Open(1, token, abandon), item => item.Ready, item => item.Dispose(),
            TimeSpan.FromSeconds(2), clock, cancellation.Token);
        await clock.WaitForTimersAsync(TimeSpan.FromSeconds(2));
        clock.Advance(TimeSpan.FromSeconds(2));
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, Volatile.Read(ref starts));
        Assert.All(stopped, attempt => Assert.False(attempt.Task.IsCompleted));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(TimeSpan.FromSeconds(5)));
        await Task.WhenAll(stopped.Select(attempt => attempt.Task)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(abandonment, token => Assert.False(token.IsCancellationRequested));
        Assert.Equal(2, Volatile.Read(ref starts));
    }

    private sealed class Result(bool ready) : IDisposable
    {
        public bool Ready { get; } = ready;
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Dispose() => Disposed.TrySetResult();
    }
}
