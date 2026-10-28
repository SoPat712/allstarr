using System.Collections.Immutable;
using System.Net;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Routing;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using Microsoft.Extensions.Configuration;
using Moq;

namespace allstarr.Tests;

public sealed class ProviderUseRoutingTests
{
    [Fact]
    public async Task OnAllowsAutomaticUseOfASourceWithAManualDefault()
    {
        var plan = await Router(ProviderUseMode.Automatic).PlanAsync<IProviderMetadataCapability>(Request());
        Assert.Equal("manual", Assert.Single(plan.Candidates).Provider.Id);
    }

    [Fact]
    public async Task ManualOnlyProviderIsExcludedFromAutomaticSearch()
    {
        var plan = await Router().PlanAsync<IProviderMetadataCapability>(Request());
        Assert.Empty(plan.Candidates);
        Assert.Contains(plan.Decision.Candidates, item => item.ReasonCode == "manual-selection-required");
    }

    [Fact]
    public async Task PersonSearchCanUseManualOnlyProvider()
    {
        var plan = await Router().PlanAsync<IProviderMetadataCapability>(Request(ProviderRoutePurpose.ManualSelection));
        Assert.Equal("manual", Assert.Single(plan.Candidates).Provider.Id);
    }

    [Fact]
    public async Task ExactManualOnlyTrackCanBeOpenedWithoutCanonicalTranslation()
    {
        var source = new ProviderExternalResourceId("manual", ProviderResourceKind.Track, "version-3");
        var plan = await Router().PlanAsync<IProviderMetadataCapability>(Request(source: source));
        Assert.Equal(source, Assert.Single(plan.Candidates).TrackId);
    }

    [Fact]
    public async Task CanonicalRecordingNeverSelectsManualOnlyProvider()
    {
        var source = new ProviderExternalResourceId("allstarr", ProviderResourceKind.Track, Guid.NewGuid().ToString());
        var plan = await Router().PlanAsync<IProviderMetadataCapability>(Request(source: source));
        Assert.Empty(plan.Candidates);
    }

