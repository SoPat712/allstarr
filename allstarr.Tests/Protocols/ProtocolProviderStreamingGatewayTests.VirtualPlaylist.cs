using System.Diagnostics;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Matching;
using allstarr.Core.Playlists;
using allstarr.Core.Protocols;
using allstarr.Core.Protocols.Jellyfin;
using allstarr.Core.Routing;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using allstarr.Services.Common;
using allstarr.Services.Jellyfin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolProviderStreamingGatewayTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VirtualPlaylistSongDetailsPreserveKnownSourceResponseFromFullCache(bool accountScoped)
    {
        var context = Context();
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var metadata = SearchRankingCapability("deezer");
        Mock.Get(metadata).Setup(item => item.GetTrackAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderTrackLookupRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderTrackMetadata>.Success(new(new("deezer", ProviderResourceKind.Track, "42"),
                "Cached title", [new("Cached artist", new("deezer", ProviderResourceKind.Artist, "artist"))],
                albumTitle: "Cached album", duration: TimeSpan.FromMilliseconds(241160),
                artwork: new(publicUri: new Uri("https://images.example.test/song.jpg")),
                isrc: "USAAA2600001")));
        var registry = MetadataRegistry(metadata);
        var account = accountScoped ? new ProviderAccountContext(Guid.NewGuid(), "deezer", ProviderAccountScope.Personal,
            1, ownerUserId: context.RequireActor().EffectiveUserId) : null;
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        router.Setup(item => item.PlanAsync<IProviderMetadataCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) =>
            {
                var plan = MetadataPlan(request, registry, metadata);
                return plan with
                {
                    Candidates = plan.Candidates.Select(candidate => candidate with
                    {
                        Context = new ProviderExecutionContext(request.Actor, "deezer", account, request.Policy,
                            request.OperationId, request.CorrelationId, request.Deadline, request.CancellationToken),
                        TrackId = new("deezer", ProviderResourceKind.Track, "42")
                    }).ToArray()
                };
            });
        var recordingId = Guid.NewGuid();
        var publicId = "ext-allstarr-song-" + recordingId.ToString("D");
        var identities = new Mock<ITrackIdentityService>(MockBehavior.Strict);
        identities.Setup(item => item.GetStoredSongsAsync(context.RequireActor(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => (IReadOnlyList<Song>)[new Song
            {
                Id = publicId,
                CanonicalRecordingId = recordingId,
                Title = "Stored title",
                Artist = "Stored artist",
                Album = "Stored album",
                ExternalProvider = "deezer",
                ExternalId = "42"
            }]);
        identities.Setup(item => item.ResolveRecordingAsync(context.RequireActor(), "ext-deezer-song-42", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CanonicalRecordingIdentity(recordingId, context.RequireActor().EffectiveUserId!.Value, null, null, 1)
            { PublicId = publicId });
        var legacy = new ProtocolProviderGateway(router.Object, registry, new HttpClientFactory(), identities: identities.Object);
        var expected = Assert.IsType<Song>(await legacy.GetSongAsync(context, "deezer", "42"));
        var expectedJson = JsonSerializer.Serialize(expected);
        using var cache = new MemoryApplicationCache(new ExternalMetadataClock(clock));
        var gateway = new ProtocolProviderGateway(router.Object, registry, new HttpClientFactory(),
            applicationCache: cache, identities: identities.Object, timeProvider: clock);
        Assert.NotNull(await gateway.GetExactSongAsync(context, "deezer", "42"));
        clock.Advance(TimeSpan.FromHours(6));

        var songs = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => gateway.GetSongAsync(context, "deezer", "42")));

        Assert.All(songs, song =>
        {
            Assert.NotNull(song);
            Assert.Equal(expectedJson, JsonSerializer.Serialize(song));
            Assert.Equal(publicId, song.Id);
            Assert.Equal(recordingId, song.CanonicalRecordingId);
            Assert.Equal("Cached title", song.Title);
            Assert.Equal("Cached album", song.Album);
            Assert.Null(song.AlbumId);
            Assert.Equal(TimeSpan.FromMilliseconds(241160), song.PreciseDuration);
        });
        songs[0]!.Title = "Changed by caller";
        var repeated = await gateway.GetSongAsync(context, "deezer", "42");
        Assert.Equal(expectedJson, JsonSerializer.Serialize(repeated));
        Assert.Equal(TimeSpan.FromMilliseconds(241160), repeated!.PreciseDuration);
        Mock.Get(metadata).Verify(item => item.GetTrackAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderTrackLookupRequest>()), Times.Exactly(2));
    }

    [Fact]
    public async Task VirtualPlaylistSongSummaryStillChecksKnownRecordingMetadata()
    {
        var context = Context();
        var identities = new Mock<ITrackIdentityService>(MockBehavior.Strict);
        identities.Setup(item => item.GetStoredSongsAsync(context.RequireActor(), "ext-deezer-song-42", It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<Song>)[]);
        identities.Setup(item => item.ResolveRecordingAsync(context.RequireActor(), "ext-deezer-song-42", It.IsAny<CancellationToken>()))
            .ReturnsAsync((CanonicalRecordingIdentity?)null);
        using var cache = new MemoryApplicationCache(new allstarr.Core.Operations.SystemPlatformClock());
        var summary = new Song
        {
            Id = "ext-deezer-song-42",
            Title = "Summary title",
            Artist = "Summary artist",
            Album = "Summary album",
            ExternalProvider = "deezer",
            ExternalId = "42"
        };
        await cache.SetStringAsync(ProtocolProviderGateway.MetadataCacheKey(context.RequireActor(), "deezer",
            ProviderResourceKind.Track, "42"), JsonSerializer.Serialize(summary), TimeSpan.FromHours(24));
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        var gateway = new ProtocolProviderGateway(router.Object, MetadataRegistry(), new HttpClientFactory(),
            applicationCache: cache, identities: identities.Object);

        var song = await gateway.GetSongAsync(context, "deezer", "42");

        Assert.Equal(JsonSerializer.Serialize(summary), JsonSerializer.Serialize(song));
        identities.Verify(item => item.ResolveRecordingAsync(context.RequireActor(), "ext-deezer-song-42", It.IsAny<CancellationToken>()), Times.Once);
        router.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task VirtualPlaylistThirtyThreeUniqueSongsReuseFinalDetailsPastOneMinuteWithoutProviderReads()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new VirtualPlaylistCacheFactory(database.Options);
        var user = FeedReadsTests.User("principal");
        user.BackendInstanceId = "backend";
        await using (var seed = factory.CreateDbContext())
        {
            seed.Users.Add(user);
            await seed.SaveChangesAsync();
        }
        var context = FeedReadsTests.Context(user);
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var platformClock = new ExternalMetadataClock(clock);
        using var cache = new MemoryApplicationCache(platformClock);
        var songs = Enumerable.Range(0, 33).Select(index => new Song
        {
            CanonicalRecordingId = Guid.NewGuid(),
            Title = "Cached song " + index,
            Artist = "Cached artist",
            Artists = ["Cached artist"],
            ArtistId = "ext-deezer-artist-artist",
            ArtistIds = ["ext-deezer-artist-artist"],
            Album = "Cached album " + index,
            Duration = 241,
            PreciseDuration = TimeSpan.FromMilliseconds(241160 + index),
            Genres = ["Rock", "Pop"],
            Genre = "Rock",
            CoverArtUrl = "https://images.example.test/" + index + ".jpg",
            ExternalProvider = "deezer",
            ExternalId = index.ToString()
        }).ToArray();
        foreach (var song in songs)
        {
            song.Id = "ext-allstarr-song-" + song.CanonicalRecordingId!.Value.ToString("D");
            var payload = JsonSerializer.SerializeToNode(song)!.AsObject();
            payload[nameof(Song.PreciseDuration)] = song.PreciseDuration!.Value.ToString("c");
            var key = ProtocolProviderGateway.MetadataCacheKey(context.RequireActor(), "deezer", ProviderResourceKind.Track,
                song.ExternalId!, true);
            await cache.SetStringAsync(key, payload.ToJsonString(), TimeSpan.FromDays(7));
            await cache.SetStringAsync(key + ":fetched", clock.GetUtcNow().AddHours(-6).ToString("O"), TimeSpan.FromDays(7));
        }
        Song Source(string itemId) => songs.Single(song => song.Id == itemId ||
            itemId == "ext-deezer-song-" + song.ExternalId);
        var identities = new Mock<ITrackIdentityService>(MockBehavior.Strict);
        identities.Setup(item => item.GetStoredSongsAsync(context.RequireActor(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProviderActorContext _, string itemId, CancellationToken _) =>
            {
                var source = Source(itemId);
                return (IReadOnlyList<Song>)[new Song
                {
                    Id = source.Id,
                    CanonicalRecordingId = source.CanonicalRecordingId,
                    Title = source.Title,
                    Artist = source.Artist,
                    Album = source.Album,
                    ExternalProvider = source.ExternalProvider,
                    ExternalId = source.ExternalId
                }];
            });
        identities.Setup(item => item.ResolveRecordingAsync(context.RequireActor(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProviderActorContext _, string itemId, CancellationToken _) =>
            {
                var source = Source(itemId);
                return new CanonicalRecordingIdentity(source.CanonicalRecordingId!.Value, user.Id, null, null, 1)
                { PublicId = source.Id };
            });
        var providerReads = 0;
        var metadata = SearchRankingCapability("deezer");
        Mock.Get(metadata).Setup(item => item.GetTrackAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderTrackLookupRequest>()))
            .Returns(async (ProviderExecutionContext _, ProviderTrackLookupRequest request) =>
            {
                Interlocked.Increment(ref providerReads);
                await Task.Delay(800);
                return ProviderOutcome<ProviderTrackMetadata>.Success(new(request.Id, "Provider song", [new("Provider artist")],
                    albumTitle: "Provider album"));
            });
        var registry = MetadataRegistry(metadata);
        var metadataPlans = 0;
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        router.Setup(item => item.PlanAsync<IProviderMetadataCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) =>
            {
                Interlocked.Increment(ref metadataPlans);
                var source = songs.Single(song => song.CanonicalRecordingId!.Value.ToString("D") == request.SourceTrackId!.Value);
                var plan = MetadataPlan(request, registry, metadata);
                return plan with
                {
                    Candidates = plan.Candidates.Select(candidate => candidate with
                    { TrackId = new("deezer", ProviderResourceKind.Track, source.ExternalId!) }).ToArray()
                };
            });
        var gateway = new ProtocolProviderGateway(router.Object, registry, new HttpClientFactory(),
            applicationCache: cache, identities: identities.Object, timeProvider: clock);
        var linkId = Guid.NewGuid();
        var protocolId = PlaylistVirtualizationService.CreateProtocolId(linkId);
        var model = new VirtualPlaylistReadModel(protocolId, linkId, Guid.NewGuid(), "Cached playlist", null, null,
            "spotify", "source", "revision", PlaylistLinkMode.Virtual,
            songs.Select((song, index) => new VirtualPlaylistTrack(index, "ext-deezer-song-" + song.ExternalId,
                song.Title, song.Artist, song.Album, null, 241160 + index, null, TrackMatchState.Accepted,
                RouteKind: TrackRouteKind.External, RouteProviderId: "deezer", RouteExternalId: song.ExternalId)).ToArray());
        var service = new Mock<IPlaylistVirtualizationService>(MockBehavior.Strict);
        service.Setup(item => item.ReadAsync(It.IsAny<ProtocolExecutionContext>(), protocolId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(model);
        var mutations = new Mock<IJellyfinPlaylistMutationResolver>(MockBehavior.Strict);
        var builder = new JellyfinResponseBuilder();
        var original = new JellyfinVirtualPlaylistProtocolAdapter(service.Object, mutations.Object,
            responseBuilder: builder, providerGateway: gateway);
        var adapter = new JellyfinVirtualPlaylistProtocolAdapter(service.Object, mutations.Object,
            responseBuilder: builder, providerGateway: gateway, readContexts: factory, readCache: cache, readClock: platformClock);
        var query = new QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues> { ["Limit"] = "33" });
        async Task<string> ReadAsync(JellyfinVirtualPlaylistProtocolAdapter target) =>
            JsonSerializer.Serialize(Assert.IsType<JsonResult>(await target.ReadItemsAsync(context, protocolId, null, query, default)).Value);

        var watch = Stopwatch.StartNew();
        var expected = await ReadAsync(original);
        var legacyMilliseconds = watch.Elapsed.TotalMilliseconds;
        Assert.Equal(33, metadataPlans);
        watch.Restart();
        Assert.Equal(expected, await ReadAsync(adapter));
        var firstMilliseconds = watch.Elapsed.TotalMilliseconds;
        Assert.Equal(66, metadataPlans);
        watch.Restart();
        Assert.Equal(expected, await ReadAsync(adapter));
        var repeatMilliseconds = watch.Elapsed.TotalMilliseconds;
        Assert.Equal(66, metadataPlans);
        clock.Advance(TimeSpan.FromSeconds(61));
        watch.Restart();
        Assert.Equal(expected, await ReadAsync(adapter));
        var expiredWrapperMilliseconds = watch.Elapsed.TotalMilliseconds;
        Assert.Equal(66, metadataPlans);
        Assert.Equal(0, providerReads);
        using var response = JsonDocument.Parse(expected);
        var items = response.RootElement.GetProperty("Items").EnumerateArray().ToArray();
        Assert.Equal(33, items.Length);
        Assert.Equal(songs.Select(song => song.Id), items.Select(item => item.GetProperty("Id").GetString()));
        Assert.Equal(songs.Select(song => song.Duration!.Value * TimeSpan.TicksPerSecond),
            items.Select(item => item.GetProperty("RunTimeTicks").GetInt64()));
        Mock.Get(metadata).Verify(item => item.GetTrackAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderTrackLookupRequest>()), Times.Never);
        searchOutput.WriteLine($"Served playlist 33 unique songs, real gateway, complete six-hour-old details with album titles and no album IDs, 800 ms fake provider: original selection {legacyMilliseconds:F1} ms; first page {firstMilliseconds:F1} ms; repeat {repeatMilliseconds:F1} ms; after 61 seconds {expiredWrapperMilliseconds:F1} ms; provider calls 0 throughout; metadata plans 33 + 33 then 0 + 0; exact response JSON and source order preserved.");
    }

    private sealed class VirtualPlaylistCacheFactory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
    }
}
