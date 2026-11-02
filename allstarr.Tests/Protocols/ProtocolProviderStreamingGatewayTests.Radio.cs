using allstarr.Core.Capabilities;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Core.Routing;
using allstarr.Models.Domain;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolProviderStreamingGatewayTests
{
    [Fact]
    public async Task RadioSeed_ArtistDiscoveryFetchesColdRelationshipsForACustomStart()
    {
        var metadata = new Mock<IProviderMetadataCapability>(MockBehavior.Strict);
        metadata.SetupGet(item => item.ProviderId).Returns("deezer");
        metadata.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Metadata);
        metadata.Setup(item => item.GetArtistTracksAsync(It.IsAny<ProviderExecutionContext>(),
                It.Is<ProviderArtistItemsRequest>(request => request.Id.Value == "artist")))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Success(new("deezer",
                [new(new("deezer", ProviderResourceKind.Track, "track"), "Track", [new("Artist")])])));
        metadata.Setup(item => item.GetSimilarArtistsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderSimilarArtistsRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderArtistMetadata>>.Success(new("deezer", [])));
        metadata.Setup(item => item.GetSimilarTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderSimilarTracksRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Success(new("deezer", [])));
        var registry = MetadataRegistry(metadata.Object);
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        router.Setup(item => item.PlanAsync<IProviderMetadataCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) => MetadataPlan(request, registry, metadata.Object));
        var gateway = new ProtocolProviderGateway(router.Object, registry, new HttpClientFactory());
        using var radio = new MusicMixService(gateway);
        var context = Context() with { IsReadOnly = true };

        var result = await radio.SeedCandidatesAsync(context, null, new Artist { Name = "Artist" }, "deezer", "artist", default);

        Assert.Equal("ext-deezer-song-track", Assert.Single(result).Id);
        Assert.True(context.IsReadOnly);
        metadata.Verify(item => item.GetArtistTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderArtistItemsRequest>()), Times.Once);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task RadioQualification_RequiresAnAllowedResolvedLeaseWithoutProbingAudio(bool allowed, bool available)
    {
        var streaming = new Mock<IProviderStreamingCapability>(MockBehavior.Strict);
        streaming.SetupGet(item => item.ProviderId).Returns("deezer");
        streaming.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Streaming);
        streaming.Setup(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(),
                It.Is<ProviderStreamLeaseRequest>(request => request.TrackId.Value == "track")))
            .ReturnsAsync(available ? ProviderOutcome<ProviderStreamLease>.Success(new("fixture",
                new Uri("https://audio.invalid/track"), DateTimeOffset.UtcNow.AddMinutes(1), true, true,
                new("audio/flac", "flac", "flac"), ProviderStreamRetryBehavior.DoNotRetry)) :
                ProviderOutcome<ProviderStreamLease>.Failure(new(ProviderErrorKind.NotFound)));
        var registry = MetadataRegistry(Mock.Of<IProviderMetadataCapability>(item =>
            item.ProviderId == "deezer" && item.Capability == ProviderCapabilityKind.Metadata));
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        router.Setup(item => item.PlanAsync<IProviderStreamingCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) => allowed ? Plan(request, registry, streaming.Object) :
                EmptyPlan<IProviderStreamingCapability>(request));
        router.Setup(item => item.EvaluateFallback(It.IsAny<ProviderRoutePlan<IProviderStreamingCapability>>(),
                It.IsAny<int>(), It.IsAny<ProviderError>()))
            .Returns(new ProviderFallbackDecision<IProviderStreamingCapability>(ProviderFallbackDisposition.Exhausted, "fallback-exhausted", null));
        var gateway = new ProtocolProviderGateway(router.Object, registry, new HttpClientFactory(), audioVerifier: FixtureAudioVerifier.Instance);

        Assert.Equal(allowed && available, await gateway.CanPlaySongAsync(Context(), new Song
        {
            Id = "ext-deezer-song-track",
            ExternalProvider = "deezer",
            ExternalId = "track"
        }));

        streaming.Verify(item => item.ProbeStreamAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderStreamLeaseRequest>()), Times.Never);
        streaming.Verify(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderStreamLeaseRequest>()), allowed ? Times.Once : Times.Never);
    }
}
