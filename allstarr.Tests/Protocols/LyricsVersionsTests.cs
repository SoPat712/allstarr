using System.Collections.Immutable;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace allstarr.Tests;

public sealed class LyricsVersionsTests
{
    [Fact]
    public void LyricsVersionMetadataHashNormalizesTextAndPreservesDuration()
    {
        var first = LyricsVersions.SourceId("fixture", new(null, "  TWO  moons ", "Artist", "Ａlbum", 240000));
        Assert.Equal(first, LyricsVersions.SourceId("fixture", new(null, "two moons", "ARTIST", "album", 240000)));
        Assert.Equal(16, first.Split(':')[1].Length);
        Assert.NotEqual(first, LyricsVersions.SourceId("fixture", new(null, "two moons", "artist", "album", 271000)));
        Assert.Equal("fixture:record-42", LyricsVersions.SourceId("fixture", new("record-42", null, null, null, null)));
    }

    [Fact]
    public async Task LyricsVersionsBoundEachProviderAndTotalWithoutRepeatingFetches()
    {
        var context = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "fixture", "listener",
            new AllstarrPrincipal(Guid.NewGuid(), "jellyfin", "fixture", "listener", "Listener", false), "versions", DateTimeOffset.UtcNow.AddMinutes(1), default);
        var gateway = new Mock<IProtocolProviderGateway>();
        var order = new[] { "lrclib", "genius", "unison", "lyricsplus", "lrcred" };
        gateway.Setup(service => service.GetProviderOrder(ProviderCapabilityKind.Lyrics)).Returns(order);
        var versions = Enumerable.Range(0, 9).Select(index => new ProviderLyricsResult(ProviderLyricsAvailabilityState.Available,
            "fixture", ProviderLyricsFormat.PlainText, "Verse " + index)
        { Metadata = new(index.ToString(), "Fixture", "Artist", null, null) }).ToArray();
        gateway.Setup(service => service.GetLyricsAsync(context, It.IsAny<string>(), It.IsAny<string>(), ProviderLyricsFormat.LineTimed,
            It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>(), It.IsAny<int?>(), null)).ReturnsAsync(versions[0] with { Versions = versions });
        var result = await new ProtocolLyricsResolver(gateway.Object, NullLogger<ProtocolLyricsResolver>.Instance).FindAllAsync(context,
            new Song { Title = "Fixture", Artist = "Artist", ExternalProvider = "deezer", ExternalId = "fixture" }, "fixture");
        Assert.Equal(20, result.Count);
        Assert.Equal(order.Take(4), result.Select(candidate => LyricsVersions.ProviderId(candidate.SourceId)).Distinct());
        Assert.All(result.GroupBy(candidate => LyricsVersions.ProviderId(candidate.SourceId)), group => Assert.Equal(5, group.Count()));
        gateway.Verify(service => service.GetLyricsAsync(context, It.IsAny<string>(), It.IsAny<string>(), ProviderLyricsFormat.LineTimed,
            It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>(), It.IsAny<int?>(), null), Times.Exactly(4));
    }
}
