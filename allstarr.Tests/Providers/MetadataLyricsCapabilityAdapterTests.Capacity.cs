using allstarr.Core.Caching;
using System.Net;
using allstarr.Core.Capabilities;
using Moq;

namespace allstarr.Tests;

public sealed partial class MetadataLyricsCapabilityAdapterTests
{
    [Fact]
    public async Task LyricsProbeBypassesBothStoredFailureLayersAndTheBodyCache()
    {
        var clock = new ManualTimeProvider();
        var cache = new Mock<IApplicationCache>();
        var handler = new Handler((_, count) => count == 1 ? new(HttpStatusCode.ServiceUnavailable) : Response("""{"lyrics":"Fresh words"}"""));
        var adapter = Adapter("genius", handler, clock, cache.Object);
        Assert.False((await adapter.FetchLyricsAsync(Context("genius", clock: clock), Request("genius"))).IsSuccess);
        Assert.False((await adapter.FetchLyricsAsync(Context("genius", clock: clock), Request("genius"))).IsSuccess);
        Assert.Single(handler.Requests);
        cache.Setup(item => item.GetAsync<ProviderLyricsResult>(It.IsAny<string>())).ReturnsAsync(
            new ProviderLyricsResult(ProviderLyricsAvailabilityState.Available, "genius", ProviderLyricsFormat.PlainText, "Old words"));
        var context = Context("genius", clock: clock);
        var probe = new ProviderExecutionContext(context.Actor, context.ProviderId, context.Account, context.Policy,
            "connection-probe", context.CorrelationId, context.Deadline, context.CancellationToken);
        var pending = adapter.FetchLyricsAsync(probe, Request("genius"));
        await clock.WaitForTimersAsync(TimeSpan.FromMilliseconds(250));
        clock.Advance(TimeSpan.FromMilliseconds(250));
        Assert.Equal("Fresh words", (await pending.WaitAsync(TimeSpan.FromSeconds(5))).RequireValue().Content);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task LyricsRelayBurstDoesNotRememberLocalQueueExpiryAsAnUpstreamFailure()
    {
        var clock = new ManualTimeProvider();
        var handler = new RelayDelayHandler(clock);
        var adapter = Adapter("genius", handler, clock);
        var requests = Enumerable.Range(0, 20).Select(index => new ProviderLyricsRequest(Guid.NewGuid(),
            new("genius", ProviderResourceKind.Track, "lookup"), trackTitle: "Fixture " + index, artistNames: ["Artist"])).ToArray();
        var pending = requests.Select(request => adapter.FetchLyricsAsync(Context("genius", clock: clock), request)).ToArray();
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(6));
        var results = await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(results, result => Assert.True(result.IsSuccess || result.Error?.Code == "local-capacity-unavailable"));
        var fresh = adapter.FetchLyricsAsync(Context("genius", clock: clock), requests[^1]);
        if (!fresh.IsCompleted)
        {
            await Task.WhenAny(fresh, clock.WaitForTimersAsync(TimeSpan.FromMilliseconds(300))).WaitAsync(TimeSpan.FromSeconds(5));
            clock.Advance(TimeSpan.FromMilliseconds(300));
        }
        Assert.True((await fresh.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
        Assert.InRange(handler.Maximum, 1, 2);
    }

    private sealed class RelayDelayHandler(TimeProvider clock) : HttpMessageHandler
    {
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int active;
        internal int Maximum;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var current = Interlocked.Increment(ref active);
            Maximum = Math.Max(Maximum, current);
            Started.TrySetResult();
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(300), clock, cancellationToken);
                return Response("""{"lyrics":"Fresh words"}""");
            }
            finally { Interlocked.Decrement(ref active); }
        }
    }
}
