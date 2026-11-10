using allstarr.Core.Capabilities;
using allstarr.Models.Search;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolProviderStreamingGatewayTests
{
    [Fact]
    public async Task SearchStreamDisconnectKeepsTheMergedSearchSharedFetchAlive()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var provider = DelayedSearchProvider("deezer", clock, TimeSpan.Zero);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ProviderOutcome<ProviderPage<ProviderTrackMetadata>>>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken fetchToken = default;
        provider.Setup(item => item.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .Returns((ProviderExecutionContext execution, ProviderMetadataSearchRequest _) =>
            {
                fetchToken = execution.CancellationToken;
                entered.TrySetResult();
                return release.Task;
            });
        var gateway = BudgetGateway(clock, new TestMemoryApplicationCache(), provider.Object);
        var context = Context();
        var merged = gateway.SearchAsync(context, "a", 10, 0, 0);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var stop = new CancellationTokenSource();
        var stream = gateway.StreamSearchAsync(context, "a", 10, true, false, false, false,
            (_, _, _, _) => Task.CompletedTask, stop.Token);
        stop.Cancel();
        Assert.Equal(["deezer"], await stream.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(fetchToken.IsCancellationRequested);
        release.SetResult(ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Success(new("deezer",
            [new(new("deezer", ProviderResourceKind.Track, "shared"), "Track", [new("Artist")])])));
        Assert.Single((await merged.WaitAsync(TimeSpan.FromSeconds(5))).Songs);
        VerifySearchCount(provider, "songs", 1);
    }

    [Fact]
    public async Task SearchStreamEmitsFastProviderBeforeSlowProviderCompletes()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var fast = DelayedSearchProvider("deezer", clock, TimeSpan.Zero);
        var slow = DelayedSearchProvider("qobuz", clock, TimeSpan.Zero);
        var release = new TaskCompletionSource<ProviderOutcome<ProviderPage<ProviderTrackMetadata>>>(TaskCreationOptions.RunContinuationsAsynchronously);
        slow.Setup(item => item.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .Returns(release.Task);
        var emitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var batches = new List<string>();
        var stream = BudgetGateway(clock, new TestMemoryApplicationCache(), fast.Object, slow.Object)
            .StreamSearchAsync(Context(), "a", 10, true, false, false, false, (provider, _, _, _) =>
            { batches.Add(provider); emitted.TrySetResult(); return Task.CompletedTask; });
        await emitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["deezer"], batches);
        Assert.False(stream.IsCompleted);
        release.SetResult(ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Success(new("qobuz", [])));
        Assert.Empty(await stream);
        Assert.Equal(["deezer", "qobuz"], batches);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SearchStreamCancelsPendingProviderAndReportsItLate(bool deadline)
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var provider = DelayedSearchProvider("deezer", clock, TimeSpan.Zero);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.Setup(item => item.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .Returns(async (ProviderExecutionContext context, ProviderMetadataSearchRequest _) =>
            {
                entered.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken); }
                catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
                return ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Success(new("deezer", []));
            });
        using var stop = new CancellationTokenSource();
        var stream = BudgetGateway(clock, new TestMemoryApplicationCache(), provider.Object)
            .StreamSearchAsync(Context(), "a", 10, true, false, false, false, (_, _, _, _) => Task.CompletedTask, stop.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (deadline) clock.Advance(TimeSpan.FromSeconds(10));
        else stop.Cancel();
        Assert.Equal(["deezer"], await stream.WaitAsync(TimeSpan.FromSeconds(5)));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
