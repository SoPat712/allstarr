using System.Net;
using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
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
        await fixture.Source.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(2));
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
        await fixture.Source.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(2));
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
    public async Task ActiveUnknownLength_RangeRemainsFull200FromByteZeroWithoutRangeHeaders()
    {
        await using var fixture = new ProgressiveFixture(knownLength: false);
        await fixture.StartAsync();
        await fixture.Source.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var cached = (await fixture.OpenAsync())!.Response;
        var firstWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var execution = ExecuteAsync(cached, "bytes=10-", firstWrite);
        await firstWrite.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(execution.IsCompleted);
        fixture.Source.Release.TrySetResult();
        var (context, bytes) = await execution;
        Assert.Equal(200, context.Response.StatusCode);
        Assert.False(context.Response.Headers.ContainsKey("Content-Range"));
        Assert.False(context.Response.Headers.ContainsKey("Accept-Ranges"));
        Assert.Null(context.Response.ContentLength);
        Assert.Equal(fixture.Bytes, bytes);
    }

    [Fact]
    public async Task ActiveTransfer_IsIsolatedByAccountRevisionAndQuality()
    {
        await using var fixture = new ProgressiveFixture(knownLength: true);
        await fixture.StartAsync();
        await fixture.Source.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(2));
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
        await fixture.Source.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(2));
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
        await using var fixture = new ProgressiveFixture(knownLength: true);
        await fixture.StartAsync();
        await fixture.Source.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(2));
        fixture.Response.Dispose();
        using var cached = (await fixture.OpenAsync())!.Response;
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
        await fixture.Source.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await fixture.Service.DisposeAsync();
        Assert.True(fixture.Source.Closed);
        Assert.Null(fixture.PublishedPath);
        Assert.Empty(Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task LastReaderAbandoned_CancelsAfterSeekGraceWithoutPublishing()
    {
        await using var fixture = new ProgressiveFixture(knownLength: true);
        await fixture.StartAsync();
        await fixture.Source.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(2));
        fixture.Response.Dispose();
        await fixture.Source.ClosedSignal.Task.WaitAsync(TimeSpan.FromSeconds(15));
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
        await fixture.Source.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(2));
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
        await fixture.Source.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(2));
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
            Mock.Of<IHttpClientFactory>(MockBehavior.Strict), managedTrackCache: fixture.Service, playbackActivity: activity);
        if (previousRevision != 4)
        {
            var error = await Assert.ThrowsAsync<HttpRequestException>(() => gateway.OpenStreamAsync(protocol,
                "deezer", "track-1", ProviderAudioQuality.Any, "bytes=2-3"));
            Assert.Equal(HttpStatusCode.Conflict, error.StatusCode);
            capability.Verify(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(),
                It.IsAny<ProviderStreamLeaseRequest>()), Times.Never);
            return;
        }
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
        await fixture.Source.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(2));
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
        await fixture.Source.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(2));
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
        await fixture.Source.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(2));
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
        await fixture.Source.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var cached = (await fixture.OpenAsync())!.Response;
        var reader = await cached.Content.ReadAsStreamAsync();
        Assert.True(reader.CanSeek);
        Assert.Equal(16, reader.Length);
        fixture.Source.Release.TrySetResult();
        Assert.Equal(fixture.Bytes, await fixture.Response.Content.ReadAsByteArrayAsync());
    }

    private static async Task<(HttpContext Context, byte[] Bytes)> ExecuteAsync(HttpResponseMessage response,
        string range, TaskCompletionSource? firstWrite = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControllers();
        await using var provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Method = "GET";
        context.Request.Headers.Range = range;
        using var body = new SignalingBody(firstWrite);
        context.Response.Body = body;
        var result = await new ProtocolStreamingResponseAdapter().CreateAsync(context, response, CancellationToken.None, true);
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
        public readonly ProviderExternalResourceId Track = new("deezer", ProviderResourceKind.Track, "track-1");
        public readonly ControlledSource Source;
        public readonly HttpResponseMessage Response;
        public readonly ManagedTrackCacheService Service;
        public string? PublishedPath;

        public ProgressiveFixture(bool knownLength, bool shortBody = false, bool fail = false, IHostApplicationLifetime? lifetime = null, bool registrationFails = false, int? declaredLength = null)
        {
            Source = new ControlledSource(Bytes, shortBody, fail);
            Response = new(HttpStatusCode.OK) { Content = new StreamContent(Source) };
            Response.Content.Headers.ContentType = new("audio/flac");
            if (knownLength) Response.Content.Headers.ContentLength = declaredLength ?? Bytes.Length;
            var local = new Mock<ILocalLibraryService>(MockBehavior.Strict);
            local.Setup(item => item.GetLocalPathForExternalSongAsync(It.IsAny<DownloadedSongMappingScope>(), "deezer", "track-1"))
                .ReturnsAsync(() => PublishedPath);
            local.Setup(item => item.RegisterDownloadedSongAsync(It.IsAny<DownloadedSongMappingScope>(), It.IsAny<Song>(), It.IsAny<string>()))
                .Callback<DownloadedSongMappingScope, Song, string>((_, _, path) => PublishedPath = path).Returns(Task.CompletedTask);
            if (registrationFails)
                local.Setup(item => item.RegisterDownloadedSongAsync(It.IsAny<DownloadedSongMappingScope>(), It.IsAny<Song>(), It.IsAny<string>()))
                    .ThrowsAsync(new IOException("Registration unavailable"));
            Service = CreateService(Root, "Cache", local.Object, lifetime);
        }

        public async Task StartAsync()
        {
            await Service.WrapAsync(ProviderStream(Response) with
            { ServingAccountId = Scope.ProviderAccountId, ServingAccountRevision = 4 }, Context(), "deezer", "track-1", false,
                () => Task.FromResult<Song?>(new Song { Title = "Song", Artist = "Artist", Album = "Album" }), CancellationToken.None);
            var reader = await Response.Content.ReadAsStreamAsync();
            var first = new byte[1];
            Assert.Equal(1, await reader.ReadAsync(first));
            Assert.Equal(0, first[0]);
            if (reader.CanSeek) reader.Position = 0;
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
