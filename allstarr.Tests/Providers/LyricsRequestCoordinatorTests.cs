using allstarr.Core.Capabilities;
using allstarr.Core.Providers.Lyrics;

namespace allstarr.Tests;

public sealed class LyricsRequestCoordinatorTests
{
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
        await coordinator.RunAsync("first", Read, default);
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

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
