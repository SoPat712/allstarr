using allstarr.Core.Identity;
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
            .ReturnsAsync(new SearchResult { Songs = [song], Albums = [new Album { Id = "clean-album", Title = "Track", Artist = "Artist", IsExplicit = false }] });
        var index = new Mock<ILibraryIndexService>();
        index.Setup(item => item.FindKnownRecordingTracksAsync(context, It.IsAny<IReadOnlyList<LibraryRecordingLookup>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string> { ["external"] = "unknown-native" });
        var native = new FakeNative([new(MusicItemKind.Song, "unknown-native", "Track", "Artist", "native"),
                new(MusicItemKind.Album, "unknown-album", "Track", "Artist", "native-album")],
            new NativeMusicItem<string>(MusicItemKind.Song, "unknown-native", "Track", "Artist", "native"));
        var service = new MusicSearchService(gateway.Object, index.Object);
        var results = await service.SearchAsync(context, Request("Track"), native);
        Assert.Equal("external", Assert.Single(results.Songs).Song!.Id);
        Assert.Equal("clean-album", Assert.Single(results.Albums).Album!.Id);
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
    public async Task MergedSearchKeepsReadySongsWhenPlaylistSearchIsLate()
    {
        var gateway = Gateway(new SearchResult { Songs = [new Song { Id = "fast", Title = "Track" }] });
        var playlists = new TaskCompletionSource<List<ExternalPlaylist>>(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.Setup(service => service.SearchPlaylistsAsync(_context, "Track", 20)).Returns(playlists.Task);
        var service = new MusicSearchService(gateway.Object);
        var request = Request("Track") with { Playlists = new(20), LimitProviderWait = true };
        try
        {
            var result = await service.SearchAsync(_context, request, new FakeNative([])).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("fast", Assert.Single(result.Songs).Song!.Id);
            Assert.False(playlists.Task.IsCompleted);
        }
        finally { playlists.TrySetResult([]); }
    }

    [Fact]
    public async Task SearchBoundsSynchronousOwnershipWorkWithoutBlockingTheRequest()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var index = new Mock<ILibraryIndexService>();
        index.Setup(service => service.FindKnownRecordingTracksAsync(_context, It.IsAny<IReadOnlyList<LibraryRecordingLookup>>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                entered.TrySetResult();
                release.Wait();
                return Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
            });
        var service = new MusicSearchService(Gateway(new SearchResult { Songs = [new Song { Id = "external", Title = "Track" }] }).Object,
            index.Object, timeProvider: clock);
        var pending = service.SearchAsync(_context, Request("Track") with { LimitProviderWait = true }, new FakeNative([]));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            clock.Advance(TimeSpan.FromSeconds(2));
            Assert.Equal("external", Assert.Single((await pending.WaitAsync(TimeSpan.FromSeconds(5))).Songs).Song!.Id);
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task SearchReadsOwnedCopiesInOneBatchAndPreservesProviderOrder()
    {
        var songs = Enumerable.Range(0, 8).Select(index => new Song { Id = $"external-{index}", Title = "Track" }).ToArray();
        var index = new Mock<ILibraryIndexService>();
        index.Setup(service => service.FindKnownRecordingTracksAsync(_context, It.IsAny<IReadOnlyList<LibraryRecordingLookup>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(songs.ToDictionary(song => song.Id, song => song.Id.Replace("external", "native")));
        var legacy = new Mock<INativeMusicSearch<string>>(MockBehavior.Strict);
        legacy.Setup(service => service.GetSongAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) => new NativeMusicItem<string>(MusicItemKind.Song, id, "Track", "Artist", "complete " + id));
        await ((INativeMusicSearch<string>)new DelegatingNative(legacy.Object)).GetSongsAsync(songs.Select(song => song.Id.Replace("external", "native")).ToArray(), default);
        legacy.Verify(service => service.GetSongAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(8));
        var native = new Mock<INativeMusicSearch<string>>(MockBehavior.Strict);
        native.Setup(service => service.SearchAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<NativeMusicItem<string>>());
        native.Setup(service => service.GetSongsAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string> ids, CancellationToken _) => ids.Reverse().Select(id =>
                new NativeMusicItem<string>(MusicItemKind.Song, id, "Track", "Artist", "complete " + id)).ToArray());
        var result = await new MusicSearchService(Gateway(new SearchResult { Songs = songs.ToList() }).Object, index.Object)
            .SearchAsync(_context, Request("Track"), native.Object);
        Assert.Equal(songs.Select(song => song.Id.Replace("external", "native")), result.Songs.Select(Id));
        Assert.All(result.Songs, row => Assert.Equal("complete " + row.Native!.Id, row.Native.Payload));
        native.Verify(service => service.GetSongsAsync(It.Is<IReadOnlyList<string>>(ids => ids.Count == 8), It.IsAny<CancellationToken>()), Times.Once);
        native.Verify(service => service.GetSongAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SearchLeavesExternalSongsPlayableWhenOwnedResolutionIsSlow()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<IReadOnlyDictionary<string, string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var index = new Mock<ILibraryIndexService>();
        index.Setup(service => service.FindKnownRecordingTracksAsync(_context, It.IsAny<IReadOnlyList<LibraryRecordingLookup>>(), It.IsAny<CancellationToken>()))
            .Returns(() => { entered.TrySetResult(); return release.Task; });
        var song = new Song { Id = "external", Title = "Track" };
        var service = new MusicSearchService(Gateway(new SearchResult { Songs = [song] }).Object, index.Object, timeProvider: clock);
        var native = new FakeNative([], new NativeMusicItem<string>(MusicItemKind.Song, "native", "Track", "Artist", "complete"));
        var request = Request("Track") with { LimitProviderWait = true };
        var pending = service.SearchAsync(_context, request, native);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal("external", Assert.Single((await pending.WaitAsync(TimeSpan.FromSeconds(5))).Songs).Song!.Id);
        release.SetResult(new Dictionary<string, string> { ["external"] = "native" });
        var repeat = await service.SearchAsync(_context, request, native);
        Assert.Equal("native", Assert.Single(repeat.Songs).Native!.Id);
        Assert.Equal("external", song.Id);
        index.Verify(service => service.FindKnownRecordingTracksAsync(_context, It.IsAny<IReadOnlyList<LibraryRecordingLookup>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MergedSearchLimitsProviderWaitAfterNativeAndUsesTheLateAnswerOnRepeat()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var providerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new TaskCompletionSource<SearchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(service => service.SearchAsync(_context, "Track", 20, 20, 20, null))
            .Returns(() => { providerStarted.TrySetResult(); return late.Task; });
        var service = new MusicSearchService(gateway.Object, timeProvider: clock);
        var native = new FakeNative([new(MusicItemKind.Song, "native", "Track", "Artist", "original")]);
        var request = Request("Track") with { LimitProviderWait = true };
        var first = service.SearchAsync(_context, request, native);
        await providerStarted.Task;
        clock.Advance(TimeSpan.FromSeconds(3));
        var partial = await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("native", Assert.Single(partial.Songs).Native!.Id);
        Assert.False(late.Task.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(7));
        late.SetResult(new SearchResult { Songs = [new Song { Id = "external", Title = "Track" }] });
        var repeated = await service.SearchAsync(_context, request, native);
        Assert.Equal(["native", "external"], repeated.Songs.Select(Id));
    }

    [Fact]
    public async Task SearchStartsNativeAndProviderReadsBeforeEitherCompletes()
    {
        var nativeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var providerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var native = new Mock<INativeMusicSearch<string>>();
        native.Setup(service => service.SearchAsync(It.IsAny<CancellationToken>())).Returns(async () =>
        {
            nativeStarted.TrySetResult();
            await release.Task;
            return Array.Empty<NativeMusicItem<string>>();
        });
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(service => service.SearchAsync(_context, "Track", 20, 20, 20, null)).Returns(async () =>
        {
            providerStarted.TrySetResult();
            await release.Task;
            return new SearchResult();
        });
        var pending = new MusicSearchService(gateway.Object).SearchAsync(_context, Request("Track"), native.Object);
        try { await Task.WhenAll(nativeStarted.Task, providerStarted.Task).WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { release.TrySetResult(); }
        await pending;
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
            .ProjectSongsAsync(_context, songs, new DelegatingNative(native.Object));
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
    public async Task RepeatedSearchSharesOwnedRecordingLookupAndKeepsNativeReadsFresh()
    {
        var gateway = Gateway(new SearchResult { Songs = [new Song { Id = "external", Title = "Track" }] });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<IReadOnlyDictionary<string, string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var index = new Mock<ILibraryIndexService>(MockBehavior.Strict);
        index.Setup(item => item.FindKnownRecordingTracksAsync(_context,
                It.IsAny<IReadOnlyList<LibraryRecordingLookup>>(), It.IsAny<CancellationToken>()))
            .Returns(() => { entered.TrySetResult(); return release.Task; });
        var native = new FakeNative([], new NativeMusicItem<string>(MusicItemKind.Song, "native", "Track", null, "complete row"));
        var service = new MusicSearchService(gateway.Object, index.Object);
        var first = service.SearchAsync(_context, Request("Track"), native);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = service.SearchAsync(_context, Request("Track"), new FakeNative([],
            new NativeMusicItem<string>(MusicItemKind.Song, "native", "Track", null, "complete row")));
        release.SetResult(new Dictionary<string, string> { ["external"] = "native" });
        var answers = await Task.WhenAll(first, second);
        var repeated = await service.SearchAsync(_context, Request("Track"), new FakeNative([]));

        Assert.All(answers, answer => Assert.Equal("complete row", Assert.Single(answer.Songs).Native!.Payload));
        Assert.Equal("external", Assert.Single(repeated.Songs).Song!.Id);
        index.Verify(item => item.FindKnownRecordingTracksAsync(_context,
            It.IsAny<IReadOnlyList<LibraryRecordingLookup>>(), It.IsAny<CancellationToken>()), Times.Once);
        await service.ProjectSongsAsync(_context, [new Song { Id = "external" }], native);
        index.Verify(item => item.FindKnownRecordingTracksAsync(_context,
            It.IsAny<IReadOnlyList<LibraryRecordingLookup>>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task OwnedSearchCacheExpiresAndSeparatesPrincipalsAndRecordingEvidence()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var other = new ProtocolExecutionContext(_context.Protocol, _context.BackendInstanceId, "other-principal", null,
            "other-search", _context.Deadline, default);
        var song = new Song { Id = "external", Title = "Track", Isrc = "USUM72409273" };
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.SearchAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), 20, 20, 20, null))
            .ReturnsAsync(new SearchResult { Songs = [song] });
        var index = new Mock<ILibraryIndexService>();
        index.Setup(item => item.FindKnownRecordingTracksAsync(It.IsAny<ProtocolExecutionContext>(),
                It.IsAny<IReadOnlyList<LibraryRecordingLookup>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string>());
        var service = new MusicSearchService(gateway.Object, index.Object, timeProvider: clock);
        var native = new FakeNative([]);
        await service.SearchAsync(_context, Request("Track"), native);
        await service.SearchAsync(_context, Request("Track"), native);
        await service.SearchAsync(other, Request("Track"), native);
        song.Isrc = "USUM72409274";
        await service.SearchAsync(_context, Request("Track"), native);
        clock.Advance(TimeSpan.FromMinutes(10));
        await service.SearchAsync(_context, Request("Track"), native);
        index.Verify(item => item.FindKnownRecordingTracksAsync(It.IsAny<ProtocolExecutionContext>(),
            It.IsAny<IReadOnlyList<LibraryRecordingLookup>>(), It.IsAny<CancellationToken>()), Times.Exactly(4));
    }

    [Fact]
    public async Task OwnedSearchLookupFailureCanRetry()
    {
        var gateway = Gateway(new SearchResult { Songs = [new Song { Id = "external", Title = "Track" }] });
        var index = new Mock<ILibraryIndexService>();
        index.SetupSequence(item => item.FindKnownRecordingTracksAsync(_context,
                It.IsAny<IReadOnlyList<LibraryRecordingLookup>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Unavailable"))
            .ReturnsAsync(new Dictionary<string, string>());
        var service = new MusicSearchService(gateway.Object, index.Object);
        await Assert.ThrowsAsync<HttpRequestException>(() => service.SearchAsync(_context, Request("Track"), new FakeNative([])));
        Assert.Equal("external", Assert.Single((await service.SearchAsync(_context, Request("Track"), new FakeNative([]))).Songs).Song!.Id);
        index.Verify(item => item.FindKnownRecordingTracksAsync(_context,
            It.IsAny<IReadOnlyList<LibraryRecordingLookup>>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task CancelledSearchWaiterKeepsTheSharedOwnedLookupAvailable()
    {
        var gateway = Gateway(new SearchResult { Songs = [new Song { Id = "external", Title = "Track" }] });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<IReadOnlyDictionary<string, string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var index = new Mock<ILibraryIndexService>();
        index.Setup(item => item.FindKnownRecordingTracksAsync(_context,
                It.IsAny<IReadOnlyList<LibraryRecordingLookup>>(), It.IsAny<CancellationToken>()))
            .Returns(() => { entered.TrySetResult(); return release.Task; });
        var service = new MusicSearchService(gateway.Object, index.Object);
        using var cancellation = new CancellationTokenSource();
        var cancelled = service.SearchAsync(_context, Request("Track"), new FakeNative([]), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var other = service.SearchAsync(_context, Request("Track"), new FakeNative([]));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        release.SetResult(new Dictionary<string, string>());
        Assert.Equal("external", Assert.Single((await other).Songs).Song!.Id);
        index.Verify(item => item.FindKnownRecordingTracksAsync(_context,
            It.IsAny<IReadOnlyList<LibraryRecordingLookup>>(), It.IsAny<CancellationToken>()), Times.Once);
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
    public async Task OnlySongLinkedServiceArtistsHideBehindALibraryArtist()
    {
        var listener = ListenerContext();
        var gateway = Gateway(new SearchResult
        {
            Artists =
            [
                new Artist { Id = "ext-deezer-artist-linked", Name = "daft punk", ExternalProvider = "deezer", ExternalId = "linked" },
                new Artist { Id = "ext-applemusic-artist-namesake", Name = "Daft Punk", ExternalProvider = "applemusic", ExternalId = "namesake" },
                new Artist { Id = "ext-deezer-artist-other", Name = "Justice", ExternalProvider = "deezer", ExternalId = "other" }
            ]
        }, context: listener);
        var native = new FakeNative([new NativeMusicItem<string>(MusicItemKind.Artist, "native-artist", "Daft Punk", null, "native")]);
        var links = new Mock<ILibraryArtistLinks>();
        links.Setup(item => item.ReadSavedAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, IReadOnlyList<LibraryArtistLink>> { ["Daft Punk"] = [new("deezer", "linked")] });
        var service = new MusicSearchService(gateway.Object, artistLinks: links.Object);

        var hidden = await service.SearchAsync(listener, Request("punk") with { HideLinkedProviderArtists = true }, native);
        var shown = await service.SearchAsync(listener, Request("punk"), native);

        // Apple's same-named artist has no song links, so it stays beside the library artist.
        Assert.Equal(["ext-applemusic-artist-namesake", "ext-deezer-artist-other", "native-artist"],
            hidden.Artists.Select(Id).Order(StringComparer.Ordinal));
        // Without the link the two same-named service artists are one streaming artist, the first one found.
        Assert.Equal(["ext-deezer-artist-linked", "ext-deezer-artist-other", "native-artist"],
            shown.Artists.Select(Id).Order(StringComparer.Ordinal));
        links.Verify(item => item.RequestAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ASameNamedServiceArtistWithoutLinksStaysAndQueuesSongMatching()
    {
        var listener = ListenerContext();
        var gateway = Gateway(new SearchResult
        {
            Artists = [new Artist { Id = "ext-deezer-artist-1", Name = "Say Now", ExternalProvider = "deezer", ExternalId = "1" }]
        }, context: listener);
        var native = new FakeNative([new NativeMusicItem<string>(MusicItemKind.Artist, "native-artist", "say now", null, "native")]);
        var links = new Mock<ILibraryArtistLinks>();
        links.Setup(item => item.ReadSavedAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, IReadOnlyList<LibraryArtistLink>>());

        var result = await new MusicSearchService(gateway.Object, artistLinks: links.Object)
            .SearchAsync(listener, Request("say now") with { HideLinkedProviderArtists = true }, native);

        Assert.Equal(["ext-deezer-artist-1", "native-artist"], result.Artists.Select(Id).Order(StringComparer.Ordinal));
        links.Verify(item => item.RequestAsync(listener.Actor!.EffectiveUserId!.Value, "say now", It.IsAny<CancellationToken>()), Times.Once);
        links.Verify(item => item.RecordCandidatesAsync(listener.Actor!.EffectiveUserId!.Value,
            It.Is<IReadOnlyCollection<string>>(names => names.Contains("say now")),
            It.Is<IReadOnlyCollection<Artist>>(artists => artists.Any(artist => artist.ExternalId == "1")), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void EachNameKeepsOneStreamingArtist()
    {
        Artist Artist(string provider, string id, string name = "Say Now") =>
            new() { Id = $"ext-{provider}-artist-{id}", Name = name, ExternalProvider = provider, ExternalId = id };

        var kept = MusicSearchService.OneServiceArtistPerName(
            [Artist("deezer", "1"), Artist("applemusic", "2", "SAY NOW"), Artist("qobuz", "3", "Say Now See"), Artist("deezer", "4")]);

        Assert.Equal(["ext-deezer-artist-1", "ext-qobuz-artist-3"], kept.Select(artist => artist.Id));
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

    private static ProtocolExecutionContext ListenerContext()
    {
        var listener = Guid.CreateVersion7();
        return new(ProtocolKind.Subsonic, "backend", listener.ToString(),
            new AllstarrPrincipal(listener, "subsonic", "backend", listener.ToString(), "Listener", false), "search-test",
            DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);
    }

    private static MusicSearchRequest Request(string query) => new(
        query,
        new MusicSearchWindow(20),
        new MusicSearchWindow(20),
        new MusicSearchWindow(20),
        MusicSearchWindow.None);

    private Mock<IProtocolProviderGateway> Gateway(SearchResult result, List<ExternalPlaylist>? playlists = null,
        ProtocolExecutionContext? context = null)
    {
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.SearchAsync(context ?? _context, It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<int>(), null))
            .ReturnsAsync(result);
        gateway.Setup(item => item.SearchPlaylistsAsync(context ?? _context, It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync(playlists ?? []);
        return gateway;
    }

    private static string Id(MusicSearchEntry<string> entry) =>
        entry.Native?.Id ?? entry.Song?.Id ?? entry.Album?.Id ?? entry.Artist?.Id ?? entry.Playlist!.Id;

    private sealed class DelegatingNative(INativeMusicSearch<string> single) : INativeMusicSearch<string>
    {
        public Task<IReadOnlyList<NativeMusicItem<string>>> SearchAsync(CancellationToken token) => single.SearchAsync(token);
        public Task<NativeMusicItem<string>?> GetSongAsync(string id, CancellationToken token) => single.GetSongAsync(id, token);
    }

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
