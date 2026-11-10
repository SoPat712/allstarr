using System.Diagnostics;
using System.Net;
using allstarr.Core.Capabilities;
using allstarr.Core.Operations;
using allstarr.Core.Music;
using allstarr.Core.Playlists;
using allstarr.Core.Protocols;
using allstarr.Services.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SkiaSharp;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Fact]
    public async Task ExternalImageCacheKeepsCaseSensitiveSourceIdsSeparate()
    {
        var root = Path.Combine(Path.GetTempPath(), "case-art-" + Guid.NewGuid().ToString("N"));
        using var disk = new FileMediaApplicationCache(new FileMediaCacheOptions(root), new SystemPlatformClock(), NullLogger<FileMediaApplicationCache>.Instance);
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(service => service.GetPublicArtworkUriAsync("spotify", ProviderResourceKind.Track,
            It.IsAny<string>(), It.IsAny<CancellationToken>(), false, false)).ReturnsAsync((string _, ProviderResourceKind _, string id, CancellationToken _, bool _, bool _) =>
                new Uri($"https://images.example.test/{id}.png"));
        var blue = CaseArtwork(SKColors.Blue);
        var red = CaseArtwork(SKColors.Red);
        var fetches = 0;
        var measurements = new List<double>();
        try
        {
            using var factory = new ProtocolFactory("Jellyfin", request =>
            {
                fetches++;
                Thread.Sleep(710);
                var bytes = request.RequestUri!.AbsolutePath == "/Case123.png" ? blue : red;
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) { Headers = { ContentType = new("image/png") } } };
            }, services =>
            {
                services.RemoveAll<IProtocolProviderGateway>();
                services.AddSingleton(gateway.Object);
                services.RemoveAll<IApplicationCache>();
                services.AddSingleton<IApplicationCache>(disk);
            });
            using var client = factory.CreateClient();
            foreach (var _ in Enumerable.Range(0, 2))
            {
                var elapsed = Stopwatch.StartNew();
                using var upper = await client.GetAsync("/Items/ext-spotify-song-Case123/Images/Primary?tag=one");
                using var lower = await client.GetAsync("/Items/ext-spotify-song-case123/Images/Primary?tag=one");
                measurements.Add(elapsed.Elapsed.TotalMilliseconds);
                Assert.Equal(HttpStatusCode.OK, upper.StatusCode);
                Assert.Equal(HttpStatusCode.OK, lower.StatusCode);
                Assert.Equal(blue, await upper.Content.ReadAsByteArrayAsync());
                Assert.Equal(red, await lower.Content.ReadAsByteArrayAsync());
                Assert.NotEqual(upper.Headers.ETag, lower.Headers.ETag);
            }
            Assert.Equal(2, fetches);
            _lyricsSearchOutput.WriteLine($"Case-sensitive artwork pair (710 ms per source): first {measurements[0]:F2} ms; disk repeat {measurements[1]:F2} ms; source fetches 2 -> 0.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static byte[] CaseArtwork(SKColor color)
    {
        using var bitmap = new SKBitmap(4, 4);
        bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }

    [Fact]
    public async Task ExternalImageBudgetKeepsATimeoutPlaceholderBrieflyAndThenRetries()
    {
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(service => service.GetPublicArtworkUriAsync("deezer", ProviderResourceKind.Track,
            "budget", It.IsAny<CancellationToken>())).ReturnsAsync(new Uri("https://images.example.test/cover.png"));
        var cache = new TestMemoryApplicationCache();
        var fetches = 0;
        var fail = true;
        byte[] bytes = [137, 80, 78, 71, 13, 10, 26, 10];
        using var factory = new ProtocolFactory("Jellyfin", _ => throw new InvalidOperationException(), services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
            services.RemoveAll<IApplicationCache>();
            services.AddSingleton<IApplicationCache>(cache);
            services.RemoveAll<IHttpClientFactory>();
            services.AddSingleton<IHttpClientFactory>(new StubHttpClientFactory(new BudgetArtworkHandler(async token =>
            {
                Interlocked.Increment(ref fetches);
                if (fail) await Task.Delay(Timeout.Infinite, token);
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) { Headers = { ContentType = new("image/png") } } };
            })));
        });
        using var client = factory.CreateClient();
        const string path = "/Items/ext-deezer-song-budget/Images/Primary?tag=one";
        using var first = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(60), first.Headers.CacheControl?.MaxAge);
        Assert.True(first.Headers.CacheControl?.Private);
        Assert.Null(first.Headers.ETag);
        using var placeholder = SKBitmap.Decode(await first.Content.ReadAsByteArrayAsync());
        Assert.NotNull(placeholder);
        using var repeated = await client.GetAsync(path);
        Assert.Equal(await first.Content.ReadAsByteArrayAsync(), await repeated.Content.ReadAsByteArrayAsync());
        Assert.Equal(1, fetches);
        await cache.DeleteByPatternAsync("artwork:image:v1:*");
        fail = false;
        using var retried = await client.GetAsync(path);
        Assert.Equal(bytes, await retried.Content.ReadAsByteArrayAsync());
        Assert.Equal(2, fetches);
        Assert.NotNull(retried.Headers.ETag);
    }

    private sealed class BudgetArtworkHandler(Func<CancellationToken, Task<HttpResponseMessage>> read) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => read(token);
    }

    [Fact]
    public async Task ServedPlaylistCoverDiskCacheKeepsBytesAndRechecksAccess()
    {
        var root = Path.Combine(Path.GetTempPath(), "playlist-art-" + Guid.NewGuid().ToString("N"));
        using var disk = new FileMediaApplicationCache(new FileMediaCacheOptions(root), new SystemPlatformClock(), NullLogger<FileMediaApplicationCache>.Instance);
        using var bitmap = new SKBitmap(40, 30);
        bitmap.Erase(SKColors.Blue);
        using var image = SKImage.FromBitmap(bitmap);
        var bytes = image.Encode(SKEncodedImageFormat.Png, 100).ToArray();
        var loads = 0;
        var readable = true;
        var model = CoverPlaylist() with { MixCover = new("mix", 1, "daily", Count: 1, Art: ["library:album"]) };
        var playlists = new Mock<IPlaylistVirtualizationService>(MockBehavior.Strict);
        playlists.Setup(service => service.ReadAsync(It.IsAny<ProtocolExecutionContext>(), model.ProtocolId,
            It.IsAny<CancellationToken>())).ReturnsAsync(() => readable ? model : null);
        var covers = new MixCoverImageService(MixCoverRenderer.Render, async (_, token) =>
        {
            Interlocked.Increment(ref loads);
            await Task.Delay(1000, token);
            return bytes;
        }, cache: disk);
        try
        {
            using var factory = new ProtocolFactory("Jellyfin", request => request.RequestUri!.AbsolutePath == "/Users/Me"
                ? Json(200, """{"Id":"user-1"}""") : throw new InvalidOperationException("Unexpected image relay"), services =>
            {
                AddArtworkPrincipal(services, "user-1");
                services.RemoveAll<IPlaylistVirtualizationService>();
                services.AddSingleton(playlists.Object);
                services.RemoveAll<MixCoverImageService>();
                services.AddSingleton(covers);
            });
            using var client = factory.CreateClient();
            var path = $"/Items/{model.ProtocolId}/Images/Primary?api_key=fixture-key&fillWidth=600&quality=90&tag=one";
            var timer = Stopwatch.StartNew();
            using var first = await client.GetAsync(path);
            var cold = timer.Elapsed.TotalMilliseconds;
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            Assert.Equal(MixCoverRenderer.RenderCollage([bytes]), await first.Content.ReadAsByteArrayAsync());
            timer.Restart();
            using var repeated = await client.GetAsync(path);
            var warm = timer.Elapsed.TotalMilliseconds;
            Assert.Equal(await first.Content.ReadAsByteArrayAsync(), await repeated.Content.ReadAsByteArrayAsync());
            Assert.Equal(1, loads);
            using var changed = await client.GetAsync(path.Replace("tag=one", "tag=two"));
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
            Assert.Equal(2, loads);
            readable = false;
            using var denied = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
            Assert.Equal(2, loads);
            _lyricsSearchOutput.WriteLine($"Served playlist cover (1000 ms library art): first {cold:F2} ms; warm {warm:F2} ms; artwork calls 1 -> 0 per repeat.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RecordingImageDiskCacheSharesFetchAndInvalidatesTagAndVariant()
    {
        var root = Path.Combine(Path.GetTempPath(), "recording-art-" + Guid.NewGuid().ToString("N"));
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        var lookups = 0;
        gateway.Setup(service => service.GetPublicArtworkUriAsync("allstarr", ProviderResourceKind.Track,
            "42", It.IsAny<CancellationToken>(), true, false)).ReturnsAsync(() =>
            {
                Interlocked.Increment(ref lookups);
                return new Uri("https://images.example.test/cover.png");
            });
        using var bitmap = new SKBitmap(40, 30);
        bitmap.Erase(SKColors.Blue);
        using var image = SKImage.FromBitmap(bitmap);
        var bytes = image.Encode(SKEncodedImageFormat.Png, 100).ToArray();
        var fetches = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new FileMediaCacheOptions(root);
        using var disk = new FileMediaApplicationCache(options, new SystemPlatformClock(), NullLogger<FileMediaApplicationCache>.Instance);
        try
        {
            using var factory = new ProtocolFactory("Jellyfin", _ => throw new InvalidOperationException("Unexpected image relay"), services =>
            {
                services.RemoveAll<IProtocolProviderGateway>();
                services.AddSingleton(gateway.Object);
                services.RemoveAll<IApplicationCache>();
                services.AddSingleton<IApplicationCache>(disk);
                services.RemoveAll<IHttpClientFactory>();
                services.AddSingleton<IHttpClientFactory>(new StubHttpClientFactory(new BudgetArtworkHandler(async token =>
                {
                    Interlocked.Increment(ref fetches);
                    entered.TrySetResult();
                    await release.Task.WaitAsync(token);
                    await Task.Delay(710, token);
                    return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) { Headers = { ContentType = new("image/png") } } };
                })));
            });
            using var client = factory.CreateClient();
            const string path = "/Items/ext-allstarr-song-42/Images/Primary?fillWidth=600&quality=90&tag=one&format=png";
            var timer = Stopwatch.StartNew();
            using var abandoned = new CancellationTokenSource();
            var abandonedRequest = client.GetAsync(path, abandoned.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var requests = Enumerable.Range(0, 8).Select(_ => client.GetAsync(path)).ToArray();
            abandoned.Cancel();
            release.SetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandonedRequest);
            var replies = await Task.WhenAll(requests);
            foreach (var reply in replies)
            {
                Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
                Assert.Equal(bytes, await reply.Content.ReadAsByteArrayAsync());
                reply.Dispose();
            }
            var cold = timer.Elapsed.TotalMilliseconds;
            Assert.Equal(1, fetches);
            Assert.Equal(1, lookups);
            timer.Restart();
            using var first = await client.GetAsync(path);
            var warm = timer.Elapsed.TotalMilliseconds;
            _lyricsSearchOutput.WriteLine($"Recording cover (710 ms source): cold concurrent batch {cold:F2} ms; disk hit {warm:F2} ms; source calls 1 -> 0 per repeat.");
            using var conditional = new HttpRequestMessage(HttpMethod.Get, path);
            conditional.Headers.IfNoneMatch.Add(first.Headers.ETag!);
            using var unchanged = await client.SendAsync(conditional);
            Assert.Equal(HttpStatusCode.NotModified, unchanged.StatusCode);
            Assert.Empty(await unchanged.Content.ReadAsByteArrayAsync());
            Assert.Equal(1, fetches);
            using var changed = await client.GetAsync(path.Replace("tag=one", "tag=two"));
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
            Assert.Equal(2, fetches);
            using var variant = await client.GetAsync(path.Replace("format=png", "format=webp"));
            Assert.Equal("image/webp", variant.Content.Headers.ContentType?.MediaType);
            Assert.Equal(2, fetches);
            using var restartedDisk = new FileMediaApplicationCache(options, new SystemPlatformClock(), NullLogger<FileMediaApplicationCache>.Instance);
            var unusedResolver = new Mock<IMediaAssetResolver>(MockBehavior.Strict);
            using var restarted = new ProtocolFactory("Jellyfin", _ => throw new InvalidOperationException("Disk hit must not fetch"), services =>
            {
                services.RemoveAll<IProtocolProviderGateway>();
                services.AddSingleton(gateway.Object);
                services.RemoveAll<IApplicationCache>();
                services.AddSingleton<IApplicationCache>(restartedDisk);
                services.RemoveAll<IMediaAssetResolver>();
                services.AddSingleton(unusedResolver.Object);
            });
            using var restartedClient = restarted.CreateClient();
            using var retained = await restartedClient.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, retained.StatusCode);
            Assert.Equal(bytes, await retained.Content.ReadAsByteArrayAsync());
            Assert.Equal(2, lookups);
            unusedResolver.VerifyNoOtherCalls();
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
