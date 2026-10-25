using allstarr.Core.Storage;
using allstarr.Core.Music;
using allstarr.Core.Matching;
using System.Net;
using allstarr.Core.Capabilities;
using allstarr.Core.Protocols;
using allstarr.Core.Routing;
using allstarr.Models.Settings;
using allstarr.Services.Common;
using allstarr.Services.Local;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolProviderStreamingGatewayTests
{
    [Theory]
    [InlineData("apple-musickit")]
    [InlineData("apple-download")]
    public async Task OriginalDownloadBypassesPreparationCacheAndPreservesContent(string provider)
    {
        using var upstream = AudioResponse();
        upstream.Content.Headers.ContentLength = 4;
        var apple = Streaming("apple-musickit", (_, _) => Task.FromResult(upstream));
        var registry = Registry(apple.Object);
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        router.Setup(item => item.PlanAsync<IProviderStreamingCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) => Plan(request, registry, apple.Object));
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Library:DownloadPath"] = root }).Build();
        var library = new Mock<ILocalLibraryService>(MockBehavior.Strict);
        await using var cache = new ManagedTrackCacheService(config,
            Options.Create(new SubsonicSettings { StorageMode = StorageMode.Cache }),
            library.Object, NullLogger<ManagedTrackCacheService>.Instance);
        using var activity = new PlaybackDeliveryActivityStore();
        var identities = new Mock<ITrackIdentityService>(MockBehavior.Strict);
        var gateway = new ProtocolProviderGateway(router.Object, registry, new HttpClientFactory(), config,
            managedTrackCache: cache, playbackActivity: activity, identities: identities.Object);
        var context = Context() with
        {
            IsOriginalDownload = true,
            RequestedItemId = $"ext-{provider}-song-next",
            AudioDelivery = new ProviderAudioDeliveryRequest
            {
                Profiles = [new(["mp3"], ["mp3"], ["http"])],
                MaxBitrate = 64000,
                StartTimeTicks = TimeSpan.FromSeconds(30).Ticks
            }
        };
        var result = await gateway.OpenStreamAsync(context, provider, "next", context.AudioDelivery.Quality, null);
        Assert.NotNull(result);
        using var response = result.Response;
        Assert.Equal(4L, response.Content.Headers.ContentLength);
        Assert.Null(ManagedTrackCacheService.SeekRung(response.Content));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await response.Content.ReadAsByteArrayAsync());
        Assert.False(result.IsCached);
        Assert.False(Directory.Exists(root));
        Assert.Null(activity.StreamFor(context.Actor!.EffectiveUserId, "device", context.RequestedItemId));
        apple.Verify(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(),
            It.Is<ProviderStreamLeaseRequest>(request => request.RequestedQuality == ProviderAudioQuality.Any)), Times.Once);
        library.VerifyNoOtherCalls();
        identities.VerifyNoOtherCalls();
        router.VerifyAll();
        router.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SourceInspectionBypassesManagedPlaybackCache(bool headOnly)
    {
        var apple = Streaming("apple-musickit", (_, _) => Task.FromResult(AudioResponse()));
        var registry = Registry(apple.Object);
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        router.Setup(item => item.PlanAsync<IProviderStreamingCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) => Plan(request, registry, apple.Object));
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Library:DownloadPath"] = root }).Build();
        var library = new Mock<ILocalLibraryService>(MockBehavior.Strict);
        await using var cache = new ManagedTrackCacheService(config,
            Options.Create(new SubsonicSettings { StorageMode = StorageMode.Cache }),
            library.Object, NullLogger<ManagedTrackCacheService>.Instance);
        using var activity = new PlaybackDeliveryActivityStore();
        var identities = new Mock<ITrackIdentityService>(MockBehavior.Strict);
        var gateway = new ProtocolProviderGateway(router.Object, registry, new HttpClientFactory(), config,
            managedTrackCache: cache, playbackActivity: activity, identities: identities.Object);
        var context = Context() with { IsReadOnly = true, RequestedItemId = "ext-apple-musickit-song-next" };

        var result = await gateway.OpenStreamAsync(context, "apple-musickit", "next", ProviderAudioQuality.Any, null, headOnly);

        Assert.NotNull(result);
        using var response = result.Response;
        Assert.Null(ManagedTrackCacheService.SeekRung(response.Content));
        Assert.False(Directory.Exists(root));
        Assert.Null(activity.StreamFor(context.Actor!.EffectiveUserId, "device", context.RequestedItemId));
        library.VerifyNoOtherCalls();
        identities.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AudioDeliveryRejectsIncompatibleAppleSourceBeforeCachingOrRecordingPlayback(bool preparing)
    {
        await using var fixture = new PreparationStreamFixture();
        fixture.OpenResponse.TrySetResult();
        var context = Context() with
        {
            IsPreparation = preparing,
            PreparationQuality = ProviderAudioQuality.Lossy,
            AudioDelivery = new ProviderAudioDeliveryRequest { Formats = [new(["mp3"], ["mp3"], ["http"])] }
        };

        await Assert.ThrowsAsync<ProviderAudioProfileException>(() => fixture.Gateway.OpenStreamAsync(
            context, "apple-musickit", "next", ProviderAudioQuality.Lossy, null));

        Assert.Equal(1, fixture.Fetches);
        Assert.True(fixture.Source.Closed.Task.IsCompletedSuccessfully);
        Assert.False(fixture.Source.Waiting.Task.IsCompleted);
        Assert.False(Directory.Exists(fixture.Root));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RealPlaybackTakesOverPreparationWithOneUpstreamFetch(bool openingResponse, bool supportsRanges)
    {
        await using var fixture = new PreparationStreamFixture(supportsRanges);
        var context = Context() with { IsPreparation = true, RequestedItemId = "ext-apple-musickit-song-next" };
        var opener = new ProviderStreamOpener(fixture.Gateway, new ProtocolStreamingResponseAdapter(), fixture.Cache);
        var preparing = opener.PrepareAsync(context, context.RequestedItemId);
        try
        {
            await fixture.Opening.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (!openingResponse)
            {
                fixture.OpenResponse.TrySetResult();
                await fixture.Source.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            var playing = fixture.Gateway.OpenStreamAsync(Context(), "apple-musickit", "next", ProviderAudioQuality.Any, null);
            fixture.OpenResponse.TrySetResult();
            using var response = (await playing.WaitAsync(TimeSpan.FromSeconds(10)))!.Response;
            await preparing.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, fixture.Fetches);
            Assert.False(fixture.Source.Release.Task.IsCompleted);
            fixture.Source.Release.TrySetResult();
            Assert.Equal(fixture.Source.Bytes, await response.Content.ReadAsByteArrayAsync());
            Assert.Equal(1, fixture.Fetches);
        }
        finally
        {
            fixture.OpenResponse.TrySetResult();
            fixture.Source.Release.TrySetResult();
            await preparing.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task PreparationSkipsAnUpcomingItemWhileTheClientIsOpeningItsLease()
    {
        await using var fixture = new PreparationStreamFixture();
        var playing = fixture.Gateway.OpenStreamAsync(Context(), "apple-musickit", "next", ProviderAudioQuality.Any, null);
        try
        {
            await fixture.Opening.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var preparing = fixture.Gateway.OpenStreamAsync(Context() with { IsPreparation = true },
                "apple-musickit", "next", ProviderAudioQuality.Any, null);
            Assert.Null(await preparing.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(1, fixture.Fetches);
        }
        finally
        {
            fixture.OpenResponse.TrySetResult();
            fixture.Source.Release.TrySetResult();
            (await playing.WaitAsync(TimeSpan.FromSeconds(10)))?.Response.Dispose();
        }
    }

    [Fact]
    public async Task QueueCancellationAfterOpeningStillStopsAnUnclaimedPreparation()
    {
        await using var fixture = new PreparationStreamFixture();
        using var cancellation = new CancellationTokenSource();
        var original = Context();
        var context = new ProtocolExecutionContext(original.Protocol, original.BackendInstanceId,
            original.VerifiedBackendPrincipalId, original.Principal, original.CorrelationId, original.Deadline,
            cancellation.Token, original.Client)
        { IsPreparation = true };
        var opener = new ProviderStreamOpener(fixture.Gateway, new ProtocolStreamingResponseAdapter(), fixture.Cache);
        fixture.OpenResponse.TrySetResult();
        var preparing = opener.PrepareAsync(context, "ext-apple-musickit-song-next");
        try
        {
            await fixture.Source.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            await preparing.WaitAsync(TimeSpan.FromSeconds(10));
            await fixture.Source.Closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, fixture.Fetches);
        }
        finally
        {
            fixture.Source.Release.TrySetResult();
            await preparing.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private sealed class PreparationStreamFixture : IAsyncDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        public string Root => root;
        public readonly TaskCompletionSource Opening = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource OpenResponse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly PreparationSource Source = new();
        public readonly ManagedTrackCacheService Cache;
        public readonly ProtocolProviderGateway Gateway;
        public int Fetches;

        public PreparationStreamFixture(bool supportsRanges = true, string provider = "apple-musickit", string container = "flac",
            Func<ProviderRouteRequest, ProviderAccountContext?>? account = null)
        {
            var apple = Capability(provider, ProviderOutcome<ProviderStreamLease>.Success(new(
                "lease", new Uri("https://media.example.test/audio"), DateTimeOffset.UtcNow.AddMinutes(1),
                supportsRanges, supportsRanges, new($"audio/{container}", container, container), ProviderStreamRetryBehavior.DoNotRetry,
                async (request, cancellationToken) =>
                {
                    var fetch = Interlocked.Increment(ref Fetches);
                    Opening.TrySetResult();
                    await OpenResponse.Task.WaitAsync(cancellationToken);
                    if (supportsRanges && request.Headers.Range?.Ranges.Single().From is { } start && start > 0)
                    {
                        var end = request.Headers.Range.Ranges.Single().To ?? Source.Bytes.Length - 1;
                        var partial = new HttpResponseMessage(HttpStatusCode.PartialContent)
                        { Content = new ByteArrayContent(Source.Bytes[(int)start..((int)end + 1)]) };
                        partial.Content.Headers.ContentRange = new(start, end, Source.Bytes.Length);
                        return partial;
                    }
                    var response = new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StreamContent(fetch == 1 ? Source : new MemoryStream(Source.Bytes)) };
                    response.Content.Headers.ContentLength = Source.Bytes.Length;
                    return response;
                })));
            var registry = Registry(apple.Object);
            var router = new Mock<IProviderRouter>();
            router.Setup(item => item.PlanAsync<IProviderStreamingCapability>(It.IsAny<ProviderRouteRequest>()))
                .ReturnsAsync((ProviderRouteRequest request) =>
                {
                    var plan = Plan(request, registry, apple.Object);
                    if (account == null) return plan;
                    return plan with
                    {
                        Candidates = plan.Candidates.Select(candidate => candidate with
                        {
                            Context = new ProviderExecutionContext(request.Actor, provider, account(request), request.Policy,
                            request.OperationId, request.CorrelationId, request.Deadline, request.CancellationToken)
                        }).ToArray()
                    };
                });
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Library:DownloadPath"] = root }).Build();
            Cache = new(config, Options.Create(new SubsonicSettings { StorageMode = StorageMode.Cache }),
                Mock.Of<ILocalLibraryService>(), NullLogger<ManagedTrackCacheService>.Instance);
            Gateway = new(router.Object, registry, new HttpClientFactory(), config, managedTrackCache: Cache);
        }

        public async ValueTask DisposeAsync()
        {
            OpenResponse.TrySetResult();
            Source.Release.TrySetResult();
            await Cache.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class PreparationSource : Stream
    {
        public readonly byte[] Bytes = Enumerable.Range(0, 16).Select(value => (byte)value).ToArray();
        public readonly TaskCompletionSource Waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int position;
        protected override void Dispose(bool disposing) { Closed.TrySetResult(); base.Dispose(disposing); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (position > 0)
            {
                Waiting.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            var count = Math.Min(buffer.Length, position == 0 ? 1 : Bytes.Length - position);
            Bytes.AsMemory(position, count).CopyTo(buffer);
            position += count;
            return count;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData("deezer", false, false)]
    [InlineData("apple-musickit", true, false)]
    [InlineData("apple-musickit", false, true)]
    public async Task PreparationUsesNormalSelectedRouteAndNeverSkipsToApple(string selected, bool opens, bool cached)
    {
        var apple = Streaming("apple-musickit", (_, _) => Task.FromResult(AudioResponse()));
        var other = Streaming("deezer", (_, _) => Task.FromResult(AudioResponse()));
        var registry = Registry(apple.Object, other.Object);
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        router.Setup(item => item.PlanAsync<IProviderStreamingCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) => Plan(request, registry,
                selected == "deezer" ? other.Object : apple.Object, selected == "deezer" ? apple.Object : other.Object));
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Library:DownloadPath"] = root }).Build();
        var path = Path.Combine(root, "cached.mp3");
        if (cached) { Directory.CreateDirectory(root); await File.WriteAllBytesAsync(path, [1, 2, 3, 4]); }
        var library = new Mock<ILocalLibraryService>();
        library.Setup(item => item.GetLocalPathForExternalSongAsync(It.IsAny<allstarr.Core.Downloads.DownloadedSongMappingScope>(),
            "apple-musickit", "apple-musickit-track")).ReturnsAsync(cached ? path : null);
        await using var cache = new ManagedTrackCacheService(config, Options.Create(new SubsonicSettings { StorageMode = StorageMode.Cache }),
            library.Object, NullLogger<ManagedTrackCacheService>.Instance);
        using var activity = new PlaybackDeliveryActivityStore();
        var identities = new Mock<ITrackIdentityService>(MockBehavior.Strict);
        var gateway = new ProtocolProviderGateway(router.Object, registry, new HttpClientFactory(), config,
            managedTrackCache: cache, playbackActivity: activity, identities: identities.Object);
        var context = Context() with { IsPreparation = true };
        using var current = AudioResponse();
        activity.StreamOpened(context with { IsPreparation = false }, "current", ProviderAudioQuality.Lossless,
            new(current, new("current", new Uri("https://media.example.test/audio"), context.Deadline, true, true,
                new("audio/flac", "flac", "flac"), ProviderStreamRetryBehavior.DoNotRetry), "deezer", "current"));
        var before = activity.StreamFor(context.Actor!.EffectiveUserId, "device", "current");
        var result = await gateway.OpenStreamAsync(context, "allstarr", "source-track", ProviderAudioQuality.Lossless, null);
        Assert.Equal(opens, result != null);
        result?.Response.Dispose();
        apple.Verify(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderStreamLeaseRequest>()),
            opens ? Times.Once : Times.Never);
        other.Verify(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderStreamLeaseRequest>()), Times.Never);
        Assert.Null(activity.StreamFor(context.Actor!.EffectiveUserId, "device", "ext-allstarr-song-source-track"));
        Assert.Same(before, activity.StreamFor(context.Actor!.EffectiveUserId, "device", "current"));
        identities.VerifyNoOtherCalls();
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Assert.False(activity.WasDelivered(context.Actor.EffectiveUserId, "ext-allstarr-song-source-track", "device"));
    }

    [Fact]
    public async Task PreparationDataSaverDoesNotPlanOrOpenARoute()
    {
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        var gateway = new ProtocolProviderGateway(router.Object, new ProviderRegistry([]), new HttpClientFactory());
        Assert.Null(await gateway.OpenStreamAsync(Context() with { IsPreparation = true }, "apple-download", "next",
            ProviderAudioQuality.DataSaver, null));
        router.VerifyNoOtherCalls();
    }
}
