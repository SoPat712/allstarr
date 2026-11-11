using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using Moq;

namespace allstarr.Tests;

public sealed class EmptyServiceArtistsTests
{
    private readonly ProtocolExecutionContext _context = new(
        ProtocolKind.Jellyfin, "backend", "principal", null, "empty-artists-test",
        DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);

    [Fact]
    public async Task SameNamedServiceArtistsThatOpenToNothingAreLeftOutForADay()
    {
        var clock = new StepClock();
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetArtistAlbumsAsync(It.IsAny<ProtocolExecutionContext>(), "applemusic", "real"))
            .ReturnsAsync([new Album { Id = "ext-applemusic-album-1", Title = "Brick By Brick" }]);
        gateway.Setup(item => item.GetArtistAlbumsAsync(It.IsAny<ProtocolExecutionContext>(), "spotiflac-spotify-web", "stub"))
            .ReturnsAsync([]);
        var empty = new EmptyServiceArtists(clock);
        Artist[] found = [Artist("applemusic", "real"), Artist("spotiflac-spotify-web", "stub"), Artist("deezer", "other", "Someone Else")];

        await empty.Probe(gateway.Object, _context, found);

        Assert.False(empty.IsEmpty(found[0]));
        Assert.True(empty.IsEmpty(found[1]));
        // An artist without a same-named neighbour is never looked at.
        gateway.Verify(item => item.GetArtistAlbumsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "other"), Times.Never);
        await empty.Probe(gateway.Object, _context, found);
        gateway.Verify(item => item.GetArtistAlbumsAsync(It.IsAny<ProtocolExecutionContext>(), "spotiflac-spotify-web", "stub"), Times.Once);
        clock.Now += TimeSpan.FromDays(1.1);
        Assert.False(empty.IsEmpty(found[1]));
    }

    [Fact]
    public async Task AServiceThatFailsSaysNothingAboutTheArtist()
    {
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetArtistAlbumsAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new HttpRequestException("down"));
        var empty = new EmptyServiceArtists();
        var artist = Artist("deezer", "1");

        await empty.Probe(gateway.Object, _context, [artist], ["Say Now"]);

        Assert.False(empty.IsEmpty(artist));
    }

    private static Artist Artist(string provider, string id, string name = "Say Now") =>
        new() { Id = $"ext-{provider}-artist-{id}", Name = name, ExternalProvider = provider, ExternalId = id };

    private sealed class StepClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
