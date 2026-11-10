using System.Collections.Immutable;
using System.Diagnostics;
using System.Net;
using allstarr.Core.Capabilities;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Releases;
using allstarr.Core.Settings;
using allstarr.Models.Domain;
using allstarr.Services.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SkiaSharp;
using Xunit.Abstractions;

namespace allstarr.Tests;

public sealed class ReleaseArtworkReaderTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"allstarr-release-cover-{Guid.NewGuid():N}");
    private readonly Guid _owner = Guid.NewGuid();
    private readonly ProtocolExecutionContext _protocol = new(ProtocolKind.Subsonic, "fixture", "listener", null,
        "release-cover", DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None, new("cover-client", "cover-device"))
    {
        Policy = new(ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty,
            ImmutableHashSet<string>.Empty, "Lossless", .07),
        IsReadOnly = true
    };

    [Fact]
    public async Task Existing_album_bytes_remain_available_without_source_url_metadata()
    {
        using var cache = DiskCache();
        var media = new MediaAssetResolver(cache, NullLogger<MediaAssetResolver>.Instance);
        var bytes = Png(SKColors.Blue);
        var existing = await media.ResolveAsync(new(_owner, null, "deezer", "album", "album", Width: 384),
            _ => Task.FromResult<MediaAssetSource?>(new(bytes, "image/png")), 8 * 1024 * 1024);
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(value => value.GetStoredAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "album"))
            .ReturnsAsync((Album?)null);
        using var handler = new CoverHandler(_ => throw new InvalidOperationException("Cached artwork must avoid HTTP"));
        var reader = new ReleaseArtworkReader(gateway.Object, media, new CoverClients(handler),
            NullLogger<ReleaseArtworkReader>.Instance, cache);
        var actual = await Read(reader);
        Assert.NotNull(actual);
        Assert.True(actual.FromCache);
        Assert.Equal(existing!.Bytes, actual.Bytes);
        Assert.Equal(0, handler.RequestCount);
        gateway.Verify(value => value.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "album"), Times.Never);
    }

    [Fact]
    public async Task Disk_cache_keeps_the_same_variant_and_fallback_url_across_restart()
    {
        var original = Png(SKColors.Teal);
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(value => value.GetAlbumAsync(It.Is<ProtocolExecutionContext>(context => MatchesContext(context)), "deezer", "album"))
            .ReturnsAsync(new Album { CoverArtUrl = "https://covers.example.test/first.png" });
        using var handler = new CoverHandler(_ => original, token => Task.Delay(3500, token));
        ResolvedMediaAsset first;
        ResolvedMediaAsset repeated;
        double firstMilliseconds;
        double repeatedMilliseconds;
        using (var cache = DiskCache())
        {
            var reader = Reader(gateway.Object, cache, handler);
            var elapsed = Stopwatch.StartNew();
            first = (await Read(reader))!;
            firstMilliseconds = elapsed.Elapsed.TotalMilliseconds;
            elapsed.Restart();
            repeated = (await Read(reader))!;
            repeatedMilliseconds = elapsed.Elapsed.TotalMilliseconds;
        }

        using var restartedCache = DiskCache();
        var restartedReader = Reader(gateway.Object, restartedCache, handler);
        var restartElapsed = Stopwatch.StartNew();
        var restarted = await Read(restartedReader);
        var restartedMilliseconds = restartElapsed.Elapsed.TotalMilliseconds;
        var baseline = await new MediaAssetResolver(new TestMemoryApplicationCache(), NullLogger<MediaAssetResolver>.Instance)
            .ResolveAsync(new(_owner, null, "deezer", "album", "album", Width: 384),
                _ => Task.FromResult<MediaAssetSource?>(new(original, "image/png")), 8 * 1024 * 1024);

        Assert.NotNull(first);
        Assert.NotNull(repeated);
        Assert.NotNull(restarted);
        Assert.False(first.FromCache);
        Assert.True(repeated.FromCache);
        Assert.True(restarted.FromCache);
        Assert.Equal(baseline!.Bytes, first.Bytes);
        Assert.Equal(baseline.ContentType, first.ContentType);
        Assert.Equal(first.Bytes, repeated.Bytes);
        Assert.Equal(first.Bytes, restarted.Bytes);
        Assert.Equal(first.Sha256, restarted.Sha256);
        using var decoded = SKBitmap.Decode(restarted.Bytes);
        Assert.Equal(384, decoded.Width);
        Assert.Equal(256, decoded.Height);
        Assert.Equal(1, handler.RequestCount);
        gateway.Verify(value => value.GetAlbumAsync(It.Is<ProtocolExecutionContext>(context => MatchesContext(context)), "deezer", "album"), Times.Once);
        gateway.Verify(value => value.GetStoredAlbumAsync(It.Is<ProtocolExecutionContext>(context => MatchesContext(context)), "deezer", "album"), Times.Exactly(3));
        output.WriteLine($"Source delay: 3500 ms; first: {firstMilliseconds:F2} ms; repeated: {repeatedMilliseconds:F2} ms; " +
            $"restarted: {restartedMilliseconds:F2} ms; image requests: {handler.RequestCount}.");
    }

    [Fact]
    public async Task Stored_url_changes_and_distinct_release_ids_select_new_cached_bytes()
    {
        var firstBytes = Png(SKColors.Red);
        var secondBytes = Png(SKColors.Blue);
        var sourceUrl = "https://covers.example.test/first.png";
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(value => value.GetStoredAlbumAsync(It.Is<ProtocolExecutionContext>(context => MatchesContext(context)), "deezer", "album"))
            .ReturnsAsync(() => new Album { CoverArtUrl = sourceUrl });
        using var handler = new CoverHandler(uri => uri.AbsolutePath == "/first.png" ? firstBytes : secondBytes);
        using var cache = DiskCache();
        var reader = Reader(gateway.Object, cache, handler);

        var first = await Read(reader);
        var repeated = await Read(reader);
        sourceUrl = "https://covers.example.test/second.png";
        var changed = await Read(reader);
        var otherRelease = await Read(reader, "other-release");
        var changedRepeated = await Read(reader);

        Assert.NotNull(first);
        Assert.NotNull(repeated);
        Assert.NotNull(changed);
        Assert.NotNull(otherRelease);
        Assert.NotNull(changedRepeated);
        Assert.Equal(first.Bytes, repeated.Bytes);
        Assert.NotEqual(first.Sha256, changed.Sha256);
        Assert.Equal(changed.Bytes, otherRelease.Bytes);
        Assert.Equal(changed.Bytes, changedRepeated.Bytes);
        Assert.True(repeated.FromCache);
        Assert.False(changed.FromCache);
        Assert.False(otherRelease.FromCache);
        Assert.True(changedRepeated.FromCache);
        Assert.Equal(3, handler.RequestCount);
        gateway.Verify(value => value.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Concurrent_requests_for_the_same_release_and_url_fetch_once()
    {
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(value => value.GetStoredAlbumAsync(It.Is<ProtocolExecutionContext>(context => MatchesContext(context)), "deezer", "album"))
            .ReturnsAsync(new Album { CoverArtUrl = "https://covers.example.test/first.png" });
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var original = Png(SKColors.Teal);
        using var handler = new CoverHandler(_ => original, token => complete.Task.WaitAsync(token));
        var cache = new TestMemoryApplicationCache();
        var metrics = new ApplicationCacheActivityMetrics();
        var resolver = new MediaAssetResolver(cache, NullLogger<MediaAssetResolver>.Instance, metrics);
        var reader = new ReleaseArtworkReader(gateway.Object, resolver, new CoverClients(handler),
            NullLogger<ReleaseArtworkReader>.Instance, cache);

        var first = Read(reader);
        var second = Read(reader);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(1, metrics.Snapshot().CoalescedRequests);
        complete.SetResult();
        var results = await Task.WhenAll(first, second);
        var cached = await Read(reader);

        Assert.All(results, result => Assert.NotNull(result));
        Assert.Equal(results[0]!.Bytes, results[1]!.Bytes);
        Assert.Equal(results[0]!.Bytes, cached!.Bytes);
        Assert.True(cached.FromCache);
        Assert.Equal(1, handler.RequestCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Caller_cancellation_reaches_lookup_and_http_without_changing_the_original_context(bool duringHttp)
    {
        using var caller = new CancellationTokenSource();
        ProtocolExecutionContext? observed = null;
        CancellationToken httpToken = default;
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(value => value.GetStoredAlbumAsync(It.Is<ProtocolExecutionContext>(context => MatchesContext(context)), "deezer", "album"))
            .Returns((ProtocolExecutionContext context, string provider, string album) =>
            {
                observed = context;
                if (duringHttp)
                    return Task.FromResult<Album?>(new Album { CoverArtUrl = "https://covers.example.test/first.png" });
                caller.Cancel();
                return Task.FromCanceled<Album?>(context.CancellationToken);
            });
        using var handler = new CoverHandler(_ => Png(SKColors.Teal), token =>
        {
            httpToken = token;
            caller.Cancel();
            return Task.FromCanceled(token);
        });
        var reader = Reader(gateway.Object, new TestMemoryApplicationCache(), handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            reader.ReadAsync(_owner, _protocol, "deezer", "album", caller.Token, "release"));

        Assert.NotNull(observed);
        Assert.NotSame(_protocol, observed);
        Assert.True(MatchesContext(observed));
        Assert.True(observed.CancellationToken.IsCancellationRequested);
        Assert.False(_protocol.CancellationToken.IsCancellationRequested);
        Assert.Equal(duringHttp ? 1 : 0, handler.RequestCount);
        if (duringHttp) Assert.True(httpToken.IsCancellationRequested);
    }

    [Fact]
    public async Task Upstream_cancellation_without_caller_cancellation_reports_a_timeout()
    {
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(value => value.GetStoredAlbumAsync(It.Is<ProtocolExecutionContext>(context => MatchesContext(context)), "deezer", "album"))
            .ThrowsAsync(new OperationCanceledException(new CancellationToken(true)));
        using var handler = new CoverHandler(_ => Png(SKColors.Teal));
        var reader = Reader(gateway.Object, new TestMemoryApplicationCache(), handler);

        await Assert.ThrowsAsync<TimeoutException>(() => Read(reader));

        Assert.Equal(0, handler.RequestCount);
        gateway.Verify(value => value.GetStoredAlbumAsync(It.Is<ProtocolExecutionContext>(context => MatchesContext(context)), "deezer", "album"), Times.Once);
        Assert.False(_protocol.CancellationToken.IsCancellationRequested);
    }

    private Task<ResolvedMediaAsset?> Read(ReleaseArtworkReader reader, string releaseId = "release") =>
        reader.ReadAsync(_owner, _protocol, "deezer", "album", CancellationToken.None, releaseId);

    private bool MatchesContext(ProtocolExecutionContext context) =>
        context.Protocol == _protocol.Protocol && context.BackendInstanceId == _protocol.BackendInstanceId &&
        context.VerifiedBackendPrincipalId == _protocol.VerifiedBackendPrincipalId && context.Principal == _protocol.Principal &&
        context.CorrelationId == _protocol.CorrelationId && context.Client == _protocol.Client &&
        context.Policy == _protocol.Policy && context.IsReadOnly == _protocol.IsReadOnly;

    private ReleaseArtworkReader Reader(IProtocolProviderGateway gateway, IApplicationCache cache, HttpMessageHandler handler) =>
        new(gateway, new MediaAssetResolver(cache, NullLogger<MediaAssetResolver>.Instance), new CoverClients(handler),
            NullLogger<ReleaseArtworkReader>.Instance, cache);

    private FileMediaApplicationCache DiskCache() => new(new FileMediaCacheOptions(_root), new SystemPlatformClock(),
        NullLogger<FileMediaApplicationCache>.Instance);

    private static byte[] Png(SKColor color)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(768, 512, SKColorType.Rgba8888, SKAlphaType.Opaque));
        bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class CoverClients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false);
    }

    private sealed class CoverHandler(Func<Uri, byte[]> bytes, Func<CancellationToken, Task>? beforeResponse = null) : HttpMessageHandler
    {
        private int _requests;
        public int RequestCount => Volatile.Read(ref _requests);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            if (beforeResponse != null) await beforeResponse(cancellationToken);
            var content = new ByteArrayContent(bytes(request.RequestUri!));
            content.Headers.ContentType = new("image/png");
            return new(HttpStatusCode.OK) { Content = content };
        }
    }
}

