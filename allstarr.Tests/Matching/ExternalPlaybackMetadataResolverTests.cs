using allstarr.Models.Domain;
using allstarr.Core.Operations;
using allstarr.Services;
using allstarr.Services.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Net;

namespace allstarr.Tests;

public sealed class ExternalPlaybackMetadataResolverTests
{
    [Fact]
    public async Task ResolvesAppleDownloadPlayerMetadata()
    {
        var service = new Mock<IMusicMetadataService>();
        service.Setup(item => item.GetSongAsync("apple-download", "1573475841", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Song
            {
                Title = "Sunflower",
                Artist = "Post Malone, Swae Lee",
                Duration = 158,
                CoverArtUrlLarge = "https://artwork.example/sunflower.jpg"
            });
        var resolver = new ExternalPlaybackMetadataResolver(
            service.Object,
            new TestMemoryApplicationCache(),
            new StubHttpClientFactory(new HttpClient()),
            new SystemPlatformClock(),
            NullLogger<ExternalPlaybackMetadataResolver>.Instance);

        var result = await resolver.ResolveAsync("ext-apple-download-song-1573475841", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("Sunflower", result.Title);
        Assert.Equal(158, result.DurationSeconds);
        Assert.Equal("https://artwork.example/sunflower.jpg", result.CoverArtUrl);
    }

    [Fact]
    public async Task ResolvesOnlyBoundedImageArtwork()
    {
        var service = new Mock<IMusicMetadataService>();
        service.Setup(item => item.GetSongAsync("deezer", "42", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Song { CoverArtUrl = "https://cdn.example/cover.png" });
        var resolver = new ExternalPlaybackMetadataResolver(
            service.Object,
            new TestMemoryApplicationCache(),
            new StubHttpClientFactory(new HttpClient(new StubHandler())),
            new SystemPlatformClock(),
            NullLogger<ExternalPlaybackMetadataResolver>.Instance);

        var result = await resolver.ResolveArtworkAsync(
            "ext-deezer-song-42", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("image/png", result.ContentType);
        Assert.Equal([1, 2, 3], result.Content);
    }

    [Fact]
    public async Task MetadataMissesUseTheNegativeCache()
    {
        var service = new Mock<IMusicMetadataService>();
        service.Setup(item => item.GetSongAsync("deezer", "404", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Song?)null);
        var cache = new TestMemoryApplicationCache();
        var resolver = new ExternalPlaybackMetadataResolver(
            service.Object,
            cache,
            new StubHttpClientFactory(new HttpClient()),
            new SystemPlatformClock(),
            NullLogger<ExternalPlaybackMetadataResolver>.Instance);

        Assert.Null(await resolver.ResolveAsync("ext-deezer-song-404", CancellationToken.None));
        Assert.Null(await resolver.ResolveAsync("ext-deezer-song-404", CancellationToken.None));

        service.Verify(
            item => item.GetSongAsync("deezer", "404", It.IsAny<CancellationToken>()),
            Times.Once);
        Assert.Contains(
            CacheKeyBuilder.BuildPlaybackMetadataNegativeKey("deezer", "404"),
            cache.GetKeysByPattern("negative:*"));
    }

    [Fact]
    public async Task ConcurrentMetadataRequestsShareOneProviderFetch()
    {
        var release = new TaskCompletionSource<Song?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new Mock<IMusicMetadataService>();
        service.Setup(item => item.GetSongAsync("deezer", "42", It.IsAny<CancellationToken>()))
            .Returns(release.Task);
        var resolver = new ExternalPlaybackMetadataResolver(
            service.Object,
            new TestMemoryApplicationCache(),
            new StubHttpClientFactory(new HttpClient()),
            new SystemPlatformClock(),
            NullLogger<ExternalPlaybackMetadataResolver>.Instance);

        var first = resolver.ResolveAsync("ext-deezer-song-42", CancellationToken.None);
        var second = resolver.ResolveAsync("ext-deezer-song-42", CancellationToken.None);
        release.SetResult(new Song { Title = "Shared", Artist = "Artist" });

        var results = await Task.WhenAll(first, second);
        Assert.All(results, result => Assert.Equal("Shared", result!.Title));
        service.Verify(
            item => item.GetSongAsync("deezer", "42", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task StaleMetadataReturnsWhileOneRefreshRuns()
    {
        var clock = new TestClock(
            new DateTimeOffset(2026, 7, 26, 12, 0, 0, TimeSpan.Zero));
        var refreshStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshRelease = new TaskCompletionSource<Song?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var metrics = new ApplicationCacheActivityMetrics();
        var calls = 0;
        var service = new Mock<IMusicMetadataService>();
        service.Setup(item => item.GetSongAsync("deezer", "42", It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                    return Task.FromResult<Song?>(new Song { Title = "Stale", Artist = "Artist" });
                refreshStarted.TrySetResult();
                return refreshRelease.Task;
            });
        var resolver = new ExternalPlaybackMetadataResolver(
            service.Object,
            new TestMemoryApplicationCache(),
            new StubHttpClientFactory(new HttpClient()),
            clock,
            NullLogger<ExternalPlaybackMetadataResolver>.Instance,
            metrics);
        Assert.Equal(
            "Stale",
            (await resolver.ResolveAsync("ext-deezer-song-42", CancellationToken.None))!.Title);
        clock.UtcNow = clock.UtcNow.AddMinutes(11);

        var stale = await resolver.ResolveAsync(
            "ext-deezer-song-42",
            CancellationToken.None);
        await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal("Stale", stale!.Title);
        Assert.Equal(2, calls);
        Assert.Equal(1, metrics.Snapshot().StaleServes);
        refreshRelease.SetResult(new Song { Title = "Fresh", Artist = "Artist" });
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3])
                {
                    Headers = { ContentType = new("image/png") }
                }
            });
    }

    private sealed class TestClock(DateTimeOffset now) : IPlatformClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }
}
