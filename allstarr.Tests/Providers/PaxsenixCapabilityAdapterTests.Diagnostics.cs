using System.Net;
using allstarr.Core.Capabilities;
using allstarr.Core.Providers.Lyrics;

namespace allstarr.Tests;

public sealed partial class PaxsenixCapabilityAdapterTests
{
    [Fact]
    public async Task ConnectionProbe_AllowsASlowResponseBeyondThePlaybackRequestBudget()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var http = new HttpClient(new SlowDiagnosticHandler(clock));
        var adapter = new PaxsenixCapabilityAdapter(http, null, new PaxsenixRequestPacer(clock, TimeSpan.Zero), clock, Catalog());
        var pending = adapter.FetchLyricsAsync(Context(operation: "connection-probe", clock: clock), Request());
        await clock.WaitForTimersAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(ProviderLyricsAvailabilityState.Available, (await pending).RequireValue().Availability);
    }

    [Fact]
    public async Task ConnectionProbe_RetriesASongSuppressedByAnEarlierPlaybackTimeout()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var handler = new Handler((_, call) => call == 1 ? Busy() : Response(Lyrics));
        var adapter = new PaxsenixCapabilityAdapter(new HttpClient(handler), null,
            new PaxsenixRequestPacer(clock, TimeSpan.Zero), clock, Catalog());
        Assert.False((await adapter.FetchLyricsAsync(Context(clock: clock), Request())).IsSuccess);
        var result = await adapter.FetchLyricsAsync(Context(operation: "connection-probe", clock: clock), Request());
        Assert.Equal(ProviderLyricsAvailabilityState.Available, result.RequireValue().Availability);
        Assert.Equal(2, handler.Requests.Count);
    }

    private sealed class SlowDiagnosticHandler(TimeProvider clock) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), clock, cancellationToken);
            return Response(Lyrics);
        }
    }
}
