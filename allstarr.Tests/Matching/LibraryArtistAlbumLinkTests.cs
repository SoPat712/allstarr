using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace allstarr.Tests;

public sealed partial class TrackIdentityServiceTests
{
    [Theory]
    [InlineData("Smoochies", "Ashnikko", "Smoochies")]
    [InlineData("Smoochies (Deluxe Edition)", "Ashnikko", "Smoochies")]
    [InlineData("Smoochies", "", "Smoochies")]
    // Many acts have one of these, an album named after the act says nothing new, and another act's album is not this one's.
    [InlineData("Greatest Hits", "Ashnikko", null)]
    [InlineData("Ashnikko", "Ashnikko", null)]
    [InlineData("Smoochies", "Someone Else", null)]
    [InlineData("Weedkiller", "Ashnikko", null)]
    public void LibraryArtist_SharedAlbumNeedsATitleOfItsOwnUnderTheSameArtist(string serviceTitle, string serviceArtist, string? expected) =>
        Assert.Equal(expected, LibraryArtistIndex.SharedAlbum("Ashnikko", ["Smoochies", "Greatest Hits", "Ashnikko"],
            [new Album { Title = serviceTitle, Artist = serviceArtist }]));

    [Fact]
    public async Task LibraryArtist_ASameNamedServiceArtistWithALibraryAlbumIsTiedUntilAPersonSaysOtherwise()
    {
        await SeedArtistAlbumAsync("Ashnikko", "Smoochies");
        var index = ArtistAlbumIndex(new Mock<IProtocolProviderGateway>());

        Assert.False(await index.ProveByAlbumsAsync(_userA, "Ashnikko", "deezer", "namesake", [new Album { Title = "Other Record", Artist = "Ashnikko" }]));
        Assert.True(await index.ProveByAlbumsAsync(_userA, "Ashnikko", "apple-musickit", "721130075",
            [new Album { Title = "Weedkiller", Artist = "Ashnikko" }, new Album { Title = "Smoochies", Artist = "Ashnikko" }]));

        var tied = Assert.Single(await index.ReadAsync(_userA, "Ashnikko"));
        Assert.Equal(new LibraryArtistLink("apple-musickit", "721130075"), tied);
        var proof = await index.ReadProofAsync(_userA, "Ashnikko");
        Assert.Empty(proof.Songs);
        Assert.Equal("Smoochies", Assert.Single(proof.Albums).Album);
        // Search reads the saved ties without rechecking them.
        Assert.Equal(tied, Assert.Single((await index.ReadSavedAsync(_userA, ["Ashnikko"]))["Ashnikko"]));
        // The same artist read through the service's other source is the same artist.
        Assert.True(await index.ProveByAlbumsAsync(_userA, "Ashnikko", "spotiflac-apple-music", "721130075", []));

        // "Not the same" for either source of the service removes the tie and keeps it from coming back.
        Assert.True(await index.SetFixAsync(_userA, "Ashnikko", "spotiflac-apple-music", "721130075", false));
        Assert.Empty(await index.ReadAsync(_userA, "Ashnikko"));
        Assert.Empty((await index.ReadSavedAsync(_userA, ["Ashnikko"])).GetValueOrDefault("Ashnikko") ?? []);
        Assert.False(await index.ProveByAlbumsAsync(_userA, "Ashnikko", "apple-musickit", "721130075", [new Album { Title = "Smoochies", Artist = "Ashnikko" }]));
    }

    [Fact]
    public async Task LibraryArtist_ArtistsASearchShowedAreCheckedAgainstTheLibrarysAlbumsOnceEach()
    {
        await SeedArtistAlbumAsync("Ashnikko", "Smoochies");
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetArtistAlbumsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "same"))
            .ReturnsAsync([new Album { Title = "Smoochies", Artist = "Ashnikko" }]);
        gateway.Setup(item => item.GetArtistAlbumsAsync(It.IsAny<ProtocolExecutionContext>(), "qobuz", "namesake"))
            .ReturnsAsync([new Album { Title = "Unrelated", Artist = "Ashnikko" }]);
        var index = ArtistAlbumIndex(gateway);
        var shown = new[]
        {
            new Artist { Name = "Ashnikko", ExternalProvider = "deezer", ExternalId = "same", AlbumCount = 9 },
            new Artist { Name = "ASHNIKKO", ExternalProvider = "qobuz", ExternalId = "namesake" },
            // A different name is never checked, and neither is a second account on a service whose artist is already tied.
            new Artist { Name = "Ashnikko Tribute", ExternalProvider = "deezer", ExternalId = "tribute" },
            new Artist { Name = "Ashnikko", ExternalProvider = "spotiflac-deezer", ExternalId = "fan account", AlbumCount = 0 }
        };

        await index.ProveCandidatesAsync(_userA, ["Ashnikko"], shown);
        await index.ProveCandidatesAsync(_userA, ["Ashnikko"], shown);

        Assert.Equal(new LibraryArtistLink("deezer", "same"), Assert.Single(await index.ReadAsync(_userA, "Ashnikko")));
        gateway.Verify(item => item.GetArtistAlbumsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "same"), Times.Once);
        gateway.Verify(item => item.GetArtistAlbumsAsync(It.IsAny<ProtocolExecutionContext>(), "qobuz", "namesake"), Times.Once);
        gateway.Verify(item => item.GetArtistAlbumsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "tribute"), Times.Never);
        gateway.Verify(item => item.GetArtistAlbumsAsync(It.IsAny<ProtocolExecutionContext>(), "spotiflac-deezer", "fan account"), Times.Never);
    }

    private LibraryArtistIndex ArtistAlbumIndex(Mock<IProtocolProviderGateway> gateway)
    {
        var (_, context) = LinkLibraryIndex();
        var access = new Mock<IBackendLibraryAccessResolver>();
        access.Setup(item => item.ResolveUserAsync(_userA, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BackendLibraryAccessContext(context, new(true, ["music"])));
        return new LibraryArtistIndex(_factory, access.Object, _clock,
            new(gateway.Object, new(), Microsoft.Extensions.Logging.Abstractions.NullLogger<TrackMatchPlayableSearch>.Instance), gateway.Object, _service);
    }

    private async Task SeedArtistAlbumAsync(string artist, string album)
    {
        var track = await SeedLinkLibraryTrackAsync(artist, artist, 180000, title: "Song on " + album, createRecording: false);
        await using var db = await _factory.CreateDbContextAsync();
        await db.LibraryTracks.Where(item => item.Id == track.Id).ExecuteUpdateAsync(update => update.SetProperty(item => item.Album, album));
    }
}
