using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Models.Search;
using Moq;

namespace allstarr.Tests;

public sealed class StreamingArtistCatalogTests
{
    private readonly ProtocolExecutionContext _context = new(
        ProtocolKind.Jellyfin, "backend", "principal", null, "streaming-artist-test",
        DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);

    [Fact]
    public async Task AddsTheSameArtistsAlbumsFromAnotherServiceButNotANamesakes()
    {
        var gateway = Gateway();
        gateway.Setup(item => item.GetArtistAlbumsAsync(It.IsAny<ProtocolExecutionContext>(), "applemusic", "apple"))
            .ReturnsAsync([Album("applemusic", "a1", "Visions"), Album("applemusic", "a2", "Miss Anthropocene")]);
        gateway.Setup(item => item.GetArtistAlbumsAsync(It.IsAny<ProtocolExecutionContext>(), "qobuz", "namesake"))
            .ReturnsAsync([Album("qobuz", "q1", "Grimes Plays the Hits")]);

        var albums = await new StreamingArtistCatalog(gateway.Object).AlbumsAsync(_context, "deezer", "grimes", Grimes());

        // Apple's Visions is the same album as Deezer's, so only Apple's Miss Anthropocene joins, filed under the Deezer artist.
        Assert.Equal(["ext-deezer-album-dz1", "ext-deezer-album-dz2", "ext-applemusic-album-a2"], albums.Select(album => album.Id));
        Assert.Equal("ext-deezer-artist-grimes", albums[2].ArtistId);
    }

    [Fact]
    public async Task ASlowServiceJoinsOnTheNextVisit()
    {
        var gateway = Gateway();
        var apple = new TaskCompletionSource<List<Album>>();
        gateway.Setup(item => item.GetArtistAlbumsAsync(It.IsAny<ProtocolExecutionContext>(), "applemusic", "apple")).Returns(apple.Task);
        gateway.Setup(item => item.GetArtistAlbumsAsync(It.IsAny<ProtocolExecutionContext>(), "qobuz", "namesake")).ReturnsAsync([]);
        var catalog = new StreamingArtistCatalog(gateway.Object) { FirstWait = TimeSpan.FromMilliseconds(200) };

        Assert.Equal(2, (await catalog.AlbumsAsync(_context, "deezer", "grimes", Grimes())).Count);
        apple.SetResult([Album("applemusic", "a1", "Visions"), Album("applemusic", "a2", "Miss Anthropocene")]);

        Assert.Equal(3, (await catalog.AlbumsAsync(_context, "deezer", "grimes", Grimes())).Count);
        gateway.Verify(item => item.SearchAsync(It.IsAny<ProtocolExecutionContext>(), "Grimes", 0, 0, 20, null), Times.Once);
    }

    [Fact]
    public async Task AServiceThatFailedIsCheckedAgainNextTime()
    {
        var gateway = Gateway();
        gateway.SetupSequence(item => item.GetArtistAlbumsAsync(It.IsAny<ProtocolExecutionContext>(), "applemusic", "apple"))
            .ThrowsAsync(new HttpRequestException("Apple is down"))
            .ReturnsAsync([Album("applemusic", "a1", "Visions"), Album("applemusic", "a2", "Miss Anthropocene")])
            .ReturnsAsync([Album("applemusic", "a1", "Visions"), Album("applemusic", "a2", "Miss Anthropocene")]);
        gateway.Setup(item => item.GetArtistAlbumsAsync(It.IsAny<ProtocolExecutionContext>(), "qobuz", "namesake")).ReturnsAsync([]);
        var catalog = new StreamingArtistCatalog(gateway.Object);

        Assert.Equal(2, (await catalog.AlbumsAsync(_context, "deezer", "grimes", Grimes())).Count);
        Assert.Equal(3, (await catalog.AlbumsAsync(_context, "deezer", "grimes", Grimes())).Count);
    }

    private static Task<Artist?> Grimes() => Task.FromResult<Artist?>(
        new Artist { Id = "ext-deezer-artist-grimes", Name = "Grimes", ExternalProvider = "deezer", ExternalId = "grimes" });

    private static Mock<IProtocolProviderGateway> Gateway()
    {
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetArtistAlbumsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "grimes"))
            .ReturnsAsync(() => [Album("deezer", "dz1", "Visions", "012345678912"), Album("deezer", "dz2", "Art Angels", "012345678905")]);
        gateway.Setup(item => item.SearchAsync(It.IsAny<ProtocolExecutionContext>(), "Grimes", 0, 0, 20, null))
            .ReturnsAsync(new SearchResult
            {
                Artists =
                [
                    new Artist { Id = "ext-deezer-artist-grimes", Name = "Grimes", ExternalProvider = "deezer", ExternalId = "grimes" },
                    new Artist { Id = "ext-applemusic-artist-apple", Name = "Grimes", ExternalProvider = "applemusic", ExternalId = "apple", AlbumCount = 9 },
                    new Artist { Id = "ext-qobuz-artist-namesake", Name = "GRIMES", ExternalProvider = "qobuz", ExternalId = "namesake", AlbumCount = 40 },
                    new Artist { Id = "ext-qobuz-artist-other", Name = "Gary Grimes", ExternalProvider = "qobuz", ExternalId = "other" }
                ]
            });
        return gateway;
    }

    private static Album Album(string provider, string id, string title, string? upc = null) => new()
    {
        Id = $"ext-{provider}-album-{id}",
        Title = title,
        Artist = "Grimes",
        ExternalProvider = provider,
        ExternalId = id,
        Upc = upc
    };
}
