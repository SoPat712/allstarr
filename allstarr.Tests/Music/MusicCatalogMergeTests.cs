using allstarr.Core.Identity;
using allstarr.Core.Matching;
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

    private static readonly Guid Listener = Guid.CreateVersion7();
    private readonly ProtocolExecutionContext _listener = new(
        ProtocolKind.Subsonic, "backend", Listener.ToString(),
        new AllstarrPrincipal(Listener, "subsonic", "backend", Listener.ToString(), "Listener", false), "merge-test",
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

        var albums = await new MusicCatalogMerge(gateway.Object).MissingArtistAlbumsAsync(_context, "Grimes", [NativeAlbum("Visions")]);

        Assert.Equal(["ext-deezer-album-r2"], albums.Select(album => album.Id));
        gateway.Verify(item => item.SearchAsync(_context, "Grimes", 0, 0, 20, null), Times.Once);
        gateway.Verify(item => item.GetArtistAlbumsAsync(_context, "deezer", "namesake"), Times.Once);
    }

    [Fact]
    public async Task ArtistAlbums_ReturnOnlyMissingAlbumsWithDuplicatesCollapsed()
    {
        var copies = new[] { ProviderAlbum("r2", "Art Angels"), ProviderAlbum("r3", "art angels") };
        foreach (var album in copies) album.Upc = "012345678905";
        var gateway = ArtistGateway(
            [ProviderArtist("real", "Grimes", 8)],
            new Dictionary<string, List<Album>>
            {
                ["real"] =
                [
                    ProviderAlbum("r1", "Visions"),
                    copies[0],
                    copies[1],
                    ProviderAlbum("r4", "Miss Anthropocene"),
                    ProviderAlbum("r5", "Halfaxa")
                ]
            });

        var albums = await new MusicCatalogMerge(gateway.Object).MissingArtistAlbumsAsync(
            _context, "Grimes", [NativeAlbum("Visions"), NativeAlbum("HALFAXA")]);

        Assert.Equal(["ext-deezer-album-r2", "ext-deezer-album-r4"], albums.Select(album => album.Id));
    }

    [Fact]
    public async Task ArtistAlbums_PreservesDifferentEditionsBesideTheNativeRelease()
    {
        var owned = NativeAlbum("Visions");
        owned.IsExplicit = true;
        var copy = ProviderAlbum("copy", "Visions");
        copy.IsExplicit = true;
        var clean = ProviderAlbum("clean", "Visions");
        clean.IsExplicit = false;
        var gateway = ArtistGateway([ProviderArtist("real", "Grimes", 8)],
            new Dictionary<string, List<Album>> { ["real"] = [copy, clean] });

        var albums = await new MusicCatalogMerge(gateway.Object).MissingArtistAlbumsAsync(_context, "Grimes", [owned]);

        Assert.Equal("ext-deezer-album-clean", Assert.Single(albums).Id);
        Assert.Equal("Visions (Clean)", albums[0].Title);
        Assert.Equal("Visions", owned.Title);
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

        var albums = await new MusicCatalogMerge(gateway.Object).MissingArtistAlbumsAsync(_context, "Grimes", [NativeAlbum("Visions")]);

        Assert.Equal(["ext-deezer-album-r2"], albums.Select(album => album.Id));
        gateway.Verify(item => item.GetArtistAlbumsAsync(_context, It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task ArtistAlbums_LibraryArtistWithoutAlbumsNeverJoinsANamesakeByName()
    {
        var gateway = ArtistGateway(
            [ProviderArtist("smaller", "Grimes", 2), ProviderArtist("larger", "Grimes", 9)],
            new Dictionary<string, List<Album>>
            {
                ["smaller"] = [ProviderAlbum("s1", "Small Album")],
                ["larger"] = [ProviderAlbum("l1", "Visions"), ProviderAlbum("l2", "Art Angels")]
            });

        Assert.Empty(await new MusicCatalogMerge(gateway.Object).MissingArtistAlbumsAsync(_context, "Grimes", []));
        gateway.Verify(item => item.GetArtistAlbumsAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ArtistAlbums_SongLinkedServicesEachAddTheirAlbums()
    {
        var links = Links(new("deezer", "linked"), new("apple-musickit", "apple"));
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.SearchAsync(_listener, "Grimes", 0, 0, 20, null))
            .ReturnsAsync(new SearchResult { Artists = [ProviderArtist("namesake", "Grimes", 50)] });
        gateway.Setup(item => item.GetArtistAlbumsAsync(_listener, "deezer", "linked"))
            .ReturnsAsync([ProviderAlbum("r1", "Visions"), ProviderAlbum("r2", "Art Angels")]);
        gateway.Setup(item => item.GetArtistAlbumsAsync(_listener, "apple-musickit", "apple"))
            .ReturnsAsync([AppleAlbum("a1", "Art Angels"), AppleAlbum("a2", "Miss Anthropocene")]);

        var albums = await new MusicCatalogMerge(gateway.Object, links.Object).MissingArtistAlbumsAsync(_listener, "Grimes", [NativeAlbum("Visions")]);

        // Apple's copy of Art Angels has no barcode but agrees on everything else, so Deezer's copy stands for both.
        Assert.Equal(["ext-deezer-album-r2", "ext-applemusic-album-a2"], albums.Select(album => album.Id));
        gateway.Verify(item => item.GetArtistAlbumsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "namesake"), Times.Never);
        links.Verify(item => item.RequestAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ArtistAlbums_WithoutSongLinksQueueMatchingAndStillUseASharedAlbum()
    {
        var links = Links();
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.SearchAsync(_listener, "Grimes", 0, 0, 20, null))
            .ReturnsAsync(new SearchResult { Artists = [ProviderArtist("real", "Grimes", 8), AppleArtist("apple", "Grimes", 5)] });
        gateway.Setup(item => item.GetArtistAlbumsAsync(_listener, "deezer", "real"))
            .ReturnsAsync([ProviderAlbum("r1", "Visions"), ProviderAlbum("r2", "Art Angels")]);
        gateway.Setup(item => item.GetArtistAlbumsAsync(_listener, "applemusic", "apple"))
            .ReturnsAsync([AppleAlbum("a1", "Visions"), AppleAlbum("a3", "Halfaxa Demos")]);

        var albums = await new MusicCatalogMerge(gateway.Object, links.Object).MissingArtistAlbumsAsync(_listener, "Grimes", [NativeAlbum("Visions")]);

        Assert.Equal(["ext-deezer-album-r2", "ext-applemusic-album-a3"], albums.Select(album => album.Id));
        links.Verify(item => item.RequestAsync(_listener.Actor!.EffectiveUserId!.Value, "Grimes", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ArtistAlbums_OneFailingServiceKeepsTheOthersAlbums()
    {
        var links = Links(new("deezer", "linked"), new("apple-musickit", "apple"));
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.SearchAsync(_listener, "Grimes", 0, 0, 20, null)).ReturnsAsync(new SearchResult());
        gateway.Setup(item => item.GetArtistAlbumsAsync(_listener, "deezer", "linked"))
            .ReturnsAsync([ProviderAlbum("r1", "Visions"), ProviderAlbum("r2", "Art Angels")]);
        gateway.Setup(item => item.GetArtistAlbumsAsync(_listener, "apple-musickit", "apple"))
            .ThrowsAsync(new HttpRequestException("Apple is down"));

        var albums = await new MusicCatalogMerge(gateway.Object, links.Object).MissingArtistAlbumsAsync(_listener, "Grimes", [NativeAlbum("Visions")]);

        Assert.Equal(["ext-deezer-album-r2"], albums.Select(album => album.Id));
    }

    [Fact]
    public async Task ArtistAlbums_NeedAnArtistName()
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);

        Assert.Empty(await new MusicCatalogMerge(gateway.Object).MissingArtistAlbumsAsync(_context, "  ", [NativeAlbum("Visions")]));
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

    private static Mock<ILibraryArtistLinks> Links(params LibraryArtistLink[] found)
    {
        var links = new Mock<ILibraryArtistLinks>();
        links.Setup(item => item.ReadAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(found);
        return links;
    }

    private static Artist AppleArtist(string id, string name, int albumCount) => new()
    {
        Id = $"ext-applemusic-artist-{id}",
        Name = name,
        AlbumCount = albumCount,
        ExternalProvider = "applemusic",
        ExternalId = id
    };

    private static Album AppleAlbum(string id, string title) => new()
    {
        Id = $"ext-applemusic-album-{id}",
        Title = title,
        Artist = "Grimes",
        ExternalProvider = "applemusic",
        ExternalId = id
    };

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
        ExternalId = id,
        Upc = title.ToLowerInvariant() switch
        {
            "visions" => "012345678912",
            "halfaxa" => "012345678929",
            "art angels" => "012345678905",
            _ => null
        }
    };

    private static Album NativeAlbum(string title)
    {
        var album = ProviderAlbum("native-" + title, title);
        album.IsLocal = true;
        album.ExternalProvider = null;
        album.ExternalId = null;
        return album;
    }

    private static Song ProviderSong(string id, string title) => new()
    {
        Id = $"ext-deezer-song-{id}",
        Title = title,
        Artist = "Grimes",
        ExternalProvider = "deezer",
        ExternalId = id
    };
}
