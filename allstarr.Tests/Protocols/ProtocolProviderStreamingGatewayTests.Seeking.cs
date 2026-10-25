using allstarr.Core.Capabilities;
using allstarr.Core.Protocols;
using allstarr.Core.Routing;
using allstarr.Core.Storage;
using allstarr.Services.Common;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolProviderStreamingGatewayTests
{
    [Theory]
    [InlineData("shared", 1)]
    [InlineData("personal", 2)]
    [InlineData("revision", 2)]
    [InlineData("quality", 2)]
    [InlineData("purpose", 2)]
    public async Task ConcurrentListenersShareOnlyTheSameAuthorizedAccountRevisionQualityAndPurpose(string separation, int fetches)
    {
        var shared = Guid.NewGuid();
        var personal = Guid.NewGuid();
        var firstContext = Context();
        var secondUser = Guid.NewGuid();
        var secondContext = new ProtocolExecutionContext(firstContext.Protocol, firstContext.BackendInstanceId, "other-listener",
            new allstarr.Core.Identity.AllstarrPrincipal(secondUser, "jellyfin", firstContext.BackendInstanceId, "other-listener", "Listener", false),
            "other-request", firstContext.Deadline, default);
        allstarr.Core.Capabilities.ProviderAccountContext Account(allstarr.Core.Routing.ProviderRouteRequest request)
        {
            var second = request.Actor.EffectiveUserId == secondUser;
            return new(second && separation == "personal" ? personal : shared, "deezer",
                separation == "personal" ? ProviderAccountScope.Personal : ProviderAccountScope.Shared,
                second && separation == "revision" ? 2 : 1,
                ownerUserId: separation == "personal" ? request.Actor.EffectiveUserId : null, secretReferenceId: Guid.NewGuid());
        }
        await using var fixture = new PreparationStreamFixture(false, "deezer", account: Account);
        var first = fixture.Gateway.OpenStreamAsync(firstContext, "deezer", "next", ProviderAudioQuality.Any, null);
        await fixture.Opening.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (separation == "purpose") secondContext = secondContext with { IsOriginalDownload = true };
        var second = fixture.Gateway.OpenStreamAsync(secondContext, "deezer", "next",
            separation == "quality" ? ProviderAudioQuality.Lossless : ProviderAudioQuality.Any, null);
        fixture.OpenResponse.TrySetResult();
        using var one = (await first.WaitAsync(TimeSpan.FromSeconds(10)))!.Response;
        using var two = (await second.WaitAsync(TimeSpan.FromSeconds(10)))!.Response;
        Assert.Equal(fetches, fixture.Fetches);
        fixture.Source.Release.TrySetResult();
        Assert.Equal(fixture.Source.Bytes, await one.Content.ReadAsByteArrayAsync());
        Assert.Equal(fixture.Source.Bytes, await two.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SeekOpeningHasASmallDeadlineIncludingDownloadBackedSources(bool downloadBacked)
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var capability = Streaming("qobuz", async (_, token) =>
        {
            waiting.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return AudioResponse();
        });
        if (downloadBacked) capability.As<IProviderDownloadStreamingCapability>();
        using var cancellation = new CancellationTokenSource();
        var pending = RangeGateway(capability.Object, clock).OpenStreamAsync(ClientContext(cancellation.Token),
            "qobuz", "source-track", ProviderAudioQuality.Any, "bytes=10-");
        try
        {
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(TimeSpan.FromSeconds(8), await clock.NextTimerAsync());
            clock.Advance(TimeSpan.FromSeconds(8));
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(504, allstarr.Controllers.JellyfinController.MapExternalStreamException(error).statusCode);
            Assert.Equal(504, allstarr.Filters.SubsonicExceptionFilter.Map(error).StatusCode);
        }
        finally
        {
            cancellation.Cancel();
            try { (await pending)?.Response.Dispose(); }
            catch (OperationCanceledException) { }
        }
    }

    [Theory]
    [InlineData("apple-musickit")]
    [InlineData("apple-download")]
    [InlineData("deezer")]
    [InlineData("monochrome")]
    [InlineData("spotiflac-sample")]
    public async Task ConcurrentColdPlaybackSharesOneFillForEveryProvider(string provider)
    {
        var registered = provider == "apple-download" ? "apple-musickit" : provider;
        await using var fixture = new PreparationStreamFixture(false, registered);
        var context = Context();
        var first = fixture.Gateway.OpenStreamAsync(context, provider, "next", ProviderAudioQuality.Any, null);
        await fixture.Opening.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = fixture.Gateway.OpenStreamAsync(context, provider, "next", ProviderAudioQuality.Any, null);
        fixture.OpenResponse.TrySetResult();
        using var one = (await first.WaitAsync(TimeSpan.FromSeconds(10)))!.Response;
        using var two = (await second.WaitAsync(TimeSpan.FromSeconds(10)))!.Response;
        Assert.Equal(1, fixture.Fetches);
        Assert.NotNull(ManagedTrackCacheService.SeekRung(one.Content));
        Assert.NotNull(ManagedTrackCacheService.SeekRung(two.Content));
        fixture.Source.Release.TrySetResult();
        Assert.Equal(fixture.Source.Bytes, await one.Content.ReadAsByteArrayAsync());
        Assert.Equal(fixture.Source.Bytes, await two.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("apple-musickit", false)]
    [InlineData("apple-download", false)]
    [InlineData("deezer", false)]
    [InlineData("apple-musickit", true)]
    [InlineData("deezer", true)]
    [InlineData("monochrome", true)]
    public async Task AbortedInitialClientCanOpenTwoFarRangesWithoutStoppingTheFill(string provider, bool supportsRanges)
    {
        var registered = provider == "apple-download" ? "apple-musickit" : provider;
        await using var fixture = new PreparationStreamFixture(supportsRanges, registered, "wav");
        var context = Context();
        using var cancellation = new CancellationTokenSource();
        var original = new ProtocolExecutionContext(context.Protocol, context.BackendInstanceId,
            context.VerifiedBackendPrincipalId, context.Principal, context.CorrelationId, context.Deadline,
            cancellation.Token, context.Client);
        fixture.OpenResponse.TrySetResult();
        var first = (await fixture.Gateway.OpenStreamAsync(original, provider, "next", ProviderAudioQuality.Any, null))!;
        var firstBody = await first.Response.Content.ReadAsStreamAsync();
        Assert.Equal(0, firstBody.ReadByte());
        await fixture.Source.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        first.Response.Dispose();
        var one = (await fixture.Gateway.OpenStreamAsync(context, provider, "next", ProviderAudioQuality.Any, "bytes=10-11"))!;
        using var firstRange = one.Response;
        var two = (await fixture.Gateway.OpenStreamAsync(context, provider, "next", ProviderAudioQuality.Any, "bytes=12-15"))!;
        using var secondRange = two.Response;
        Assert.False(fixture.Source.Closed.Task.IsCompleted);
        if (supportsRanges)
        {
            Assert.Equal(System.Net.HttpStatusCode.PartialContent, firstRange.StatusCode);
            Assert.Equal("bytes 10-11/16", firstRange.Content.Headers.ContentRange!.ToString());
            Assert.Equal(fixture.Source.Bytes[10..12], await firstRange.Content.ReadAsByteArrayAsync());
            Assert.Equal(fixture.Source.Bytes[12..], await secondRange.Content.ReadAsByteArrayAsync());
            Assert.False(fixture.Source.Release.Task.IsCompleted);
            Assert.Equal(3, fixture.Fetches);
        }
        else
        {
            Assert.Equal(1, fixture.Fetches);
            var firstStream = await firstRange.Content.ReadAsStreamAsync();
            firstStream.Position = 10;
            var secondStream = await secondRange.Content.ReadAsStreamAsync();
            secondStream.Position = 12;
            var firstBytes = new byte[2];
            var secondBytes = new byte[4];
            var readingOne = firstStream.ReadExactlyAsync(firstBytes).AsTask();
            var readingTwo = secondStream.ReadExactlyAsync(secondBytes).AsTask();
            Assert.False(readingOne.IsCompleted);
            Assert.False(readingTwo.IsCompleted);
            fixture.Source.Release.TrySetResult();
            await Task.WhenAll(readingOne, readingTwo).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(fixture.Source.Bytes[10..12], firstBytes);
            Assert.Equal(fixture.Source.Bytes[12..], secondBytes);
        }
    }
}
