using System.Diagnostics;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Routing;
using allstarr.Services.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolProviderStreamingGatewayTests
{
    [Fact]
    public async Task ExternalMetadataCacheRestartAvoidsProviderFetchAndPreservesExactOrder()
    {
        var root = Path.Combine(Path.GetTempPath(), $"allstarr-external-{Guid.NewGuid():N}");
        var capability = SearchRankingCapability("deezer");
        var calls = 0;
        Mock.Get(capability).Setup(item => item.GetAlbumAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderAlbumLookupRequest>()))
            .Returns(async () =>
            {
                Interlocked.Increment(ref calls);
                await Task.Delay(800);
                return ProviderOutcome<ProviderAlbumMetadata>.Success(new(new("deezer", ProviderResourceKind.Album, "7"), "Album", [new("Artist")], tracks:
                    [new(new("deezer", ProviderResourceKind.Track, "2"), "Second", [new("Artist")], duration: TimeSpan.FromMilliseconds(241160)),
                     new(new("deezer", ProviderResourceKind.Track, "1"), "First", [new("Artist")])]));
            });
        var registry = MetadataRegistry(capability);
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        router.Setup(item => item.PlanAsync<IProviderMetadataCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) => MetadataPlan(request, registry, capability));
        var clock = new SystemPlatformClock();
        try
        {
            string? expected = null;
            var context = Context();
            for (var instance = 0; instance < 4; instance++)
            {
                using var memory = new MemoryApplicationCache(clock);
                using var disk = new FileMediaApplicationCache(new FileMediaCacheOptions(root), clock, NullLogger<FileMediaApplicationCache>.Instance);
                var cache = new HybridApplicationCache(memory, disk, clock: clock);
                var gateway = new ProtocolProviderGateway(router.Object, registry, new HttpClientFactory(), applicationCache: instance < 2 ? memory : cache);
                var timer = Stopwatch.StartNew();
                var album = await gateway.GetAlbumAsync(context, "deezer", "7");
                Assert.Equal(Math.Min(instance + 1, 3), calls);
                var payload = JsonSerializer.Serialize(album);
                if (instance == 0) expected = payload;
                else Assert.Equal(expected, payload);
                Assert.Equal(TimeSpan.FromMilliseconds(241160), album!.Songs[0].PreciseDuration);
                Assert.Equal(["2", "1"], album.Songs.Select(song => song.ExternalId));
                searchOutput.WriteLine($"External album: mode={(instance < 2 ? "memory" : "disk")} instance={instance % 2} elapsed={timer.Elapsed.TotalMilliseconds:F2} ms provider-calls={calls}");
                await disk.FlushWritesAsync();
            }
            Assert.Equal(3, calls);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ProviderSearchCacheRestartAvoidsProviderSearch()
    {
        var root = Path.Combine(Path.GetTempPath(), $"allstarr-search-{Guid.NewGuid():N}");
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var clock = new ExternalMetadataClock(time);
        var provider = DelayedSearchProvider("deezer", TimeProvider.System, TimeSpan.FromMilliseconds(800));
        try
        {
            string? expected = null;
            var context = Context();
            for (var instance = 0; instance < 4; instance++)
            {
                using var memory = new MemoryApplicationCache(clock);
                using var disk = new FileMediaApplicationCache(new FileMediaCacheOptions(root), clock, NullLogger<FileMediaApplicationCache>.Instance);
                var cache = new HybridApplicationCache(memory, disk, clock: clock);
                var gateway = BudgetGateway(time, instance < 2 ? memory : cache, provider.Object);
                var timer = Stopwatch.StartNew();
                var result = await gateway.SearchAsync(context, "fixture", 10, 0, 0);
                var payload = JsonSerializer.Serialize(result);
                if (instance == 0) expected = payload;
                else Assert.Equal(expected, payload);
                searchOutput.WriteLine($"Provider search: mode={(instance < 2 ? "memory" : "disk")} instance={instance % 2} elapsed={timer.Elapsed.TotalMilliseconds:F2} ms");
                await disk.FlushWritesAsync();
            }
            provider.Verify(item => item.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()), Times.Exactly(3));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
