using allstarr.Core.Matching;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Models.Search;
using allstarr.Models.Subsonic;
using Moq;

namespace allstarr.Tests;

public sealed class MusicSearchServiceTests
{
    private readonly ProtocolExecutionContext _context = new(
        ProtocolKind.Subsonic, "backend", "principal", null, "search-test",
        DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);

    [Fact]
    public async Task OwnedProviderSongIsReplacedByTheNativeCopy()
    {
        var gateway = Gateway(new SearchResult
        {
            Songs =
            [
                new Song { Id = "ext-deezer-song-1", Title = "Luther", Isrc = "USUM72409273" },
                new Song { Id = "ext-deezer-song-2", Title = "Luther (Remix)" }
            ]
        });
        var index = new Mock<ILibraryIndexService>(MockBehavior.Strict);
        index.Setup(item => item.FindKnownRecordingTracksAsync(_context,
                It.Is<IReadOnlyList<LibraryRecordingLookup>>(lookups => lookups.Count == 2), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string> { ["ext-deezer-song-1"] = "native-7" });
        var native = new FakeNative([], new NativeMusicItem<string>(MusicItemKind.Song, "native-7", "Luther", "Kendrick Lamar", "native"));

        var result = await new MusicSearchService(gateway.Object, index.Object).SearchAsync(
            _context, Request("Luther"), native);

        Assert.Equal(["native-7", "ext-deezer-song-2"], result.Songs.Select(Id));
        Assert.Equal(["native-7"], native.FetchedSongs);
    }

    [Fact]
    public async Task UnreadableNativeCopyKeepsTheProviderSong()
    {
        var gateway = Gateway(new SearchResult { Songs = [new Song { Id = "ext-deezer-song-1", Title = "Luther" }] });
        var index = new Mock<ILibraryIndexService>();
        index.Setup(item => item.FindKnownRecordingTracksAsync(_context,
                It.IsAny<IReadOnlyList<LibraryRecordingLookup>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string> { ["ext-deezer-song-1"] = "native-hidden" });

        var result = await new MusicSearchService(gateway.Object, index.Object).SearchAsync(
            _context, Request("Luther"), new FakeNative([]));

        Assert.Equal(["ext-deezer-song-1"], result.Songs.Select(Id));
    }

    [Fact]
    public async Task ProviderWindowsApplyBeforeMerging()
    {
        var gateway = Gateway(new SearchResult
        {
            Songs = Enumerable.Range(0, 5).Select(index => new Song { Id = $"ext-{index}", Title = "Song" }).ToList()
        });

        var result = await new MusicSearchService(gateway.Object).SearchAsync(
            _context,
            Request("Song") with { Songs = new MusicSearchWindow(5, Skip: 2, Take: 2) },
            new FakeNative([]));

        Assert.Equal(["ext-2", "ext-3"], result.Songs.Select(Id));
        gateway.Verify(item => item.SearchAsync(_context, "Song", 5, 20, 20, null), Times.Once);
    }

    [Fact]
    public async Task ProviderSearchIsSkippedWhenProvidersAreExcluded()
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        var native = new FakeNative([new NativeMusicItem<string>(MusicItemKind.Album, "native-album", "Album", null, "native")]);

        var result = await new MusicSearchService(gateway.Object).SearchAsync(
            _context, Request("Album") with { IncludeProviders = false }, native);

        Assert.Equal(["native-album"], result.Albums.Select(Id));
        gateway.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ProviderArtistsNamedLikeNativeArtistsCanBeHidden()
    {
        var gateway = Gateway(new SearchResult
        {
            Artists = [new Artist { Id = "ext-artist-1", Name = "daft punk" }, new Artist { Id = "ext-artist-2", Name = "Justice" }]
        });
        var native = new FakeNative([new NativeMusicItem<string>(MusicItemKind.Artist, "native-artist", "Daft Punk", null, "native")]);
        var service = new MusicSearchService(gateway.Object);

        var hidden = await service.SearchAsync(_context, Request("punk") with { HideProviderArtistsNamedLikeNative = true }, native);
        var shown = await service.SearchAsync(_context, Request("punk"), native);

        Assert.Equal(["ext-artist-2", "native-artist"], hidden.Artists.Select(Id).Order(StringComparer.Ordinal));
        Assert.Equal(["ext-artist-1", "ext-artist-2", "native-artist"], shown.Artists.Select(Id).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task PlaylistsJoinAlbumsWithTheirOwnWindow()
    {
        var gateway = Gateway(new SearchResult(), [
            new ExternalPlaylist { Id = "ext-p-1", Name = "Road trip", Provider = "deezer", ExternalId = "1" },
            new ExternalPlaylist { Id = "ext-p-2", Name = "Road trip mix", Provider = "deezer", ExternalId = "2" }
        ]);

        var result = await new MusicSearchService(gateway.Object).SearchAsync(
            _context, Request("Road trip") with { Playlists = new MusicSearchWindow(2, Skip: 1) }, new FakeNative([]));

        Assert.Equal(["ext-p-2"], result.Albums.Select(Id));
        gateway.Verify(item => item.SearchPlaylistsAsync(_context, "Road trip", 2), Times.Once);
    }

    private static MusicSearchRequest Request(string query) => new(
        query,
        new MusicSearchWindow(20),
        new MusicSearchWindow(20),
        new MusicSearchWindow(20),
        MusicSearchWindow.None);

    private Mock<IProtocolProviderGateway> Gateway(SearchResult result, List<ExternalPlaylist>? playlists = null)
    {
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.SearchAsync(_context, It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<int>(), null))
            .ReturnsAsync(result);
        gateway.Setup(item => item.SearchPlaylistsAsync(_context, It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync(playlists ?? []);
        return gateway;
    }

    private static string Id(MusicSearchEntry<string> entry) =>
        entry.Native?.Id ?? entry.Song?.Id ?? entry.Album?.Id ?? entry.Artist?.Id ?? entry.Playlist!.Id;

    private sealed class FakeNative(
        IReadOnlyList<NativeMusicItem<string>> rows,
        params NativeMusicItem<string>[] songs) : INativeMusicSearch<string>
    {
        public List<string> FetchedSongs { get; } = [];

        public Task<IReadOnlyList<NativeMusicItem<string>>> SearchAsync(CancellationToken cancellationToken) =>
            Task.FromResult(rows);

        public Task<NativeMusicItem<string>?> GetSongAsync(string id, CancellationToken cancellationToken)
        {
            FetchedSongs.Add(id);
            return Task.FromResult(songs.FirstOrDefault(song => song.Id == id));
        }
    }
}
