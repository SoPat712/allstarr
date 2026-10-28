using System.Collections.Immutable;
using allstarr.Core.Capabilities;
using allstarr.Core.Protocols;
using allstarr.Core.Settings;
using allstarr.Core.Routing;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolProviderStreamingGatewayTests
{
    [Theory]
    [InlineData(ProtocolKind.Jellyfin, null)]
    [InlineData(ProtocolKind.Subsonic, null)]
    [InlineData(ProtocolKind.Jellyfin, 1)]
    [InlineData(ProtocolKind.Subsonic, 1)]
    [InlineData(ProtocolKind.Jellyfin, 2)]
    [InlineData(ProtocolKind.Subsonic, 7)]
    public async Task CleanOnlyDirectProviderPlaybackRequiresItemEvidence(ProtocolKind protocol, int? rating)
    {
        var metadata = new Mock<IProviderMetadataCapability>(MockBehavior.Strict);
        metadata.SetupGet(service => service.ProviderId).Returns("deezer");
        metadata.SetupGet(service => service.Capability).Returns(ProviderCapabilityKind.Metadata);
        metadata.Setup(service => service.GetTrackAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderTrackLookupRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderTrackMetadata>.Success(new(
                new("deezer", ProviderResourceKind.Track, "explicit"), "Song", [new("Artist")], explicitContentLyrics: rating)));
        var registry = MetadataRegistry(metadata.Object);
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        router.Setup(service => service.PlanAsync<IProviderMetadataCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) => MetadataPlan(request, registry, metadata.Object));
        var context = Context();
        if (protocol == ProtocolKind.Subsonic)
            context = new ProtocolExecutionContext(protocol, "backend", "listener",
                new allstarr.Core.Identity.AllstarrPrincipal(context.Principal!.UserId, "subsonic", "backend", "listener", "Listener", false),
                "clean-playback", DateTimeOffset.UtcNow.AddMinutes(1), default);
        var policy = new EffectiveProviderPolicySnapshot(ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty,
            ImmutableHashSet<string>.Empty, AudioQualityPolicy.DefaultStep, 0.07)
        { UserId = context.Principal!.UserId, Preferences = new("CleanOnly") };
        var policies = new Mock<IEffectiveProviderPolicyResolver>(MockBehavior.Strict);
        policies.Setup(service => service.ResolveForUserAsync(context.Principal.UserId, It.IsAny<CancellationToken>())).ReturnsAsync(policy);
        context = context with { Policy = policy with { Preferences = new("All") } };
        var gateway = new ProtocolProviderGateway(router.Object, registry, new HttpClientFactory(),
            effectivePolicies: policies.Object, audioVerifier: FixtureAudioVerifier.Instance);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => gateway.OpenStreamAsync(context,
            "deezer", "explicit", ProviderAudioQuality.Any, null));
        router.Verify(service => service.PlanAsync<IProviderStreamingCapability>(It.IsAny<ProviderRouteRequest>()), Times.Never);
    }
}
