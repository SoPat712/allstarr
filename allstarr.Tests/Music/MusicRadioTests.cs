using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Intelligence;
using allstarr.Core.Matching;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Core.Settings;
using allstarr.Models.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace allstarr.Tests;

public sealed class MusicRadioTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Radio_RemembersCandidatesOnlyForTheLinkedListener(bool linked)
    {
        var gateway = Gateway();
        var context = MusicObservationsTests.Context(linked);
        var retained = new List<(Guid? User, string Id)>();
        gateway.Setup(item => item.RememberTrackAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>()))
            .Callback<ProtocolExecutionContext, string>((actor, id) => retained.Add((actor.Actor?.EffectiveUserId, id)))
            .Returns(Task.CompletedTask);
        using var service = new MusicMixService(gateway.Object);

        var result = await Radio(service, context, 1);

        Assert.Equal("ext-deezer-song-seed", Assert.Single(result).Song!.Id);
        if (linked) Assert.Equal((context.Actor!.EffectiveUserId, "ext-deezer-song-seed"), Assert.Single(retained));
        else Assert.Empty(retained);
    }

    [Fact]
    public async Task SongRadio_SeedFirstDropsUnplayableAndDuplicateRecordingsAcrossProviders()
    {
        var gateway = Gateway();
        var recording = Guid.NewGuid();
        var first = Song("first");
        first.CanonicalRecordingId = recording;
        first.Isrc = "USABC2600001";
        var duplicate = Song("copy", "qobuz");
        duplicate.CanonicalRecordingId = recording;
        var isrcCopy = Song("isrc-copy", "qobuz");
        isrcCopy.Isrc = "US-ABC-26-00001";
        var live = Song("live");
        live.Title = "First (Live)";
        gateway.Setup(item => item.GetSimilarSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "seed", 50))
            .ReturnsAsync([Song("seed"), Song("missing"), first, duplicate, isrcCopy, live]);
        gateway.Setup(item => item.CanPlaySongAsync(It.IsAny<ProtocolExecutionContext>(), It.Is<Song>(song => song.ExternalId == "missing")))
            .ReturnsAsync(false);

        var result = await Radio(new MusicMixService(gateway.Object), Context(), 500);

        Assert.Equal(["ext-deezer-song-seed", "ext-deezer-song-first", "ext-deezer-song-live"], result.Select(row => row.Song!.Id));
    }

    [Fact]
    public async Task ArtistRadio_StartsWithTheArtistsTracksThenRelatedArtists()
    {
        var gateway = Gateway();
        gateway.Setup(item => item.GetArtistAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "seed"))
            .ReturnsAsync(new Artist { Name = "Seed artist" });
        gateway.Setup(item => item.GetArtistTracksAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "seed"))
            .ReturnsAsync([Song("own")]);
        gateway.Setup(item => item.GetSimilarArtistsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "seed", 5))
            .ReturnsAsync([new Artist { Name = "Related", ExternalProvider = "deezer", ExternalId = "related" }]);
        gateway.Setup(item => item.GetArtistTracksAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "related"))
            .ReturnsAsync([Song("neighbor")]);

        var result = await new MusicMixService(gateway.Object).RadioAsync(Context(), "deezer", "artist", "seed",
            "ext-deezer-artist-seed", 50, Native());

        Assert.Equal(["ext-deezer-song-own", "ext-deezer-song-neighbor"], result.Select(row => row.Song!.Id));
    }

    [Fact]
    public async Task Radio_TimeoutReturnsQualifiedPartialListEvenWhenSourceIgnoresCancellation()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var gateway = Gateway();
        var pending = new TaskCompletionSource<List<Song>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.Setup(item => item.GetSimilarSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "seed", 50))
            .Returns(() => { started.SetResult(); return pending.Task; });
        var service = new MusicMixService(gateway.Object, timeProvider: clock);

        var request = Radio(service, Context(), 50);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromSeconds(5));
        var result = await request.WaitAsync(TimeSpan.FromSeconds(10));
        pending.SetResult([Song("late")]);

        Assert.Equal("ext-deezer-song-seed", Assert.Single(result).Song!.Id);
        gateway.Verify(item => item.CanPlaySongAsync(It.IsAny<ProtocolExecutionContext>(), It.Is<Song>(song => song.ExternalId == "late")), Times.Never);
    }

    [Fact]
    public async Task Radio_CacheIsScopedExpiresAndRechecksPlayability()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var gateway = Gateway();
        var service = new MusicMixService(gateway.Object, timeProvider: clock);
        var context = Context();
        Assert.Single(await Radio(service, context, 50));
        gateway.Setup(item => item.CanPlaySongAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<Song>())).ReturnsAsync(false);
        Assert.Empty(await Radio(service, context, 50));
        gateway.Verify(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "seed"), Times.Once);

        await Radio(service, Context("other"), 50);
        clock.Advance(TimeSpan.FromMinutes(2));
        await Radio(service, context, 50);
        gateway.Verify(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "seed"), Times.Exactly(3));
    }

    [Fact]
    public async Task Radio_CallerCancellationPropagates()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Radio(
            new MusicMixService(Gateway().Object), Context(), 50, cancelled.Token));
    }

    [Fact]
    public async Task Radio_CleanOnlySkipsExplicitCandidatesBeforeQualification()
    {
        var gateway = Gateway();
        var explicitSong = Song("explicit");
        explicitSong.ExplicitContentLyrics = 1;
        gateway.Setup(item => item.GetSimilarSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "seed", 50))
            .ReturnsAsync([explicitSong, Song("clean")]);
        var context = Context() with
        {
            Policy = new EffectiveProviderPolicySnapshot(
            ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty,
            ImmutableHashSet<string>.Empty, AudioQualityPolicy.DefaultStep, 0.07)
            { Preferences = new("CleanOnly") }
        };

        var result = await Radio(new MusicMixService(gateway.Object), context, 50);

        Assert.Equal(["ext-deezer-song-seed", "ext-deezer-song-clean"], result.Select(row => row.Song!.Id));
        gateway.Verify(item => item.CanPlaySongAsync(It.IsAny<ProtocolExecutionContext>(), explicitSong), Times.Never);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Radio_NativeCopyMustBeReadableBeforeItReplacesAnUnplayableProvider(bool readable)
    {
        var gateway = Gateway();
        gateway.Setup(item => item.CanPlaySongAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<Song>())).ReturnsAsync(false);
        var index = new Mock<ILibraryIndexService>(MockBehavior.Strict);
        index.Setup(item => item.FindKnownRecordingTracksAsync(It.IsAny<ProtocolExecutionContext>(),
                It.IsAny<IReadOnlyList<LibraryRecordingLookup>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string> { ["ext-deezer-song-seed"] = "native" });
        var native = new Mock<INativeMusicSearch<string>>(MockBehavior.Strict);
        native.Setup(item => item.GetSongAsync("native", It.IsAny<CancellationToken>()))
            .ReturnsAsync(readable ? new NativeMusicItem<string>(MusicItemKind.Song, "native", "Seed", "Artist", "preserved") : null);
        var service = new MusicMixService(gateway.Object, musicSearch: new MusicSearchService(gateway.Object, index.Object));

        var result = await service.RadioAsync(Context(), "deezer", "song", "seed", "ext-deezer-song-seed", 1, native.Object);

        if (readable) Assert.Equal("preserved", Assert.Single(result).Native!.Payload);
        else Assert.Empty(result);
        gateway.Verify(item => item.CanPlaySongAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<Song>()), readable ? Times.Never : Times.Once);
    }

    [Fact]
    public async Task Radio_QualificationTimeoutKeepsEarlierPlayableRows()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var gateway = Gateway();
        gateway.Setup(item => item.GetSimilarSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "seed", 50))
            .ReturnsAsync([Song("first"), Song("slow"), Song("later")]);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.Setup(item => item.CanPlaySongAsync(It.IsAny<ProtocolExecutionContext>(), It.Is<Song>(song => song.ExternalId == "slow")))
            .Returns(() => { started.SetResult(); return pending.Task; });

        var request = Radio(new MusicMixService(gateway.Object, timeProvider: clock), Context(), 50);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromSeconds(5));
        var result = await request.WaitAsync(TimeSpan.FromSeconds(10));
        pending.SetResult(true);

        Assert.Equal(["ext-deezer-song-seed", "ext-deezer-song-first"], result.Select(row => row.Song!.Id));
        gateway.Verify(item => item.CanPlaySongAsync(It.IsAny<ProtocolExecutionContext>(), It.Is<Song>(song => song.ExternalId == "later")), Times.Never);
    }

    [Fact]
    public async Task SongRadio_ResolvesOptionalSuggestionsAcrossProvidersWithoutSubstitutingLiveVersions()
    {
        var gateway = Gateway();
        var studio = Song("studio", "qobuz");
        studio.Title = "Suggested";
        var live = Song("live");
        live.Title = "Suggested (Live)";
        gateway.Setup(item => item.SearchAsync(It.IsAny<ProtocolExecutionContext>(), "Artist Suggested", 3, 0, 0, null))
            .ReturnsAsync(new allstarr.Models.Search.SearchResult { Songs = [live, studio] });
        gateway.Setup(item => item.SearchAsync(It.IsAny<ProtocolExecutionContext>(), "Artist Missing", 3, 0, 0, null))
            .ReturnsAsync(new allstarr.Models.Search.SearchResult());
        using var handler = new SuggestionsHandler();
        using var http = new HttpClient(handler);
        var clients = new Mock<IHttpClientFactory>();
        clients.Setup(item => item.CreateClient(nameof(LastFmSimilarTracks))).Returns(http);
        var lastFm = new LastFmSimilarTracks(clients.Object, new SuggestionAccount(), NullLogger<LastFmSimilarTracks>.Instance);

        var result = await Radio(new MusicMixService(gateway.Object, lastFm), MusicObservationsTests.Context(), 50);

        Assert.Equal(["ext-deezer-song-seed", "ext-qobuz-song-studio"], result.Select(row => row.Song!.Id));
        gateway.Verify(item => item.CanPlaySongAsync(It.IsAny<ProtocolExecutionContext>(), live), Times.Never);
        gateway.VerifyAll();
    }

    private sealed class SuggestionAccount : IScopedRecommendationAccountAccessor
    {
        public Task<bool> HasAccountAsync(IntelligenceScope scope, string providerId, CancellationToken cancellationToken) => Task.FromResult(true);

        public async Task<T> UseAsync<T>(IntelligenceScope scope, string providerId,
            Func<JsonElement, CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
        {
            using var secret = JsonDocument.Parse("""{"apiKey":"fixture-key"}""");
            return await operation(secret.RootElement, cancellationToken);
        }
    }

    private sealed class SuggestionsHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"similartracks":{"track":[{"name":"Suggested","artist":{"name":"Artist"}},{"name":"Missing","artist":{"name":"Artist"}}]}}""")
            });
    }

    private static Task<IReadOnlyList<MusicSearchEntry<string>>> Radio(MusicMixService service,
        ProtocolExecutionContext context, int limit, CancellationToken token = default) =>
        service.RadioAsync(context, "deezer", "song", "seed", "ext-deezer-song-seed", limit, Native(), token);

    private static Mock<IProtocolProviderGateway> Gateway()
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "seed")).ReturnsAsync(Song("seed"));
        gateway.Setup(item => item.GetSimilarSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "seed", It.IsAny<int>())).ReturnsAsync([]);
        gateway.Setup(item => item.CanPlaySongAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<Song>())).ReturnsAsync(true);
        return gateway;
    }

    private static INativeMusicSearch<string> Native() => Mock.Of<INativeMusicSearch<string>>(MockBehavior.Strict);

    private static ProtocolExecutionContext Context(string principal = "listener") => new(
        ProtocolKind.Jellyfin, "backend", principal, null, "radio-test", DateTimeOffset.UtcNow.AddMinutes(10), default);

    private static Song Song(string id, string provider = "deezer") => new()
    {
        Id = $"ext-{provider}-song-{id}",
        Title = id,
        Artist = "Artist",
        Duration = 200,
        ExplicitContentLyrics = 0,
        ExternalProvider = provider,
        ExternalId = id
    };
}
