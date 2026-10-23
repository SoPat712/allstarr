using System.Net;
using System.Collections.Immutable;
using allstarr.Core.Capabilities;
using allstarr.Core.Health;
using allstarr.Core.Protocols;
using allstarr.Core.Routing;
using allstarr.Core.Settings;
using allstarr.Services.Common;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolProviderStreamingGatewayTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StalledSourceStartLeavesTimeForVerifiedFallback(bool ignoresCancellation)
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayed = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken openingToken = default;
        var first = Streaming("deezer", async (_, token) =>
        {
            openingToken = token;
            waiting.TrySetResult();
            return ignoresCancellation ? await delayed.Task : await delayed.Task.WaitAsync(token);
        });
        var second = Streaming("qobuz", (_, _) => Task.FromResult(AudioResponse()));
        var (gateway, _) = FailoverGateway(first, second, clock: clock);
        using var cancellation = new CancellationTokenSource();
        var pending = gateway.OpenStreamAsync(ClientContext(cancellation.Token), "deezer", "source-track", ProviderAudioQuality.Any, null);
        try
        {
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await clock.WaitForTimersAsync(TimeSpan.FromSeconds(15));
            clock.Advance(TimeSpan.FromSeconds(15));
            var opened = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            using var response = Assert.IsType<ProtocolProviderStream>(opened).Response;
            Assert.Equal("qobuz", opened.ServingProviderId);
            Assert.True(openingToken.IsCancellationRequested);
            var late = new ObservedContent([1, 2]);
            delayed.TrySetResult(new(HttpStatusCode.OK) { Content = late });
            if (ignoresCancellation)
                await Task.Run(async () => { while (!late.Disposed) await Task.Yield(); }).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            cancellation.Cancel();
            delayed.TrySetResult(AudioResponse());
            try { (await pending)?.Response.Dispose(); } catch (OperationCanceledException) { }
        }
    }

    [Theory]
    [InlineData("<html>Server busy</html>")]
    [InlineData("  <!DOCTYPE html><html>Verification required</html>")]
    [InlineData("{\"error\":\"Server busy\"}")]
    public async Task MislabeledErrorBodyFailsOverBeforeHeaders(string body)
    {
        var first = Streaming("deezer", (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(body)) }));
        var second = Streaming("qobuz", (_, _) => Task.FromResult(AudioResponse()));
        var (gateway, _) = FailoverGateway(first, second);
        using var response = (await gateway.OpenStreamAsync(Context(), "deezer", "source-track", ProviderAudioQuality.Any, null))!.Response;
        Assert.Equal("qobuz", Assert.Single(response.Headers.GetValues("X-Allstarr-Provider")));
    }

    [Fact]
    public async Task FailedItemIsSkippedBrieflyWithoutDisablingOtherItems()
    {
        var first = Streaming("deezer", (_, _) => Task.FromResult(AudioResponse(HttpStatusCode.BadGateway)));
        var second = Streaming("qobuz", (_, _) => Task.FromResult(AudioResponse()));
        var (gateway, _) = FailoverGateway(first, second, outcomes: new ProviderRuntimeHealth());
        var context = Context();
        for (var index = 0; index < 2; index++)
        {
            using var response = (await gateway.OpenStreamAsync(context, "deezer", "source-track", ProviderAudioQuality.Any, null))!.Response;
            Assert.Equal("qobuz", Assert.Single(response.Headers.GetValues("X-Allstarr-Provider")));
        }
        first.Verify(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(),
            It.IsAny<ProviderStreamLeaseRequest>()), Times.Once);
        using var other = (await gateway.OpenStreamAsync(context, "deezer", "other", ProviderAudioQuality.Any, null))!.Response;
        first.Verify(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(),
            It.IsAny<ProviderStreamLeaseRequest>()), Times.Exactly(2));
    }

    [Fact]
    public async Task SubsonicApplicationIdentityKeepsTheFallbackEncodingAcrossRanges()
    {
        using var activity = new PlaybackDeliveryActivityStore();
        var first = Streaming("deezer", (_, _) => Task.FromResult(AudioResponse(HttpStatusCode.BadGateway)));
        var second = Streaming("qobuz", (request, _) =>
        {
            if (request.Headers.Range == null) return Task.FromResult(AudioResponse());
            Assert.Equal("bytes=1-", request.Headers.Range.ToString());
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent([2, 3, 4]) };
            response.Content.Headers.ContentRange = new(1, 3, 4);
            return Task.FromResult(response);
        });
        var original = Context();
        var context = new ProtocolExecutionContext(ProtocolKind.Subsonic, original.BackendInstanceId,
            original.VerifiedBackendPrincipalId, original.Principal! with { BackendType = "subsonic" }, original.CorrelationId,
            original.Deadline, default, new ProtocolClientDescriptor("test-client"));
        var (gateway, _) = FailoverGateway(first, second, activity);
        using var opened = (await gateway.OpenStreamAsync(context, "deezer", "source-track", ProviderAudioQuality.Any, null))!.Response;
        using var resumed = (await gateway.OpenStreamAsync(context, "deezer", "source-track", ProviderAudioQuality.Any, "bytes=1-"))!.Response;
        Assert.Equal([2, 3, 4], await resumed.Content.ReadAsByteArrayAsync());
        first.Verify(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderStreamLeaseRequest>()), Times.Once);
    }

    [Theory]
    [InlineData(ProviderUseMode.ManualOnly)]
    [InlineData(ProviderUseMode.Off)]
    public async Task AutomaticFailoverNeverUsesAManualOrDisabledSource(ProviderUseMode mode)
    {
        var first = Streaming("deezer", (_, _) => Task.FromResult(AudioResponse(HttpStatusCode.BadGateway)));
        var second = Streaming("qobuz", (_, _) => Task.FromResult(AudioResponse()));
        var (gateway, _) = FailoverGateway(first, second);
        var context = Context();
        context = context with
        {
            Policy = new(ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty
                .Add(ProviderCapabilityKind.Streaming, ["deezer", "qobuz"]), ImmutableHashSet<string>.Empty, AudioQualityPolicy.DefaultStep, .07)
            { UserId = context.RequireActor().EffectiveUserId, ProviderModes = ImmutableDictionary<string, ProviderUseMode>.Empty.Add("qobuz", mode) }
        };
        try { (await gateway.OpenStreamAsync(context, "deezer", "source-track", ProviderAudioQuality.Any, null))?.Response.Dispose(); }
        catch (HttpRequestException) { }
        second.Verify(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(),
            It.IsAny<ProviderStreamLeaseRequest>()), Times.Never);
    }

    [Theory]
    [InlineData("\"same\"", true)]
    [InlineData(null, false)]
    [InlineData("W/\"same\"", false)]
    [InlineData("\"changed\"", false)]
    public async Task MidstreamRecoveryRequiresAnIdenticalEntity(string? replacementTag, bool recovers)
    {
        var requests = 0;
        var initial = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new InterruptingAudio()) };
        initial.Content.Headers.ContentLength = 8;
        if (replacementTag != null) initial.Headers.ETag = System.Net.Http.Headers.EntityTagHeaderValue.Parse(
            replacementTag == "\"changed\"" ? "\"same\"" : replacementTag);
        var first = Streaming("deezer", (request, _) =>
        {
            if (++requests == 1) return Task.FromResult(initial);
            Assert.Equal("bytes=4-", request.Headers.Range?.ToString());
            Assert.Equal("\"same\"", request.Headers.IfRange?.ToString());
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent([5, 6, 7, 8]) };
            response.Headers.ETag = System.Net.Http.Headers.EntityTagHeaderValue.Parse(replacementTag!);
            response.Content.Headers.ContentRange = new(4, 7, 8);
            return Task.FromResult(response);
        });
        var second = Streaming("qobuz", (_, _) => Task.FromResult(AudioResponse()));
        var (gateway, _) = FailoverGateway(first, second, outcomes: new ProviderRuntimeHealth());
        var context = Context();
        using var response = (await gateway.OpenStreamAsync(context, "deezer", "source-track", ProviderAudioQuality.Any, null))!.Response;
        if (recovers) Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], await response.Content.ReadAsByteArrayAsync());
        else
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => response.Content.ReadAsByteArrayAsync());
            using var retry = (await gateway.OpenStreamAsync(context, "deezer", "source-track", ProviderAudioQuality.Any, null))!.Response;
            Assert.Equal("qobuz", Assert.Single(retry.Headers.GetValues("X-Allstarr-Provider")));
        }
        Assert.Equal(replacementTag is "\"same\"" or "\"changed\"" ? 2 : 1, requests);
    }

    [Fact]
    public async Task FailoverStopsAfterFourSourceAttempts()
    {
        var sources = Enumerable.Range(0, 6).Select(index => Streaming($"source-{index}",
            (_, _) => Task.FromResult(AudioResponse(HttpStatusCode.BadGateway)))).ToArray();
        var registry = Registry(sources.Select(item => item.Object).ToArray());
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        router.Setup(item => item.PlanAsync<IProviderStreamingCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) => Plan(request, registry, sources.Select(item => item.Object).ToArray()));
        router.Setup(item => item.EvaluateFallback(It.IsAny<ProviderRoutePlan<IProviderStreamingCapability>>(),
            It.IsAny<int>(), It.IsAny<ProviderError>())).Returns(new ProviderFallbackDecision<IProviderStreamingCapability>(
                ProviderFallbackDisposition.Advance, "fallback-transient-failure", null));
        var gateway = new ProtocolProviderGateway(router.Object, registry, new HttpClientFactory());
        await Assert.ThrowsAsync<HttpRequestException>(() => gateway.OpenStreamAsync(Context(), "source-0", "track", ProviderAudioQuality.Any, null));
        for (var index = 0; index < sources.Length; index++)
            sources[index].Verify(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(),
                It.IsAny<ProviderStreamLeaseRequest>()), index < 4 ? Times.Once() : Times.Never());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StalledAudioIsBoundedAndRemembersTheFailedItem(bool midstream)
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var source = new StallingAudio(midstream);
        var first = Streaming("deezer", (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(source) }));
        var second = Streaming("qobuz", (_, _) => Task.FromResult(AudioResponse()));
        var (gateway, _) = FailoverGateway(first, second, outcomes: new ProviderRuntimeHealth(), clock: clock);
        using var cancellation = new CancellationTokenSource();
        var context = ClientContext(cancellation.Token);
        var opening = gateway.OpenStreamAsync(context, "deezer", "source-track", ProviderAudioQuality.Any, null);
        try
        {
            if (midstream)
            {
                using var response = (await opening)!.Response;
                var reading = response.Content.ReadAsByteArrayAsync();
                await source.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await clock.WaitForTimersAsync(TimeSpan.FromSeconds(8));
                clock.Advance(TimeSpan.FromSeconds(8));
                await Assert.ThrowsAsync<HttpRequestException>(() => reading.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            else
            {
                await source.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await clock.WaitForTimersAsync(TimeSpan.FromSeconds(15));
                clock.Advance(TimeSpan.FromSeconds(15));
                using var response = (await opening.WaitAsync(TimeSpan.FromSeconds(5)))!.Response;
                Assert.Equal("qobuz", Assert.Single(response.Headers.GetValues("X-Allstarr-Provider")));
            }
            using var retry = (await gateway.OpenStreamAsync(context, "deezer", "source-track", ProviderAudioQuality.Any, null))!.Response;
            Assert.Equal("qobuz", Assert.Single(retry.Headers.GetValues("X-Allstarr-Provider")));
            first.Verify(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderStreamLeaseRequest>()), Times.Once);
        }
        finally { cancellation.Cancel(); }
    }

    [Fact]
    public async Task ACancelledReadCannotOverwriteBytesFromTheResumedResponse()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var source = new LateAudio();
        var first = Streaming("deezer", (request, _) =>
        {
            var response = new HttpResponseMessage(request.Headers.Range == null ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
            { Content = request.Headers.Range == null ? new StreamContent(source) : new ByteArrayContent([5, 6, 7, 8]) };
            response.Headers.ETag = new("\"same\"");
            response.Content.Headers.ContentLength = request.Headers.Range == null ? 8 : 4;
            if (request.Headers.Range != null) response.Content.Headers.ContentRange = new(4, 7, 8);
            return Task.FromResult(response);
        });
        var second = Streaming("qobuz", (_, _) => Task.FromResult(AudioResponse()));
        var (gateway, _) = FailoverGateway(first, second, clock: clock);
        using var response = (await gateway.OpenStreamAsync(Context(), "deezer", "source-track", ProviderAudioQuality.Any, null))!.Response;
        var stream = await response.Content.ReadAsStreamAsync();
        var buffer = new byte[4];
        Assert.Equal(4, await stream.ReadAsync(buffer));
        var reading = stream.ReadAsync(buffer).AsTask();
        await source.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await clock.WaitForTimersAsync(TimeSpan.FromSeconds(8));
        clock.Advance(TimeSpan.FromSeconds(8));
        Assert.Equal(4, await reading.WaitAsync(TimeSpan.FromSeconds(5)));
        source.Complete();
        Assert.Equal([5, 6, 7, 8], buffer);
    }

    [Fact]
    public async Task TruncatedChunkedRangeIsRememberedBeforeTheNextPlaybackRequest()
    {
        var first = Streaming("deezer", (_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            { Content = new StreamContent(new UnseekableAudio([1, 2, 3, 4])) };
            response.Content.Headers.ContentRange = new(0, 7, 8);
            return Task.FromResult(response);
        });
        var second = Streaming("qobuz", (_, _) => Task.FromResult(AudioResponse()));
        var (gateway, _) = FailoverGateway(first, second, outcomes: new ProviderRuntimeHealth());
        var context = Context();
        using var response = (await gateway.OpenStreamAsync(context, "deezer", "source-track", ProviderAudioQuality.Any, "bytes=0-"))!.Response;
        await Assert.ThrowsAsync<HttpRequestException>(() => response.Content.ReadAsByteArrayAsync());
        using var retry = (await gateway.OpenStreamAsync(context, "deezer", "source-track", ProviderAudioQuality.Any, null))!.Response;
        Assert.Equal("qobuz", Assert.Single(retry.Headers.GetValues("X-Allstarr-Provider")));
        first.Verify(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderStreamLeaseRequest>()), Times.Once);
    }

    private sealed class UnseekableAudio(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    private sealed class LateAudio : MemoryStream
    {
        private bool first = true;
        private Memory<byte> pending;
        private readonly TaskCompletionSource<int> completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (first)
            {
                first = false;
                new byte[] { 1, 2, 3, 4 }.AsMemory().CopyTo(buffer);
                return ValueTask.FromResult(4);
            }
            pending = buffer;
            Waiting.TrySetResult();
            return new(completed.Task);
        }
        public void Complete()
        {
            pending.Span[..4].Fill(9);
            completed.TrySetResult(4);
        }
    }

    private sealed class StallingAudio(bool prefix) : MemoryStream
    {
        public override bool CanSeek => false;
        private bool first = true;
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (first && prefix)
            {
                first = false;
                buffer.Span[0] = 1;
                return 1;
            }
            Waiting.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private sealed class InterruptingAudio : MemoryStream
    {
        private bool prefetched;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (prefetched) throw new IOException("upstream interrupted");
            prefetched = true;
            new byte[] { 1, 2, 3, 4 }.AsMemory().CopyTo(buffer);
            return ValueTask.FromResult(4);
        }
    }
}
