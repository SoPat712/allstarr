using System.Collections.Immutable;
using allstarr.Core.Capabilities;
using allstarr.Core.Matching;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Core.Settings;
using allstarr.Models.Domain;
using allstarr.Models.Search;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace allstarr.Tests;

public sealed class MusicMixServiceTimingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SimilarAndInstantMix_ShareConcurrentReadsAndCacheForSixHours(bool album)
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var gateway = Gateway();
        var seed = new TaskCompletionSource<Song?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAlbum = new TaskCompletionSource<Album?>(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.Setup(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "seed")).Returns(seed.Task);
        gateway.Setup(item => item.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "seed")).Returns(releaseAlbum.Task);
        using var service = new MusicMixService(gateway.Object, timeProvider: clock);
        var context = Context(clock);

        var first = Read(service, context, album);
        var second = Read(service, context, album);
        AssertReads(gateway, album, 1);
        seed.SetResult(Song("seed"));
        releaseAlbum.SetResult(new Album { Songs = [Song("ready")] });
        var firstResult = await first;
        Assert.Equal(firstResult.Select(song => song.Id), (await second).Select(song => song.Id));
        firstResult[0].Title = "changed by a caller";
        Assert.Equal("ready", Assert.Single(await Read(service, context, album)).Title);
        clock.Advance(TimeSpan.FromHours(6) - TimeSpan.FromSeconds(1));
        await Read(service, Context(clock), album);
        AssertReads(gateway, album, 1);
        clock.Advance(TimeSpan.FromSeconds(1));
        await Read(service, Context(clock), album);
        AssertReads(gateway, album, 2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SimilarAndInstantMix_CacheSeparatesListenerBackendPolicyAndLimit(bool album)
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var gateway = Gateway();
        using var service = new MusicMixService(gateway.Object, timeProvider: clock);
        await Read(service, Context(clock), album);
        await Read(service, Context(clock, "other-listener"), album);
        await Read(service, Context(clock, backend: "other-backend"), album);
        await Read(service, Context(clock) with
        {
            Policy = new EffectiveProviderPolicySnapshot(
                ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty,
                ImmutableHashSet<string>.Empty, AudioQualityPolicy.DefaultStep, 0.07)
            { Preferences = new("CleanOnly") }
        }, album);
        await Read(service, Context(clock), album, 2);
        AssertReads(gateway, album, 5);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SimilarAndInstantMix_CancellingOneWaiterKeepsSharedReadAlive(bool album)
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var gateway = Gateway();
        var seed = new TaskCompletionSource<Song?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAlbum = new TaskCompletionSource<Album?>(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.Setup(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "seed")).Returns(seed.Task);
        gateway.Setup(item => item.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "seed")).Returns(releaseAlbum.Task);
        using var service = new MusicMixService(gateway.Object, timeProvider: clock);
        using var caller = new CancellationTokenSource();
        var cancelled = Read(service, Context(clock, token: caller.Token), album);
        var waiting = Read(service, Context(clock), album);
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        seed.SetResult(Song("seed"));
        releaseAlbum.SetResult(new Album { Songs = [Song("ready")] });
        Assert.Equal("ready", Assert.Single(await waiting).Id);
        AssertReads(gateway, album, 1);
    }

    [Fact]
    public async Task InstantMix_ThreeSecondBudgetKeepsAlbumSongsWhenArtistSearchHangs()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var gateway = Gateway();
        gateway.Setup(item => item.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "seed"))
            .ReturnsAsync(new Album { Artist = "Artist", Songs = [Song("ready")] });
        var pending = new TaskCompletionSource<SearchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.Setup(item => item.SearchAsync(It.IsAny<ProtocolExecutionContext>(), "Artist", 2, 0, 0, "deezer"))
            .Returns(pending.Task);
        using var service = new MusicMixService(gateway.Object, timeProvider: clock);

        var request = Read(service, Context(clock), true, 2);
        await clock.WaitForTimersAsync(TimeSpan.FromSeconds(3));
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal("ready", Assert.Single(await request.WaitAsync(TimeSpan.FromSeconds(10))).Id);
        pending.SetResult(new SearchResult { Songs = [Song("late")] });
        Assert.Equal("ready", Assert.Single(await Read(service, Context(clock), true, 2)).Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SimilarAndInstantMix_HangingSeedTimesOutAndDoesNotCacheEmptyResult(bool album)
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var gateway = Gateway();
        var seed = new TaskCompletionSource<Song?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAlbum = new TaskCompletionSource<Album?>(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.Setup(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "seed")).Returns(seed.Task);
        gateway.Setup(item => item.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "seed")).Returns(releaseAlbum.Task);
        using var service = new MusicMixService(gateway.Object, timeProvider: clock);

        var request = Read(service, Context(clock), album);
        await clock.WaitForTimersAsync(TimeSpan.FromSeconds(3));
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Empty(await request.WaitAsync(TimeSpan.FromSeconds(10)));
        seed.SetResult(Song("seed"));
        releaseAlbum.SetResult(new Album { Songs = [Song("ready")] });
        Assert.Equal("ready", Assert.Single(await Read(service, Context(clock), album)).Id);
        AssertReads(gateway, album, 2);
    }

    [Fact]
    public async Task SimilarSongs_ThreeSecondBudgetReturnsReadySuggestionDespiteHangingLookup()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var gateway = Gateway();
        gateway.Setup(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "seed"))
            .ReturnsAsync(Song("seed"));
        gateway.Setup(item => item.GetSimilarSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "seed", 2))
            .ReturnsAsync([]);
        var pending = new TaskCompletionSource<SearchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.Setup(item => item.SearchAsync(It.IsAny<ProtocolExecutionContext>(), "Artist First", 3, 0, 0, "deezer"))
            .Returns(pending.Task);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.Setup(item => item.SearchAsync(It.IsAny<ProtocolExecutionContext>(), "Artist Second", 3, 0, 0, "deezer"))
            .Callback(() => ready.SetResult())
            .ReturnsAsync(new SearchResult { Songs = [new Song { Id = "ready", Artist = "Artist", Title = "Second" }] });
        using var handler = new ListenBrainzSimilarTracksTests.Handler("ok");
        using var service = new MusicMixService(gateway.Object,
            listenBrainz: ListenBrainzSimilarTracksTests.Create(handler), timeProvider: clock);
        var linked = MusicObservationsTests.Context();

        var request = service.SimilarSongsAsync(linked, "deezer", "seed", "seed", 2);
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal("ready", Assert.Single(await request.WaitAsync(TimeSpan.FromSeconds(10))).Id);
        pending.SetResult(new SearchResult { Songs = [new Song { Id = "late", Artist = "Artist", Title = "First" }] });
    }

    [Fact]
    public async Task SimilarSongs_MatchesAtMostFourSuggestionsConcurrentlyAndKeepsSuggestionOrder()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var gateway = Gateway();
        gateway.Setup(item => item.GetSimilarSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "seed", 8))
            .ReturnsAsync([]);
        var searches = Enumerable.Range(0, 8).Select(_ =>
            new TaskCompletionSource<SearchResult>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var active = 0;
        var maximum = 0;
        var started = Enumerable.Range(0, 8).Select(_ =>
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        for (var index = 0; index < searches.Length; index++)
        {
            var slot = index;
            gateway.Setup(item => item.SearchAsync(It.IsAny<ProtocolExecutionContext>(), $"Artist Track {slot}", 3, 0, 0, "deezer"))
                .Returns(async () =>
                {
                    var count = Interlocked.Increment(ref active);
                    int known;
                    do { known = Volatile.Read(ref maximum); }
                    while (count > known && Interlocked.CompareExchange(ref maximum, count, known) != known);
                    started[slot].SetResult();
                    try { return await searches[slot].Task; }
                    finally { Interlocked.Decrement(ref active); }
                });
        }
        var identities = new Mock<ITrackIdentityService>(MockBehavior.Strict);
        identities.Setup(item => item.ResolveRecordingAsync(It.IsAny<ProviderActorContext>(), "seed", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CanonicalRecordingIdentity(Guid.NewGuid(), Guid.NewGuid(), null, ListenBrainzSimilarTracksTests.Mbid, 1));
        var cache = new TestMemoryApplicationCache();
        await cache.SetAsync($"music:listenbrainz:similar:{ListenBrainzSimilarTracksTests.Mbid}",
            Enumerable.Range(0, 8).Select(index => new SimilarTrackSuggestion("Artist", $"Track {index}")).ToArray(), TimeSpan.FromMinutes(1));
        var suggestions = new ListenBrainzSimilarTracks(Mock.Of<IHttpClientFactory>(MockBehavior.Strict),
            identities.Object, cache, NullLogger<ListenBrainzSimilarTracks>.Instance);
        using var service = new MusicMixService(gateway.Object, listenBrainz: suggestions, timeProvider: clock);

        var request = service.SimilarSongsAsync(MusicObservationsTests.Context(), "deezer", "seed", "seed", 8);
        await Task.WhenAll(started.Take(4).Select(item => item.Task)).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.All(started.Skip(4), item => Assert.False(item.Task.IsCompleted));
        for (var index = 3; index >= 0; index--) Complete(index);
        await Task.WhenAll(started.Skip(4).Select(item => item.Task)).WaitAsync(TimeSpan.FromSeconds(10));
        for (var index = 7; index >= 4; index--) Complete(index);
        Assert.Equal(Enumerable.Range(0, 8).Select(index => $"found-{index}"), (await request).Select(song => song.Id));
        Assert.Equal(4, maximum);

        void Complete(int index) => searches[index].SetResult(new SearchResult
        { Songs = [new Song { Id = $"found-{index}", Artist = "Artist", Title = $"Track {index}" }] });
    }

    private static Mock<IProtocolProviderGateway> Gateway()
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "seed"))
            .ReturnsAsync(Song("seed"));
        gateway.Setup(item => item.GetSimilarSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "seed", It.IsAny<int>()))
            .ReturnsAsync([Song("ready")]);
        gateway.Setup(item => item.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "seed"))
            .ReturnsAsync(new Album { Songs = [Song("ready"), Song("another")] });
        return gateway;
    }

    private static void AssertReads(Mock<IProtocolProviderGateway> gateway, bool album, int count)
    {
        if (album) gateway.Verify(item => item.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "seed"), Times.Exactly(count));
        else gateway.Verify(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "seed"), Times.Exactly(count));
    }

    private static Task<IReadOnlyList<Song>> Read(MusicMixService service, ProtocolExecutionContext context, bool album, int limit = 1) =>
        album ? service.InstantMixAsync(context, "deezer", "album", "seed", "seed", limit) :
            service.SimilarSongsAsync(context, "deezer", "seed", "seed", limit);

    private static ProtocolExecutionContext Context(TimeProvider clock, string listener = "listener", string backend = "backend", CancellationToken token = default) =>
        new(ProtocolKind.Jellyfin, backend, listener, null, "mix-timing", clock.GetUtcNow().AddMinutes(1), token);

    private static Song Song(string id) => new() { Id = id, ExternalId = id, Artist = "Artist", Title = id };
}
