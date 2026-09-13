using System.Net;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Protocols;
using allstarr.Core.Protocols.Jellyfin;
using allstarr.Core.Routing;
using allstarr.Services;
using allstarr.Services.Common;
using allstarr.Services.Local;
using allstarr.Models.Settings;
using allstarr.Models.Domain;
using allstarr.Services.Jellyfin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolProviderStreamingGatewayTests
{
    [Theory]
    [InlineData("missing", ProviderErrorKind.NotFound)]
    [InlineData("server", ProviderErrorKind.TransientFailure)]
    [InlineData("rate", ProviderErrorKind.RateLimited)]
    [InlineData("transport", ProviderErrorKind.TransientFailure)]
    [InlineData("timeout", ProviderErrorKind.TransientFailure)]
    [InlineData("body", ProviderErrorKind.TransientFailure)]
    [InlineData("empty", ProviderErrorKind.IncompatibleMedia)]
    [InlineData("html", ProviderErrorKind.IncompatibleMedia)]
    public async Task OpenStream_FallsThroughBeforeCommittingMedia(string failure, ProviderErrorKind kind)
    {
        var content = new ObservedContent(failure == "empty" ? [] : [8, 9]);
        if (failure == "html") content.Headers.ContentType = new("text/html");
        var failed = new HttpResponseMessage(failure switch
        {
            "missing" => HttpStatusCode.NotFound,
            "server" => HttpStatusCode.BadGateway,
            "rate" => HttpStatusCode.TooManyRequests,
            _ => HttpStatusCode.OK
        })
        { Content = content };
        var first = Streaming("deezer", (_, _) => failure switch
        {
            "transport" => throw new HttpRequestException("private provider URL must not escape"),
            "timeout" => throw new TaskCanceledException(),
            "body" => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StreamContent(new BrokenStream()) }),
            _ => Task.FromResult(failed)
        });
        var second = Streaming("qobuz", (_, _) => Task.FromResult(AudioResponse()));
        var (gateway, router) = FailoverGateway(first, second);
        var opened = await gateway.OpenStreamAsync(Context(), "deezer", "source-track", ProviderAudioQuality.Any, null);

        Assert.NotNull(opened);
        using var response = opened.Response;
        Assert.Equal("qobuz", opened.ServingProviderId);
        Assert.Equal("qobuz-track", opened.ServingExternalId);
        Assert.Equal([1, 2, 3, 4], await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("qobuz", Assert.Single(response.Headers.GetValues("X-Allstarr-Provider")));
        router.Verify(item => item.EvaluateFallback(It.IsAny<ProviderRoutePlan<IProviderStreamingCapability>>(), 0,
            It.Is<ProviderError>(error => error.Kind == kind)), Times.Once);
        if (failure is not ("transport" or "timeout" or "body")) Assert.True(content.Disposed);
        failed.Dispose();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task OpenStream_DoesNotBypassAuthenticationFailures(HttpStatusCode status)
    {
        var content = new ObservedContent([1]);
        var first = Streaming("deezer", (_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = content }));
        var second = Streaming("qobuz", (_, _) => Task.FromResult(AudioResponse()));
        var (gateway, _) = FailoverGateway(first, second);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            gateway.OpenStreamAsync(Context(), "deezer", "source-track", ProviderAudioQuality.Any, null));
        Assert.True(content.Disposed);
        second.Verify(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(),
            It.IsAny<ProviderStreamLeaseRequest>()), Times.Never);
    }

    [Fact]
    public async Task OpenStream_UsesConfiguredOrderInsteadOfTheCatalogProvider()
    {
        var preferred = Streaming("qobuz", (_, _) => Task.FromResult(AudioResponse()));
        var catalog = Streaming("deezer", (_, _) => Task.FromResult(AudioResponse()));
        var (gateway, router) = FailoverGateway(preferred, catalog);
        var opened = await gateway.OpenStreamAsync(Context(), "deezer", "source-track", ProviderAudioQuality.Any, null);
        using var response = Assert.IsType<ProtocolProviderStream>(opened).Response;
        Assert.Equal("qobuz", opened.ServingProviderId);
        router.Verify(item => item.PlanAsync<IProviderStreamingCapability>(It.Is<ProviderRouteRequest>(request =>
            request.ProviderPriority.SequenceEqual(new[] { "qobuz", "deezer" }))), Times.Once);
        catalog.Verify(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(),
            It.IsAny<ProviderStreamLeaseRequest>()), Times.Never);
    }

    [Fact]
    public async Task OpenStream_OnlyOneSameSourceRetryBeforeFailover()
    {
        var attempts = 0;
        var first = Streaming("deezer", (_, _) =>
        {
            attempts++;
            throw new HttpRequestException();
        }, ProviderStreamRetryBehavior.RetrySameLeaseOnce);
        var second = Streaming("qobuz", (_, _) => Task.FromResult(AudioResponse()));
        var (gateway, _) = FailoverGateway(first, second);
        using var response = (await gateway.OpenStreamAsync(Context(), "deezer", "source-track",
            ProviderAudioQuality.Any, null))!.Response;
        Assert.Equal(2, attempts);
        first.Verify(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(),
            It.IsAny<ProviderStreamLeaseRequest>()), Times.Once);
    }

    [Fact]
    public async Task OpenStream_CancellationStopsWithoutTryingAnotherProvider()
    {
        using var cancelled = new CancellationTokenSource();
        var first = Streaming("deezer", (_, _) => { cancelled.Cancel(); throw new OperationCanceledException(cancelled.Token); });
        var second = Streaming("qobuz", (_, _) => Task.FromResult(AudioResponse()));
        var (gateway, _) = FailoverGateway(first, second);
        var context = ClientContext(cancelled.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            gateway.OpenStreamAsync(context, "deezer", "source-track", ProviderAudioQuality.Any, null));
        second.Verify(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(),
            It.IsAny<ProviderStreamLeaseRequest>()), Times.Never);
    }

    [Fact]
    public async Task OpenStream_ResumeUsesPreviousEncodingAndHeadDoesNotOverwriteIt()
    {
        using var activity = new PlaybackDeliveryActivityStore();
        var context = ClientContext();
        var first = Streaming("deezer", (_, _) => Task.FromResult(AudioResponse(HttpStatusCode.NotFound)));
        string? observedRange = null;
        var second = Streaming("qobuz", (request, _) =>
        {
            observedRange = request.Headers.Range?.ToString();
            return Task.FromResult(AudioResponse());
        });
        var (gateway, router) = FailoverGateway(first, second, activity);
        using var firstResponse = (await gateway.OpenStreamAsync(context, "deezer", "source-track",
            ProviderAudioQuality.Any, null))!.Response;
        var source = activity.StreamFor(context, "ext-deezer-song-source-track", ProviderAudioQuality.Any);
        Assert.Equal("qobuz", source!.ProviderId);

        using var resumed = (await gateway.OpenStreamAsync(context, "deezer", "source-track",
            ProviderAudioQuality.Any, "bytes=1-"))!.Response;
        Assert.Equal("bytes=1-", observedRange);
        router.Verify(item => item.PlanAsync<IProviderStreamingCapability>(It.Is<ProviderRouteRequest>(request =>
            request.ProviderPriority.SequenceEqual(new[] { "qobuz" }))), Times.Once);

        var lastOpened = activity.StreamFor(context, "ext-deezer-song-source-track", ProviderAudioQuality.Any);
        using var head = (await gateway.OpenStreamAsync(context, "deezer", "source-track",
            ProviderAudioQuality.Any, null, headOnly: true))!.Response;
        Assert.Same(lastOpened, activity.StreamFor(context, "ext-deezer-song-source-track", ProviderAudioQuality.Any));
    }

    [Fact]
    public async Task OpenStream_ResumeWithoutScopedSelectionRequiresRestart()
    {
        using var activity = new PlaybackDeliveryActivityStore();
        var first = Streaming("deezer", (_, _) => Task.FromResult(AudioResponse()));
        var second = Streaming("qobuz", (_, _) => Task.FromResult(AudioResponse()));
        var (gateway, router) = FailoverGateway(first, second, activity);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => gateway.OpenStreamAsync(ClientContext(),
            "deezer", "source-track", ProviderAudioQuality.Any, "bytes=100-"));
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, error.StatusCode);
        router.Verify(item => item.PlanAsync<IProviderStreamingCapability>(It.IsAny<ProviderRouteRequest>()), Times.Never);
    }

    [Fact]
    public async Task OpenStream_DeniedAccountCannotReadTheWarmCache()
    {
        var local = new Mock<ILocalLibraryService>(MockBehavior.Strict);
        var cache = new ManagedTrackCacheService(new ConfigurationBuilder().Build(),
            Options.Create(new SubsonicSettings()), local.Object, NullLogger<ManagedTrackCacheService>.Instance);
        var capability = Streaming("deezer", (_, _) => Task.FromResult(AudioResponse()));
        var registry = Registry(capability.Object);
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        router.Setup(item => item.PlanAsync<IProviderStreamingCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) => new ProviderRoutePlan<IProviderStreamingCapability>(request, [],
                new ProviderRouteDecisionRecord(request.CorrelationId, ProviderCapabilityKind.Streaming, null, null,
                    [new ProviderRouteCandidateDecision("deezer", null, ProviderRouteDecisionStatus.Rejected, "account-scope-denied", 0)])));
        var gateway = new ProtocolProviderGateway(router.Object, registry, Mock.Of<IProviderRouteAccountResolver>(),
            Mock.Of<IMusicMetadataService>(MockBehavior.Strict), new HttpClientFactory(), managedTrackCache: cache);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => gateway.OpenStreamAsync(Context(), "deezer", "source-track",
            ProviderAudioQuality.Any, null));
        local.VerifyNoOtherCalls();
        capability.Verify(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(),
            It.IsAny<ProviderStreamLeaseRequest>()), Times.Never);
    }

    [Fact]
    public async Task PlaybackSourceIsScopedToUserDeviceLibraryAndQuality()
    {
        using var activity = new PlaybackDeliveryActivityStore();
        var context = ClientContext();
        var first = Streaming("deezer", (_, _) => Task.FromResult(AudioResponse()));
        var second = Streaming("qobuz", (_, _) => Task.FromResult(AudioResponse()));
        var (gateway, _) = FailoverGateway(first, second, activity);
        using var response = (await gateway.OpenStreamAsync(context, "deezer", "source-track",
            ProviderAudioQuality.Any, null))!.Response;
        const string itemId = "ext-deezer-song-source-track";
        Assert.NotNull(activity.StreamFor(context, itemId, ProviderAudioQuality.Any));
        Assert.Null(activity.StreamFor(context.WithLibraryScope("other-library"), itemId, ProviderAudioQuality.Any));
        Assert.Null(activity.StreamFor(context, itemId, ProviderAudioQuality.Lossy));
        Assert.Null(activity.StreamFor(context.Actor!.TenantId, Guid.CreateVersion7(), "device", itemId));
        Assert.Null(activity.StreamFor(Guid.CreateVersion7(), context.Actor.UserId, "device", itemId));
        Assert.Null(activity.StreamFor(context.Actor.TenantId, context.Actor.UserId, "other-device", itemId));
        Assert.Null(activity.StreamFor(context.Actor.TenantId, context.Actor.UserId, "device", "ext-deezer-song-another"));
    }

    [Fact]
    public async Task JellyfinSongInfoShowsOnlyThisListenersLastOpenedSource()
    {
        using var activity = new PlaybackDeliveryActivityStore();
        var context = ClientContext();
        var first = Streaming("qobuz", (_, _) => Task.FromResult(AudioResponse()));
        var second = Streaming("deezer", (_, _) => Task.FromResult(AudioResponse()));
        var (gateway, _) = FailoverGateway(first, second, activity);
        using var response = (await gateway.OpenStreamAsync(context, "deezer", "source-track", ProviderAudioQuality.Any, null))!.Response;
        var http = new DefaultHttpContext();
        http.Items[ProtocolExecutionContextFactory.HttpContextItemKey] = context;
        var adapter = new JellyfinItemProtocolAdapter(new JellyfinResponseBuilder(),
            new HttpContextAccessor { HttpContext = http }, activity);
        var song = new Song { Id = "ext-deezer-song-source-track", Title = "Track", ExternalProvider = "deezer", ExternalId = "source-track" };
        var result = Assert.IsType<JsonResult>(adapter.ShapeSong(song));
        var json = JsonSerializer.Serialize(result.Value);
        Assert.Contains("Last stream: qobuz", json);
        Assert.DoesNotContain("https://media", json);
        Assert.Equal("private, no-store", http.Response.Headers.CacheControl);
        http.Items[ProtocolExecutionContextFactory.HttpContextItemKey] = ClientContext();
        var other = Assert.IsType<JsonResult>(adapter.ShapeSong(song));
        Assert.DoesNotContain("Last stream:", JsonSerializer.Serialize(other.Value));
    }

    [Fact]
    public async Task OpenStream_DeadlineCancelsAnUnresponsiveSource()
    {
        var first = Streaming("deezer", async (_, token) => { await Task.Delay(Timeout.InfiniteTimeSpan, token); return AudioResponse(); });
        var second = Streaming("qobuz", (_, _) => Task.FromResult(AudioResponse()));
        var (gateway, _) = FailoverGateway(first, second);
        var context = ClientContext();
        context = new ProtocolExecutionContext(context.Protocol, context.BackendInstanceId, context.VerifiedBackendPrincipalId,
            context.Principal, context.CorrelationId, DateTimeOffset.UtcNow.AddMilliseconds(100), default, context.Client);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gateway.OpenStreamAsync(context, "deezer", "source-track",
            ProviderAudioQuality.Any, null));
        second.Verify(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderStreamLeaseRequest>()), Times.Never);
    }

    private static HttpResponseMessage AudioResponse(HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new ByteArrayContent([1, 2, 3, 4])
    };

    private static Mock<IProviderStreamingCapability> Streaming(string provider,
        ProviderStreamResponseFactory open, ProviderStreamRetryBehavior retry = ProviderStreamRetryBehavior.DoNotRetry) =>
        Capability(provider, ProviderOutcome<ProviderStreamLease>.Success(new ProviderStreamLease(
            "lease", new Uri("https://media.example.test/audio"), DateTimeOffset.UtcNow.AddMinutes(1), true, true,
            new ProviderMediaFormat("audio/flac", "flac", "flac"), retry, open)));

    private static (ProtocolProviderGateway Gateway, Mock<IProviderRouter> Router) FailoverGateway(
        Mock<IProviderStreamingCapability> first, Mock<IProviderStreamingCapability> second,
        PlaybackDeliveryActivityStore? activity = null)
    {
        var registry = Registry(first.Object, second.Object);
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        router.Setup(item => item.PlanAsync<IProviderStreamingCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) => Plan(request, registry,
                new[] { first.Object, second.Object }.Where(item => request.ProviderPriority.Contains(item.ProviderId)).ToArray()));
        router.Setup(item => item.EvaluateFallback(It.IsAny<ProviderRoutePlan<IProviderStreamingCapability>>(),
                It.IsAny<int>(), It.IsAny<ProviderError>()))
            .Returns((ProviderRoutePlan<IProviderStreamingCapability> plan, int index, ProviderError error) =>
                new ProviderFallbackDecision<IProviderStreamingCapability>(
                    error.Kind is ProviderErrorKind.Unauthorized or ProviderErrorKind.Forbidden || index + 1 == plan.Candidates.Count
                        ? ProviderFallbackDisposition.StopFailure : ProviderFallbackDisposition.Advance,
                    error.Code, index + 1 < plan.Candidates.Count ? plan.Candidates[index + 1] : null));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Providers:StreamingOrder"] = $"{first.Object.ProviderId},{second.Object.ProviderId}"
        }).Build();
        return (new ProtocolProviderGateway(router.Object, registry, Mock.Of<IProviderRouteAccountResolver>(),
            Mock.Of<IMusicMetadataService>(MockBehavior.Strict), new HttpClientFactory(), configuration,
            playbackActivity: activity), router);
    }

    private static ProtocolExecutionContext ClientContext(CancellationToken cancellationToken = default)
    {
        var original = Context();
        return new ProtocolExecutionContext(original.Protocol, original.BackendInstanceId, original.VerifiedBackendPrincipalId,
            original.Principal, original.CorrelationId, original.Deadline, cancellationToken,
            new ProtocolClientDescriptor("client", "device"), original.LibraryScopeId);
    }

    private sealed class ObservedContent(byte[] bytes) : ByteArrayContent(bytes)
    {
        public bool Disposed { get; private set; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class BrokenStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new IOException("upstream media failed");
    }
}
