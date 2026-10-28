using System.Net;
using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Core.Routing;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using allstarr.Services.Common;
using allstarr.Services.Local;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;

namespace allstarr.Tests;

public sealed partial class ManagedTrackCacheServiceTests
{
    [Fact]
    public async Task ReadAhead_ContinuesWithoutReaderDemandAndPublishesOnlyVerifiedEof()
    {
        await using var fixture = new ProgressiveFixture(knownLength: true);
        await fixture.StartAsync();
        await fixture.Source.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Null(fixture.PublishedPath);
        Assert.Equal(8, new FileInfo(Assert.Single(Directory.GetFiles(fixture.Root, "*.partial", SearchOption.AllDirectories))).Length);
        fixture.Source.Release.TrySetResult();
        Assert.Equal(fixture.Bytes, await fixture.Response.Content.ReadAsByteArrayAsync());
        Assert.NotNull(fixture.PublishedPath);
        Assert.Equal(fixture.Bytes, await File.ReadAllBytesAsync(fixture.PublishedPath));
    }

    [Theory]
    [InlineData(2, 3)]
    [InlineData(10, 11)]
    public async Task ActiveKnownLength_ServesExactMvcRangeAndWaitsForBytes(int start, int end)
    {
        await using var fixture = new ProgressiveFixture(knownLength: true);
        await fixture.StartAsync();
        await fixture.Source.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(30));
        using var cached = (await fixture.OpenAsync())!.Response;
        var execution = ExecuteAsync(cached, $"bytes={start}-{end}");
        if (start >= 8) Assert.False(execution.IsCompleted);
        fixture.Source.Release.TrySetResult();
        var (context, bytes) = await execution;
        Assert.Equal(206, context.Response.StatusCode);
        Assert.Equal($"bytes {start}-{end}/16", context.Response.Headers.ContentRange.ToString());
        Assert.Equal(2, context.Response.ContentLength);
        Assert.Equal(fixture.Bytes[start..(end + 1)], bytes);
    }

    [Fact]
    public async Task ActiveUnknownLength_RangeWaitsForCompletion()
    {
        await using var fixture = new ProgressiveFixture(knownLength: false);
        await fixture.StartAsync();
        await fixture.Source.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(30));
        using var cached = (await fixture.OpenAsync())!.Response;
        var execution = ExecuteAsync(cached, "bytes=10-");
        Assert.False(execution.IsCompleted);
        fixture.Source.Release.TrySetResult();
        var (context, bytes) = await execution;
        Assert.Equal(206, context.Response.StatusCode);
        Assert.Equal("bytes 10-15/16", context.Response.Headers.ContentRange.ToString());
        Assert.Equal("bytes", context.Response.Headers.AcceptRanges.ToString());
        Assert.Equal(6, context.Response.ContentLength);
        Assert.Equal(fixture.Bytes[10..], bytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActiveUnknownLength_RangePreparationHonorsCancellationAndClock(bool cancel)
    {
        var clock = new ManualTimeProvider();
        await using var fixture = new ProgressiveFixture(knownLength: false, timeProvider: clock);
        await fixture.StartAsync();
        await fixture.Source.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var cached = (await fixture.OpenAsync())!.Response;
        using var cancellation = new CancellationTokenSource();
        var http = new DefaultHttpContext();
        http.Request.Method = "GET";
        http.Request.Headers.Range = "bytes=10-";
        var opening = new ProtocolStreamingResponseAdapter().CreateAsync(http, cached, cancellation.Token, true);
        Assert.False(opening.IsCompleted);
        await clock.WaitForTimersAsync(TimeSpan.FromSeconds(8));
        if (cancel)
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        else
        {
            clock.Advance(TimeSpan.FromSeconds(8));
            await Assert.ThrowsAsync<TimeoutException>(() => opening.WaitAsync(TimeSpan.FromSeconds(10)));
        }
    }

    [Fact]
    public async Task ActiveKnownLength_FutureRangeHonorsInjectedReadDeadline()
    {
        var clock = new ManualTimeProvider();
        await using var fixture = new ProgressiveFixture(knownLength: true, timeProvider: clock);
        await fixture.StartAsync();
        await fixture.Source.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var cached = (await fixture.OpenAsync())!.Response;
        var execution = ExecuteAsync(cached, "bytes=10-");
        await clock.WaitForTimersAsync(TimeSpan.FromSeconds(8));
        clock.Advance(TimeSpan.FromSeconds(8));
        await Assert.ThrowsAsync<TimeoutException>(() => execution.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Theory]
    [InlineData("apple-musickit", false)]
    [InlineData("deezer", true)]
    public async Task SequentialPlayback_InitialRangeWaitsForCacheAndReturnsExactBytes(string provider, bool knownLength)
    {
        await using var fixture = new ProgressiveFixture(knownLength, provider: provider);
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        var protocol = Context();
        var sequential = new ProviderStreamLease("sequential", new Uri("https://media.example.test/audio"),
            protocol.Deadline, false, false, provider == "deezer" ? new("audio/mpeg", "mp3", "mp3") : new("audio/flac", "flac", "flac"),
            ProviderStreamRetryBehavior.DoNotRetry);
        gateway.Setup(item => item.OpenStreamAsync(protocol, provider, "track-1", ProviderAudioQuality.Any, "bytes=10-", false))
            .ReturnsAsync(new ProtocolProviderStream(fixture.Response, sequential, provider, "track-1"));
        gateway.Setup(item => item.GetSongAsync(protocol, provider, "track-1")).ReturnsAsync((Song?)null);
        using var services = new ServiceCollection().AddLogging().AddControllers().Services.BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        http.Request.Method = "GET";
        http.Request.Headers.Range = "bytes=10-";
        using var body = new MemoryStream();
        http.Response.Body = body;
        var opening = new allstarr.Core.Music.ProviderStreamOpener(gateway.Object,
            new ProtocolStreamingResponseAdapter(), fixture.Service).OpenAsync(http, protocol, provider, "track-1", ProviderAudioQuality.Any);
        if (!knownLength)
        {
            await fixture.Source.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(opening.IsCompleted);
        }
        fixture.Source.Release.TrySetResult();
        var result = await opening.WaitAsync(TimeSpan.FromSeconds(10));
        await result.ExecuteResultAsync(new ActionContext(http, new(), new()));
        Assert.Equal(206, http.Response.StatusCode);
        Assert.Equal("bytes 10-15/16", http.Response.Headers.ContentRange.ToString());
        Assert.Equal("bytes", http.Response.Headers.AcceptRanges.ToString());
        Assert.Equal(6, http.Response.ContentLength);
        Assert.Equal(fixture.Bytes[10..], body.ToArray());
    }

    [Fact]
    public async Task SequentialPlayback_PermanentStorageUsesTemporarySeekFileWithoutPublishing()
    {
        await using var fixture = new ProgressiveFixture(knownLength: true, mode: "Permanent");
        var sequential = new ProviderStreamLease("sequential", new Uri("https://media.example.test/audio"),
            DateTimeOffset.UtcNow.AddMinutes(1), false, false, new("audio/flac", "flac", "flac"), ProviderStreamRetryBehavior.DoNotRetry);
        await fixture.Service.WrapAsync(new(fixture.Response, sequential, "deezer", "track-1",
            fixture.Scope.ProviderAccountId, ServingAccountRevision: 4), Context(), "deezer", "track-1", false,
            () => Task.FromResult<Song?>(null), CancellationToken.None);
        var initial = await fixture.Response.Content.ReadAsStreamAsync();
        Assert.Equal(0, initial.ReadByte());
        await fixture.Source.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var opened = (await fixture.OpenAsync())!.Response;
        var execution = ExecuteAsync(opened, "bytes=10-");
        Assert.False(execution.IsCompleted);
        fixture.Source.Release.TrySetResult();
        var (http, bytes) = await execution;
        Assert.Equal(206, http.Response.StatusCode);
        Assert.Equal(fixture.Bytes[10..], bytes);
        await fixture.Service.DisposeAsync();
        Assert.Null(fixture.PublishedPath);
        Assert.Empty(Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task CompletedTemporarySeekFile_RemainsAvailableWhilePlaybackIsOpen()
    {
        await using var fixture = new ProgressiveFixture(knownLength: true, mode: "Permanent");
        var sequential = new ProviderStreamLease("sequential", new Uri("https://media.example.test/audio"),
            DateTimeOffset.UtcNow.AddMinutes(1), false, false, new("audio/flac", "flac", "flac"), ProviderStreamRetryBehavior.DoNotRetry);
        await fixture.Service.WrapAsync(new(fixture.Response, sequential, "deezer", "track-1",
            fixture.Scope.ProviderAccountId, ServingAccountRevision: 4), Context(), "deezer", "track-1", false,
            () => Task.FromResult<Song?>(null), CancellationToken.None);
        fixture.Source.Release.TrySetResult();
        Assert.Equal(fixture.Bytes, await fixture.Response.Content.ReadAsByteArrayAsync());
        using var cached = (await fixture.OpenAsync())!.Response;
        var (http, bytes) = await ExecuteAsync(cached, "bytes=10-");
        Assert.Equal(206, http.Response.StatusCode);
        Assert.Equal(fixture.Bytes[10..], bytes);
        Assert.Null(fixture.PublishedPath);
    }

    [Theory]
    [InlineData("apple-musickit")]
    [InlineData("deezer")]
    public async Task CompletedSequentialCache_ServesExactRanges(string provider)
    {
        await using var fixture = new ProgressiveFixture(knownLength: false, provider: provider);
        await fixture.StartAsync();
        fixture.Source.Release.TrySetResult();
        Assert.Equal(fixture.Bytes, await fixture.Response.Content.ReadAsByteArrayAsync());
        using var opened = (await fixture.OpenAsync())!.Response;
        var (http, bytes) = await ExecuteAsync(opened, "bytes=10-");
        Assert.Equal(206, http.Response.StatusCode);
        Assert.Equal("bytes 10-15/16", http.Response.Headers.ContentRange.ToString());
        Assert.Equal("bytes", http.Response.Headers.AcceptRanges.ToString());
        Assert.Equal(6, http.Response.ContentLength);
        Assert.Equal(fixture.Bytes[10..], bytes);
    }

    [Fact]
    public async Task ActiveTransfer_IsIsolatedByAccountRevisionAndQuality()
    {
        await using var fixture = new ProgressiveFixture(knownLength: true);
        await fixture.StartAsync();
        await fixture.Source.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Null(await fixture.Service.TryOpenAsync(fixture.Track,
            new(Guid.NewGuid(), ProviderAudioQuality.Any), CancellationToken.None, 4));
        Assert.Null(await fixture.Service.TryOpenAsync(fixture.Track, fixture.Scope, CancellationToken.None, 5));
        Assert.Null(await fixture.Service.TryOpenAsync(fixture.Track,
            new(fixture.Scope.ProviderAccountId, ProviderAudioQuality.Lossless), CancellationToken.None, 4));
        using var cached = (await fixture.OpenAsync())!.Response;
        Assert.Equal(4, (await fixture.OpenAndDisposeAsync())!.ServingAccountRevision);
        Assert.True((await cached.Content.ReadAsStreamAsync()).CanSeek);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IncompleteOrFailedTransfer_NeverPublishesAndDeletesOwnedPartial(bool shortBody)
    {
        await using var fixture = new ProgressiveFixture(knownLength: true, shortBody: shortBody, fail: !shortBody);
        await fixture.StartAsync();
        await fixture.Source.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(30));
        fixture.Source.Release.TrySetResult();
        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Response.Content.CopyToAsync(Stream.Null));
        Assert.IsType<IOException>(exception.InnerException);
        Assert.DoesNotContain("private", exception.ToString());
        Assert.DoesNotContain("https://", exception.ToString());
        Assert.Null(fixture.PublishedPath);
        await fixture.Service.DisposeAsync();
        Assert.Empty(Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ClosingOldRequest_AllowsSeekReattachmentWithoutCancelingProducer()
    {
        var clock = new ManualTimeProvider();
        await using var fixture = new ProgressiveFixture(knownLength: true, timeProvider: clock);
        await fixture.StartAsync();
        await fixture.Source.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(30));
        fixture.Response.Dispose();
        await clock.WaitForTimersAsync(TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromSeconds(5));
        using var cached = (await fixture.OpenAsync())!.Response;
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.False(fixture.Source.Closed);
        var reader = await cached.Content.ReadAsStreamAsync();
        reader.Position = 10;
        using var canceled = new CancellationTokenSource();
        var pending = reader.ReadAsync(new byte[1], canceled.Token).AsTask();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        fixture.Source.Release.TrySetResult();
        Assert.Equal(10, reader.ReadByte());
    }

    [Fact]
    public async Task ServiceShutdown_CancelsOwnedProducerAndCleansPartialWithoutPublication()
    {
        await using var fixture = new ProgressiveFixture(knownLength: true);
        await fixture.StartAsync();
        await fixture.Source.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await fixture.Service.DisposeAsync();
        Assert.True(fixture.Source.Closed);
        Assert.Null(fixture.PublishedPath);
        Assert.Empty(Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task LastReaderAbandoned_CancelsAfterSeekGraceWithoutPublishing()
    {
        var clock = new ManualTimeProvider();
        await using var fixture = new ProgressiveFixture(knownLength: true, timeProvider: clock);
        await fixture.StartAsync();
        await fixture.Source.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(30));
        fixture.Response.Dispose();
        await clock.WaitForTimersAsync(TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromSeconds(9));
        Assert.False(fixture.Source.Closed);
        clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.Source.ClosedSignal.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.Service.DisposeAsync();
        Assert.Null(fixture.PublishedPath);
        Assert.Empty(Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task HostStopping_CancelsBlockedPreparationAndCleansOwnedTransfer()
    {
        using var lifetime = new CacheLifetime();
        await using var fixture = new ProgressiveFixture(knownLength: true, lifetime: lifetime);
        await fixture.StartAsync();
        await fixture.Source.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(30));
        lifetime.Stopping.Cancel();
        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Response.Content.CopyToAsync(Stream.Null));
        Assert.IsType<IOException>(failure.InnerException);
        await fixture.Service.DisposeAsync();
        Assert.True(fixture.Source.Closed);
        Assert.Null(fixture.PublishedPath);
        Assert.Empty(Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories));
    }

    private sealed class CacheLifetime : IHostApplicationLifetime, IDisposable
    {
        public readonly CancellationTokenSource Stopping = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public CancellationToken ApplicationStopping => Stopping.Token;
        public void StopApplication() => Stopping.Cancel();
        public void Dispose() => Stopping.Dispose();
    }

    [Theory]
    [InlineData(4)]
    [InlineData(3)]
    public async Task AuthorizedGateway_ReusesExactRevisionWithoutOpeningAnotherLease(long previousRevision)
    {
        await using var fixture = new ProgressiveFixture(knownLength: true);
        await fixture.StartAsync();
        await fixture.Source.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var capability = new Mock<IProviderStreamingCapability>(MockBehavior.Strict);
        capability.SetupGet(item => item.ProviderId).Returns("deezer");
        capability.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Streaming);
        var descriptor = new ProviderDescriptor("deezer", "deezer", "Fixture", ProviderOrigin.BuiltIn, "1", "1.0",
            [new(ProviderCapabilityKind.Streaming, ProviderCapabilitySupportState.Supported, ProviderAccountRequirement.None,
                "1.0", ["getStreamLease"])], new ProviderPermissionDescriptor());
        var registry = new ProviderRegistry([new ProviderRegistration(descriptor, [capability.Object])]);
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        router.Setup(item => item.PlanAsync<IProviderStreamingCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) =>
            {
                var account = new ProviderAccountContext(fixture.Scope.ProviderAccountId!.Value, "deezer",
                    ProviderAccountScope.Personal, 4, ownerUserId: request.Actor.EffectiveUserId,
                    secretReferenceId: Guid.NewGuid());
                var context = new ProviderExecutionContext(request.Actor, "deezer", account, request.Policy,
                    request.OperationId, request.CorrelationId, request.Deadline, request.CancellationToken);
                var candidate = new ProviderRouteCandidate<IProviderStreamingCapability>(0, descriptor,
                    descriptor.Capabilities.Single(), capability.Object, context, fixture.Track);
                return new ProviderRoutePlan<IProviderStreamingCapability>(request, [candidate],
                    new(request.CorrelationId, ProviderCapabilityKind.Streaming, "deezer", account.AccountId, []));
            });
        using var activity = new PlaybackDeliveryActivityStore();
        var protocol = Context();
        using var previousResponse = new HttpResponseMessage(HttpStatusCode.OK);
        activity.StreamOpened(protocol, "ext-deezer-song-track-1", ProviderAudioQuality.Any,
            ProviderStream(previousResponse) with
            { ServingExternalId = "track-1", ServingAccountId = fixture.Scope.ProviderAccountId, ServingAccountRevision = previousRevision });
        var gateway = new ProtocolProviderGateway(router.Object, registry,
            Mock.Of<IHttpClientFactory>(MockBehavior.Strict), managedTrackCache: fixture.Service, playbackActivity: activity, audioVerifier: FixtureAudioVerifier.Instance);
        var opened = await gateway.OpenStreamAsync(protocol, "deezer", "track-1", ProviderAudioQuality.Any, "bytes=2-3");
        Assert.NotNull(opened);
        Assert.True(opened.IsCached);
        Assert.Equal(4, opened.ServingAccountRevision);
        using var response = opened.Response;
        var (http, bytes) = await ExecuteAsync(response, "bytes=2-3");
        Assert.Equal(206, http.Response.StatusCode);
        Assert.Equal("bytes 2-3/16", http.Response.Headers.ContentRange.ToString());
        Assert.Equal(new byte[] { 2, 3 }, bytes);
        router.VerifyAll();
        capability.Verify(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(),
            It.IsAny<ProviderStreamLeaseRequest>()), Times.Never);
    }

    [Fact]
    public async Task ActiveKnownLength_OutOfBoundsReturns416WithoutInventingASeekWait()
    {
        await using var fixture = new ProgressiveFixture(knownLength: true);
        await fixture.StartAsync();
        await fixture.Source.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(30));
        using var cached = (await fixture.OpenAsync())!.Response;
        var (context, bytes) = await ExecuteAsync(cached, "bytes=16-");
        Assert.Equal(416, context.Response.StatusCode);
        Assert.Equal("bytes */16", context.Response.Headers.ContentRange.ToString());
        Assert.Empty(bytes);
    }

    [Fact]
    public async Task FailedPublication_PreservesCompleteAudioButLeavesNoArtifactOrMapping()
    {
        await using var fixture = new ProgressiveFixture(knownLength: true, registrationFails: true);
        await fixture.StartAsync();
        await fixture.Source.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(30));
        fixture.Source.Release.TrySetResult();
        Assert.Equal(fixture.Bytes, await fixture.Response.Content.ReadAsByteArrayAsync());
        await fixture.Service.DisposeAsync();
        Assert.Null(fixture.PublishedPath);
        Assert.Empty(Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task OversizedBody_IsRejectedBeforePublication()
    {
        await using var fixture = new ProgressiveFixture(knownLength: true, declaredLength: 12);
        await fixture.StartAsync();
        await fixture.Source.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(30));
        fixture.Source.Release.TrySetResult();
        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Response.Content.CopyToAsync(Stream.Null));
        Assert.IsType<IOException>(failure.InnerException);
        await fixture.Service.DisposeAsync();
        Assert.Null(fixture.PublishedPath);
        Assert.Empty(Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Complete206_UsesContentRangeTotalWhenContentLengthIsAbsent()
    {
        await using var fixture = new ProgressiveFixture(knownLength: false);
        fixture.Response.StatusCode = HttpStatusCode.PartialContent;
        fixture.Response.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(0, 15, 16);
        await fixture.StartAsync();
        await fixture.Source.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(30));
        using var cached = (await fixture.OpenAsync())!.Response;
        var reader = await cached.Content.ReadAsStreamAsync();
        Assert.True(reader.CanSeek);
        Assert.Equal(16, reader.Length);
        fixture.Source.Release.TrySetResult();
        Assert.Equal(fixture.Bytes, await fixture.Response.Content.ReadAsByteArrayAsync());
    }

    private static async Task<(HttpContext Context, byte[] Bytes)> ExecuteAsync(HttpResponseMessage response,
        string range, TaskCompletionSource? firstWrite = null, CancellationToken cancellationToken = default)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControllers();
        await using var provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = provider, RequestAborted = cancellationToken };
        context.Request.Method = "GET";
        context.Request.Headers.Range = range;
        using var body = new SignalingBody(firstWrite);
        context.Response.Body = body;
        var result = await new ProtocolStreamingResponseAdapter().CreateAsync(context, response, cancellationToken, true);
        await result.ExecuteResultAsync(new ActionContext(context, new RouteData(), new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor()));
        return (context, body.ToArray());
    }

    private sealed class SignalingBody(TaskCompletionSource? firstWrite) : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var result = base.WriteAsync(buffer, cancellationToken);
            firstWrite?.TrySetResult();
            return result;
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var result = base.WriteAsync(buffer, offset, count, cancellationToken);
            firstWrite?.TrySetResult();
            return result;
        }
    }

    private sealed class ProgressiveFixture : IAsyncDisposable
    {
        public readonly string Root = CreateRoot();
        public readonly byte[] Bytes = Enumerable.Range(0, 16).Select(value => (byte)value).ToArray();
        public readonly DownloadedSongMappingScope Scope = new(Guid.NewGuid(), ProviderAudioQuality.Any);
        public readonly ProviderExternalResourceId Track;
        public readonly ControlledSource Source;
        public readonly HttpResponseMessage Response;
        public readonly ManagedTrackCacheService Service;
        public readonly TaskCompletionSource Published = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? PublishedPath;

        public ProgressiveFixture(bool knownLength, bool shortBody = false, bool fail = false, IHostApplicationLifetime? lifetime = null, bool registrationFails = false, int? declaredLength = null, string provider = "deezer", string mode = "Cache", TimeProvider? timeProvider = null)
        {
            Track = new(provider, ProviderResourceKind.Track, "track-1");
            Source = new ControlledSource(Bytes, shortBody, fail);
            Response = new(HttpStatusCode.OK) { Content = new StreamContent(Source) };
            Response.Content.Headers.ContentType = new("audio/flac");
            if (knownLength) Response.Content.Headers.ContentLength = declaredLength ?? Bytes.Length;
            var local = new Mock<ILocalLibraryService>(MockBehavior.Strict);
            local.Setup(item => item.GetLocalPathForExternalSongAsync(It.IsAny<DownloadedSongMappingScope>(), provider, "track-1"))
                .ReturnsAsync(() => PublishedPath);
            local.Setup(item => item.RegisterDownloadedSongAsync(It.IsAny<DownloadedSongMappingScope>(), It.IsAny<Song>(), It.IsAny<string>()))
                .Callback<DownloadedSongMappingScope, Song, string>((_, _, path) =>
                { PublishedPath = path; Published.TrySetResult(); }).Returns(Task.CompletedTask);
            if (registrationFails)
                local.Setup(item => item.RegisterDownloadedSongAsync(It.IsAny<DownloadedSongMappingScope>(), It.IsAny<Song>(), It.IsAny<string>()))
                    .ThrowsAsync(new IOException("Registration unavailable"));
            Service = CreateService(Root, mode, local.Object, lifetime, timeProvider);
        }

        public async Task StartAsync(Func<TrackMediaFacts, CancellationToken, Task>? record = null)
        {
            var stream = ProviderStream(Response) with
            { ServingProviderId = Track.ProviderId, ServingAccountId = Scope.ProviderAccountId, ServingAccountRevision = 4 };
            stream.FileFacts.RecordAsync = record;
            await Service.WrapAsync(stream, Context(), Track.ProviderId, "track-1", false,
                () => Task.FromResult<Song?>(new Song { Title = "Song", Artist = "Artist", Album = "Album" }), CancellationToken.None);
            using var startup = (await OpenAsync())!.Response;
            var reader = await startup.Content.ReadAsStreamAsync();
            var first = new byte[1];
            Assert.Equal(1, await reader.ReadAsync(first));
            Assert.Equal(0, first[0]);
        }
        public Task<ProtocolProviderStream?> OpenAsync() => Service.TryOpenAsync(Track, Scope, CancellationToken.None, 4);
        public async Task<ProtocolProviderStream?> OpenAndDisposeAsync()
        {
            var opened = await OpenAsync();
            opened?.Response.Dispose();
            return opened;
        }
        public async ValueTask DisposeAsync()
        {
            Response.Dispose();
            await Service.DisposeAsync();
            DeleteRoot(Root);
        }
    }

    private sealed class ControlledSource(byte[] bytes, bool shortBody, bool fail) : Stream
    {
        private int position;
        public readonly TaskCompletionSource Waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Closed;
        public readonly TaskCompletionSource ClosedSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (position >= 8)
            {
                Waiting.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
                if (fail) throw new IOException("private provider https://media.invalid/?token=private");
            }
            var length = shortBody ? 12 : bytes.Length;
            var count = Math.Min(buffer.Length, Math.Min(8, length - position));
            bytes.AsMemory(position, count).CopyTo(buffer);
            position += count;
            return count;
        }
        protected override void Dispose(bool disposing) { Closed = true; ClosedSignal.TrySetResult(); base.Dispose(disposing); }
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
}
