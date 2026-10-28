using System.Net;
using System.Text;
using allstarr.Core.Capabilities;
using allstarr.Core.Protocols;
using allstarr.Core.Providers.AppleDownload;
using allstarr.Core.Providers.AppleMusicKit;
using allstarr.Core.Routing;
using allstarr.Core.Storage;
using allstarr.Models.Settings;
using allstarr.Services.AppleMusic;
using Microsoft.Extensions.Configuration;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolProviderStreamingGatewayTests
{
    [Theory]
    [InlineData(HttpStatusCode.BadGateway, "audio_artifact_missing", ProviderErrorKind.NotFound, 1)]
    [InlineData(HttpStatusCode.BadGateway, "artifact_missing", ProviderErrorKind.NotFound, 1)]
    [InlineData(HttpStatusCode.GatewayTimeout, "audio_prepare_timeout", ProviderErrorKind.TransientFailure, 2)]
    [InlineData(HttpStatusCode.Forbidden, "account_mismatch", ProviderErrorKind.Forbidden, 1)]
    public async Task OpenStream_MapsAppleAudioFileErrorsBeforeServingMedia(
        HttpStatusCode status, string detail, ProviderErrorKind expectedError, int expectedRequests)
    {
        var requests = 0;
        using var http = new HttpClient(new AppleProviderTestFactory.Handler(request =>
        {
            requests++;
            Assert.Equal("/api/audio/101", request.RequestUri!.AbsolutePath);
            return new(status)
            {
                RequestMessage = request,
                Content = new StringContent($$"""{"detail":"{{detail}}"}""", Encoding.UTF8, "application/json")
            };
        }));
        var adapter = new AppleDownloadStreamingCapabilityAdapter(http,
            new AppleDownloadSettings { BaseUrl = "https://gateway.test/" },
            new AudioFileDiscovery(), AppleProviderTestFactory.Client());
        var second = Streaming("qobuz", (_, _) => Task.FromResult(AudioResponse()));
        var (gateway, router) = AppleAudioGateway(adapter, second.Object);

        if (expectedError == ProviderErrorKind.Forbidden)
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => gateway.OpenStreamAsync(
                Context(), AppleMusicClient.ProviderId, "source-track", ProviderAudioQuality.Any, null));
            second.Verify(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(),
                It.IsAny<ProviderStreamLeaseRequest>()), Times.Never);
        }
        else
        {
            var opened = await gateway.OpenStreamAsync(
                Context(), AppleMusicClient.ProviderId, "source-track", ProviderAudioQuality.Any, null);
            Assert.NotNull(opened);
            using var response = opened.Response;
            Assert.Equal("qobuz", opened.ServingProviderId);
            Assert.Equal([1, 2, 3, 4], await response.Content.ReadAsByteArrayAsync());
        }

        Assert.Equal(expectedRequests, requests);
        router.Verify(item => item.EvaluateFallback(It.IsAny<ProviderRoutePlan<IProviderStreamingCapability>>(), 0,
            It.Is<ProviderError>(error => error.Kind == expectedError)), Times.Once);
    }

    [Theory]
    [InlineData("*/4")]
    [InlineData("bytes */4")]
    public async Task OpenStream_PreservesUnsatisfiableAppleAudioFileRanges(string contentRange)
    {
        var requests = 0;
        using var http = new HttpClient(new AppleProviderTestFactory.Handler(request =>
        {
            requests++;
            Assert.Equal("bytes=10-19", request.Headers.Range!.ToString());
            var response = new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                RequestMessage = request,
                Content = new ByteArrayContent([])
            };
            response.Content.Headers.TryAddWithoutValidation("Content-Range", contentRange);
            return response;
        }));
        var adapter = new AppleDownloadStreamingCapabilityAdapter(http,
            new AppleDownloadSettings { BaseUrl = "https://gateway.test/" },
            new AudioFileDiscovery(), AppleProviderTestFactory.Client());
        var second = Streaming("qobuz", (_, _) => Task.FromResult(AudioResponse()));
        var (gateway, _) = AppleAudioGateway(adapter, second.Object);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => gateway.OpenStreamAsync(
            Context(), AppleMusicClient.ProviderId, "101", ProviderAudioQuality.Any, "bytes=10-19"));

        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, error.StatusCode);
        Assert.Equal(1, requests);
        second.Verify(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(),
            It.IsAny<ProviderStreamLeaseRequest>()), Times.Never);
    }

    [Fact]
    public async Task OpenStream_GivesAppleAudioFilesTheDownloadOpeningDeadline()
    {
        using var http = new HttpClient(new AppleProviderTestFactory.Handler(request =>
        {
            var response = AudioResponse();
            response.RequestMessage = request;
            response.Content.Headers.ContentType = new("audio/flac");
            return response;
        }));
        var adapter = new AppleDownloadStreamingCapabilityAdapter(http,
            new AppleDownloadSettings { BaseUrl = "https://gateway.test/" },
            new AudioFileDiscovery(), AppleProviderTestFactory.Client());
        var clock = new AppleOpeningClock();
        var second = Streaming("qobuz", (_, _) => Task.FromResult(AudioResponse()));
        var (gateway, _) = AppleAudioGateway(adapter, second.Object, clock);

        var opened = await gateway.OpenStreamAsync(Context(), AppleMusicClient.ProviderId, "101", ProviderAudioQuality.Any, null);

        Assert.NotNull(opened);
        using var response = opened.Response;
        Assert.Equal(AppleMusicClient.ProviderId, opened.ServingProviderId);
        Assert.Contains(clock.Timeouts, timeout => timeout >= TimeSpan.FromSeconds(148) && timeout <= TimeSpan.FromSeconds(150));
    }

    private static (ProtocolProviderGateway Gateway, Mock<IProviderRouter> Router) AppleAudioGateway(
        AppleDownloadStreamingCapabilityAdapter adapter, IProviderStreamingCapability fallback, TimeProvider? clock = null)
    {
        var registry = Registry(adapter, fallback);
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        router.Setup(item => item.PlanAsync<IProviderStreamingCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) =>
            {
                var plan = Plan(request, registry, new IProviderStreamingCapability[] { adapter, fallback }
                    .Where(capability => request.Policy.AllowsProvider(capability.ProviderId)).ToArray());
                return plan with
                {
                    Candidates = plan.Candidates.Select(candidate => candidate.Implementation != adapter ? candidate : candidate with
                    {
                        Context = new ProviderExecutionContext(request.Actor, adapter.ProviderId,
                            new ProviderAccountContext(Guid.CreateVersion7(), adapter.ProviderId, ProviderAccountScope.Personal,
                                1, ownerUserId: request.Actor.EffectiveUserId, secretReferenceId: Guid.CreateVersion7()),
                            request.Policy, request.OperationId, request.CorrelationId, request.Deadline, request.CancellationToken),
                        TrackId = new(adapter.ProviderId, ProviderResourceKind.Track, "101")
                    }).ToArray()
                };
            });
        router.Setup(item => item.EvaluateFallback(It.IsAny<ProviderRoutePlan<IProviderStreamingCapability>>(),
                It.IsAny<int>(), It.IsAny<ProviderError>()))
            .Returns((ProviderRoutePlan<IProviderStreamingCapability> plan, int index, ProviderError error) =>
                new ProviderFallbackDecision<IProviderStreamingCapability>(
                    error.Kind == ProviderErrorKind.Forbidden ? ProviderFallbackDisposition.StopFailure : ProviderFallbackDisposition.Advance,
                    error.Code, plan.Candidates[index + 1]));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Providers:StreamingOrder"] = $"{adapter.ProviderId},{fallback.ProviderId}"
        }).Build();
        return (new ProtocolProviderGateway(router.Object, registry, new HttpClientFactory(), configuration, timeProvider: clock, audioVerifier: FixtureAudioVerifier.Instance), router);
    }

    private sealed class AudioFileDiscovery : IAppleDownloadEndpointDiscovery
    {
        public Task<AppleDownloadEndpointSnapshot> DiscoverAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AppleDownloadEndpointSnapshot(AppleDownloadEndpointState.Available, null, "2.1", true,
                [new("streaming", AppleDownloadCapabilityState.Available), new("audio-file", AppleDownloadCapabilityState.Available)]));
    }

    private sealed class AppleOpeningClock : TimeProvider
    {
        public List<TimeSpan> Timeouts { get; } = [];

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Timeouts.Add(dueTime);
            return new OpeningTimer(base.CreateTimer(callback, state, dueTime, period), Timeouts);
        }

        private sealed class OpeningTimer(ITimer timer, List<TimeSpan> timeouts) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                timeouts.Add(dueTime);
                return timer.Change(dueTime, period);
            }

            public void Dispose() => timer.Dispose();
            public ValueTask DisposeAsync() => timer.DisposeAsync();
        }
    }
}
