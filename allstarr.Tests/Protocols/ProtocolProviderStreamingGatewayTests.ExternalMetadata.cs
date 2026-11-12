using allstarr.Core.Caching;
using System.Diagnostics;
using allstarr.Core.Playback;
using allstarr.Core.Matching;
using allstarr.Models.Domain;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Routing;
using allstarr.Core.Storage;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolProviderStreamingGatewayTests
{
    [Fact]
    public async Task ExternalAlbumReads_ShareFetchAndKeepTrackOrderAndIndependentCopies()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var capability = SearchRankingCapability("deezer");
        var calls = 0;
        Mock.Get(capability).Setup(value => value.GetAlbumAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderAlbumLookupRequest>()))
            .Returns(async () =>
            {
                Interlocked.Increment(ref calls);
                started.TrySetResult();
                await release.Task;
                return ProviderOutcome<ProviderAlbumMetadata>.Success(new(new("deezer", ProviderResourceKind.Album, "7"),
                    "Album", [new("Artist")], tracks:
                    [new(new("deezer", ProviderResourceKind.Track, "2"), "Second", [new("Artist")], duration: TimeSpan.FromMilliseconds(241160)),
                     new(new("deezer", ProviderResourceKind.Track, "1"), "First", [new("Artist")])]));
            });
        var registry = MetadataRegistry(capability);
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        router.Setup(value => value.PlanAsync<IProviderMetadataCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) => MetadataPlan(request, registry, capability));
        using var cache = new MemoryApplicationCache(new SystemPlatformClock());
        var gateway = new ProtocolProviderGateway(router.Object, registry, new HttpClientFactory(), applicationCache: cache);
        var context = Context();
        var reads = Enumerable.Range(0, 12).Select(_ => gateway.GetAlbumAsync(context, "deezer", "7")).ToArray();
        await started.Task;
        Assert.Equal(1, Volatile.Read(ref calls));
        release.SetResult();
        var albums = await Task.WhenAll(reads);
        var expected = JsonSerializer.Serialize(albums[0]);
        Assert.All(albums, album => Assert.Equal(expected, JsonSerializer.Serialize(album)));
        albums[0]!.Songs.Clear();
        using var timing = RequestTiming.Begin();
        var repeat = await gateway.GetAlbumAsync(context, "deezer", "7");
        Assert.All(new[] { "resolve", "fetch", "match" }, stage => Assert.Contains(stage, timing.Snapshot("jellyfin").Stages.Keys));
        Assert.Equal(expected, JsonSerializer.Serialize(repeat));
        Assert.Equal(["2", "1"], repeat!.Songs.Select(song => song.ExternalId));
        Assert.Equal(TimeSpan.FromMilliseconds(241160), repeat.Songs[0].PreciseDuration);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ExternalAlbumReads_MeasureProviderFloorAndWarmReuse()
    {
        var capability = SearchRankingCapability("deezer");
        var calls = 0;
        Mock.Get(capability).Setup(value => value.GetAlbumAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderAlbumLookupRequest>()))
            .Returns(async () =>
            {
                Interlocked.Increment(ref calls);
                await Task.Delay(78);
                return ProviderOutcome<ProviderAlbumMetadata>.Success(new(new("deezer", ProviderResourceKind.Album, "7"), "Album", [new("Artist")]));
            });
        var registry = MetadataRegistry(capability);
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        router.Setup(value => value.PlanAsync<IProviderMetadataCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) => MetadataPlan(request, registry, capability));
        var simulated = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var cache = new MemoryApplicationCache(new ExternalMetadataClock(simulated));
        using var previous = new MemoryApplicationCache(new ExternalMetadataClock(simulated));
        var gateway = new ProtocolProviderGateway(router.Object, registry, new HttpClientFactory(), applicationCache: cache, timeProvider: simulated);
        var context = Context();
        var watch = Stopwatch.StartNew();
        await PreviousReadAsync();
        simulated.Advance(TimeSpan.FromHours(6));
        watch.Restart();
        await PreviousReadAsync();
        var before = watch.Elapsed.TotalMilliseconds;
        Assert.Equal(2, calls);
        var beforeCalls = calls;
        watch.Restart();
        await gateway.GetAlbumAsync(context, "deezer", "7");
        var cold = watch.Elapsed.TotalMilliseconds;
        simulated.Advance(TimeSpan.FromHours(6));
        watch.Restart();
        await gateway.GetAlbumAsync(context, "deezer", "7");
        Console.WriteLine($"External album: provider=78 ms previous-six-hour-repeat={before:F2} ms cold={cold:F2} ms six-hour-repeat={watch.Elapsed.TotalMilliseconds:F2} ms provider-calls-before={beforeCalls} after={calls - beforeCalls}");
        Assert.Equal(1, calls - beforeCalls);

        async Task PreviousReadAsync()
        {
            if (await previous.GetStringAsync(CacheKeyBuilder.BuildProtocolMetadataKey("deezer", "previous", "Album", "7")) != null) return;
            await gateway.GetAlbumAsync(context, "deezer", "baseline");
            await cache.PurgeAllAsync();
            await previous.SetStringAsync(CacheKeyBuilder.BuildProtocolMetadataKey("deezer", "previous", "Album", "7"), "cached", TimeSpan.FromMinutes(5));
        }
    }

    [Fact]
    public async Task ExternalAlbumReads_StaleTracklistReturnsWhileOneRefreshRuns()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var capability = SearchRankingCapability("deezer");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        Mock.Get(capability).Setup(value => value.GetAlbumAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderAlbumLookupRequest>()))
            .Returns(async () =>
            {
                if (Interlocked.Increment(ref calls) > 1) { entered.TrySetResult(); await release.Task; }
                return ProviderOutcome<ProviderAlbumMetadata>.Success(new(new("deezer", ProviderResourceKind.Album, "7"), "Album", [new("Artist")]));
            });
        var registry = MetadataRegistry(capability);
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        router.Setup(value => value.PlanAsync<IProviderMetadataCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) => MetadataPlan(request, registry, capability));
        using var cache = new MemoryApplicationCache(new SystemPlatformClock());
        var gateway = new ProtocolProviderGateway(router.Object, registry, new HttpClientFactory(), applicationCache: cache, timeProvider: clock);
        var context = Context();
        await gateway.GetAlbumAsync(context, "deezer", "7");
        clock.Advance(TimeSpan.FromHours(24));
        var reads = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => gateway.GetAlbumAsync(context, "deezer", "7")));
        Assert.All(reads, album => Assert.Equal("Album", album!.Title));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, calls);
        release.SetResult();
    }

    [Fact]
    public async Task ExternalAlbumPrefetch_SkipsCachedDeduplicatesAndBoundsQueueAndProviderConcurrency()
    {
        var capability = SearchRankingCapability("deezer");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var twoEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maximum = 0;
        var calls = new System.Collections.Concurrent.ConcurrentBag<string>();
        Mock.Get(capability).Setup(value => value.GetAlbumAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderAlbumLookupRequest>()))
            .Returns(async (ProviderExecutionContext context, ProviderAlbumLookupRequest request) =>
            {
                calls.Add(request.Id.Value);
                var count = Interlocked.Increment(ref active);
                lock (calls) maximum = Math.Max(maximum, count);
                if (count == 2) twoEntered.TrySetResult();
                if (request.Id.Value != "cached") await release.Task;
                Interlocked.Decrement(ref active);
                return ProviderOutcome<ProviderAlbumMetadata>.Success(new(request.Id, "Album", [new("Artist")]));
            });
        var registry = MetadataRegistry(capability);
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        router.Setup(value => value.PlanAsync<IProviderMetadataCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) => MetadataPlan(request, registry, capability));
        using var cache = new MemoryApplicationCache(new SystemPlatformClock());
        var gateway = new ProtocolProviderGateway(router.Object, registry, new HttpClientFactory(), applicationCache: cache);
        var context = Context();
        await gateway.GetAlbumAsync(context, "deezer", "cached");
        var ids = new[] { "ext-deezer-album-cached" }.Concat(Enumerable.Range(0, 20).Select(id => $"ext-deezer-album-{id}")).ToArray();
        await gateway.QueueAlbumPrefetchAsync(context, ids);
        await twoEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(10, gateway.PendingAlbumPrefetches);
        await gateway.QueueAlbumPrefetchAsync(context, ids);
        Assert.Equal(10, gateway.PendingAlbumPrefetches);
        for (var batch = 1; batch < 9; batch++)
            await gateway.QueueAlbumPrefetchAsync(context, Enumerable.Range(batch * 20, 20).Select(id => $"ext-deezer-album-{id}"));
        Assert.Equal(64, gateway.PendingAlbumPrefetches);
        Assert.Equal(2, active);
        Assert.Equal(2, maximum);
        Assert.Equal(1, calls.Count(id => id == "cached"));
        release.SetResult();
        await WaitForAlbumPrefetchAsync(gateway);
    }

    [Fact]
    public async Task ExternalAlbumPrefetch_QueuedWorkYieldsToForegroundReads()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prefetched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var capability = SearchRankingCapability("deezer");
        Mock.Get(capability).Setup(value => value.GetAlbumAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderAlbumLookupRequest>()))
            .Returns(async (ProviderExecutionContext _, ProviderAlbumLookupRequest request) =>
            {
                if (request.Id.Value == "foreground") { entered.TrySetResult(); await release.Task; }
                else prefetched.TrySetResult();
                return ProviderOutcome<ProviderAlbumMetadata>.Success(new(request.Id, "Album", [new("Artist")], tracks: request.Id.Value == "foreground" ? [] :
                    [new(new("deezer", ProviderResourceKind.Track, "track"), "Track", [new("Artist")])]));
            });
        var registry = MetadataRegistry(capability);
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        router.Setup(value => value.PlanAsync<IProviderMetadataCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) => MetadataPlan(request, registry, capability));
        using var cache = new MemoryApplicationCache(new SystemPlatformClock());
        var identities = new Mock<ITrackIdentityService>(MockBehavior.Strict);
        var gateway = new ProtocolProviderGateway(router.Object, registry, new HttpClientFactory(), applicationCache: cache, timeProvider: clock, identities: identities.Object);
        var context = Context();
        var foreground = gateway.GetAlbumAsync(context, "deezer", "foreground");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await gateway.QueueAlbumPrefetchAsync(context, ["ext-deezer-album-one", "ext-deezer-album-two"]);
        await clock.WaitForTimersAsync(TimeSpan.FromMilliseconds(25), TimeSpan.FromMilliseconds(25));
        Assert.False(prefetched.Task.IsCompleted);
        release.SetResult();
        await foreground;
        clock.Advance(TimeSpan.FromMilliseconds(25));
        await prefetched.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForAlbumPrefetchAsync(gateway);
        identities.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExternalAlbumMetadata_SongAndArtistReadsReuseFullMetadataAndIsolateListeners(bool artist)
    {
        var capability = SearchRankingCapability("deezer");
        Mock.Get(capability).Setup(value => value.GetTrackAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderTrackLookupRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderTrackMetadata>.Success(new(new("deezer", ProviderResourceKind.Track, "1"), "Song",
                [new("Artist", new("deezer", ProviderResourceKind.Artist, "2"))], albumId: new("deezer", ProviderResourceKind.Album, "7"))));
        Mock.Get(capability).Setup(value => value.GetArtistAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderArtistLookupRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderArtistMetadata>.Success(new(new("deezer", ProviderResourceKind.Artist, "2"), "Artist")));
        var registry = MetadataRegistry(capability);
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        router.Setup(value => value.PlanAsync<IProviderMetadataCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) => MetadataPlan(request, registry, capability));
        using var cache = new MemoryApplicationCache(new SystemPlatformClock());
        var gateway = new ProtocolProviderGateway(router.Object, registry, new HttpClientFactory(), applicationCache: cache);
        var context = Context();
        await ReadAsync(context);
        await ReadAsync(context);
        VerifyCalls(1);
        await ReadAsync(Context());
        VerifyCalls(2);

        async Task ReadAsync(ProtocolExecutionContext execution)
        {
            if (artist) Assert.NotNull(await gateway.GetArtistAsync(execution, "deezer", "2"));
            else Assert.NotNull(await gateway.GetSongAsync(execution, "deezer", "1"));
        }
        void VerifyCalls(int count)
        {
            if (artist) Mock.Get(capability).Verify(value => value.GetArtistAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderArtistLookupRequest>()), Times.Exactly(count));
            else Mock.Get(capability).Verify(value => value.GetTrackAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderTrackLookupRequest>()), Times.Exactly(count));
        }
    }

    [Fact]
    public async Task ExternalAlbumReads_LoadDisplayMetadataInBoundedBatchesAndKeepTrackOrder()
    {
        var capability = SearchRankingCapability("deezer");
        Mock.Get(capability).Setup(value => value.GetAlbumAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderAlbumLookupRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderAlbumMetadata>.Success(new(new("deezer", ProviderResourceKind.Album, "7"), "Album", [new("Artist")],
                tracks: Enumerable.Range(0, 8).Select(index => new ProviderTrackMetadata(new("deezer", ProviderResourceKind.Track, index.ToString()),
                    $"Track {index}", [new("Artist")])).ToArray())));
        var registry = MetadataRegistry(capability);
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        router.Setup(value => value.PlanAsync<IProviderMetadataCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) => MetadataPlan(request, registry, capability));
        var starts = Enumerable.Range(0, 8).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var releases = Enumerable.Range(0, 8).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var identities = new Mock<ITrackIdentityService>(MockBehavior.Strict);
        identities.Setup(value => value.ResolveManyAsync(It.IsAny<IReadOnlyList<TrackIdentityLookup>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TrackIdentityLookup> lookups, CancellationToken _) => lookups.Select(lookup =>
                (TrackIdentityResolution?)new TrackIdentityResolution(Guid.NewGuid(), Guid.NewGuid(), lookup.ExternalId,
                    ProviderIdentityScope.Catalog, null, ProviderIdentityVerification.Verified, "fixture", 1)
                { PublicId = "ext-allstarr-song-" + lookup.ExternalId.Value }).ToArray());
        identities.Setup(value => value.GetStoredSongsAsync(It.IsAny<ProviderActorContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (ProviderActorContext _, string id, CancellationToken token) =>
            {
                var index = int.Parse(id.Split('-')[^1]);
                starts[index].TrySetResult();
                await releases[index].Task;
                return (IReadOnlyList<Song>)[new Song { Id = id, Title = $"Display {index}", Artist = "Artist", Album = "Album",
                    AlbumId = "ext-deezer-album-7", ExternalProvider = "deezer", ExternalId = index.ToString() }];
            });
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var context = Context();
        for (var index = 0; index < 8; index++)
        {
            var serial = identities.Object.GetStoredSongsAsync(context.RequireActor(), $"ext-deezer-song-{index}");
            await starts[index].Task;
            clock.Advance(TimeSpan.FromMilliseconds(150));
            releases[index].SetResult();
            await serial;
        }
        var before = clock.GetElapsedTime(0).TotalMilliseconds;
        starts = Enumerable.Range(0, 8).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        releases = Enumerable.Range(0, 8).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        identities.Invocations.Clear();
        var started = clock.GetTimestamp();
        var gateway = new ProtocolProviderGateway(router.Object, registry, new HttpClientFactory(), identities: identities.Object, timeProvider: clock);
        var pending = gateway.GetAlbumAsync(context, "deezer", "7");
        await Task.WhenAll(starts.Take(4).Select(value => value.Task)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(starts.Skip(4), value => Assert.False(value.Task.IsCompleted));
        clock.Advance(TimeSpan.FromMilliseconds(150));
        for (var index = 3; index >= 0; index--) releases[index].SetResult();
        await Task.WhenAll(starts.Skip(4).Select(value => value.Task)).WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromMilliseconds(150));
        for (var index = 7; index >= 4; index--) releases[index].SetResult();
        Assert.Equal(Enumerable.Range(0, 8).Select(index => $"Display {index}"), (await pending)!.Songs.Select(song => song.Title));
        var after = clock.GetElapsedTime(started).TotalMilliseconds;
        Assert.Equal(1200, before);
        Assert.Equal(300, after);
        Console.WriteLine($"External display lookup replay: before={before:F0} ms after={after:F0} ms stored-song-calls=8 identity-batches=1");
        identities.Verify(value => value.ResolveManyAsync(It.IsAny<IReadOnlyList<TrackIdentityLookup>>(), It.IsAny<CancellationToken>()), Times.Once);
        identities.Verify(value => value.GetStoredSongsAsync(It.IsAny<ProviderActorContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(8));
    }

    [Fact]
    public async Task ExternalAudioTiming_SeparatesPreparationAndProcessingWithoutChangingDelivery()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var timings = new PlaybackStartTimings(clock);
        timings.Enter(); timings.Enable();
        var source = Streaming("deezer", (_, _) =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(800));
            var response = AudioResponse();
            response.Headers.Add("X-Allstarr-Prepare-Download-Ms", "300");
            response.Headers.Add("X-Allstarr-Prepare-Process-Ms", "500");
            timings.Prepared(response, "deezer");
            return Task.FromResult(response);
        });
        var unused = Streaming("qobuz", (_, _) => Task.FromResult(AudioResponse()));
        var (gateway, _) = FailoverGateway(source, unused, clock: clock);
        using var response = (await gateway.OpenStreamAsync(DownloadStartContext(ProtocolKind.Jellyfin, "Balanced", clock),
            "allstarr", "recording", ProviderAudioQuality.Any, null))!.Response;
        var sample = timings.Snapshot("completed");
        Assert.Contains(sample.Phases, phase => phase.Phase == PlaybackStartPhase.Preparation && phase.ElapsedMs == 300);
        Assert.Contains(sample.Phases, phase => phase.Phase == PlaybackStartPhase.Process && phase.ElapsedMs == 500);
        Assert.Contains(sample.Phases, phase => phase.Phase == PlaybackStartPhase.Source && phase.ElapsedMs == 800);
        Console.WriteLine($"External audio fake stages: {JsonSerializer.Serialize(sample.Phases)}");
    }

    private static async Task WaitForAlbumPrefetchAsync(ProtocolProviderGateway gateway)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (gateway.PendingAlbumPrefetches > 0) await Task.Delay(10, timeout.Token);
    }

    private sealed class ExternalMetadataClock(TimeProvider clock) : IPlatformClock
    {
        public DateTimeOffset UtcNow => clock.GetUtcNow();
    }

}
