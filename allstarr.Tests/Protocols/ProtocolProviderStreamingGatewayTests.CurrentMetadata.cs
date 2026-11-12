using allstarr.Core.Caching;
using allstarr.Core.Capabilities;
using allstarr.Core.Matching;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Routing;
using allstarr.Core.Settings;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolProviderStreamingGatewayTests
{
    [Fact]
    public async Task CurrentMetadataReadsOnlyTheRequestedProviderRecordingForTheViewer()
    {
        var context = Context() with { IsReadOnly = true };
        var capability = new Mock<IProviderMetadataCapability>(MockBehavior.Strict);
        capability.SetupGet(item => item.ProviderId).Returns("deezer");
        capability.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Metadata);
        capability.Setup(item => item.GetTrackAsync(
                It.Is<ProviderExecutionContext>(execution => execution.Actor == context.RequireActor()),
                It.Is<ProviderTrackLookupRequest>(request => request.Id.ProviderId == "deezer" && request.Id.Value == "42")))
            .ReturnsAsync(ProviderOutcome<ProviderTrackMetadata>.Success(new(new("deezer", ProviderResourceKind.Track, "42"),
                "Current title", [new("Current artist")], albumTitle: "Current album", duration: TimeSpan.FromMilliseconds(241160), isrc: "USAAA2600001")));
        var registry = MetadataRegistry(capability.Object);
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        router.Setup(item => item.PlanAsync<IProviderMetadataCapability>(It.Is<ProviderRouteRequest>(request =>
                request.Actor == context.RequireActor() && request.Purpose == ProviderRoutePurpose.ManualSelection &&
                request.ProviderPriority.SequenceEqual(new[] { "deezer" }))))
            .ReturnsAsync((ProviderRouteRequest request) => MetadataPlan(request, registry, capability.Object));
        var identities = new Mock<ITrackIdentityService>(MockBehavior.Strict);
        var gateway = new ProtocolProviderGateway(router.Object, registry, new HttpClientFactory(), identities: identities.Object);

        var song = await gateway.GetExactSongAsync(context, "deezer", "42");

        Assert.NotNull(song);
        Assert.Equal("deezer", song.ExternalProvider);
        Assert.Equal("42", song.ExternalId);
        Assert.Equal("Current title", song.Title);
        Assert.Equal("Current artist", song.Artist);
        Assert.Equal("Current album", song.Album);
        Assert.Equal(TimeSpan.FromMilliseconds(241160), song.PreciseDuration);
        Assert.Equal("USAAA2600001", song.Isrc);
        identities.VerifyNoOtherCalls();
        capability.VerifyAll();
        router.VerifyAll();
    }

    [Theory]
    [InlineData("deezer", "different")]
    [InlineData("qobuz", "42")]
    public async Task CurrentMetadataRejectsAResponseForADifferentRecording(string provider, string id)
    {
        var capability = new Mock<IProviderMetadataCapability>(MockBehavior.Strict);
        capability.SetupGet(item => item.ProviderId).Returns("deezer");
        capability.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Metadata);
        capability.Setup(item => item.GetTrackAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderTrackLookupRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderTrackMetadata>.Success(new(new(provider, ProviderResourceKind.Track, id),
                "Different recording", [new("Artist")])));
        var registry = MetadataRegistry(capability.Object);
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        router.Setup(item => item.PlanAsync<IProviderMetadataCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) => MetadataPlan(request, registry, capability.Object));
        var gateway = new ProtocolProviderGateway(router.Object, registry, new HttpClientFactory());

        Assert.Null(await gateway.GetExactSongAsync(Context(), "deezer", "42"));
    }

    [Fact]
    public async Task CurrentMetadataHonorsDisabledProviderPolicyBeforeAnyLookup()
    {
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        var identities = new Mock<ITrackIdentityService>(MockBehavior.Strict);
        var gateway = new ProtocolProviderGateway(router.Object, MetadataRegistry(), new HttpClientFactory(), identities: identities.Object);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => gateway.GetExactSongAsync(
            DisplayPolicyContext(ProtocolKind.Jellyfin, "deezer", ProviderUseMode.Off), "deezer", "42"));

        router.VerifyNoOtherCalls();
        identities.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CurrentMetadataKeepsTheExactAuthorizedCacheWhenTheProviderCannotAnswer()
    {
        var capability = new Mock<IProviderMetadataCapability>(MockBehavior.Strict);
        capability.SetupGet(item => item.ProviderId).Returns("deezer");
        capability.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Metadata);
        capability.SetupSequence(item => item.GetTrackAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderTrackLookupRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderTrackMetadata>.Success(new(new("deezer", ProviderResourceKind.Track, "42"),
                "Current title", [new("Current artist")], albumTitle: "Current album", isrc: "USAAA2600001")))
            .ThrowsAsync(new HttpRequestException("Provider unavailable"));
        var registry = MetadataRegistry(capability.Object);
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        router.Setup(item => item.PlanAsync<IProviderMetadataCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) => MetadataPlan(request, registry, capability.Object));
        var identities = new Mock<ITrackIdentityService>(MockBehavior.Strict);
        using var cache = new MemoryApplicationCache(new SystemPlatformClock());
        var gateway = new ProtocolProviderGateway(router.Object, registry, new HttpClientFactory(), applicationCache: cache,
            identities: identities.Object);
        var context = Context();

        Assert.NotNull(await gateway.GetExactSongAsync(context, "deezer", "42"));
        var cached = await gateway.GetExactSongAsync(context, "deezer", "42");

        Assert.NotNull(cached);
        Assert.Equal("deezer", cached.ExternalProvider);
        Assert.Equal("42", cached.ExternalId);
        Assert.Equal("Current title", cached.Title);
        Assert.Equal("Current album", cached.Album);
        Assert.Equal("USAAA2600001", cached.Isrc);
        capability.Verify(item => item.GetTrackAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderTrackLookupRequest>()), Times.Exactly(2));
        identities.VerifyNoOtherCalls();
    }
}
