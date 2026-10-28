using allstarr.Core.Capabilities;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Routing;
using allstarr.Services.Common;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolProviderStreamingGatewayTests
{
    [Theory]
    [InlineData(ProtocolKind.Jellyfin, true)]
    [InlineData(ProtocolKind.Subsonic, true)]
    [InlineData(ProtocolKind.Jellyfin, false)]
    [InlineData(ProtocolKind.Subsonic, false)]
    public async Task DuplicateCompactAlbumSearchUsesCachedReleaseDetailsOrKeepsUnprovenCopies(ProtocolKind protocol, bool detailsAvailable)
    {
        var capabilities = new[] { SearchRankingCapability("deezer"), SearchRankingCapability("qobuz") };
        foreach (var capability in capabilities)
        {
            var provider = capability.ProviderId;
            Mock.Get(capability).Setup(item => item.SearchAlbumsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
                .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderAlbumMetadata>>.Success(new(provider,
                    [new(new(provider, ProviderResourceKind.Album, "release"), "PRIMA", [new("ADÉLA")], trackCount: 11)])));
            Mock.Get(capability).Setup(item => item.GetAlbumAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderAlbumLookupRequest>()))
                .ReturnsAsync(detailsAvailable ? ProviderOutcome<ProviderAlbumMetadata>.Success(new(
                    new(provider, ProviderResourceKind.Album, "release"), "PRIMA", [new("ADÉLA")], trackCount: 11,
                    isExplicit: true, upc: "012345678905")) : ProviderOutcome<ProviderAlbumMetadata>.Failure(new(ProviderErrorKind.NotSupported)));
        }
        var registry = MetadataRegistry(capabilities);
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        router.Setup(item => item.PlanAsync<IProviderMetadataCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) => MetadataPlan(request, registry, capabilities));
        using var cache = new MemoryApplicationCache(new SystemPlatformClock());
        var gateway = new ProtocolProviderGateway(router.Object, registry, new HttpClientFactory(), applicationCache: cache,
            audioVerifier: FixtureAudioVerifier.Instance);
        var context = SearchRankingContext(protocol);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var result = await gateway.SearchAsync(context, "PRIMA", 0, 20, 0);
            Assert.Equal(detailsAvailable ? 1 : 2, result.Albums.Count);
            Assert.All(result.Albums, album => Assert.Equal("PRIMA", album.Title));
        }
        foreach (var capability in capabilities)
            Mock.Get(capability).Verify(item => item.GetAlbumAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderAlbumLookupRequest>()),
                Times.Exactly(detailsAvailable ? 1 : 2));
    }
}
