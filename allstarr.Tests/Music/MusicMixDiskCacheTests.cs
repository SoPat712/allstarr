using System.Text.Json;
using allstarr.Core.Identity;
using allstarr.Core.Music;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Services.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace allstarr.Tests;

public sealed class MusicMixDiskCacheTests
{
    [Fact]
    public async Task InstantMixRestartKeepsOrderAndPreciseDurationWithoutProviderRead()
    {
        var root = Path.Combine(Path.GetTempPath(), $"allstarr-item-mix-{Guid.NewGuid():N}");
        var providers = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        providers.Setup(item => item.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "fixture", "album"))
            .ReturnsAsync(new Album { Songs = [new Song { Id = "two", PreciseDuration = TimeSpan.FromMilliseconds(241160), SearchRoutes = [new("fixture", allstarr.Core.Capabilities.ProviderResourceKind.Track, "two")] }, new Song { Id = "one" }] });
        var owner = Guid.NewGuid();
        var context = new ProtocolExecutionContext(ProtocolKind.Subsonic, "main", "listener",
            new AllstarrPrincipal(owner, "subsonic", "main", "listener", "Listener", false), "test", DateTimeOffset.UtcNow.AddMinutes(5), default);
        var clock = new SystemPlatformClock();
        try
        {
            string? expected = null;
            for (var instance = 0; instance < 2; instance++)
            {
                using var memory = new MemoryApplicationCache(clock);
                using var disk = new FileMediaApplicationCache(new FileMediaCacheOptions(root), clock, NullLogger<FileMediaApplicationCache>.Instance);
                using var mix = new MusicMixService(providers.Object, applicationCache: new HybridApplicationCache(memory, disk, clock: clock));
                var result = await mix.InstantMixAsync(context, "fixture", "album", "album", "ext-fixture-album-album", 2);
                if (instance == 0) expected = JsonSerializer.Serialize(result);
                else Assert.Equal(expected, JsonSerializer.Serialize(result));
                Assert.Equal("two", Assert.Single(result.Single(item => item.Id == "two").SearchRoutes).Value);
                Assert.Equal(TimeSpan.FromMilliseconds(241160), result.Single(item => item.Id == "two").PreciseDuration);
            }
            providers.Verify(item => item.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "fixture", "album"), Times.Once);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