public sealed partial class NewReleasesTests
{
    [Fact]
    public async Task Release_cover_conditional_request_keeps_the_same_bytes_and_week_cache_header()
    {
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(value => value.GetStoredAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "one"))
            .ReturnsAsync(new Album { CoverArtUrl = "https://covers.example.test/one.png" });
        var first = Artwork(gateway.Object, Listener(_first, _first.ToString()));
        var image = Assert.IsType<FileContentResult>(await first.Get("deezer", "one", default));
        var etag = first.Response.Headers.ETag.ToString();
        Assert.Equal(ImageConditionalRequestHelper.ComputeStrongETag(image.FileContents), etag);
        var repeated = Artwork(gateway.Object, Listener(_first, _first.ToString()));
        repeated.Request.Headers.IfNoneMatch = etag;

        Assert.Equal(304, Assert.IsType<StatusCodeResult>(await repeated.Get("deezer", "one", default)).StatusCode);
        Assert.Equal(etag, repeated.Response.Headers.ETag.ToString());
        Assert.Equal("private, max-age=604800", repeated.Response.Headers.CacheControl.ToString());
        Assert.Single(_coverSource.Requests);
    }

    [Fact]
    public async Task Release_cover_timeout_keeps_the_placeholder_with_a_short_private_retry()
    {
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(value => value.GetStoredAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "one"))
            .ThrowsAsync(new TimeoutException("Source lookup timed out."));
        var controller = Artwork(gateway.Object, Listener(_first, _first.ToString()));

        Assert.IsType<NoContentResult>(await controller.Get("deezer", "one", default));
        Assert.Equal("private, max-age=60", controller.Response.Headers.CacheControl.ToString());
        Assert.Empty(_coverSource.Requests);
    }
}
