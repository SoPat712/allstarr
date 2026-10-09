using allstarr.Core.Capabilities;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Routing;
using allstarr.Core.Storage;
using allstarr.Services;
using Microsoft.Extensions.Configuration;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolProviderStreamingGatewayTests
{
    [Theory]
    [InlineData(ProviderResourceKind.Track)]
    [InlineData(ProviderResourceKind.Album)]
    [InlineData(ProviderResourceKind.Artist)]
    [InlineData(ProviderResourceKind.Playlist)]
    public async Task PublicArtwork_UsesTypedAccountFreeMetadataAndPreservesAppleAlias(ProviderResourceKind kind)
    {
        const string provider = "apple-download";
        var id = new ProviderExternalResourceId(provider, kind, "public-resource");
        var artwork = new ProviderArtworkReference(publicUri: new("https://artwork.example.invalid/cover.jpg"));
        var capability = new Mock<IProviderMetadataCapability>(MockBehavior.Strict);
        capability.SetupGet(item => item.ProviderId).Returns(provider);
        capability.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Metadata);
        ProviderExecutionContext? execution = null;
        switch (kind)
        {
            case ProviderResourceKind.Track:
                capability.Setup(item => item.GetTrackAsync(It.IsAny<ProviderExecutionContext>(),
                        It.Is<ProviderTrackLookupRequest>(request => request.Id == id)))
                    .Callback((ProviderExecutionContext context, ProviderTrackLookupRequest _) => execution = context)
                    .ReturnsAsync(ProviderOutcome<ProviderTrackMetadata>.Success(new(id, "Track", [new("Artist")], artwork: artwork)));
                break;
            case ProviderResourceKind.Album:
                capability.Setup(item => item.GetAlbumAsync(It.IsAny<ProviderExecutionContext>(),
                        It.Is<ProviderAlbumLookupRequest>(request => request.Id == id)))
                    .Callback((ProviderExecutionContext context, ProviderAlbumLookupRequest _) => execution = context)
                    .ReturnsAsync(ProviderOutcome<ProviderAlbumMetadata>.Success(new(id, "Album", [new("Artist")], artwork: artwork)));
                break;
            case ProviderResourceKind.Artist:
                capability.Setup(item => item.GetArtistAsync(It.IsAny<ProviderExecutionContext>(),
                        It.Is<ProviderArtistLookupRequest>(request => request.Id == id)))
                    .Callback((ProviderExecutionContext context, ProviderArtistLookupRequest _) => execution = context)
                    .ReturnsAsync(ProviderOutcome<ProviderArtistMetadata>.Success(new(id, "Artist", artwork)));
                break;
            case ProviderResourceKind.Playlist:
                capability.Setup(item => item.GetPlaylistArtworkAsync(It.IsAny<ProviderExecutionContext>(), id))
                    .Callback((ProviderExecutionContext context, ProviderExternalResourceId _) => execution = context)
                    .ReturnsAsync(ProviderOutcome<ProviderArtworkReference>.Success(artwork));
                break;
        }
        var registry = MetadataRegistry(capability.Object);
        var accounts = new Mock<IProviderRouteAccountResolver>(MockBehavior.Strict);
        var health = new Mock<IProviderRouteHealthSource>(MockBehavior.Strict);
        health.Setup(item => item.Get(provider, null, ProviderCapabilityKind.Metadata))
            .Returns(new ProviderRouteHealthSnapshot(ProviderRouteHealthState.Unknown, false));
        var router = new ProviderRouter(registry, accounts.Object, health.Object,
            Mock.Of<IProviderRouteSidecarSource>(MockBehavior.Strict), Mock.Of<ITrackIdentityService>(MockBehavior.Strict));
        var legacy = new Mock<IMusicMetadataService>(MockBehavior.Strict);
        var gateway = new ProtocolProviderGateway(router, registry, accounts.Object, legacy.Object, new HttpClientFactory());

        var result = await gateway.GetPublicArtworkUriAsync("applemusic", kind, id.Value);

        Assert.Equal(artwork.PublicUri, result);
        Assert.NotNull(execution);
        Assert.Equal(ProviderActorKind.PublicRead, execution.Actor.Kind);
        Assert.Null(execution.Actor.EffectiveUserId);
        Assert.Null(execution.Account);
        Assert.False(execution.Policy.AllowSharedAccount);
        Assert.False(execution.Policy.AllowFallback);
        Assert.InRange(execution.Remaining(DateTimeOffset.UtcNow), TimeSpan.Zero, TimeSpan.FromSeconds(30));
        accounts.VerifyNoOtherCalls();
        legacy.VerifyNoOtherCalls();
        capability.VerifyAll();
    }

    [Theory]
    [InlineData(ProviderAccountRequirement.Optional)]
    [InlineData(ProviderAccountRequirement.Required)]
    public async Task PublicArtwork_NeverEntersAccountBoundMetadata(ProviderAccountRequirement requirement)
    {
        var capability = new Mock<IProviderMetadataCapability>(MockBehavior.Strict);
        capability.SetupGet(item => item.ProviderId).Returns("private");
        capability.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Metadata);
        var registry = new ProviderRegistry([new ProviderRegistration(new ProviderDescriptor(
            "private", "Private", "Account-bound metadata", ProviderOrigin.BuiltIn, "1", "1",
            [new(ProviderCapabilityKind.Metadata, ProviderCapabilitySupportState.Supported, requirement,
                "1", ["searchTracks", "getTrack"], [ProviderAccountScope.Personal, ProviderAccountScope.Shared])],
            new ProviderPermissionDescriptor()), [capability.Object])]);
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        var accounts = new Mock<IProviderRouteAccountResolver>(MockBehavior.Strict);
        var legacy = new Mock<IMusicMetadataService>(MockBehavior.Strict);
        var gateway = new ProtocolProviderGateway(router.Object, registry, accounts.Object, legacy.Object, new HttpClientFactory());

        Assert.Null(await gateway.GetPublicArtworkUriAsync("private", ProviderResourceKind.Track, "private-resource"));
        router.VerifyNoOtherCalls();
        accounts.VerifyNoOtherCalls();
        legacy.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PublicArtwork_RespectsDisabledProvidersBeforeLookup()
    {
        var capability = new Mock<IProviderMetadataCapability>(MockBehavior.Strict);
        capability.SetupGet(item => item.ProviderId).Returns("deezer");
        capability.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Metadata);
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Providers:Disabled"] = "deezer"
        }).Build();
        var gateway = new ProtocolProviderGateway(router.Object, MetadataRegistry(capability.Object),
            Mock.Of<IProviderRouteAccountResolver>(MockBehavior.Strict), Mock.Of<IMusicMetadataService>(MockBehavior.Strict),
            new HttpClientFactory(), configuration);

        Assert.Null(await gateway.GetPublicArtworkUriAsync("deezer", ProviderResourceKind.Track, "track"));
        router.VerifyNoOtherCalls();
    }
}
