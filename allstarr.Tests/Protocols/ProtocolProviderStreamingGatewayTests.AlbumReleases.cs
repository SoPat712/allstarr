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
    [InlineData(ProtocolKind.Jellyfin)]
    [InlineData(ProtocolKind.Subsonic)]
    public async Task CleanOnlySearchAndArtistAlbumsPreferLabeledCleanEditionWithoutDetailCalls(ProtocolKind protocol)
    {
        var capability = SearchRankingCapability("deezer");
        var albums = new ProviderAlbumMetadata[]
        {
            new(new("deezer", ProviderResourceKind.Album, "explicit"), "Album", [new("Artist")], isExplicit: true),
            new(new("deezer", ProviderResourceKind.Album, "clean"), "Album", [new("Artist")], isExplicit: false),
            new(new("deezer", ProviderResourceKind.Album, "unknown"), "Other Album", [new("Artist")])
        };
        Mock.Get(capability).Setup(item => item.SearchAlbumsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderAlbumMetadata>>.Success(new("deezer", albums)));
        Mock.Get(capability).Setup(item => item.GetArtistAlbumsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderArtistItemsRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderAlbumMetadata>>.Success(new("deezer", albums)));
        var registry = MetadataRegistry(capability);
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        router.Setup(item => item.PlanAsync<IProviderMetadataCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) => MetadataPlan(request, registry, capability));
        var gateway = new ProtocolProviderGateway(router.Object, registry, new HttpClientFactory(), audioVerifier: FixtureAudioVerifier.Instance);
        var context = SearchRankingContext(protocol);
        context = context with
        {
            Policy = new allstarr.Core.Settings.EffectiveProviderPolicySnapshot(
                System.Collections.Immutable.ImmutableDictionary<ProviderCapabilityKind, System.Collections.Immutable.ImmutableArray<string>>.Empty,
                System.Collections.Immutable.ImmutableHashSet<string>.Empty, "Lossless", 0.07)
            { UserId = context.Actor?.EffectiveUserId, Preferences = new("CleanOnly") }
        };
        var search = Assert.Single((await gateway.SearchAsync(context, "Album", 0, 20, 0)).Albums);
        var artist = Assert.Single(await gateway.GetArtistAlbumsAsync(context, "deezer", "artist"));
        Assert.Equal("clean", search.ExternalId);
        Assert.Equal("Album (Clean)", search.Title);
        Assert.Equal("clean", artist.ExternalId);
        Mock.Get(capability).Verify(item => item.GetAlbumAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderAlbumLookupRequest>()), Times.Never);
    }

    [Theory]
    [InlineData(ProtocolKind.Jellyfin)]
    [InlineData(ProtocolKind.Subsonic)]
    public async Task DifferentCatalogUpcsUseCompleteRecordingEvidenceBeforeKeepingDuplicateAlbums(ProtocolKind protocol)
    {
        var capabilities = new[] { SearchRankingCapability("deezer"), SearchRankingCapability("qobuz") };
        foreach (var capability in capabilities)
        {
            var provider = capability.ProviderId;
            var upc = provider == "deezer" ? "881061182917" : "00881061182924";
            Mock.Get(capability).Setup(item => item.SearchAlbumsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
                .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderAlbumMetadata>>.Success(new(provider,
                    [new(new(provider, ProviderResourceKind.Album, "release"), "PRIMA", [new("ADÉLA")], trackCount: 1,
                        isExplicit: true, upc: upc)])));
            Mock.Get(capability).Setup(item => item.GetAlbumAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderAlbumLookupRequest>()))
                .ReturnsAsync(ProviderOutcome<ProviderAlbumMetadata>.Success(new(
                    new(provider, ProviderResourceKind.Album, "release"), "PRIMA", [new("ADÉLA")], trackCount: 1,
                    tracks: [new(new(provider, ProviderResourceKind.Track, "track"), "KGB", [new("ADÉLA")], isrc: "USUM72601922")],
                    isExplicit: true, upc: upc)));
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
            Assert.Single((await gateway.SearchAsync(context, "PRIMA", 0, 20, 0)).Albums);
        foreach (var capability in capabilities)
            Mock.Get(capability).Verify(item => item.GetAlbumAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderAlbumLookupRequest>()), Times.Once);
    }

    [Theory]
    [InlineData(ProtocolKind.Jellyfin)]
    [InlineData(ProtocolKind.Subsonic)]
    public async Task AlbumEditionLabelsKeepExactTitleSearchRankingBeforeLimiting(ProtocolKind protocol)
    {
        var capability = SearchRankingCapability("deezer");
        Mock.Get(capability).Setup(item => item.SearchAlbumsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderAlbumMetadata>>.Success(new("deezer",
                [new(new("deezer", ProviderResourceKind.Album, "explicit"), "PRIMA", [new("ADÉLA")], isExplicit: true, upc: "012345678905"),
                 new(new("deezer", ProviderResourceKind.Album, "clean"), "PRIMA", [new("ADÉLA")], isExplicit: false, upc: "012345678912"),
                 new(new("deezer", ProviderResourceKind.Album, "other"), "PRIMA", [new("Another artist")])])));
        var registry = MetadataRegistry(capability);
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        router.Setup(item => item.PlanAsync<IProviderMetadataCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) => MetadataPlan(request, registry, capability));
        var gateway = new ProtocolProviderGateway(router.Object, registry, new HttpClientFactory(), audioVerifier: FixtureAudioVerifier.Instance);

        var result = await gateway.SearchAsync(SearchRankingContext(protocol), "PRIMA", 0, 2, 0);

        Assert.Equal(["explicit", "clean"], result.Albums.Select(album => album.ExternalId));
    }

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
