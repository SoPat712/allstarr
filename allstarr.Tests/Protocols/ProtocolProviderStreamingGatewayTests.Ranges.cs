using System.Net;
using System.Net.Http.Headers;
using allstarr.Core.Capabilities;
using allstarr.Core.Protocols;
using allstarr.Core.Routing;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolProviderStreamingGatewayTests
{
    [Theory]
    [InlineData("bytes=10-19", 200, null, 10)]
    [InlineData("bytes=10-19", 206, null, 10)]
    [InlineData("bytes=10-19", 206, "bytes 0-9/100", 10)]
    [InlineData("bytes=10-19", 206, "bytes 10-20/100", 11)]
    [InlineData("bytes=10-19", 206, "bytes 10-19/100", 9)]
    [InlineData("bytes=10-19", 206, "items 10-19/100", 10)]
    [InlineData("bytes=-10", 206, "bytes 0-9/100", 10)]
    [InlineData("bytes=-10", 206, "bytes 90-99/*", 10)]
    [InlineData(null, 206, "bytes 10-19/100", 10)]
    public async Task OpenStream_RejectsIgnoredOrMismatchedOffsets(
        string? requested, int status, string? contentRange, int bodyLength)
    {
        var body = new MemoryStream(new byte[bodyLength]);
        var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StreamContent(body) };
        response.Content.Headers.ContentLength = bodyLength;
        if (contentRange != null) response.Content.Headers.ContentRange = ContentRangeHeaderValue.Parse(contentRange);
        var capability = Streaming("qobuz", (_, _) => Task.FromResult(response));
        var gateway = RangeGateway(capability.Object);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => gateway.OpenStreamAsync(
            Context(), "qobuz", "source-track", ProviderAudioQuality.Any, requested));

        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, error.StatusCode);
        Assert.False(body.CanRead);
        capability.Verify(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(),
            It.IsAny<ProviderStreamLeaseRequest>()), Times.Once);
    }

    [Theory]
    [InlineData("bytes=10-19", "bytes 10-19/*", 10)]
    [InlineData("bytes=90-200", "bytes 90-99/100", 10)]
    [InlineData("bytes=-10", "bytes 90-99/100", 10)]
    [InlineData("bytes=-200", "bytes 0-99/100", 100)]
    public async Task OpenStream_AcceptsExactOffsetsAndClampsOnlyAtKnownEnd(
        string requested, string contentRange, int bodyLength)
    {
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
        { Content = new ByteArrayContent(new byte[bodyLength]) };
        response.Content.Headers.ContentRange = ContentRangeHeaderValue.Parse(contentRange);
        var capability = Streaming("qobuz", (_, _) => Task.FromResult(response));
        using var opened = (await RangeGateway(capability.Object).OpenStreamAsync(
            Context(), "qobuz", "source-track", ProviderAudioQuality.Any, requested))!.Response;
        Assert.Equal(contentRange, opened.Content.Headers.ContentRange!.ToString());
        Assert.Equal(bodyLength, (await opened.Content.ReadAsByteArrayAsync()).Length);
    }

    [Theory]
    [InlineData("items=10-19")]
    [InlineData("bytes=-0")]
    [InlineData("bytes=1-2,4-5")]
    public async Task OpenStream_RejectsInvalidByteRequestsBeforeRouting(string range)
    {
        var capability = Streaming("qobuz", (_, _) => throw new InvalidOperationException("Must not open."));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => RangeGateway(capability.Object).OpenStreamAsync(
            Context(), "qobuz", "source-track", ProviderAudioQuality.Any, range));
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, error.StatusCode);
        capability.Verify(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(),
            It.IsAny<ProviderStreamLeaseRequest>()), Times.Never);
    }

    [Fact]
    public async Task OpenStream_NonRangeLeaseNeverRestartsANonzeroSeek()
    {
        var opened = false;
        var lease = new ProviderStreamLease("lease", new Uri("https://media.example.test/track"),
            DateTimeOffset.UtcNow.AddMinutes(1), false, false, new ProviderMediaFormat("audio/flac", "flac", "flac"),
            ProviderStreamRetryBehavior.DoNotRetry, (_, _) =>
            {
                opened = true;
                return Task.FromResult(AudioResponse());
            });
        var capability = Capability("qobuz", ProviderOutcome<ProviderStreamLease>.Success(lease));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => RangeGateway(capability.Object).OpenStreamAsync(
            Context(), "qobuz", "source-track", ProviderAudioQuality.Any, "bytes=10-"));
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, error.StatusCode);
        Assert.False(opened);
    }

    [Fact]
    public async Task OpenStream_InitialZeroRangeCanRemainSequential()
    {
        var capability = Streaming("qobuz", (_, _) => Task.FromResult(AudioResponse()));
        using var response = (await RangeGateway(capability.Object).OpenStreamAsync(
            Context(), "qobuz", "source-track", ProviderAudioQuality.Any, "bytes=0-"))!.Response;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task OpenStream_HeadIgnoresRangeWithoutRequiringAPlaybackSelection()
    {
        string? range = "not-called";
        var capability = Streaming("qobuz", (request, _) =>
        {
            range = request.Headers.Range?.ToString();
            Assert.Equal(HttpMethod.Head, request.Method);
            return Task.FromResult(AudioResponse());
        });
        using var response = (await RangeGateway(capability.Object).OpenStreamAsync(
            ClientContext(), "qobuz", "source-track", ProviderAudioQuality.Any, "bytes=10-", headOnly: true))!.Response;
        Assert.Null(range);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        capability.Verify(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(),
            It.Is<ProviderStreamLeaseRequest>(request => request.RangeStart == null)), Times.Once);
    }

    private static ProtocolProviderGateway RangeGateway(IProviderStreamingCapability capability)
    {
        var registry = Registry(capability);
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        router.Setup(item => item.PlanAsync<IProviderStreamingCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) => Plan(request, registry, capability));
        router.Setup(item => item.EvaluateFallback(It.IsAny<ProviderRoutePlan<IProviderStreamingCapability>>(),
                It.IsAny<int>(), It.IsAny<ProviderError>())).Returns(new ProviderFallbackDecision<IProviderStreamingCapability>(
                    ProviderFallbackDisposition.StopFailure, "stream-range-unavailable", null));
        return new ProtocolProviderGateway(router.Object, registry, new HttpClientFactory());
    }
}
