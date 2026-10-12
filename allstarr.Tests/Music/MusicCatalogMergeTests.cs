using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Models.Search;
using Moq;

namespace allstarr.Tests;

public sealed class MusicCatalogMergeTests
{
    private readonly ProtocolExecutionContext _context = new(
        ProtocolKind.Subsonic, "backend", "principal", null, "merge-test",
        DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);

    [Fact]
    public async Task ArtistAlbums_SkipANamesakeThatSharesNoAlbumWithTheLibrary()
    {
        var gateway = ArtistGateway(
            [ProviderArtist("namesake", "Grimes", 40), ProviderArtist("real", "Grimes", 8)],
            new Dictionary<string, List<Album>>
            {
                ["namesake"] = [ProviderAlbum("n1", "Grimes Plays the Hits")],
                ["real"] = [ProviderAlbum("r1", "Visions"), ProviderAlbum("r2", "Art Angels")]
            });

        var albums = await new MusicCatalogMerge(gateway.Object).MissingArtistAlbumsAsync(_context, "Grimes", ["Visions"]);

        Assert.Equal(["ext-deezer-album-r2"], albums.Select(album => album.Id));
        gateway.Verify(item => item.SearchAsync(_context, "Grimes", 0, 0, 20, null), Times.Once);
        gateway.Verify(item => item.GetArtistAlbumsAsync(_context, "deezer", "namesake"), Times.Once);
    }

    [Fact]
    public async Task ArtistAlbums_ReturnOnlyMissingAlbumsWithDuplicatesCollapsed()
    {
        var gateway = ArtistGateway(
            [ProviderArtist("real", "Grimes", 8)],
            new Dictionary<string, List<Album>>
            {
                ["real"] =
                [
                    ProviderAlbum("r1", "Visions"),
                    ProviderAlbum("r2", "Art Angels"),
                    ProviderAlbum("r3", "art angels"),
                    ProviderAlbum("r4", "Miss Anthropocene"),
                    ProviderAlbum("r5", "Halfaxa")
                ]
            });

        var albums = await new MusicCatalogMerge(gateway.Object).MissingArtistAlbumsAsync(
            _context, "Grimes", ["Visions", "HALFAXA"]);

        Assert.Equal(["ext-deezer-album-r2", "ext-deezer-album-r4"], albums.Select(album => album.Id));
    }

