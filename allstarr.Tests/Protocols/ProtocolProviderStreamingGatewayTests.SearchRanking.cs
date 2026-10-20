using allstarr.Core.Capabilities;
using allstarr.Core.Protocols;
using allstarr.Core.Routing;
using Microsoft.Extensions.Configuration;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolProviderStreamingGatewayTests
{
    [Theory]
    [InlineData(ProtocolKind.Jellyfin, "Daft Punk Get Lucky")]
    [InlineData(ProtocolKind.Subsonic, "Daft Punk Get Lucky")]
    [InlineData(ProtocolKind.Jellyfin, "Get Lucky")]
    [InlineData(ProtocolKind.Subsonic, "Get Lucky")]
    public async Task SearchRanksAllFetchedRowsBeforeApplyingTheLimit(ProtocolKind protocol, string query)
    {
        var upload = SearchRankingCapability("soundcloud",
            ("upload", "Daft Punk - Get Lucky #nightcore #bassboosted", "999333"));
        var originals = SearchRankingCapability("deezer",
            ("irrelevant", "Unrelated", "Someone Else"),
            ("original", "Get Lucky", "Daft Punk"));
        var (gateway, router) = SearchRankingGateway("soundcloud,deezer", "soundcloud,deezer", upload, originals);

        var result = await gateway.SearchAsync(SearchRankingContext(protocol), query, 1, 0, 0);

        Assert.Equal("original", Assert.Single(result.Songs).ExternalId);
        foreach (var capability in new[] { upload, originals })
        {
            Mock.Get(capability).Verify(item => item.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(),
                It.IsAny<ProviderMetadataSearchRequest>()), Times.Once);
            Mock.Get(capability).Verify(item => item.SearchAlbumsAsync(It.IsAny<ProviderExecutionContext>(),
                It.IsAny<ProviderMetadataSearchRequest>()), Times.Never);
            Mock.Get(capability).Verify(item => item.SearchArtistsAsync(It.IsAny<ProviderExecutionContext>(),
                It.IsAny<ProviderMetadataSearchRequest>()), Times.Never);
        }
        router.Verify(item => item.PlanAsync<IProviderMetadataCapability>(It.IsAny<ProviderRouteRequest>()), Times.Once);
    }

    [Theory]
    [InlineData(ProtocolKind.Jellyfin, "deezer,soundcloud", "deezer")]
    [InlineData(ProtocolKind.Subsonic, "deezer,soundcloud", "deezer")]
    [InlineData(ProtocolKind.Jellyfin, "soundcloud,deezer", "soundcloud")]
    [InlineData(ProtocolKind.Subsonic, "soundcloud,deezer", "soundcloud")]
    public async Task SearchTiesUseMetadataOrderInsteadOfStreamingOrder(
        ProtocolKind protocol, string metadataOrder, string expectedProvider)
    {
        var first = SearchRankingCapability("soundcloud", ("s", "Get Lucky", "Daft Punk"));
        var second = SearchRankingCapability("deezer", ("d", "Get Lucky", "Daft Punk"));
        var streamingOrder = expectedProvider == "deezer" ? "soundcloud,deezer" : "deezer,soundcloud";
        var (gateway, _) = SearchRankingGateway(metadataOrder, streamingOrder, first, second);

        var result = await gateway.SearchAsync(SearchRankingContext(protocol), "Daft Punk Get Lucky", 2, 0, 0);

        Assert.Equal(expectedProvider, result.Songs[0].ExternalProvider);
        Assert.Equal(2, result.Songs.Count);
    }

    [Theory]
    [InlineData(ProtocolKind.Jellyfin, "Espresso (Mixed)", null)]
    [InlineData(ProtocolKind.Subsonic, "Espresso [Mixed]", null)]
    [InlineData(ProtocolKind.Jellyfin, "Espresso", "Summer DJ Mix")]
    [InlineData(ProtocolKind.Subsonic, "Espresso", "Mixed by Someone")]
    [InlineData(ProtocolKind.Jellyfin, "Espresso", "Continuous Mix")]
    public async Task SearchRanksContinuousMixExcerptsAfterTheOriginalWithoutHidingThem(
        ProtocolKind protocol, string title, string? album)
    {
        var mixes = new Mock<IProviderMetadataCapability>(MockBehavior.Strict);
        mixes.SetupGet(item => item.ProviderId).Returns("qobuz");
        mixes.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Metadata);
        mixes.Setup(item => item.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Success(new("qobuz",
                [new(new("qobuz", ProviderResourceKind.Track, "mix"), title,
                    [new("Sabrina Carpenter")], albumTitle: album)])));
        var originals = SearchRankingCapability("deezer", ("original", "Espresso", "Sabrina Carpenter"));
        var (gateway, _) = SearchRankingGateway("qobuz,deezer", "qobuz,deezer", mixes.Object, originals);

        var result = await gateway.SearchAsync(SearchRankingContext(protocol), "Espresso", 2, 0, 0);

        Assert.Equal(new[] { "original", "mix" }, result.Songs.Select(song => song.ExternalId));
    }

    [Theory]
    [InlineData(ProtocolKind.Jellyfin)]
    [InlineData(ProtocolKind.Subsonic)]
    public async Task SharedIsrcDoesNotCollapseAMixOverTheOriginal(ProtocolKind protocol)
    {
        var capabilities = new[] { "qobuz", "deezer" }.Select(provider =>
        {
            var capability = Mock.Get(SearchRankingCapability(provider));
            capability.Setup(item => item.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
                .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Success(new(provider,
                    [new(new(provider, ProviderResourceKind.Track, provider), "Espresso", [new("Sabrina Carpenter")],
                        albumTitle: provider == "qobuz" ? "DJ Mix" : null, isrc: "USUM72403305")])));
            return capability.Object;
        }).ToArray();
        var (gateway, _) = SearchRankingGateway("qobuz,deezer", "qobuz,deezer", capabilities);

        var result = await gateway.SearchAsync(SearchRankingContext(protocol), "Espresso", 2, 0, 0);

        Assert.Equal(new[] { "deezer", "qobuz" }, result.Songs.Select(song => song.ExternalProvider));
    }

    private static IProviderMetadataCapability SearchRankingCapability(
        string provider, params (string Id, string Title, string Artist)[] tracks)
    {
        var capability = new Mock<IProviderMetadataCapability>(MockBehavior.Strict);
        capability.SetupGet(item => item.ProviderId).Returns(provider);
        capability.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Metadata);
        capability.Setup(item => item.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Success(new(provider,
                tracks.Select(track => new ProviderTrackMetadata(new(provider, ProviderResourceKind.Track, track.Id),
                    track.Title, [new(track.Artist)])).ToArray())));
        return capability.Object;
    }

    private static ProtocolExecutionContext SearchRankingContext(ProtocolKind protocol) => new(
        protocol, "backend", "principal",
        new(Guid.NewGuid(), protocol.ToString().ToLowerInvariant(), "backend", "principal", "User", false),
        "search-ranking", DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);

    private static (ProtocolProviderGateway Gateway, Mock<IProviderRouter> Router) SearchRankingGateway(
        string metadataOrder, string streamingOrder, params IProviderMetadataCapability[] capabilities)
    {
        var registry = MetadataRegistry(capabilities);
        var router = new Mock<IProviderRouter>();
        router.Setup(item => item.PlanAsync<IProviderMetadataCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) => MetadataPlan(request, registry, capabilities));
        router.Setup(item => item.PlanAsync<IProviderStreamingCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) => Plan(request, registry,
                capabilities.Select(capability => registry.GetRequiredCapability<IProviderStreamingCapability>(
                    capability.ProviderId, ProviderCapabilityKind.Streaming)).ToArray()));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Providers:MetadataOrder"] = metadataOrder,
            ["Providers:StreamingOrder"] = streamingOrder
        }).Build();
        return (new ProtocolProviderGateway(router.Object, registry, new HttpClientFactory(), configuration), router);
    }
}
