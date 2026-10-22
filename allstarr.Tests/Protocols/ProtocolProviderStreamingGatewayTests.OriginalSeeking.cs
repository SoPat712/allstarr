using System.Net;
using allstarr.Core.Capabilities;
using allstarr.Core.Protocols;
using allstarr.Core.Routing;
using allstarr.Services.Common;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolProviderStreamingGatewayTests
{
    [Fact]
    public async Task CanonicalOriginalDownloadCannotBorrowAPlaybackSelection()
    {
        using var activity = new PlaybackDeliveryActivityStore();
        var canonical = Guid.NewGuid().ToString("D");
        var context = ClientContext() with { RequestedItemId = $"ext-allstarr-song-{canonical}" };
        var source = Streaming("deezer", (_, _) => Task.FromResult(AudioResponse()));
        var unused = Streaming("qobuz", (_, _) => throw new InvalidOperationException("Unexpected source."));
        var (gateway, _) = FailoverGateway(source, unused, activity);
        using var initial = (await gateway.OpenStreamAsync(context, "allstarr", canonical, ProviderAudioQuality.Any, null))!.Response;
        Assert.NotNull(activity.StreamFor(context, context.RequestedItemId, ProviderAudioQuality.Any));
        var original = context with { IsOriginalDownload = true };
        Assert.Null(activity.StreamFor(original, context.RequestedItemId, ProviderAudioQuality.Any));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => gateway.OpenStreamAsync(original,
            "allstarr", canonical, ProviderAudioQuality.Any, "bytes=1-"));
        Assert.Equal(HttpStatusCode.Conflict, error.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanonicalOriginalDownloadKeepsItsSourceAcrossConcurrentRangesWithoutRecordingPlayback(bool foreignDevice)
    {
        using var activity = new PlaybackDeliveryActivityStore();
        var canonical = Guid.NewGuid().ToString("D");
        var context = ClientContext() with { IsOriginalDownload = true, RequestedItemId = $"ext-allstarr-song-{canonical}" };
        var first = Streaming("deezer", (_, _) => Task.FromResult(AudioResponse(HttpStatusCode.NotFound)));
        var ranges = 0;
        var selected = Streaming("qobuz", (request, _) =>
        {
            if (request.Headers.Range == null) return Task.FromResult(AudioResponse());
            Interlocked.Increment(ref ranges);
            var from = request.Headers.Range.Ranges.Single().From!.Value;
            var end = request.Headers.Range.Ranges.Single().To ?? 3;
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            { Content = new ByteArrayContent(new byte[] { 1, 2, 3, 4 }[(int)from..((int)end + 1)]) };
            response.Content.Headers.ContentRange = new(from, end, 4);
            return Task.FromResult(response);
        });
        var (gateway, router) = FailoverGateway(first, selected, activity);
        using (var initial = (await gateway.OpenStreamAsync(context, "allstarr", canonical, ProviderAudioQuality.Any, null))!.Response)
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, await initial.Content.ReadAsByteArrayAsync());
        Assert.Null(activity.StreamFor(context.Actor!.EffectiveUserId, "device", context.RequestedItemId));
        Assert.Null(activity.StreamFor(context with { IsOriginalDownload = false }, context.RequestedItemId, ProviderAudioQuality.Any));
        Assert.False(activity.WasStreamOpened(context.RequestedItemId));
        if (foreignDevice)
        {
            var other = new ProtocolExecutionContext(context.Protocol, context.BackendInstanceId,
                context.VerifiedBackendPrincipalId, context.Principal, context.CorrelationId, context.Deadline,
                context.CancellationToken, new("client", "other"))
            { IsOriginalDownload = true, RequestedItemId = context.RequestedItemId };
            var error = await Assert.ThrowsAsync<HttpRequestException>(() => gateway.OpenStreamAsync(other,
                "allstarr", canonical, ProviderAudioQuality.Any, "bytes=1-"));
            Assert.Equal(HttpStatusCode.Conflict, error.StatusCode);
            Assert.Equal(0, ranges);
            return;
        }
        var opening = await Task.WhenAll(gateway.OpenStreamAsync(context, "allstarr", canonical, ProviderAudioQuality.Any, "bytes=1-2"),
            gateway.OpenStreamAsync(context, "allstarr", canonical, ProviderAudioQuality.Any, "bytes=3-"));
        using var one = opening[0]!.Response;
        using var two = opening[1]!.Response;
        Assert.Equal(HttpStatusCode.PartialContent, one.StatusCode);
        Assert.Equal("bytes 1-2/4", one.Content.Headers.ContentRange!.ToString());
        Assert.Equal(new byte[] { 2, 3 }, await one.Content.ReadAsByteArrayAsync());
        Assert.Equal(new byte[] { 4 }, await two.Content.ReadAsByteArrayAsync());
        Assert.Equal(2, ranges);
        router.Verify(item => item.PlanAsync<IProviderStreamingCapability>(It.Is<ProviderRouteRequest>(request =>
            request.ProviderPriority.SequenceEqual(new[] { "qobuz" }) && request.SourceTrackId!.Value == "qobuz-track")), Times.Exactly(2));
        Assert.Null(activity.StreamFor(context.Actor.EffectiveUserId, "device", context.RequestedItemId));
        Assert.False(activity.WasStreamOpened(context.RequestedItemId));
    }
}