    [Fact]
    public async Task ArtistAlbums_IgnoreCandidatesWithAnotherNameOrNoProviderIdentity()
    {
        var gateway = ArtistGateway(
            [
                ProviderArtist("other", "Gary Grimes", 50),
                new Artist { Id = "no-identity", Name = "Grimes", AlbumCount = 40 },
                ProviderArtist("real", "Grimes", 8)
            ],
            new Dictionary<string, List<Album>> { ["real"] = [ProviderAlbum("r1", "Visions"), ProviderAlbum("r2", "Art Angels")] });

        var albums = await new MusicCatalogMerge(gateway.Object).MissingArtistAlbumsAsync(_context, "Grimes", ["Visions"]);

        Assert.Equal(["ext-deezer-album-r2"], albums.Select(album => album.Id));
        gateway.Verify(item => item.GetArtistAlbumsAsync(_context, It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task ArtistAlbums_LibraryArtistWithoutAlbumsTakesTheBestKnownNamesake()
    {
        var gateway = ArtistGateway(
            [ProviderArtist("smaller", "Grimes", 2), ProviderArtist("larger", "Grimes", 9)],
            new Dictionary<string, List<Album>>
            {
                ["smaller"] = [ProviderAlbum("s1", "Small Album")],
                ["larger"] = [ProviderAlbum("l1", "Visions"), ProviderAlbum("l2", "Art Angels")]
            });

        var albums = await new MusicCatalogMerge(gateway.Object).MissingArtistAlbumsAsync(_context, "Grimes", []);

        Assert.Equal(["ext-deezer-album-l1", "ext-deezer-album-l2"], albums.Select(album => album.Id));
        gateway.Verify(item => item.GetArtistAlbumsAsync(_context, "deezer", "smaller"), Times.Never);
    }

    [Fact]
    public async Task ArtistAlbums_NeedAnArtistName()
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);

        Assert.Empty(await new MusicCatalogMerge(gateway.Object).MissingArtistAlbumsAsync(_context, "  ", ["Visions"]));
        gateway.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("Visions", "Grimes", true)]
    [InlineData("visions", "GRIMES", true)]
    [InlineData("Visions (Deluxe Edition)", "Grimes", true)]
    [InlineData("Starhand Visions", "Grimes", false)]
    [InlineData("Visions of Gideon", "Grimes", false)]
    [InlineData("Visions", "Grimes feat. Janelle Monáe", true)]
    [InlineData("Visions", "Gary Grimes", false)]
    [InlineData("Visions", "Bladee & Grimes", false)]
    public async Task AlbumSongs_MergeOnlyTheSameAlbumByTheSameLeadArtist(string providerTitle, string providerArtist, bool merges)
    {
        var gateway = AlbumGateway(
            [new Album { Id = "ext-deezer-album-9", Title = providerTitle, Artist = providerArtist, ExternalProvider = "deezer", ExternalId = "9" }],
            [ProviderSong("1", "Oblivion"), ProviderSong("2", "Genesis")]);

        var songs = await new MusicCatalogMerge(gateway.Object).MissingAlbumSongsAsync(_context, "Visions", "Grimes", ["Oblivion"]);

        if (merges)
        {
            Assert.Equal(["ext-deezer-song-2"], songs.Select(song => song.Id));
            gateway.Verify(item => item.GetAlbumAsync(_context, "deezer", "9"), Times.Once);
        }
        else
        {
            Assert.Empty(songs);
            gateway.Verify(item => item.GetAlbumAsync(_context, It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }
        gateway.Verify(item => item.SearchAsync(_context, "Grimes Visions", 0, 10, 0, null), Times.Once);
    }

    [Fact]
    public async Task AlbumSongs_ExcludeOwnedTitlesAndRepeatedProviderTitles()
    {
        var gateway = AlbumGateway(
            [new Album { Title = "Visions", Artist = "Grimes", ExternalProvider = "deezer", ExternalId = "9" }],
            [
                ProviderSong("1", "Oblivion"),
                ProviderSong("2", "Genesis"),
                ProviderSong("3", "genesis"),
                ProviderSong("4", "Skin"),
                ProviderSong("5", "Know the Way")
            ]);

        var songs = await new MusicCatalogMerge(gateway.Object).MissingAlbumSongsAsync(
            _context, "Visions", "Grimes", ["OBLIVION", "Know the Way"]);

        Assert.Equal(["ext-deezer-song-2", "ext-deezer-song-4"], songs.Select(song => song.Id));
    }

    [Fact]
    public async Task AlbumSongs_SkipCandidatesWithoutProviderIdentity()
    {
        var gateway = AlbumGateway(
            [
                new Album { Title = "Visions", Artist = "Grimes" },
                new Album { Title = "Visions", Artist = "Grimes", ExternalProvider = "deezer", ExternalId = "9" }
            ],
            [ProviderSong("2", "Genesis")]);

        var songs = await new MusicCatalogMerge(gateway.Object).MissingAlbumSongsAsync(_context, "Visions", "Grimes", []);

        Assert.Equal(["ext-deezer-song-2"], songs.Select(song => song.Id));
        gateway.Verify(item => item.GetAlbumAsync(_context, "deezer", "9"), Times.Once);
    }

    [Theory]
    [InlineData("", "Grimes")]
    [InlineData("Visions", "")]
    public async Task AlbumSongs_NeedATitleAndAnArtist(string title, string artist)
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);

        Assert.Empty(await new MusicCatalogMerge(gateway.Object).MissingAlbumSongsAsync(_context, title, artist, []));
        gateway.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("Visions", "Visions", true)]
    [InlineData("Visions (Deluxe Edition)", "Visions", true)]
    [InlineData("Visions", "Visions (Deluxe Edition)", true)]
    [InlineData("Starhand Visions", "Visions", false)]
    [InlineData("Visions of Gideon", "Visions", false)]
    [InlineData(null, "Visions", false)]
    public void IsSameAlbum_AcceptsOnlyEditionSuffixes(string? candidate, string libraryTitle, bool expected) =>
        Assert.Equal(expected, MusicCatalogMerge.IsSameAlbum(candidate, libraryTitle));

    private Mock<IProtocolProviderGateway> ArtistGateway(List<Artist> artists, Dictionary<string, List<Album>> albums)
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.SearchAsync(_context, It.IsAny<string>(), 0, 0, 20, null))
            .ReturnsAsync(new SearchResult { Artists = artists });
        foreach (var (artistId, list) in albums)
            gateway.Setup(item => item.GetArtistAlbumsAsync(_context, "deezer", artistId)).ReturnsAsync(list);
        return gateway;
    }

    private Mock<IProtocolProviderGateway> AlbumGateway(List<Album> candidates, List<Song> songs)
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.SearchAsync(_context, It.IsAny<string>(), 0, 10, 0, null))
            .ReturnsAsync(new SearchResult { Albums = candidates });
        gateway.Setup(item => item.GetAlbumAsync(_context, "deezer", "9"))
            .ReturnsAsync(new Album { Title = "Visions", Artist = "Grimes", Songs = songs });
        return gateway;
    }

    private static Artist ProviderArtist(string id, string name, int albumCount) => new()
    {
        Id = $"ext-deezer-artist-{id}",
        Name = name,
        AlbumCount = albumCount,
        ExternalProvider = "deezer",
        ExternalId = id
    };

    private static Album ProviderAlbum(string id, string title) => new()
    {
        Id = $"ext-deezer-album-{id}",
        Title = title,
        Artist = "Grimes",
        ExternalProvider = "deezer",
        ExternalId = id
    };

    private static Song ProviderSong(string id, string title) => new()
    {
        Id = $"ext-deezer-song-{id}",
        Title = title,
        Artist = "Grimes",
        ExternalProvider = "deezer",
        ExternalId = id
    };
}
