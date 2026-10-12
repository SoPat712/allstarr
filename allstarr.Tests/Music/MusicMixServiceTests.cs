using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Models.Search;
using Moq;

namespace allstarr.Tests;

public sealed class MusicMixServiceTests
{
    private const string SeedId = "ext-deezer-song-42";

    private readonly ProtocolExecutionContext _context = new(
        ProtocolKind.Subsonic, "backend", "principal", null, "mix-test",
        DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);

    [Fact]
    public async Task SimilarSongs_PreferProviderRecommendationsAndDropTheSeed()
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.GetSimilarSongsAsync(_context, "deezer", "42", 2))
            .ReturnsAsync([Song(SeedId, "42"), Song("ext-deezer-song-43", "43"), Song("ext-deezer-song-44", "44"),
                Song("ext-deezer-song-45", "45")]);

        var songs = await new MusicMixService(gateway.Object).SimilarSongsAsync(_context, "deezer", "42", SeedId, 2);

        Assert.Equal(["ext-deezer-song-43", "ext-deezer-song-44"], songs.Select(song => song.Id));
        gateway.VerifyAll();
        gateway.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SimilarSongs_DropTheSeedByItsPublicIdToo()
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.GetSimilarSongsAsync(_context, "deezer", "42", 5))
            .ReturnsAsync([Song("native-7", "other-42"), Song("ext-deezer-song-43", "43")]);

        var songs = await new MusicMixService(gateway.Object).SimilarSongsAsync(_context, "deezer", "42", "native-7", 5);

        Assert.Equal(["ext-deezer-song-43"], songs.Select(song => song.Id));
    }

    [Fact]
    public async Task SimilarSongs_FallBackToTheSeedArtistWithoutRecommendations()
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.GetSimilarSongsAsync(_context, "deezer", "42", 2)).ReturnsAsync([]);
        gateway.Setup(item => item.GetSongAsync(_context, "deezer", "42")).ReturnsAsync(Song(SeedId, "42"));
        gateway.Setup(item => item.SearchAsync(_context, "Fixture Artist", 2, 0, 0, "deezer"))
            .ReturnsAsync(new SearchResult
            {
                Songs = [Song(SeedId, "42"), Song("ext-deezer-song-43", "43"), Song("ext-deezer-song-44", "44"),
                    Song("ext-deezer-song-45", "45")]
            });

        var songs = await new MusicMixService(gateway.Object).SimilarSongsAsync(_context, "deezer", "42", SeedId, 2);

        Assert.Equal(["ext-deezer-song-43", "ext-deezer-song-44"], songs.Select(song => song.Id));
        gateway.VerifyAll();
    }

    [Fact]
    public async Task SimilarSongs_AreEmptyWhenTheSeedHasNoArtist()
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.GetSimilarSongsAsync(_context, "deezer", "42", 5)).ReturnsAsync([]);
        gateway.Setup(item => item.GetSongAsync(_context, "deezer", "42"))
            .ReturnsAsync(new Song { Id = SeedId, ExternalProvider = "deezer", ExternalId = "42", Title = "Seed" });

        Assert.Empty(await new MusicMixService(gateway.Object).SimilarSongsAsync(_context, "deezer", "42", SeedId, 5));
        gateway.VerifyAll();
        gateway.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task InstantMix_AlbumSeedUsesTheAlbumSongsInStableOrder()
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.GetAlbumAsync(_context, "deezer", "9")).ReturnsAsync(new Album
        {
            Id = "ext-deezer-album-9",
            Title = "Fixture Album",
            Artist = "Fixture Artist",
            Songs = [Song("ext-deezer-song-1", "1"), Song("ext-deezer-song-2", "2"), Song("ext-deezer-song-3", "3")]
        });

        var mix = await new MusicMixService(gateway.Object).InstantMixAsync(
            _context, "deezer", "album", "9", "ext-deezer-album-9", 3);

        Assert.Equal(
            StableOrder("ext-deezer-album-9", "ext-deezer-song-1", "ext-deezer-song-2", "ext-deezer-song-3"),
            mix.Select(song => song.Id));
        gateway.VerifyAll();
        gateway.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task InstantMix_MissingAlbumOrArtistSeedIsEmpty()
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.GetAlbumAsync(_context, "deezer", "9")).ReturnsAsync((Album?)null);
        gateway.Setup(item => item.GetArtistAsync(_context, "deezer", "7")).ReturnsAsync((Artist?)null);
        var service = new MusicMixService(gateway.Object);

        Assert.Empty(await service.InstantMixAsync(_context, "deezer", "album", "9", "ext-deezer-album-9", 5));
        Assert.Empty(await service.InstantMixAsync(_context, "deezer", "artist", "7", "ext-deezer-artist-7", 5));
        gateway.VerifyAll();
        gateway.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task InstantMix_ArtistSeedReadsThreeAlbumsThenTopsUpFromSearch()
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.GetArtistAsync(_context, "deezer", "7"))
            .ReturnsAsync(new Artist { Id = "ext-deezer-artist-7", Name = "Fixture Artist" });
        gateway.Setup(item => item.GetArtistAlbumsAsync(_context, "deezer", "7")).ReturnsAsync(
        [
            new Album { Title = "First", ExternalId = "a1" },
            new Album { Title = "No identity", ExternalId = "" },
            new Album { Title = "Second", ExternalId = "a2" },
            new Album { Title = "Beyond the window", ExternalId = "a3" }
        ]);
        gateway.Setup(item => item.GetAlbumAsync(_context, "deezer", "a1"))
            .ReturnsAsync(new Album { Songs = [Song("ext-deezer-song-1", "1")] });
        gateway.Setup(item => item.GetAlbumAsync(_context, "deezer", "a2"))
            .ReturnsAsync(new Album { Songs = [Song("ext-deezer-song-2", "2")] });
        gateway.Setup(item => item.SearchAsync(_context, "Fixture Artist", 10, 0, 0, "deezer"))
            .ReturnsAsync(new SearchResult { Songs = [Song("ext-deezer-song-1", "1"), Song("ext-deezer-song-3", "3")] });

        var mix = await new MusicMixService(gateway.Object).InstantMixAsync(
            _context, "deezer", "artist", "7", "ext-deezer-artist-7", 10);

        Assert.Equal(
            StableOrder("ext-deezer-artist-7", "ext-deezer-song-1", "ext-deezer-song-2", "ext-deezer-song-3"),
            mix.Select(song => song.Id));
        gateway.VerifyAll();
        gateway.Verify(item => item.GetAlbumAsync(_context, "deezer", "a3"), Times.Never);
    }

    [Fact]
    public async Task InstantMix_SongSeedLeadsWithRecommendationsAndExcludesTheSeed()
    {
        var seed = Song(SeedId, "42");
        seed.ArtistId = "ext-deezer-artist-7";
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.GetSongAsync(_context, "deezer", "42")).ReturnsAsync(seed);
        gateway.Setup(item => item.GetSimilarSongsAsync(_context, "deezer", "42", 10))
            .ReturnsAsync([Song("ext-deezer-song-50", "50"), Song(SeedId, "42")]);
        gateway.Setup(item => item.GetArtistAlbumsAsync(_context, "deezer", "7"))
            .ReturnsAsync([new Album { Title = "First", ExternalId = "a1" }]);
        gateway.Setup(item => item.GetAlbumAsync(_context, "deezer", "a1"))
            .ReturnsAsync(new Album { Songs = [Song(SeedId, "42"), Song("ext-deezer-song-2", "2")] });
        gateway.Setup(item => item.SearchAsync(_context, "Fixture Artist", 10, 0, 0, "deezer"))
            .ReturnsAsync(new SearchResult { Songs = [Song(SeedId, "42"), Song("ext-deezer-song-3", "3")] });

        var mix = await new MusicMixService(gateway.Object).InstantMixAsync(
            _context, "deezer", "song", "42", SeedId, 10);

        Assert.Equal(
            StableOrder(SeedId, "ext-deezer-song-50", "ext-deezer-song-2", "ext-deezer-song-3"),
            mix.Select(song => song.Id));
        gateway.VerifyAll();
    }

    [Fact]
    public async Task InstantMix_SameSeedKeepsTheSameOrderAndRespectsTheLimit()
    {
        var songs = Enumerable.Range(1, 8).Select(index => Song($"ext-deezer-song-{index}", $"{index}")).ToList();
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.GetAlbumAsync(_context, "deezer", "9")).ReturnsAsync(() => new Album
        {
            Title = "Fixture Album",
            Artist = "Fixture Artist",
            // Each read returns the songs in a different order; the mix order must not follow it.
            Songs = songs.AsEnumerable().Reverse().ToList()
        });
        var service = new MusicMixService(gateway.Object);

        var first = await service.InstantMixAsync(_context, "deezer", "album", "9", "ext-deezer-album-9", 3);
        songs.Reverse();
        var second = await service.InstantMixAsync(_context, "deezer", "album", "9", "ext-deezer-album-9", 3);

        Assert.Equal(3, first.Count);
        Assert.Equal(first.Select(song => song.Id), second.Select(song => song.Id));
        Assert.Equal(
            StableOrder("ext-deezer-album-9", songs.Select(song => song.Id).ToArray()).Take(3),
            first.Select(song => song.Id));
    }

    [Fact]
    public void StableOrder_IsAFixedHashOfSeedAndCandidate()
    {
        Assert.Equal(
            MusicMixService.StableOrder("seed", "candidate"),
            MusicMixService.StableOrder("seed", "candidate"));
        Assert.NotEqual(
            MusicMixService.StableOrder("seed", "candidate"),
            MusicMixService.StableOrder("other-seed", "candidate"));
        Assert.Matches("^[0-9a-f]{64}$", MusicMixService.StableOrder("seed", "candidate"));
    }

    private static IEnumerable<string> StableOrder(string seedId, params string[] ids) =>
        ids.OrderBy(id => MusicMixService.StableOrder(seedId, id), StringComparer.Ordinal);

    private static Song Song(string id, string externalId) => new()
    {
        Id = id,
        Title = $"Song {externalId}",
        Artist = "Fixture Artist",
        ExternalProvider = "deezer",
        ExternalId = externalId
    };
}
