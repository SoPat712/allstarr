using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Services.Local;
using Moq;

namespace allstarr.Tests;

public sealed partial class ManagedTrackCacheServiceTests
{
    [Fact]
    public async Task CachedReadResolvesAgainWhenKeepMovesTheFileBeforeOpen()
    {
        var root = CreateRoot();
        try
        {
            var source = Path.Combine(root, "cache", "moving.flac");
            var kept = Path.Combine(root, "kept", "moved.flac");
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            Directory.CreateDirectory(Path.GetDirectoryName(kept)!);
            var bytes = "fLaCcached playback"u8.ToArray();
            await File.WriteAllBytesAsync(source, bytes);
            var local = new Mock<ILocalLibraryService>(MockBehavior.Strict);
            var reads = 0;
            local.Setup(item => item.GetLocalPathForExternalSongAsync(It.IsAny<DownloadedSongMappingScope>(), "deezer", "moving"))
                .ReturnsAsync(() =>
                {
                    if (++reads != 1) return kept;
                    File.Move(source, kept);
                    return source;
                });
            await using var service = CreateService(root, "Cache", local.Object);
            var result = await service.TryOpenAsync(new("deezer", ProviderResourceKind.Track, "moving"), new(null, ProviderAudioQuality.Any), default);
            Assert.NotNull(result);
            using var response = result.Response;
            Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
            Assert.True(result.IsCached);
            Assert.Equal(2, reads);
        }
        finally { DeleteRoot(root); }
    }
}
