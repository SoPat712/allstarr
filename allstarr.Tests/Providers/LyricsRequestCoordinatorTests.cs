using allstarr.Core.Capabilities;
using allstarr.Core.Providers.Lyrics;

namespace allstarr.Tests;

public sealed class LyricsRequestCoordinatorTests
{
    [Fact]
    public async Task LyricsProbeDoesNotJoinAnOrdinaryLookupThatFailsWhileItWaits()
    {
        var coordinator = new LyricsRequestCoordinator(TimeProvider.System, TimeSpan.Zero);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        async Task<ProviderOutcome<ProviderLyricsResult>> Read(CancellationToken token)
        {
            if (++calls == 1)
            {
                entered.SetResult();
                await release.Task.WaitAsync(token);
                return ProviderOutcome<ProviderLyricsResult>.Failure(new(ProviderErrorKind.TransientFailure));
            }
            return ProviderOutcome<ProviderLyricsResult>.Success(new(ProviderLyricsAvailabilityState.Unavailable, "fixture"));
        }
        var ordinary = coordinator.RunAsync("song", Read, default);
        await entered.Task;
        var probe = coordinator.RunAsync("song", Read, default, refresh: true);
        release.SetResult();
        Assert.False((await ordinary).IsSuccess);
        Assert.True((await probe).IsSuccess);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ConcurrentLookupsShareCachedResultAndKeepOneRequestInFlight()
    {
        var coordinator = new LyricsRequestCoordinator(TimeProvider.System, TimeSpan.Zero);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        async Task<ProviderOutcome<ProviderLyricsResult>> Read(CancellationToken token)
        {
            calls++;
            entered.SetResult();
            await release.Task.WaitAsync(token);
            return ProviderOutcome<ProviderLyricsResult>.Success(new(ProviderLyricsAvailabilityState.Available,
                "fixture", ProviderLyricsFormat.PlainText, "Synthetic words"));
        }
        var first = coordinator.RunAsync("song", Read, default);
        await entered.Task;
        var second = coordinator.RunAsync("song", Read, default);
        release.SetResult();
        Assert.Equal("Synthetic words", (await first).RequireValue().Content);
        Assert.Equal("Synthetic words", (await second).RequireValue().Content);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(ProviderErrorKind.RateLimited)]
    [InlineData(ProviderErrorKind.AccountNeedsReauthentication)]
    [InlineData(ProviderErrorKind.TransientFailure)]
    public async Task UpstreamFailuresBackOffAcrossDifferentSongs(ProviderErrorKind kind)
    {
        var time = new Clock();
        var coordinator = new LyricsRequestCoordinator(time, TimeSpan.Zero);
        var calls = 0;
        Task<ProviderOutcome<ProviderLyricsResult>> Read(CancellationToken token)
        {
            calls++;
            return Task.FromResult(ProviderOutcome<ProviderLyricsResult>.Failure(new(kind, TimeSpan.FromMinutes(2))));
        }
        Assert.Equal(kind, (await coordinator.RunAsync("first", Read, default)).Error?.Kind);
        Assert.Equal(kind, (await coordinator.RunAsync("second", Read, default)).Error?.Kind);
        Assert.Equal(1, calls);
        time.Now += TimeSpan.FromMinutes(6);
        await coordinator.RunAsync("second", Read, default);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task CanceledWaiterDoesNotSendAnotherRequest()
    {
        var coordinator = new LyricsRequestCoordinator(TimeProvider.System, TimeSpan.Zero);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.RunAsync("song",
            _ => throw new InvalidOperationException(), canceled.Token));
    }

    [Fact]
    public async Task TwentyDistinctLookupsUseTwoSpacedSlotsAndDoNotRememberLocalCapacityFailures()
    {
        var clock = new ManualTimeProvider();
        var coordinator = new LyricsRequestCoordinator(clock, TimeSpan.FromMilliseconds(250), concurrency: 2);
        var started = new List<DateTimeOffset>();
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maximum = 0;
        var calls = 0;
        async Task<ProviderOutcome<ProviderLyricsResult>> Read(CancellationToken token)
        {
            lock (started)
            {
                calls++;
                started.Add(clock.GetUtcNow());
                maximum = Math.Max(maximum, ++active);
                if (calls == 2) secondStarted.SetResult();
            }
            await Task.Delay(TimeSpan.FromMilliseconds(300), clock, CancellationToken.None);
            lock (started) active--;
            return Words();
        }
        var cancellations = Enumerable.Range(0, 20).Select(_ => new CancellationTokenSource()).ToArray();
        try
        {
            var requests = cancellations.Select((source, index) => coordinator.RunAsync($"song-{index}", Read, source.Token)).ToArray();
            await clock.WaitForTimersAsync(TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(250));
            clock.Advance(TimeSpan.FromMilliseconds(250));
            await secondStarted.Task;
            await clock.WaitForTimersAsync(TimeSpan.FromMilliseconds(300));
            foreach (var source in cancellations.Skip(2)) source.Cancel();
            var busy = await Task.WhenAll(requests.Skip(2));
            Assert.All(busy, result => Assert.Equal(ProviderError.LocalCapacity(), result.Error));
            clock.Advance(TimeSpan.FromMilliseconds(50));
            Assert.True((await requests[0]).IsSuccess);
            clock.Advance(TimeSpan.FromMilliseconds(250));
            Assert.True((await requests[1]).IsSuccess);
            Assert.Equal(2, calls);
            Assert.Equal(2, maximum);
            Assert.Equal(TimeSpan.FromMilliseconds(250), started[1] - started[0]);
            var retried = await coordinator.RunAsync("song-2", _ => { calls++; return Task.FromResult(Words()); }, default);
            Assert.True(retried.IsSuccess);
            Assert.Equal(3, calls);
        }
        finally { foreach (var source in cancellations) source.Dispose(); }
    }

    [Fact]
    public async Task CanceledSpacingDoesNotReserveAStartOrRememberAResult()
    {
        var clock = new ManualTimeProvider();
        var coordinator = new LyricsRequestCoordinator(clock, TimeSpan.FromMilliseconds(250), concurrency: 2);
        var calls = 0;
        Task<ProviderOutcome<ProviderLyricsResult>> Read(CancellationToken token)
        {
            calls++;
            return Task.FromResult(Words());
        }
        await coordinator.RunAsync("first", Read, default);
        using var cancellation = new CancellationTokenSource();
        var waiting = coordinator.RunAsync("second", Read, cancellation.Token);
        await clock.WaitForTimersAsync(TimeSpan.FromMilliseconds(250));
        cancellation.Cancel();
        Assert.Equal(ProviderError.LocalCapacity(), (await waiting).Error);
        clock.Advance(TimeSpan.FromMilliseconds(250));
        Assert.True((await coordinator.RunAsync("second", Read, default)).IsSuccess);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task RefreshBypassesBothRememberedSuccessAndGlobalUpstreamBackoff()
    {
        var coordinator = new LyricsRequestCoordinator(new ManualTimeProvider(), TimeSpan.Zero,
            failureCooldown: TimeSpan.FromMinutes(2));
        var calls = 0;
        Task<ProviderOutcome<ProviderLyricsResult>> Failed(CancellationToken token)
        {
            calls++;
            return Task.FromResult(ProviderOutcome<ProviderLyricsResult>.Failure(ProviderError.LyricsUpstreamBusyOrTimedOut()));
        }
        Task<ProviderOutcome<ProviderLyricsResult>> Read(CancellationToken token)
        {
            calls++;
            return Task.FromResult(Words());
        }
        await coordinator.RunAsync("first", Failed, default);
        Assert.Equal(ProviderError.LocalCapacity(), (await coordinator.RunAsync("second", Read, default)).Error);
        Assert.Equal(1, calls);
        Assert.True((await coordinator.RunAsync("second", Read, default, refresh: true)).IsSuccess);
        Assert.Equal(2, calls);
        Assert.True((await coordinator.RunAsync("second", Read, default)).IsSuccess);
        Assert.Equal(2, calls);
        Assert.True((await coordinator.RunAsync("second", Read, default, refresh: true)).IsSuccess);
        Assert.Equal(3, calls);
        Assert.True((await coordinator.RunAsync("third", Read, default)).IsSuccess);
        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task GeniusUpstreamBackoffExpiresAfterTwoMinutesAcrossSongs()
    {
        var clock = new ManualTimeProvider();
        var coordinator = new LyricsRequestCoordinator(clock, TimeSpan.Zero, concurrency: 2,
            failureCooldown: TimeSpan.FromMinutes(2));
        var calls = 0;
        Task<ProviderOutcome<ProviderLyricsResult>> Read(CancellationToken token)
        {
            calls++;
            return Task.FromResult(ProviderOutcome<ProviderLyricsResult>.Failure(
                new(ProviderErrorKind.RateLimited, TimeSpan.FromMinutes(5))));
        }
        Assert.Equal(ProviderErrorKind.RateLimited, (await coordinator.RunAsync("first", Read, default)).Error?.Kind);
        clock.Advance(TimeSpan.FromMinutes(2) - TimeSpan.FromMilliseconds(1));
        Assert.Equal(ProviderError.LocalCapacity(), (await coordinator.RunAsync("second", Read, default)).Error);
        Assert.Equal(1, calls);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(ProviderErrorKind.RateLimited, (await coordinator.RunAsync("second", Read, default)).Error?.Kind);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task DistinctLookupsNeverExceedConfiguredConcurrency(int concurrency)
    {
        var coordinator = new LyricsRequestCoordinator(new ManualTimeProvider(), TimeSpan.Zero, concurrency);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        async Task<ProviderOutcome<ProviderLyricsResult>> Read(CancellationToken token)
        {
            calls++;
            await release.Task;
            return Words();
        }
        var active = Enumerable.Range(0, concurrency).Select(index => coordinator.RunAsync($"song-{index}", Read, default)).ToArray();
        using var cancellation = new CancellationTokenSource();
        var queued = coordinator.RunAsync("queued", Read, cancellation.Token);
        Assert.Equal(concurrency, calls);
        cancellation.Cancel();
        Assert.Equal(ProviderError.LocalCapacity(), (await queued).Error);
        release.SetResult();
        Assert.All(await Task.WhenAll(active), result => Assert.True(result.IsSuccess));
        Assert.Equal(concurrency, calls);
    }

    [Fact]
    public async Task CancellationInsideTheHttpCallbackPropagatesWithoutBackoff()
    {
        var coordinator = new LyricsRequestCoordinator(new ManualTimeProvider(), TimeSpan.Zero);
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = coordinator.RunAsync("first", async token =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Words();
        }, cancellation.Token);
        await entered.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.True((await coordinator.RunAsync("second", _ => Task.FromResult(Words()), default)).IsSuccess);
    }

    [Fact]
    public async Task CancelingASharedWaiterDoesNotCancelOrReplaceTheActiveResult()
    {
        var coordinator = new LyricsRequestCoordinator(new ManualTimeProvider(), TimeSpan.Zero, concurrency: 2);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        async Task<ProviderOutcome<ProviderLyricsResult>> Read(CancellationToken token)
        {
            calls++;
            await release.Task.WaitAsync(token);
            return Words();
        }
        var first = coordinator.RunAsync("song", Read, default);
        using var cancellation = new CancellationTokenSource();
        var shared = coordinator.RunAsync("song", Read, cancellation.Token);
        cancellation.Cancel();
        Assert.Equal(ProviderError.LocalCapacity(), (await shared).Error);
        Assert.False(first.IsCompleted);
        release.SetResult();
        Assert.True((await first).IsSuccess);
        Assert.True((await coordinator.RunAsync("song", Read, default)).IsSuccess);
        Assert.Equal(1, calls);
    }

    private static ProviderOutcome<ProviderLyricsResult> Words() => ProviderOutcome<ProviderLyricsResult>.Success(
        new(ProviderLyricsAvailabilityState.Available, "fixture", ProviderLyricsFormat.PlainText, "Synthetic words"));

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
