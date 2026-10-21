using System.Collections.Immutable;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Routing;
using allstarr.Core.Settings;
using Moq;

namespace allstarr.Tests;

public sealed class ProviderUseSearchTests
{
    [Theory]
    [InlineData(ProtocolKind.Jellyfin, ProviderUseMode.ManualOnly)]
    [InlineData(ProtocolKind.Subsonic, ProviderUseMode.ManualOnly)]
    [InlineData(ProtocolKind.Jellyfin, ProviderUseMode.Off)]
    [InlineData(ProtocolKind.Subsonic, ProviderUseMode.Off)]
    public async Task PersonSearchIncludesManualSourcesAndHidesOffSources(ProtocolKind protocol, ProviderUseMode mode)
    {
        var user = Guid.CreateVersion7();
        var policy = new EffectiveProviderPolicySnapshot(
            ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty,
            ImmutableHashSet<string>.Empty, "High", 0.07)
        { ProviderModes = ImmutableDictionary<string, ProviderUseMode>.Empty.Add("source", mode) };
        var policies = new Mock<IEffectiveProviderPolicyResolver>(MockBehavior.Strict);
        policies.Setup(item => item.ResolveForUserAsync(user, It.IsAny<CancellationToken>())).ReturnsAsync(policy);
        var metadata = new Mock<IProviderMetadataCapability>(MockBehavior.Strict);
        metadata.SetupGet(item => item.ProviderId).Returns("source");
        metadata.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Metadata);
        metadata.Setup(item => item.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Success(new("source",
                [new(new("source", ProviderResourceKind.Track, "song"), "Song", [new("Artist")])])));
        metadata.Setup(item => item.SearchAlbumsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderAlbumMetadata>>.Success(new("source",
                [new(new("source", ProviderResourceKind.Album, "album"), "Album", [new("Artist")])])));
        metadata.Setup(item => item.SearchArtistsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderArtistMetadata>>.Success(new("source",
                [new(new("source", ProviderResourceKind.Artist, "artist"), "Artist")])));
        var streaming = new Mock<IProviderStreamingCapability>(MockBehavior.Strict);
        streaming.SetupGet(item => item.ProviderId).Returns("source");
        streaming.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Streaming);
        var descriptor = new ProviderDescriptor("source", "Source", "Search source", ProviderOrigin.BuiltIn,
            "1", "1", [
                new(ProviderCapabilityKind.Metadata, ProviderCapabilitySupportState.Supported,
                    ProviderAccountRequirement.None, "1", ["searchTracks", "getTrack", "searchAlbums", "searchArtists"]),
                new(ProviderCapabilityKind.Streaming, ProviderCapabilitySupportState.Supported,
                    ProviderAccountRequirement.None, "1", ["getStreamLease"])
            ], new());
        var registry = new ProviderRegistry([new(descriptor, [metadata.Object, streaming.Object])]);
        var health = new Mock<IProviderRouteHealthSource>();
        health.Setup(item => item.Get(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<ProviderCapabilityKind>()))
            .Returns(new ProviderRouteHealthSnapshot(ProviderRouteHealthState.Healthy, false));
        var router = new ProviderRouter(registry, Mock.Of<IProviderRouteAccountResolver>(), health.Object,
            Mock.Of<IProviderRouteSidecarSource>(), new Mock<ITrackIdentityService>(MockBehavior.Strict).Object,
            effectivePolicies: policies.Object);
        var gateway = new ProtocolProviderGateway(router, registry, Mock.Of<IHttpClientFactory>(MockBehavior.Strict),
            effectivePolicies: policies.Object);
        var context = new ProtocolExecutionContext(protocol, "fixture", "listener",
            new AllstarrPrincipal(user, protocol.ToString().ToLowerInvariant(), "fixture", "listener", "Listener", false),
            "search", DateTimeOffset.UtcNow.AddMinutes(1), default);

        Assert.Empty(await gateway.SearchAutomaticSongsAsync(context, "Song", 20));
        metadata.Verify(item => item.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(),
            It.IsAny<ProviderMetadataSearchRequest>()), Times.Never);
        var result = await gateway.SearchAsync(context, "Song", 20, 20, 20);
        var matches = await gateway.SearchPlayableSongsAsync(context, "Song", 20);
        if (mode == ProviderUseMode.Off)
        {
            Assert.Empty(result.Songs);
            Assert.Empty(result.Albums);
            Assert.Empty(result.Artists);
            Assert.Empty(matches);
            metadata.Verify(item => item.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(),
                It.IsAny<ProviderMetadataSearchRequest>()), Times.Never);
        }
        else
        {
            Assert.Equal("source", Assert.Single(result.Songs).ExternalProvider);
            Assert.Equal("source", Assert.Single(result.Albums).ExternalProvider);
            Assert.Equal("source", Assert.Single(result.Artists).ExternalProvider);
            Assert.Equal("source", Assert.Single(matches).ExternalProvider);
        }
    }
}
