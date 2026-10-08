using System.Collections.Immutable;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Protocols;
using allstarr.Core.Routing;
using allstarr.Core.Settings;
using allstarr.Services;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolProviderStreamingGatewayTests
{
    [Theory]
    [InlineData(ProtocolKind.Jellyfin)]
    [InlineData(ProtocolKind.Subsonic)]
    public async Task MetadataCollectionsFilterPerViewerWithoutChangingSharedProviderData(ProtocolKind protocol)
    {
        var tenant = Guid.CreateVersion7();
        var userA = Guid.CreateVersion7();
        var userB = Guid.CreateVersion7();
        var tracks = new int?[] { 0, 1, 2, 3, 6, 7, null }.Select((flag, index) =>
            new ProviderTrackMetadata(new("deezer", ProviderResourceKind.Track, index.ToString()),
                $"Track {index}", [new("Artist")], explicitContentLyrics: flag)).ToArray();
        var metadata = new Mock<IProviderMetadataCapability>(MockBehavior.Strict);
        metadata.SetupGet(s => s.ProviderId).Returns("deezer");
        metadata.SetupGet(s => s.Capability).Returns(ProviderCapabilityKind.Metadata);
        metadata.Setup(s => s.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Success(new("deezer", tracks)));
        metadata.Setup(s => s.SearchAlbumsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderAlbumMetadata>>.Success(new("deezer", [])));
        metadata.Setup(s => s.SearchArtistsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderArtistMetadata>>.Success(new("deezer", [])));
        metadata.Setup(s => s.GetArtistTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderArtistItemsRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Success(new("deezer", tracks)));
        metadata.Setup(s => s.GetAlbumAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderAlbumLookupRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderAlbumMetadata>.Success(new(
                new("deezer", ProviderResourceKind.Album, "album"), "Album", [new("Artist")], tracks: tracks)));
        metadata.Setup(s => s.GetTrackAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderTrackLookupRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderTrackMetadata>.Success(tracks[1]));
        var streaming = new Mock<IProviderStreamingCapability>(MockBehavior.Strict);
        streaming.SetupGet(s => s.ProviderId).Returns("deezer");
        streaming.SetupGet(s => s.Capability).Returns(ProviderCapabilityKind.Streaming);
        var registry = MetadataRegistry(metadata.Object);
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        router.Setup(s => s.PlanAsync<IProviderMetadataCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) => MetadataPlan(request, registry, metadata.Object));
        router.Setup(s => s.PlanAsync<IProviderStreamingCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) => Plan(request, registry, streaming.Object));
        var policies = new Mock<IEffectiveProviderPolicyResolver>(MockBehavior.Strict);
        var modeA = "CleanOnly";
        policies.Setup(s => s.ResolveForUserAsync(tenant, It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, Guid user, CancellationToken _) => new EffectiveProviderPolicySnapshot(
                tenant, ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty,
                ImmutableHashSet<string>.Empty, AudioQualityPolicy.DefaultStep, 0.07)
            {
                UserId = user,
                Preferences = new(user == userA ? modeA : "All"),
                PreferenceRevision = user == userA ? modeA : "unchanged"
            });
        var gateway = new ProtocolProviderGateway(router.Object, registry,
            Mock.Of<IProviderRouteAccountResolver>(), Mock.Of<IMusicMetadataService>(MockBehavior.Strict),
            new HttpClientFactory(), effectivePolicies: policies.Object);

        foreach (var user in new[] { userA, userB, userA })
        {
            var context = new ProtocolExecutionContext(protocol, "backend", "listener",
                new AllstarrPrincipal(tenant, user, protocol.ToString().ToLowerInvariant(), "backend", "listener", "Listener", false),
                "preferences", DateTimeOffset.UtcNow.AddMinutes(1), default);
            var expected = user == userB ? tracks : modeA == "CleanOnly"
                ? tracks.Where(track => track.ExplicitContentLyrics != 1).ToArray()
                : tracks.Where(track => track.ExplicitContentLyrics is not (0 or 3)).ToArray();
            var ids = expected.Select(track => track.Id.Value).Order().ToArray();
            Assert.Equal(ids, (await gateway.SearchAsync(context, "Track", 20, 0, 0)).Songs.Select(s => s.ExternalId).Order());
            Assert.Equal(ids, (await gateway.GetArtistTracksAsync(context, "deezer", "artist")).Select(s => s.ExternalId).Order());
            Assert.Equal(ids, (await gateway.GetAlbumAsync(context, "deezer", "album"))!.Songs.Select(s => s.ExternalId).Order());
            Assert.Equal(user == userB || modeA == "ExplicitOnly", await gateway.GetSongAsync(context, "deezer", "1") != null);
            if (user == userB) modeA = "ExplicitOnly";
        }
        Assert.Equal(new int?[] { 0, 1, 2, 3, 6, 7, null }, tracks.Select(t => t.ExplicitContentLyrics));
    }
}
