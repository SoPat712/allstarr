using System.Collections.Immutable;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Protocols;
using allstarr.Core.Routing;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using allstarr.Services.Common;
using allstarr.Services.Jellyfin;
using Microsoft.AspNetCore.Http;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolProviderStreamingGatewayTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData(null, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task CatalogNames_UseGenericExternalLabelsWithoutChangingSourceIdentity(bool? showLabel, bool canonical)
    {
        var capability = new Mock<IProviderMetadataCapability>(MockBehavior.Strict);
        capability.SetupGet(value => value.ProviderId).Returns("deezer");
        capability.SetupGet(value => value.Capability).Returns(ProviderCapabilityKind.Metadata);
        capability.Setup(value => value.GetTrackAsync(It.IsAny<ProviderExecutionContext>(),
                It.Is<ProviderTrackLookupRequest>(request => request.Id.Value == "42")))
            .ReturnsAsync(ProviderOutcome<ProviderTrackMetadata>.Success(new(new("deezer", ProviderResourceKind.Track, "42"),
                "Track", [new("Primary", new("deezer", ProviderResourceKind.Artist, "1")), new("Guest", new("deezer", ProviderResourceKind.Artist, "3"))],
                albumId: new("deezer", ProviderResourceKind.Album, "7"), albumTitle: "Album", isExplicit: true,
                artwork: new(publicUri: new Uri("https://images.invalid/catalog.jpg")),
                albumArtist: "Host", albumArtists: [new("Host", new("deezer", ProviderResourceKind.Artist, "2"))],
                providerIds: new Dictionary<string, string> { ["MusicBrainzTrack"] = "recording-id" })));
        var registry = MetadataRegistry(capability.Object);
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        router.Setup(value => value.PlanAsync<IProviderMetadataCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) => MetadataPlan(request, registry, capability.Object));
        var context = Context();
        if (showLabel.HasValue)
            context = context with
            {
                Policy = new EffectiveProviderPolicySnapshot(ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty,
                    ImmutableHashSet<string>.Empty, AudioQualityPolicy.DefaultStep, 0.07)
                { Preferences = new ListeningPreferences { ShowExternalLabel = showLabel.Value } }
            };
        var gateway = new ProtocolProviderGateway(router.Object, registry, new HttpClientFactory(), audioVerifier: FixtureAudioVerifier.Instance);
        var song = Assert.IsType<Song>(await gateway.GetSongAsync(context, "deezer", "42"));
        if (canonical)
        {
            song.CanonicalRecordingId = Guid.Parse("00000000-0000-0000-0000-000000000042");
            song.Id = "ext-allstarr-song-recording";
        }
        var before = JsonSerializer.Serialize(song);
        var http = new DefaultHttpContext();
        http.Items[ProtocolExecutionContextFactory.HttpContextItemKey] = context;
        var activity = new Mock<IPlaybackDeliveryActivitySource>(MockBehavior.Strict);
        activity.Setup(value => value.StreamFor(context.RequireActor().EffectiveUserId, "device", song.Id))
            .Returns(new PlaybackStreamSource("apple-musickit", "serving-42", null, "jellyfin", "backend", true,
                DateTimeOffset.UtcNow, "cached")
            { Media = new("audio/mp4", "mp4", "alac", 960_000, 48_000, 24, 2) });
        var builder = new JellyfinResponseBuilder(httpContexts: new HttpContextAccessor { HttpContext = http }, playbackActivity: activity.Object);
        var item = JsonSerializer.SerializeToElement(Assert.IsType<Microsoft.AspNetCore.Mvc.JsonResult>(builder.CreateSongResponse(song)).Value);
        var suffix = showLabel != false ? " [A]" : "";

        Assert.Equal("Track" + (showLabel != false ? " [A]/[E]" : " [E]"), item.GetProperty("Name").GetString());
        Assert.Equal("Album" + suffix, item.GetProperty("Album").GetString());
        Assert.Equal(["Primary" + suffix, "Guest" + suffix], item.GetProperty("Artists").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal("Host" + suffix, item.GetProperty("AlbumArtist").GetString());
        var artists = item.GetProperty("ArtistItems").EnumerateArray().ToArray();
        Assert.Equal(["ext-deezer-artist-1", "ext-deezer-artist-3"], artists.Select(value => value.GetProperty("Id").GetString()));
        Assert.Equal(["Primary" + suffix, "Guest" + suffix], artists.Select(value => value.GetProperty("Name").GetString()));
        var credit = Assert.Single(item.GetProperty("AlbumArtists").EnumerateArray());
        Assert.Equal("ext-deezer-artist-2", credit.GetProperty("Id").GetString());
        Assert.Equal("Host" + suffix, credit.GetProperty("Name").GetString());
        Assert.Equal(song.Id, item.GetProperty("Id").GetString());
        Assert.Equal("ext-deezer-album-7", item.GetProperty("AlbumId").GetString());
        Assert.Equal("ext-deezer-album-7", item.GetProperty("ParentId").GetString());
        Assert.Equal(song.Id + "-art-v2", item.GetProperty("ImageTags").GetProperty("Primary").GetString());
        Assert.Equal("https://images.invalid/catalog.jpg", song.CoverArtUrl);
        Assert.Equal("42", item.GetProperty("ProviderIds").GetProperty("deezer").GetString());
        Assert.Equal("recording-id", item.GetProperty("ProviderIds").GetProperty("MusicBrainzTrack").GetString());
        Assert.Equal(2, item.GetProperty("ProviderIds").EnumerateObject().Count());
        Assert.Equal("alac", item.GetProperty("MediaSources")[0].GetProperty("MediaStreams")[0].GetProperty("Codec").GetString());
        var album = new Album
        {
            Id = "ext-deezer-album-7",
            Title = "Album",
            Artist = "Host",
            ArtistId = "ext-deezer-artist-2",
            Artists = song.AlbumArtists,
            ExternalProvider = "deezer",
            ExternalId = "7"
        };
        var albumItem = JsonSerializer.SerializeToElement(builder.ConvertAlbumForQuery(album));
        Assert.Equal("Album" + suffix, albumItem.GetProperty("Name").GetString());
        Assert.Equal("Album" + suffix, albumItem.GetProperty("SortName").GetString());
        Assert.Equal("Host" + suffix, albumItem.GetProperty("Artists")[0].GetString());
        Assert.Equal("Host" + suffix, albumItem.GetProperty("AlbumArtist").GetString());
        foreach (var field in new[] { "ArtistItems", "AlbumArtists" })
        {
            var albumCredit = Assert.Single(albumItem.GetProperty(field).EnumerateArray());
            Assert.Equal("Host" + suffix, albumCredit.GetProperty("Name").GetString());
            Assert.Equal("ext-deezer-artist-2", albumCredit.GetProperty("Id").GetString());
        }
        Assert.Equal("ext-deezer-album-7", albumItem.GetProperty("Id").GetString());
        Assert.Equal("7", albumItem.GetProperty("ProviderIds").GetProperty("deezer").GetString());
        var artist = new Artist { Id = "ext-deezer-artist-2", Name = "Host", ExternalProvider = "deezer", ExternalId = "2" };
        var artistItem = JsonSerializer.SerializeToElement(builder.ConvertArtistForQuery(artist));
        Assert.Equal("Host" + suffix, artistItem.GetProperty("Name").GetString());
        Assert.Equal("Host" + suffix, artistItem.GetProperty("SortName").GetString());
        Assert.Equal("ext-deezer-artist-2", artistItem.GetProperty("Id").GetString());
        Assert.Equal("2", artistItem.GetProperty("ProviderIds").GetProperty("deezer").GetString());
        Assert.Equal(before, JsonSerializer.Serialize(song));
        capability.Verify(value => value.GetTrackAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderTrackLookupRequest>()), Times.Once);
        activity.VerifyAll();
    }

    [Fact]
    public async Task DescriptiveCollectionReads_ColdCacheDoesNotRouteOrWrite()
    {
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        var gateway = new ProtocolProviderGateway(router.Object, MetadataRegistry(), new HttpClientFactory(),
            applicationCache: new TestMemoryApplicationCache(), audioVerifier: FixtureAudioVerifier.Instance);
        var context = Context() with { IsReadOnly = true };

        Assert.Empty(await gateway.GetArtistAlbumsAsync(context, "deezer", "2"));
        Assert.Empty(await gateway.GetArtistTracksAsync(context, "deezer", "2"));
        Assert.Empty(await gateway.GetSimilarArtistsAsync(context, "deezer", "2", 20));
        router.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DescriptiveMetadataReads_ReuseOnlyTheCurrentlyAuthorizedAccountSnapshot()
    {
        var capability = new Mock<IProviderMetadataCapability>(MockBehavior.Strict);
        capability.SetupGet(value => value.ProviderId).Returns("deezer");
        capability.SetupGet(value => value.Capability).Returns(ProviderCapabilityKind.Metadata);
        capability.Setup(value => value.GetAlbumAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderAlbumLookupRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderAlbumMetadata>.Success(new(new("deezer", ProviderResourceKind.Album, "7"),
                "Album", [new("Host")], notes: "Stored album notes")));
        var registry = MetadataRegistry(capability.Object);
        var context = Context();
        var account = new ProviderAccountContext(Guid.NewGuid(), "deezer", ProviderAccountScope.Personal, 1,
            ownerUserId: context.RequireActor().EffectiveUserId);
        var allowed = true;
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        router.Setup(value => value.PlanAsync<IProviderMetadataCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) =>
            {
                if (!allowed) return EmptyPlan<IProviderMetadataCapability>(request);
                var plan = MetadataPlan(request, registry, capability.Object);
                var candidate = plan.Candidates.Single();
                var source = candidate.Context;
                var accountContext = new ProviderExecutionContext(source.Actor, source.ProviderId, account, source.Policy,
                    source.OperationId, source.CorrelationId, source.Deadline, source.CancellationToken);
                return new ProviderRoutePlan<IProviderMetadataCapability>(request,
                    [new(candidate.Priority, candidate.Provider, candidate.Descriptor, candidate.Implementation, accountContext, null)], plan.Decision);
            });
        var gateway = new ProtocolProviderGateway(router.Object, registry, new HttpClientFactory(), applicationCache: new TestMemoryApplicationCache(), audioVerifier: FixtureAudioVerifier.Instance);
        await gateway.GetAlbumAsync(context, "deezer", "7");
        capability.Invocations.Clear();

        Assert.Equal("Stored album notes", (await gateway.GetStoredAlbumAsync(context, "deezer", "7"))?.Notes);
        allowed = false;
        Assert.Null(await gateway.GetStoredAlbumAsync(context, "deezer", "7"));
        capability.VerifyGet(value => value.ProviderId, Times.AtLeastOnce());
        capability.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DescriptiveCollectionReads_ReuseKnownRelationshipsAndIsolateViewers()
    {
        var capability = new Mock<IProviderMetadataCapability>(MockBehavior.Strict);
        capability.SetupGet(value => value.ProviderId).Returns("deezer");
        capability.SetupGet(value => value.Capability).Returns(ProviderCapabilityKind.Metadata);
        capability.Setup(value => value.GetArtistAlbumsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderArtistItemsRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderAlbumMetadata>>.Success(new("deezer",
                [new(new("deezer", ProviderResourceKind.Album, "7"), "Album", [new("Host", new("deezer", ProviderResourceKind.Artist, "2"))])])));
        capability.Setup(value => value.GetArtistTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderArtistItemsRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Success(new("deezer",
                [new(new("deezer", ProviderResourceKind.Track, "42"), "Song", [new("Host")])])));
        capability.Setup(value => value.GetSimilarArtistsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderSimilarArtistsRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderArtistMetadata>>.Success(new("deezer",
                [new(new("deezer", ProviderResourceKind.Artist, "3"), "Related artist")])));
        var registry = MetadataRegistry(capability.Object);
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        router.Setup(value => value.PlanAsync<IProviderMetadataCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) => MetadataPlan(request, registry, capability.Object));
        var gateway = new ProtocolProviderGateway(router.Object, registry, new HttpClientFactory(), applicationCache: new TestMemoryApplicationCache(), audioVerifier: FixtureAudioVerifier.Instance);
        var context = Context();
        await gateway.GetArtistAlbumsAsync(context, "deezer", "2");
        await gateway.GetArtistTracksAsync(context, "deezer", "2");
        await gateway.GetSimilarArtistsAsync(context, "deezer", "2", 20);
        router.Invocations.Clear();
        capability.Invocations.Clear();

        Assert.Equal("ext-deezer-album-7", Assert.Single(await gateway.GetArtistAlbumsAsync(context with { IsReadOnly = true }, "deezer", "2")).Id);
        Assert.Equal("ext-deezer-song-42", Assert.Single(await gateway.GetArtistTracksAsync(context with { IsReadOnly = true }, "deezer", "2")).Id);
        Assert.Equal("ext-deezer-artist-3", Assert.Single(await gateway.GetSimilarArtistsAsync(context with { IsReadOnly = true }, "deezer", "2", 20)).Id);
        var other = Context() with { IsReadOnly = true };
        Assert.Empty(await gateway.GetArtistAlbumsAsync(other, "deezer", "2"));
        Assert.Empty(await gateway.GetArtistTracksAsync(other, "deezer", "2"));
        Assert.Empty(await gateway.GetSimilarArtistsAsync(other, "deezer", "2", 20));
        router.VerifyNoOtherCalls();
        capability.VerifyNoOtherCalls();
    }
}
