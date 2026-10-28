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

    [Theory]
    [InlineData(ProtocolKind.Jellyfin)]
    [InlineData(ProtocolKind.Subsonic)]
    public async Task CleanOnlySearchAndMixKeepVerifiedProviderWhenNativeCopyIsUnknown(ProtocolKind protocol)
    {
        var policy = new allstarr.Core.Settings.EffectiveProviderPolicySnapshot(
            System.Collections.Immutable.ImmutableDictionary<allstarr.Core.Capabilities.ProviderCapabilityKind,
                System.Collections.Immutable.ImmutableArray<string>>.Empty,
            System.Collections.Immutable.ImmutableHashSet<string>.Empty, "Lossless", 0.07)
        { Preferences = new("CleanOnly") };
        var context = new ProtocolExecutionContext(protocol, "backend", "principal", null, "clean-search", _context.Deadline, default)
        { Policy = policy };
        var song = new Song { Id = "external", Title = "Track", Artist = "Artist", ExplicitContentLyrics = 0 };
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.SearchAsync(context, It.IsAny<string>(), 20, 20, 20, null))
            .ReturnsAsync(new SearchResult { Songs = [song] });
        var index = new Mock<ILibraryIndexService>();
        index.Setup(item => item.FindKnownRecordingTracksAsync(context, It.IsAny<IReadOnlyList<LibraryRecordingLookup>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string> { ["external"] = "unknown-native" });
        var native = new FakeNative([new(MusicItemKind.Song, "unknown-native", "Track", "Artist", "native")],
            new NativeMusicItem<string>(MusicItemKind.Song, "unknown-native", "Track", "Artist", "native"));
        var service = new MusicSearchService(gateway.Object, index.Object);
        Assert.Equal("external", Assert.Single((await service.SearchAsync(context, Request("Track"), native)).Songs).Song!.Id);
        Assert.Equal("external", Assert.Single(await service.ProjectSongsAsync(context, [song], native)).Song!.Id);
    }

    [Theory]
    [InlineData(ProtocolKind.Jellyfin, "Daft Punk Get Lucky")]
    [InlineData(ProtocolKind.Subsonic, "Daft Punk Get Lucky")]
    [InlineData(ProtocolKind.Jellyfin, "Get Lucky")]
    [InlineData(ProtocolKind.Subsonic, "Get Lucky")]
    public async Task SearchPrefersTheOriginalBeforeAnUnrequestedUploadVariant(ProtocolKind protocol, string query)
    {
        var context = new ProtocolExecutionContext(protocol, "backend", "principal", null, "search-ranking",
            _context.Deadline, CancellationToken.None);
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.SearchAsync(context, query, 20, 0, 0, null))
            .ReturnsAsync(new SearchResult
            {
                Songs =
                [
                    new Song { Id = "upload", ExternalProvider = "soundcloud",
                        Title = "Daft Punk - Get Lucky #nightcore #bassboosted", Artist = "999333" },
                    new Song { Id = "original", ExternalProvider = "deezer", Title = "Get Lucky", Artist = "Daft Punk" }
                ]
            });
        var result = await new MusicSearchService(gateway.Object).SearchAsync(context,
            new MusicSearchRequest(query, new(20), MusicSearchWindow.None, MusicSearchWindow.None,
                MusicSearchWindow.None),
            new FakeNative([new(MusicItemKind.Song, "native", "Get Lucky", "Daft Punk", "complete native row")]));

        Assert.Equal(["native", "original", "upload"], result.Songs.Select(Id));
        Assert.Equal("complete native row", result.Songs[0].Native!.Payload);
        gateway.Verify(item => item.SearchAsync(context, query, 20, 0, 0, null), Times.Once);
        gateway.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task MixProjectionReplacesNativeSongsInTheirOriginalPositions()
    {
        var index = new Mock<ILibraryIndexService>();
        index.Setup(item => item.FindKnownRecordingTracksAsync(_context,
                It.IsAny<IReadOnlyList<LibraryRecordingLookup>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string> { ["second"] = "native-second", ["fourth"] = "native-fourth" });
        var native = new FakeNative([],
            new NativeMusicItem<string>(MusicItemKind.Song, "native-second", "Second", null, "complete second"),
            new NativeMusicItem<string>(MusicItemKind.Song, "native-fourth", "Fourth", null, "complete fourth"));
        var result = await new MusicSearchService(Mock.Of<IProtocolProviderGateway>(), index.Object)
            .ProjectSongsAsync(_context, [new Song { Id = "first" }, new Song { Id = "second" },
                new Song { Id = "third" }, new Song { Id = "fourth" }], native);
        Assert.Equal(["first", "native-second", "third", "native-fourth"], result.Select(Id));
        Assert.Equal("complete second", result[1].Native!.Payload);
    }

    [Fact]
    public async Task MixNativeLookupsRunConcurrentlyWithAtMostFourInFlight()
    {
        var songs = Enumerable.Range(0, 12).Select(number => new Song { Id = $"external-{number}" }).ToArray();
        var index = new Mock<ILibraryIndexService>();
        index.Setup(item => item.FindKnownRecordingTracksAsync(_context,
                It.IsAny<IReadOnlyList<LibraryRecordingLookup>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(songs.ToDictionary(song => song.Id, song => song.Id.Replace("external", "native")));
        var firstWave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var peak = 0;
        var sync = new object();
        var native = new Mock<INativeMusicSearch<string>>(MockBehavior.Strict);
        native.Setup(item => item.GetSongAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string id, CancellationToken token) =>
            {
                lock (sync) { active++; peak = Math.Max(peak, active); if (active == 4) firstWave.TrySetResult(); }
                try
                {
                    await release.Task.WaitAsync(token);
                    return new NativeMusicItem<string>(MusicItemKind.Song, id, id, null, id);
                }
                finally { lock (sync) active--; }
            });
        var pending = new MusicSearchService(Mock.Of<IProtocolProviderGateway>(), index.Object)
            .ProjectSongsAsync(_context, songs, native.Object);
        var concurrent = false;
        try { await firstWave.Task.WaitAsync(TimeSpan.FromSeconds(5)); concurrent = true; }
        catch (TimeoutException) { }
        finally { release.TrySetResult(); }
        var result = await pending;
        Assert.True(concurrent, "Four lookups should start before any completes.");
        Assert.Equal(4, peak);
        Assert.Equal(songs.Select(song => song.Id.Replace("external", "native")), result.Select(Id));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MixProjectionUsesTheCompleteAccessibleNativeCopy(bool readable)
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        var index = new Mock<ILibraryIndexService>(MockBehavior.Strict);
        index.Setup(item => item.FindKnownRecordingTracksAsync(_context,
                It.IsAny<IReadOnlyList<LibraryRecordingLookup>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string> { ["external"] = "native" });
        var payload = "{ complete native fields }";
        var native = new FakeNative([], readable
            ? [new NativeMusicItem<string>(MusicItemKind.Song, "native", "Song", "Artist", payload)]
            : []);
        var providerSong = new Song { Id = "external", Title = "Song" };

        var result = await new MusicSearchService(gateway.Object, index.Object)
            .ProjectSongsAsync(_context, [providerSong], native);

        var entry = Assert.Single(result);
        if (readable) Assert.Same(payload, entry.Native!.Payload);
        else Assert.Same(providerSong, entry.Song);
        Assert.Equal(["native"], native.FetchedSongs);
        gateway.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task MixProjectionCollapsesVerifiedRecordingsButKeepsUnverifiedLookalikes()
    {
        var recording = Guid.NewGuid();
        var result = await new MusicSearchService(Mock.Of<IProtocolProviderGateway>())
            .ProjectSongsAsync(_context,
            [
                new Song { Id = "first", Title = "Song", CanonicalRecordingId = recording },
                new Song { Id = "alias", Title = "Song", CanonicalRecordingId = recording },
                new Song { Id = "lookalike", Title = "Song" }
            ], new FakeNative([]));
        Assert.Equal(["first", "lookalike"], result.Select(Id));
    }

    [Fact]
    public async Task MixProjectionCollapsesAliasesForOneAccessibleNativeRecording()
    {
        var index = new Mock<ILibraryIndexService>();
        index.Setup(item => item.FindKnownRecordingTracksAsync(_context,
                It.IsAny<IReadOnlyList<LibraryRecordingLookup>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string> { ["first"] = "native", ["second"] = "native" });
        var native = new FakeNative([], new NativeMusicItem<string>(MusicItemKind.Song, "native", "Song", null, "payload"));
        var result = await new MusicSearchService(Mock.Of<IProtocolProviderGateway>(), index.Object)
            .ProjectSongsAsync(_context, [new Song { Id = "first" }, new Song { Id = "second" }], native);
        Assert.Equal("native", Assert.Single(result).Native!.Id);
        Assert.Equal(["native"], native.FetchedSongs);
    }

    [Fact]
    public async Task MixProjectionRetainsProviderRowsWhenTheNativeLookupFails()
    {
        var index = new Mock<ILibraryIndexService>();
        index.Setup(item => item.FindKnownRecordingTracksAsync(_context,
                It.IsAny<IReadOnlyList<LibraryRecordingLookup>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Unavailable"));
        var song = new Song { Id = "external" };
        var result = await new MusicSearchService(Mock.Of<IProtocolProviderGateway>(), index.Object)
            .ProjectSongsAsync(_context, [song], new FakeNative([]));
        Assert.Same(song, Assert.Single(result).Song);
    }

    [Fact]
    public async Task MixProjectionHonorsCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new MusicSearchService(Mock.Of<IProtocolProviderGateway>()).ProjectSongsAsync(
                _context, [new Song { Id = "external" }], new FakeNative([]), cancellation.Token));
    }

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