    [Theory]
    [InlineData(ProtocolKind.Jellyfin, false)]
    [InlineData(ProtocolKind.Subsonic, false)]
    [InlineData(ProtocolKind.Jellyfin, true)]
    [InlineData(ProtocolKind.Subsonic, true)]
    public async Task ExplicitManualSongPlaysAndSeeksUntilListenerTurnsSourceOff(ProtocolKind protocolKind, bool pinned)
    {
        var userId = Guid.CreateVersion7();
        var policy = new EffectiveProviderPolicySnapshot(
            ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty,
            ImmutableHashSet<string>.Empty, "High", 0.07)
        { ProviderModes = ImmutableDictionary<string, ProviderUseMode>.Empty.Add("manual", ProviderUseMode.ManualOnly) };
        var policies = new Mock<IEffectiveProviderPolicyResolver>(MockBehavior.Strict);
        policies.Setup(item => item.ResolveForUserAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(() => policy);
        var streaming = new Mock<IProviderStreamingCapability>(MockBehavior.Strict);
        streaming.SetupGet(item => item.ProviderId).Returns("manual");
        streaming.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Streaming);
        var lease = new ProviderStreamLease("fixture", new Uri("https://media.example.test/version-3"),
                DateTimeOffset.UtcNow.AddMinutes(1), true, true, new("audio/mpeg", "mp3", "mp3"),
                ProviderStreamRetryBehavior.DoNotRetry, (request, _) =>
                {
                    var response = new HttpResponseMessage(request.Headers.Range == null ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
                    { Content = new ByteArrayContent([1, 2, 3, 4]) };
                    if (request.Headers.Range != null)
                        response.Content.Headers.ContentRange = new(4, 7, 8);
                    return Task.FromResult(response);
                });
        streaming.Setup(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(),
                It.Is<ProviderStreamLeaseRequest>(request => request.TrackId.Value == "version-3")))
            .ReturnsAsync(ProviderOutcome<ProviderStreamLease>.Success(lease));
        var descriptor = new ProviderDescriptor("manual", "Manual", "Manual recordings", ProviderOrigin.BuiltIn,
            "1", "1", [new(ProviderCapabilityKind.Streaming, ProviderCapabilitySupportState.Supported,
                ProviderAccountRequirement.None, "1", ["getStreamLease"])], new())
        { DefaultUseMode = ProviderUseMode.ManualOnly };
        var registry = new ProviderRegistry([new(descriptor, [streaming.Object])]);
        var health = new Mock<IProviderRouteHealthSource>();
        health.Setup(item => item.Get(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<ProviderCapabilityKind>()))
            .Returns(new ProviderRouteHealthSnapshot(ProviderRouteHealthState.Healthy, false));
        var router = new ProviderRouter(registry, Mock.Of<IProviderRouteAccountResolver>(), health.Object,
            Mock.Of<IProviderRouteSidecarSource>(), new Mock<ITrackIdentityService>(MockBehavior.Strict).Object,
            effectivePolicies: policies.Object);
        var gateway = new ProtocolProviderGateway(router, registry, Mock.Of<IHttpClientFactory>(MockBehavior.Strict),
            new ConfigurationBuilder().Build(), effectivePolicies: policies.Object, audioVerifier: FixtureAudioVerifier.Instance);
        var protocol = new ProtocolExecutionContext(protocolKind, "fixture", "listener",
            new AllstarrPrincipal(userId, protocolKind.ToString().ToLowerInvariant(), "fixture", "listener", "Listener", false),
            "manual-stream", DateTimeOffset.UtcNow.AddMinutes(1), default, new("client", "device"));

        var chosenProvider = "manual";
        var chosenTrack = "version-3";
        if (pinned)
        {
            var pin = new ManualTrackOverrideRecord
            { Decision = ManualOverrideDecision.Pin, TargetProviderId = "manual", TargetExternalId = "version-3" };
            var selected = Assert.Single(DurableProviderRouteSelector.Select(
                new ProviderTrackIdentityRecord { ProviderId = "spotify", ExternalId = "original" },
                [], ["manual"], pin, providerPolicy: policy));
            Assert.True(selected.IsManual);
            chosenProvider = selected.ProviderId;
            chosenTrack = selected.ExternalId;
        }
        foreach (var range in new[] { null, "bytes=4-7" })
        {
            var opened = await gateway.OpenStreamAsync(protocol, chosenProvider, chosenTrack, ProviderAudioQuality.Any, range);
            Assert.NotNull(opened);
            Assert.Equal("manual", opened.ServingProviderId);
            Assert.Equal(range == null ? HttpStatusCode.OK : HttpStatusCode.PartialContent, opened.Response.StatusCode);
            opened.Response.Dispose();
        }
        policy = policy with { ProviderModes = policy.ProviderModes.SetItem("manual", ProviderUseMode.Off) };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            gateway.OpenStreamAsync(protocol, chosenProvider, chosenTrack, ProviderAudioQuality.Any, null));
        streaming.Verify(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(),
            It.IsAny<ProviderStreamLeaseRequest>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ManualSourceCannotBeFallbackForAnotherSource()
    {
        var plan = await Router().PlanAsync<IProviderMetadataCapability>(Request(
            ProviderRoutePurpose.ManualSelection,
            new ProviderExternalResourceId("other", ProviderResourceKind.Track, "track")));
        Assert.Empty(plan.Candidates);
        Assert.Contains(plan.Decision.Candidates, item => item.ReasonCode == "manual-selection-required");
    }

    private static ProviderRouter Router(ProviderUseMode? mode = null)
    {
        var capability = new Mock<IProviderMetadataCapability>();
        capability.SetupGet(item => item.ProviderId).Returns("manual");
        capability.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Metadata);
        var descriptor = new ProviderDescriptor("manual", "Manual", "Manual recordings", ProviderOrigin.BuiltIn,
            "1", "1", [new(ProviderCapabilityKind.Metadata, ProviderCapabilitySupportState.Supported,
                ProviderAccountRequirement.None, "1", ["searchTracks", "getTrack"])], new())
        { DefaultUseMode = ProviderUseMode.ManualOnly };
        var health = new Mock<IProviderRouteHealthSource>();
        health.Setup(item => item.Get(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<ProviderCapabilityKind>()))
            .Returns(new ProviderRouteHealthSnapshot(ProviderRouteHealthState.Healthy, false));
        var policies = new Mock<IEffectiveProviderPolicyResolver>(MockBehavior.Strict);
        if (mode.HasValue)
            policies.Setup(item => item.ResolveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
                new EffectiveProviderPolicySnapshot(
                    ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty,
                    ImmutableHashSet<string>.Empty, "High", 0.07)
                { ProviderModes = ImmutableDictionary<string, ProviderUseMode>.Empty.Add("manual", mode.Value) });
        return new(new ProviderRegistry([new(descriptor, [capability.Object])]),
            Mock.Of<IProviderRouteAccountResolver>(), health.Object,
            Mock.Of<IProviderRouteSidecarSource>(), new Mock<ITrackIdentityService>(MockBehavior.Strict).Object,
            effectivePolicies: mode.HasValue ? policies.Object : null);
    }

    private static ProviderRouteRequest Request(ProviderRoutePurpose purpose = ProviderRoutePurpose.Automatic,
        ProviderExternalResourceId? source = null) => new(
        ProviderCapabilityKind.Metadata, new(ProviderActorKind.PublicRead, null),
        new(new(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, true),
            ProviderExplicitContentPolicy.Allow, true, false, false, ["manual"]),
        "fixture", "fixture", DateTimeOffset.UtcNow.AddMinutes(1), ["manual"],
        sourceTrackId: source, purpose: purpose);
}
