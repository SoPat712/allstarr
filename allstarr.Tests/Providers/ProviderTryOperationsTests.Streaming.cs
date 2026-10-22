using allstarr.Core.Capabilities;
using allstarr.Services.Common;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProviderTryOperationsTests
{
    [Theory]
    [InlineData(ProviderCapabilityKind.Download, "download")]
    [InlineData(ProviderCapabilityKind.Playlist, "getPlaylistTracks")]
    [InlineData(ProviderCapabilityKind.Intelligence, "startAnalysis")]
    public async Task Probe_DoesNotDeclareHealthWithoutAnEndpointOperation(ProviderCapabilityKind capability, string hook)
    {
        var descriptor = Descriptor(new ProviderCapabilityDescriptor(capability, ProviderCapabilitySupportState.Supported,
            ProviderAccountRequirement.None, "1", [hook]));
        var result = await new ProviderTryOperations(Mock.Of<IProviderRegistry>()).ProbeAsync(Context(), descriptor, capability);
        Assert.False(result.Succeeded);
        Assert.Equal("probe_not_available", result.ReasonCode);
    }

    [Theory]
    [InlineData(ProviderProbeStatus.Unavailable, "service_unavailable", "service_unavailable")]
    [InlineData(ProviderProbeStatus.Unavailable, null, "service_unavailable")]
    [InlineData(ProviderProbeStatus.Unauthorized, null, "unauthorized")]
    public async Task HealthProbe_PreservesTheSourceFailureReason(ProviderProbeStatus status, string? safeCode, string expected)
    {
        var health = new Mock<IProviderHealthProbeCapability>();
        health.SetupGet(item => item.ProviderId).Returns("fixture");
        health.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Health);
        health.Setup(item => item.ProbeAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderHealthProbeRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderHealthProbeResult>.Success(new(status,
                DateTimeOffset.UtcNow, TimeSpan.Zero, safeCode)));
        var descriptor = new ProviderDescriptor("fixture", "Fixture", "Fixture", ProviderOrigin.Extension, "1", "1",
            [new(ProviderCapabilityKind.Health, ProviderCapabilitySupportState.Supported, ProviderAccountRequirement.None,
                "1", ["probeStreaming"]), new(ProviderCapabilityKind.Streaming, ProviderCapabilitySupportState.Supported,
                ProviderAccountRequirement.None, "1", ["getStreamLease"])], new(), entryPoint: "index.js", healthProbe: true);
        var streaming = new Mock<IProviderStreamingCapability>();
        streaming.SetupGet(item => item.ProviderId).Returns("fixture");
        streaming.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Streaming);
        var registry = new ProviderRegistry([new(descriptor, [health.Object, streaming.Object])]);
        var result = await new ProviderTryOperations(registry).ProbeAsync(Context(), descriptor, ProviderCapabilityKind.Streaming);
        Assert.False(result.Succeeded);
        Assert.Equal(expected, result.ReasonCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StreamingProbe_UsesTheMediaProbeInsteadOfTheCapabilityDeclaration(bool available)
    {
        var metadata = new Mock<IProviderMetadataCapability>();
        metadata.SetupGet(item => item.ProviderId).Returns("fixture");
        metadata.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Metadata);
        metadata.Setup(item => item.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Success(new("fixture",
                [new(new("fixture", ProviderResourceKind.Track, "song"), "Song", [new("Artist")])])));
        var streaming = new Mock<IProviderStreamingCapability>();
        streaming.SetupGet(item => item.ProviderId).Returns("fixture");
        streaming.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Streaming);
        streaming.Setup(item => item.ProbeStreamAsync(It.IsAny<ProviderExecutionContext>(),
                It.Is<ProviderStreamLeaseRequest>(request => request.TrackId.Value == "song")))
            .ReturnsAsync(ProviderOutcome<ProviderStreamProbeResult>.Success(new(available, DateTimeOffset.UtcNow)));
        var descriptor = Descriptor(new(ProviderCapabilityKind.Metadata, ProviderCapabilitySupportState.Supported,
            ProviderAccountRequirement.None, "1", ["searchTracks"]), new(ProviderCapabilityKind.Streaming,
            ProviderCapabilitySupportState.Supported, ProviderAccountRequirement.None, "1", ["getStreamLease", "probeStream"]));
        var registry = new ProviderRegistry([new(descriptor, [metadata.Object, streaming.Object])]);
        var result = await new ProviderTryOperations(registry).ProbeAsync(Context(), descriptor, ProviderCapabilityKind.Streaming);
        Assert.Equal(available, result.Succeeded);
        streaming.VerifyAll();
    }
}
