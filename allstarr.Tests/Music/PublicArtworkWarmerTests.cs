using allstarr.Core.Capabilities;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using Moq;

namespace allstarr.Tests;

public sealed class PublicArtworkWarmerTests
{
    [Fact]
    public async Task Reads_each_new_service_song_once_and_skips_stored_covers_and_library_songs()
    {
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(service => service.GetPublicArtworkUriAsync("deezer", ProviderResourceKind.Track, "stored",
                It.IsAny<CancellationToken>(), false, true))
            .ReturnsAsync(new Uri("https://covers.example.test/stored.jpg"));
        gateway.Setup(service => service.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new Song());
        var warmer = new PublicArtworkWarmer(gateway.Object);
        var context = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "primary", "listener",
            new AllstarrPrincipal(Guid.NewGuid(), "jellyfin", "primary", "listener", "Listener", false), "mixes",
            DateTimeOffset.UtcNow.AddMinutes(5), new CancellationTokenSource().Token) { IsReadOnly = true };
        Song Service(string provider, string id) => new() { ExternalProvider = provider, ExternalId = id };
        var songs = new[]
        {
            Service("allstarr", "01a18874-f498-7da3-a15f-d944e83fa13a"), Service("deezer", "stored"),
            Service("apple-musickit", "1841167573"), Service("apple-musickit", "1841167573"),
            new Song { Id = "library-song", IsLocal = true }
        };

        await warmer.Warm(context, songs);
        await warmer.Warm(context, songs);

        gateway.Verify(service => service.GetSongAsync(It.Is<ProtocolExecutionContext>(read => !read.IsReadOnly &&
            read.CorrelationId == "artwork-warmup" && !read.CancellationToken.CanBeCanceled),
            "allstarr", "01a18874-f498-7da3-a15f-d944e83fa13a"), Times.Once);
        gateway.Verify(service => service.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "apple-musickit", "1841167573"), Times.Once);
        gateway.Verify(service => service.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "stored"), Times.Never);
        gateway.Verify(service => service.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), "library-song"), Times.Never);
    }

    [Fact]
    public async Task A_song_that_fails_to_load_does_not_stop_the_rest()
    {
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(service => service.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "broken"))
            .ThrowsAsync(new UnauthorizedAccessException("That source is disabled for this listener."));
        gateway.Setup(service => service.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "fine")).ReturnsAsync(new Song());
        var warmer = new PublicArtworkWarmer(gateway.Object);
        var context = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "primary", "listener",
            new AllstarrPrincipal(Guid.NewGuid(), "jellyfin", "primary", "listener", "Listener", false), "sync",
            DateTimeOffset.UtcNow.AddMinutes(5), default);

        await warmer.Warm(context, [new Song { ExternalProvider = "deezer", ExternalId = "broken" }, new Song { ExternalProvider = "deezer", ExternalId = "fine" }]);

        gateway.Verify(service => service.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "fine"), Times.Once);
    }
}
