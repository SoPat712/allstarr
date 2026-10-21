using allstarr.Core.Capabilities;
using allstarr.Core.Protocols;
using allstarr.Core.Routing;
using allstarr.Core.Storage;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolProviderStreamingGatewayTests
{
    [Fact]
    public async Task DescriptiveCollectionReads_ColdCacheDoesNotRouteOrWrite()
    {
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        var gateway = new ProtocolProviderGateway(router.Object, MetadataRegistry(), new HttpClientFactory(),
            applicationCache: new TestMemoryApplicationCache());
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
        var gateway = new ProtocolProviderGateway(router.Object, registry, new HttpClientFactory(), applicationCache: new TestMemoryApplicationCache());
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
        var gateway = new ProtocolProviderGateway(router.Object, registry, new HttpClientFactory(), applicationCache: new TestMemoryApplicationCache());
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
