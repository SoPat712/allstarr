using System.Threading.Channels;
using allstarr.Core.Capabilities;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Routing;
using allstarr.Models.Search;
using allstarr.Models.Settings;
using allstarr.Services.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolProviderStreamingGatewayTests
{
    [Theory]
    [InlineData("songs")]
    [InlineData("albums")]
    [InlineData("artists")]
    public async Task MetadataSearch_ReturnsWithinBudgetAndCachesLateResults(string kind)
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var cache = new TestMemoryApplicationCache();
        var writes = Channel.CreateUnbounded<string>();
        var observedCache = new Mock<IApplicationCache>();
        observedCache.SetupGet(item => item.IsEnabled).Returns(true);
        observedCache.Setup(item => item.GetStringAsync(It.IsAny<string>()))
            .Returns((string key) => cache.GetStringAsync(key));
        observedCache.Setup(item => item.SetStringAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan?>()))
            .Returns(async (string key, string result, TimeSpan? ttl) =>
            {
                var saved = await cache.SetStringAsync(key, result, ttl);
                writes.Writer.TryWrite(key);
                return saved;
            });
        var fast = DelayedSearchProvider("deezer", clock, TimeSpan.Zero);
        var slow = DelayedSearchProvider("qobuz", clock, TimeSpan.FromSeconds(8));
        var gateway = BudgetGateway(clock, observedCache.Object, fast.Object, slow.Object);
        using var requestCancellation = new CancellationTokenSource();
        var context = WithSearchCancellation(Context(), requestCancellation.Token);
        var songs = kind == "songs" ? 10 : 0;
        var albums = kind == "albums" ? 10 : 0;
        var artists = kind == "artists" ? 10 : 0;

        var search = gateway.SearchAsync(context, "Track", songs, albums, artists);
        await clock.WaitForTimersAsync(TimeSpan.FromSeconds(8), TimeSpan.FromMilliseconds(3500));
        await writes.Reader.ReadAsync(guard.Token);
        Assert.False(search.IsCompleted);
        clock.Advance(TimeSpan.FromMilliseconds(3500));
        var result = await search;
        Assert.Equal(["deezer"], SearchProviders(result));

        requestCancellation.Cancel();
        clock.Advance(TimeSpan.FromMilliseconds(4500));
        await writes.Reader.ReadAsync(guard.Token);
        var repeated = await BudgetGateway(clock, cache, fast.Object, slow.Object)
            .SearchAsync(WithSearchCancellation(context, CancellationToken.None), "Track", songs, albums, artists);
        Assert.Equal(["deezer", "qobuz"], SearchProviders(repeated));
        VerifySearchCount(fast, kind, 1);
        VerifySearchCount(slow, kind, 1);
    }

    [Fact]
    public async Task PlayableSearch_DropsLateProvidersAndKeepsTheirCacheWorkRunning()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var cache = new TestMemoryApplicationCache();
        var writes = Channel.CreateUnbounded<string>();
        var observedCache = new Mock<IApplicationCache>();
        observedCache.SetupGet(item => item.IsEnabled).Returns(true);
        observedCache.Setup(item => item.GetStringAsync(It.IsAny<string>()))
            .Returns((string key) => cache.GetStringAsync(key));
        observedCache.Setup(item => item.SetStringAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan?>()))
            .Returns(async (string key, string result, TimeSpan? ttl) =>
            {
                var saved = await cache.SetStringAsync(key, result, ttl);
                writes.Writer.TryWrite(key);
                return saved;
            });
        var fast = DelayedSearchProvider("deezer", clock, TimeSpan.Zero);
        var slow = DelayedSearchProvider("qobuz", clock, TimeSpan.FromSeconds(12));
        foreach (var provider in new[] { fast, slow })
            provider.Setup(item => item.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
                .Returns(async (ProviderExecutionContext execution, ProviderMetadataSearchRequest _) =>
                {
                    var id = provider.Object.ProviderId;
                    await Task.Delay(id == "deezer" ? TimeSpan.Zero : TimeSpan.FromSeconds(12), clock,
                        execution.CancellationToken);
                    return ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Success(new(id,
                        [new(new(id, ProviderResourceKind.Track, "track"), "Track", [new("Artist")],
                            duration: TimeSpan.FromSeconds(240))]));
                });
        var log = new Mock<ILogger<ProtocolProviderGateway>>();
        var gateway = BudgetGateway(clock, observedCache.Object, null, log.Object, fast.Object, slow.Object);
        using var requestCancellation = new CancellationTokenSource();
        var context = WithSearchCancellation(Context(), requestCancellation.Token);
        var service = new TrackMatchPlayableSearch(gateway, new TrackMatchDecisionEngine(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TrackMatchPlayableSearch>.Instance);

        var search = service.MatchAsync(context,
            new ExternalTrackMatchSnapshot("source", "spotify", "source-track", "Track", "Artist",
                null, null, 240_000, null, null, null),
            new TrackMatchScope(context.Principal!.UserId, "main", Guid.CreateVersion7(), 2, 1,
                new HashSet<string>(StringComparer.Ordinal)), [], null, requestCancellation.Token);
        await clock.WaitForTimersAsync(TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(9));
        await writes.Reader.ReadAsync(guard.Token);
        Assert.False(search.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(9));
        var result = await search.WaitAsync(guard.Token);

        Assert.Equal(TrackMatchReviewState.Accepted, result.Decision.State);
        Assert.Equal("deezer", result.SelectedExternal!.ExternalProvider);
        Assert.DoesNotContain(result.Decision.Candidates, candidate => candidate.ProviderTrackIds?.ContainsKey("qobuz") == true);
        log.Verify(item => item.Log(LogLevel.Information, It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((state, _) => state.ToString() == "Playable search budget expired for providers qobuz"),
            null, It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);

        requestCancellation.Cancel();
        clock.Advance(TimeSpan.FromSeconds(3));
        await writes.Reader.ReadAsync(guard.Token);
        var repeated = await BudgetGateway(clock, cache, fast.Object, slow.Object)
            .SearchPlayableSongsAsync(WithSearchCancellation(context, CancellationToken.None), "Track Artist", 15);
        Assert.Equal(new[] { "deezer", "qobuz" }, repeated.Select(song => song.ExternalProvider));
        VerifySearchCount(fast, "songs", 1);
        VerifySearchCount(slow, "songs", 1);
    }

    [Fact]
    public async Task PlayableSearch_ExcludesProvidersCompletingAfterBudgetDuringFanOut()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var fast = DelayedSearchProvider("deezer", clock, TimeSpan.Zero);
        var slow = DelayedSearchProvider("qobuz", clock, TimeSpan.Zero);
        slow.Setup(item => item.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .Returns(() =>
            {
                clock.Advance(TimeSpan.FromSeconds(10));
                return Task.FromResult(ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Success(new("qobuz",
                    [new(new("qobuz", ProviderResourceKind.Track, "track"), "Track", [new("Artist")])])));
            });
        var gateway = BudgetGateway(clock, new TestMemoryApplicationCache(), fast.Object, slow.Object);

        var result = await gateway.SearchPlayableSongsAsync(Context(), "Track Artist", 15);

        Assert.Equal(new[] { "deezer" }, result.Select(song => song.ExternalProvider));
    }

    [Fact]
    public async Task MetadataSearch_CachePreservesTrackIdentityAndPreciseDuration()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var cache = new TestMemoryApplicationCache();
        var provider = DelayedSearchProvider("deezer", clock, TimeSpan.Zero);
        var trackId = new ProviderExternalResourceId("deezer", ProviderResourceKind.Track, "track", "regional");
        provider.Setup(item => item.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Success(new("deezer",
                [new(trackId, "Track", [new("Artist")], duration: TimeSpan.FromMilliseconds(3210))])));
        var registry = MetadataRegistry(provider.Object);
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        router.Setup(item => item.PlanAsync<IProviderMetadataCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) => MetadataPlan(request, registry, provider.Object));
        router.Setup(item => item.PlanAsync<IProviderStreamingCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) => Plan(request, registry,
                registry.GetRequiredCapability<IProviderStreamingCapability>("deezer", ProviderCapabilityKind.Streaming)));
        var identities = new Mock<ITrackIdentityService>(MockBehavior.Strict);
        identities.Setup(item => item.ResolveManyAsync(It.IsAny<IReadOnlyList<TrackIdentityLookup>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TrackIdentityResolution?[] { null });
        var gateway = new ProtocolProviderGateway(router.Object, registry, new HttpClientFactory(), applicationCache: cache,
            identities: identities.Object, timeProvider: clock);
        var context = Context();

        var first = Assert.Single((await gateway.SearchAsync(context, "Track", 10, 0, 0)).Songs);
        first.Id = "changed";
        first.Title = "Changed";
        var repeated = Assert.Single((await gateway.SearchAsync(context, "Track", 10, 0, 0)).Songs);

        Assert.Equal("ext-deezer-song-track", repeated.Id);
        Assert.Equal("Track", repeated.Title);
        Assert.Equal(TimeSpan.FromMilliseconds(3210), repeated.PreciseDuration);
        identities.Verify(item => item.ResolveManyAsync(It.Is<IReadOnlyList<TrackIdentityLookup>>(lookups =>
            lookups.Count == 1 && lookups[0].ExternalId == trackId), It.IsAny<CancellationToken>()), Times.Exactly(2));
        VerifySearchCount(provider, "songs", 1);
    }

    [Fact]
    public async Task MetadataSearch_ExcludesSourcesCompletingAfterTheBudgetDuringFanOut()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var fast = DelayedSearchProvider("deezer", clock, TimeSpan.Zero);
        var slow = DelayedSearchProvider("qobuz", clock, TimeSpan.Zero);
        slow.Setup(item => item.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .Returns(() =>
            {
                clock.Advance(TimeSpan.FromSeconds(4));
                return Task.FromResult(ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Success(new("qobuz",
                    [new(new("qobuz", ProviderResourceKind.Track, "track"), "Track", [new("Artist")])])));
            });
        var gateway = BudgetGateway(clock, new TestMemoryApplicationCache(), fast.Object, slow.Object);

        var result = await gateway.SearchAsync(Context(), "Track", 10, 0, 0);

        Assert.Equal(["deezer"], SearchProviders(result));
    }

    [Fact]
    public async Task MetadataSearch_UnsupportedCategoriesDoNotCoolDownHealthyProvider()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var provider = DelayedSearchProvider("deezer", clock, TimeSpan.Zero);
        provider.Setup(item => item.SearchAlbumsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderAlbumMetadata>>.Failure(new(ProviderErrorKind.NotSupported)));
        var gateway = BudgetGateway(clock, new TestMemoryApplicationCache(), provider.Object);
        var context = Context();
        for (var index = 0; index < 4; index++)
            Assert.Single((await gateway.SearchAsync(context, "Track " + index, 10, 10, 0)).Songs);
        VerifySearchCount(provider, "songs", 4);
    }

    [Fact]
    public async Task MetadataSearch_ConfiguredBudgetAndRepeatedRequestsSharePendingWork()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var provider = DelayedSearchProvider("deezer", clock, TimeSpan.FromSeconds(8));
        var gateway = BudgetGateway(clock, new TestMemoryApplicationCache(),
            new CacheSettings { SearchBudgetMilliseconds = 500 }, provider.Object);
        var context = Context();
        for (var index = 0; index < 2; index++)
        {
            var search = gateway.SearchAsync(context, "Track", 10, 0, 0);
            await clock.WaitForTimersAsync(TimeSpan.FromMilliseconds(500));
            clock.Advance(TimeSpan.FromMilliseconds(500));
            Assert.Empty((await search).Songs);
        }
        VerifySearchCount(provider, "songs", 1);
        clock.Advance(TimeSpan.FromSeconds(7));
        Assert.Single((await gateway.SearchAsync(context, "Track", 10, 0, 0)).Songs);
        VerifySearchCount(provider, "songs", 1);
    }

    [Fact]
    public async Task MetadataSearch_RepeatedFailuresCoolDownOnlySearchAndRecover()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var provider = DelayedSearchProvider("deezer", clock, TimeSpan.Zero);
        provider.Setup(item => item.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .ThrowsAsync(new HttpRequestException("unavailable"));
        var gateway = BudgetGateway(clock, new TestMemoryApplicationCache(), provider.Object);
        var context = Context();

        for (var index = 0; index < 4; index++)
            Assert.Empty((await gateway.SearchAsync(context, "Track", 10, 0, 0)).Songs);
        VerifySearchCount(provider, "songs", 3);

        clock.Advance(TimeSpan.FromMinutes(5));
        provider.Setup(item => item.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Success(new("deezer",
                [new(new("deezer", ProviderResourceKind.Track, "track"), "Track", [new("Artist")])])));
        Assert.Single((await gateway.SearchAsync(context, "Track", 10, 0, 0)).Songs);
        VerifySearchCount(provider, "songs", 4);
    }

    [Fact]
    public async Task MetadataSearch_BackgroundTimeoutsCoolDownEvenWhenProviderIgnoresCancellation()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var log = new SearchCooldownLog();
        var cache = new TestMemoryApplicationCache();
        var provider = DelayedSearchProvider("deezer", clock, TimeSpan.Zero);
        var pending = new TaskCompletionSource<ProviderOutcome<ProviderPage<ProviderTrackMetadata>>>();
        provider.Setup(item => item.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .Returns(pending.Task);
        var gateway = BudgetGateway(clock, cache, null, log, provider.Object);
        var context = Context();
        for (var index = 0; index < 3; index++)
        {
            var search = gateway.SearchAsync(context, "Track " + index, 10, 0, 0);
            await clock.WaitForTimersAsync(TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(3500));
            clock.Advance(TimeSpan.FromSeconds(30));
            Assert.Empty((await search).Songs);
        }
        await log.Cooldown.Task.WaitAsync(guard.Token);
        Assert.Empty((await gateway.SearchAsync(context, "another", 10, 0, 0).WaitAsync(guard.Token)).Songs);
        VerifySearchCount(provider, "songs", 3);
        pending.SetResult(ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Success(new("deezer", [])));
        Assert.Empty(cache.GetKeysByPattern("search:v3:*"));
    }

    private static ProtocolExecutionContext WithSearchCancellation(ProtocolExecutionContext context, CancellationToken token) =>
        new(context.Protocol, context.BackendInstanceId, context.VerifiedBackendPrincipalId, context.Principal,
            context.CorrelationId, context.Deadline, token, context.Client);

    private static string[] SearchProviders(SearchResult result) => result.Songs.Select(item => item.ExternalProvider!)
        .Concat(result.Albums.Select(item => item.ExternalProvider!))
        .Concat(result.Artists.Select(item => item.ExternalProvider!)).ToArray();

    private static Mock<IProviderMetadataCapability> DelayedSearchProvider(string id, TimeProvider clock, TimeSpan delay)
    {
        var provider = new Mock<IProviderMetadataCapability>(MockBehavior.Strict);
        provider.SetupGet(item => item.ProviderId).Returns(id);
        provider.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Metadata);
        async Task<ProviderOutcome<ProviderPage<T>>> Page<T>(ProviderExecutionContext context, T value)
        {
            await Task.Delay(delay, clock, context.CancellationToken);
            return ProviderOutcome<ProviderPage<T>>.Success(new(id, [value]));
        }
        provider.Setup(item => item.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .Returns((ProviderExecutionContext context, ProviderMetadataSearchRequest _) => Page(context,
                new ProviderTrackMetadata(new(id, ProviderResourceKind.Track, "track"), "Track", [new("Artist")])));
        provider.Setup(item => item.SearchAlbumsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .Returns((ProviderExecutionContext context, ProviderMetadataSearchRequest _) => Page(context,
                new ProviderAlbumMetadata(new(id, ProviderResourceKind.Album, "album"), "Track", [new("Artist")])));
        provider.Setup(item => item.SearchArtistsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .Returns((ProviderExecutionContext context, ProviderMetadataSearchRequest _) => Page(context,
                new ProviderArtistMetadata(new(id, ProviderResourceKind.Artist, "artist"), "Track")));
        return provider;
    }

    private static ProtocolProviderGateway BudgetGateway(ManualTimeProvider clock, IApplicationCache cache,
        params IProviderMetadataCapability[] capabilities) => BudgetGateway(clock, cache, null, capabilities);

    private static ProtocolProviderGateway BudgetGateway(ManualTimeProvider clock, IApplicationCache cache,
        CacheSettings? settings, params IProviderMetadataCapability[] capabilities) =>
        BudgetGateway(clock, cache, settings, null, capabilities);

    private static ProtocolProviderGateway BudgetGateway(ManualTimeProvider clock, IApplicationCache cache,
        CacheSettings? settings, ILogger<ProtocolProviderGateway>? log, params IProviderMetadataCapability[] capabilities)
    {
        var registry = MetadataRegistry(capabilities);
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        router.Setup(item => item.PlanAsync<IProviderMetadataCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) => MetadataPlan(request, registry, capabilities));
        router.Setup(item => item.PlanAsync<IProviderStreamingCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) => Plan(request, registry,
                capabilities.Select(item => registry.GetRequiredCapability<IProviderStreamingCapability>(item.ProviderId,
                    ProviderCapabilityKind.Streaming)).ToArray()));
        return new(router.Object, registry, new HttpClientFactory(), applicationCache: cache, timeProvider: clock, logger: log,
            searchSettings: settings == null ? null : Options.Create(settings));
    }

    private sealed class SearchCooldownLog : ILogger<ProtocolProviderGateway>
    {
        public TaskCompletionSource Cooldown { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning && formatter(state, exception).Contains("background timeout", StringComparison.Ordinal))
                Cooldown.TrySetResult();
        }
    }

    private static void VerifySearchCount(Mock<IProviderMetadataCapability> provider, string kind, int count)
    {
        if (kind == "songs") provider.Verify(item => item.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()), Times.Exactly(count));
        if (kind == "albums") provider.Verify(item => item.SearchAlbumsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()), Times.Exactly(count));
        if (kind == "artists") provider.Verify(item => item.SearchArtistsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()), Times.Exactly(count));
    }
}
