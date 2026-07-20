using allstarr.Models.Domain;
using allstarr.Services;
using allstarr.Services.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

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
            service.Object, NullLogger<ExternalPlaybackMetadataResolver>.Instance);

        var result = await resolver.ResolveAsync("ext-apple-download-song-1573475841", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("Sunflower", result.Title);
        Assert.Equal(158, result.DurationSeconds);
        Assert.Equal("https://artwork.example/sunflower.jpg", result.CoverArtUrl);
    }
}
