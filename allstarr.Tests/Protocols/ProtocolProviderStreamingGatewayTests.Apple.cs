using System.Buffers.Binary;
using System.Net;
using System.Text;
using allstarr.Core.Capabilities;
using allstarr.Core.Music;
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
    public static IEnumerable<object[]> ProgressiveAppleSamples => AppleProgressiveFixtures.Samples;

    [MediaToolsTheory]
    [MemberData(nameof(ProgressiveAppleSamples))]
    public async Task OpenStream_AppleProgressivePipelineOutputIsReadyWithTheRealVerifier(string file)
    {
        var sample = AppleProgressiveFixtures.Sample(file);
        var bytes = AppleProgressiveFixtures.Bytes(file);
        using var source = new AppleProgressiveFixtures.PendingTail(bytes);
        using var http = new HttpClient(new AppleProviderTestFactory.Handler(request =>
        {
            var response = AppleProgressiveFixtures.Response(source, sample.Mime);
            response.RequestMessage = request;
            return response;
        }));
        var adapter = new AppleDownloadStreamingCapabilityAdapter(http,
            new AppleDownloadSettings { BaseUrl = "https://gateway.test/" },
            new AudioFileDiscovery(), AppleProviderTestFactory.Client());
        var second = Streaming("qobuz", (_, _) => Task.FromResult(AudioResponse()));
        var (gateway, _) = AppleAudioGateway(adapter, second.Object, verifier: new ProviderAudioVerifier());
        var context = ClientContext() with
        {
            AudioDelivery = new()
            {
                Profiles = [new([sample.Media.Container], [sample.Codec], ["http"])],
                MaxBitDepth = sample.BitDepth,
                MaxSampleRate = sample.SampleRate,
                EnableTranscoding = false
            }
        };
        try
        {
            var opened = await gateway.OpenStreamAsync(context, AppleMusicClient.ProviderId,
                "101", ProviderAudioQuality.Any, null);

            Assert.NotNull(opened);
            using var response = opened.Response;
            Assert.Equal(AppleMusicClient.ProviderId, opened.ServingProviderId);
            Assert.Equal(sample.Mime, response.Content.Headers.ContentType!.MediaType);
            Assert.Equal(sample.Codec, opened.Lease.Media.Codec);
            Assert.Equal(sample.SampleRate, opened.Lease.Media.SampleRate);
            Assert.Equal(sample.BitDepth, opened.Lease.Media.BitDepth);
            Assert.Equal(sample.Channels, opened.Lease.Media.Channels);
            Assert.Equal(sample.DurationSeconds, opened.FileFacts.Measured!.DurationSeconds);
            Assert.True(context.AudioDelivery.Evaluate(opened.Lease.Media).SupportsDirectPlay);
            Assert.Null(opened.FileFacts.Size);
            Assert.Null(response.Content.Headers.ContentLength);
            Assert.False(source.Release.Task.IsCompleted);
            source.Release.TrySetResult();
            Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
            second.Verify(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(),
                It.IsAny<ProviderStreamLeaseRequest>()), Times.Never);
        }
        finally { source.Release.TrySetResult(); }
    }

    [MediaToolsTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenStream_AppleProgressiveInvalidAudioIsRejectedBeforeUsingAValidFallback(bool corrupt)
    {
        var bytes = AppleProgressiveFixtures.Bytes("alac-16-44100.flac");
        if (corrupt) bytes.AsSpan(AppleProgressiveFixtures.FlacAudioOffset(bytes)).Fill(0);
        else bytes = bytes[..AppleProgressiveFixtures.PrefixLength(bytes)];
        using var http = new HttpClient(new AppleProviderTestFactory.Handler(request =>
        {
            var source = new AppleProgressiveFixtures.PendingTail(bytes);
            source.Release.TrySetResult();
            var response = AppleProgressiveFixtures.Response(source, "audio/flac");
            response.RequestMessage = request;
            return response;
        }));
        var adapter = new AppleDownloadStreamingCapabilityAdapter(http,
            new AppleDownloadSettings { BaseUrl = "https://gateway.test/" },
            new AudioFileDiscovery(), AppleProviderTestFactory.Client());
        var fallbackBytes = PlaybackAudioFixtures.Wave();
        var second = PipelineSource("qobuz", (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new ByteArrayContent(fallbackBytes) }));
        var (gateway, router) = AppleAudioGateway(adapter, second.Object, verifier: new ProviderAudioVerifier());

        var opened = await gateway.OpenStreamAsync(ClientContext(), AppleMusicClient.ProviderId,
            "101", ProviderAudioQuality.Any, null);

        Assert.NotNull(opened);
        using var delivered = opened.Response;
        Assert.Equal("qobuz", opened.ServingProviderId);
        Assert.Equal(fallbackBytes, await delivered.Content.ReadAsByteArrayAsync());
        router.Verify(item => item.EvaluateFallback(It.IsAny<ProviderRoutePlan<IProviderStreamingCapability>>(), 0,
            It.Is<ProviderError>(error => error.Kind == ProviderErrorKind.IncompatibleMedia)), Times.Once);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task OpenStream_AppleProgressiveAudioStartsBeforeCompletionAndPropagatesTransportAbort(bool audioFile, bool abort)
    {
        var audio = AppleFlac();
        using var source = new AppleProgressiveAudio(audio, abort);
        using var http = new HttpClient(new AppleProviderTestFactory.Handler(request =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StreamContent(source)
            };
            response.Content.Headers.ContentType = new("audio/flac");
            response.Headers.TransferEncodingChunked = true;
            response.Headers.Add("X-Allstarr-Duration-Seconds", "2.5");
            return response;
        }));
        var adapter = new AppleDownloadStreamingCapabilityAdapter(http,
            new AppleDownloadSettings { BaseUrl = "https://gateway.test/" },
            new AudioFileDiscovery(audioFile), AppleProviderTestFactory.Client());
        var second = Streaming("qobuz", (_, _) => Task.FromResult(AudioResponse()));
        var (gateway, _) = AppleAudioGateway(adapter, second.Object);
        using var cancellation = new CancellationTokenSource();
        var opening = gateway.OpenStreamAsync(ClientContext(cancellation.Token), AppleMusicClient.ProviderId, "101", ProviderAudioQuality.Any, null);
        try
        {
            await source.WaitingForFirstByte.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(opening.IsCompleted);
            source.FirstByte.TrySetResult();

            var opened = await opening.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull(opened);
            using var response = opened.Response;
            Assert.Equal(AppleMusicClient.ProviderId, opened.ServingProviderId);
            Assert.Null(response.Content.Headers.ContentLength);
            Assert.False(source.Completion.Task.IsCompleted);
            Assert.Equal("audio/flac", response.Content.Headers.ContentType!.MediaType);
            Assert.Equal("flac", opened.Lease.Media.Codec);
            Assert.Equal(48000, opened.Lease.Media.SampleRate);
            Assert.Equal(16, opened.Lease.Media.BitDepth);
            Assert.Equal(2, opened.Lease.Media.Channels);
            Assert.Equal(2.5, opened.FileFacts.Measured!.DurationSeconds);
            Assert.Null(opened.FileFacts.Size);
            Assert.Equal("2.5", Assert.Single(response.Headers.GetValues("X-Allstarr-Duration-Seconds")));
            var reader = await response.Content.ReadAsStreamAsync();
            var prefix = new byte[audio.Length];
            Assert.Equal(audio.Length, await reader.ReadAsync(prefix));
            Assert.Equal(audio, prefix);
            source.Completion.TrySetResult();
            if (abort)
            {
                await Assert.ThrowsAsync<IOException>(() => reader.ReadAsync(prefix).AsTask());
                Assert.True(opened.FileFacts.Failed);
            }
            else
            {
                Assert.Equal(0, await reader.ReadAsync(prefix));
                Assert.False(opened.FileFacts.Failed);
            }
            second.Verify(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(),
                It.IsAny<ProviderStreamLeaseRequest>()), Times.Never);
        }
        finally
        {
            cancellation.Cancel();
            source.FirstByte.TrySetResult();
            source.Completion.TrySetResult();
            try { (await opening.WaitAsync(TimeSpan.FromSeconds(10)))?.Response.Dispose(); }
            catch (OperationCanceledException) { }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenStream_AppleTransportAbortBeforeFirstByteRetriesAndUsesFallback(bool audioFile)
    {
        var requests = 0;
        using var http = new HttpClient(new AppleProviderTestFactory.Handler(request =>
        {
            requests++;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StreamContent(new AppleAbortedAudio())
            };
            response.Content.Headers.ContentType = new("audio/flac");
            response.Headers.TransferEncodingChunked = true;
            return response;
        }));
        var adapter = new AppleDownloadStreamingCapabilityAdapter(http,
            new AppleDownloadSettings { BaseUrl = "https://gateway.test/" },
            new AudioFileDiscovery(audioFile), AppleProviderTestFactory.Client());
        var second = Streaming("qobuz", (_, _) => Task.FromResult(AudioResponse()));
        var (gateway, router) = AppleAudioGateway(adapter, second.Object);

        var opened = await gateway.OpenStreamAsync(Context(), AppleMusicClient.ProviderId, "101", ProviderAudioQuality.Any, null);

        Assert.NotNull(opened);
        using var response = opened.Response;
        Assert.Equal("qobuz", opened.ServingProviderId);
        Assert.Equal([1, 2, 3, 4], await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(2, requests);
        router.Verify(item => item.EvaluateFallback(It.IsAny<ProviderRoutePlan<IProviderStreamingCapability>>(), 0,
            It.Is<ProviderError>(error => error.Kind == ProviderErrorKind.TransientFailure)), Times.Once);
    }

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
        AppleDownloadStreamingCapabilityAdapter adapter, IProviderStreamingCapability fallback, TimeProvider? clock = null,
        IProviderAudioVerifier? verifier = null)
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
                    error.Kind == ProviderErrorKind.Forbidden || index + 1 == plan.Candidates.Count
                        ? ProviderFallbackDisposition.StopFailure : ProviderFallbackDisposition.Advance,
                    error.Code, index + 1 < plan.Candidates.Count ? plan.Candidates[index + 1] : null));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Providers:StreamingOrder"] = $"{adapter.ProviderId},{fallback.ProviderId}"
        }).Build();
        return (new ProtocolProviderGateway(router.Object, registry, new HttpClientFactory(), configuration, timeProvider: clock, audioVerifier: verifier ?? FixtureAudioVerifier.Instance), router);
    }

    private sealed class AudioFileDiscovery(bool audioFile = true) : IAppleDownloadEndpointDiscovery
    {
        public Task<AppleDownloadEndpointSnapshot> DiscoverAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AppleDownloadEndpointSnapshot(AppleDownloadEndpointState.Available, null, "2.1", true,
                [new("streaming", AppleDownloadCapabilityState.Available),
                    new(audioFile ? "audio-file" : "stream-audio-song", AppleDownloadCapabilityState.Available)]));
    }

    private static byte[] AppleFlac()
    {
        var bytes = new byte[50];
        "fLaC"u8.CopyTo(bytes);
        bytes[7] = 34;
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(18, 8),
            (48000UL << 44) | (1UL << 41) | (15UL << 36) | 120000UL);
        bytes[49] = 99;
        return bytes;
    }

    private sealed class AppleProgressiveAudio(byte[] bytes, bool abort) : MemoryStream(bytes)
    {
        public readonly TaskCompletionSource WaitingForFirstByte = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource FirstByte = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanSeek => false;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position == 0)
            {
                WaitingForFirstByte.TrySetResult();
                await FirstByte.Task.WaitAsync(cancellationToken);
            }
            else
            {
                await Completion.Task.WaitAsync(cancellationToken);
                if (abort) throw new HttpIOException(HttpRequestError.ResponseEnded, "The audio transport ended unexpectedly.");
            }
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }

    private sealed class AppleAbortedAudio() : MemoryStream([])
    {
        public override bool CanSeek => false;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new HttpIOException(HttpRequestError.ResponseEnded, "The audio transport ended unexpectedly."));
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
