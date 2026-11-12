using allstarr.Core.Caching;
using System.Net;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Theory]
    [InlineData("song")]
    [InlineData("album")]
    [InlineData("artist")]
    public async Task TimingExternalImageMissExposesEachStepAndWarmBytesStayIdentical(string kind)
    {
        var reads = 0;
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        const string url = "https://images.example.test/timing.png";
        gateway.Setup(service => service.GetStoredSongArtworkAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "picture"))
            .ReturnsAsync((string?)null);
        gateway.Setup(service => service.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "picture"))
            .ReturnsAsync(new Song { Id = "ext-deezer-song-picture", CoverArtUrl = url });
        gateway.Setup(service => service.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "picture"))
            .ReturnsAsync(new Album { Id = "ext-deezer-album-picture", CoverArtUrl = url });
        gateway.Setup(service => service.GetArtistAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "picture"))
            .ReturnsAsync(new Artist { Id = "ext-deezer-artist-picture", ImageUrl = url });
        using var factory = TimingImageFactory(gateway, () => reads++);
        using var client = factory.CreateClient();
        var path = $"/Items/ext-deezer-{kind}-picture/Images/Primary?api_key=fixture-key&maxWidth=8&format=png";
        using var cold = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, cold.StatusCode);
        // Disk writes and gate waits run off the request in the background writer, so a miss shows no store or lock stage.
        AssertTimingStages(cold, "resolve", "fetch", "render");
        var bytes = await cold.Content.ReadAsByteArrayAsync();
        Assert.Equal(1, reads);
        var metadataReads = gateway.Invocations.Count;
        factory.Services.GetRequiredService<IConfiguration>()["Diagnostics:ServerTimingEnabled"] = "false";
        using var warm = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, warm.StatusCode);
        Assert.Equal(bytes, await warm.Content.ReadAsByteArrayAsync());
        Assert.Equal(cold.Content.Headers.ContentType, warm.Content.Headers.ContentType);
        Assert.False(warm.Headers.Contains("Server-Timing"));
        Assert.Equal(1, reads);
        Assert.Equal(metadataReads, gateway.Invocations.Count);
        _lyricsSearchOutput.WriteLine($"Image {kind}: cold fetches=1; repeat fetches=0; identical {bytes.Length} bytes.");
    }

    [Fact]
    public async Task TimingConcurrentImageMissReportsJoiningLockAndFetchesOnce()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(service => service.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "picture"))
            .Returns(async () =>
            {
                entered.TrySetResult();
                await release.Task;
                return new Album { Id = "ext-deezer-album-picture", CoverArtUrl = "https://images.example.test/timing.png" };
            });
        using var factory = TimingImageFactory(gateway, () => Interlocked.Increment(ref reads));
        using var client = factory.CreateClient();
        const string path = "/Items/ext-deezer-album-picture/Images/Primary?api_key=fixture-key&maxWidth=8";
        var first = client.GetAsync(path);
        await entered.Task;
        // Starting the second request while the first owns resolution exercises the existing coalescer.
        var metrics = factory.Services.GetRequiredService<ApplicationCacheActivityMetrics>();
        var coalesced = metrics.Snapshot().CoalescedRequests;
        var second = client.GetAsync(path);
        using var waiting = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            while (metrics.Snapshot().CoalescedRequests == coalesced)
                await Task.Delay(10, waiting.Token);
        }
        finally { release.TrySetResult(); }
        using var owner = await first;
        using var joining = await second;
        Assert.Equal(HttpStatusCode.OK, owner.StatusCode);
        Assert.Equal(HttpStatusCode.OK, joining.StatusCode);
        AssertTimingStages(joining, "lock");
        Assert.Equal(await owner.Content.ReadAsByteArrayAsync(), await joining.Content.ReadAsByteArrayAsync());
        Assert.Equal(1, reads);
        gateway.Verify(service => service.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "picture"), Times.Once);
    }

    [Fact]
    public async Task TimingSongArtworkUsesStoredSearchCoverAndCachesTheRequestedVariant()
    {
        var reads = 0;
        var gateway = new Mock<IProtocolProviderGateway>();
        const string url = "https://images.example.test/timing.png";
        gateway.Setup(service => service.GetStoredSongArtworkAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "picture"))
            .ReturnsAsync((string?)null);
        gateway.Setup(service => service.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "picture"))
            .Returns(async () =>
            {
                await Task.Delay(200);
                return new Song { Id = "ext-deezer-song-picture", CoverArtUrl = url };
            });
        using var factory = TimingImageFactory(gateway, () => reads++);
        using var client = factory.CreateClient();
        const string path = "/Items/ext-deezer-song-picture/Images/Primary?api_key=fixture-key&fillWidth=600&quality=90&tag=ext-deezer-song-picture-art-v3";
        using var baseline = await client.GetAsync(path + "-baseline");
        baseline.EnsureSuccessStatusCode();
        AssertTimingStages(baseline, "resolve", "fetch", "render");
        gateway.Setup(service => service.GetStoredSongArtworkAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "picture"))
            .ReturnsAsync(url);
        using var cold = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, cold.StatusCode);
        AssertTimingStages(cold, "resolve", "fetch", "render");
        var bytes = await cold.Content.ReadAsByteArrayAsync();
        Assert.Equal(await baseline.Content.ReadAsByteArrayAsync(), bytes);
        using var warm = await client.GetAsync(path);
        AssertTimingStages(warm);
        Assert.Equal(bytes, await warm.Content.ReadAsByteArrayAsync());
        Assert.Equal(cold.Headers.ETag, warm.Headers.ETag);
        Assert.Equal(2, reads);
        foreach (var concurrent in await ReadConcurrentTimingAsync(client, path)) Assert.Equal(bytes, concurrent);
        gateway.Verify(service => service.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "picture"), Times.Once);
        gateway.Verify(service => service.GetStoredSongArtworkAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "picture"), Times.Exactly(2));
        using var revised = await client.GetAsync(path + "-changed");
        Assert.Equal(bytes, await revised.Content.ReadAsByteArrayAsync());
        Assert.Equal(3, reads);
    }

    private static ProtocolFactory TimingImageFactory(Mock<IProtocolProviderGateway> gateway, Action fetched) =>
        new("Jellyfin", request =>
        {
            if (request.RequestUri!.AbsolutePath == "/Users/Me") return Json(200, """{"Id":"user-1"}""");
            Assert.Equal("/timing.png", request.RequestUri.AbsolutePath);
            fetched();
            return new(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(MixPicture())
                { Headers = { ContentType = new("image/png") } }
            };
        }, services =>
        {
            AddArtworkPrincipal(services, "user-1");
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
            UseTimingImageCache(services);
        });

    private static void UseTimingImageCache(IServiceCollection services)
    {
        services.RemoveAll<FileMediaApplicationCache>();
        services.AddSingleton(provider => new FileMediaApplicationCache(new FileMediaCacheOptions(
            Path.Combine(provider.GetRequiredService<IConfiguration>()["Storage:DataDirectory"]!, "timing-images")),
            provider.GetRequiredService<IPlatformClock>(), NullLogger<FileMediaApplicationCache>.Instance));
        services.RemoveAll<IApplicationCache>();
        services.AddSingleton<IApplicationCache>(provider => provider.GetRequiredService<HybridApplicationCache>());
    }
}
