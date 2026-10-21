using System.Collections.Immutable;
using allstarr.Core.Capabilities;
using allstarr.Core.Providers.Monochrome;
using allstarr.Core.Routing;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProviderRouterTests
{
    [Theory]
    [InlineData(ProviderUseMode.Automatic, false, true)]
    [InlineData(ProviderUseMode.ManualOnly, false, false)]
    [InlineData(ProviderUseMode.ManualOnly, true, true)]
    [InlineData(ProviderUseMode.Off, false, false)]
    [InlineData(ProviderUseMode.Off, true, false)]
    public async Task MonochromeRoute_RespectsListenerSwitch(ProviderUseMode mode, bool exact, bool expected)
    {
        using var http = new HttpClient();
        var registration = MonochromeProviderRegistration.CreateRegistration(new(http));
        var policies = new Mock<IEffectiveProviderPolicyResolver>();
        var policy = new EffectiveProviderPolicySnapshot(
            ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty,
            ImmutableHashSet<string>.Empty, "max", 0.07)
        { ProviderModes = ImmutableDictionary<string, ProviderUseMode>.Empty.Add("monochrome", mode) };
        policies.Setup(item => item.ResolveForUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(policy);
        var router = new ProviderRouter(new ProviderRegistry([registration]), new FakeAccountResolver(), new FakeHealthSource(),
            new FakeSidecarSource(), new FakeIdentityService(ProviderIdentityVerification.Verified), effectivePolicies: policies.Object);
        var plan = await router.PlanAsync<IProviderStreamingCapability>(Request(ProviderCapabilityKind.Streaming,
            ["monochrome"], states: [new("monochrome", availableQualities: [ProviderAudioQuality.Lossless])],
            source: exact ? Track("monochrome", "155142501534011392") : null));
        Assert.Equal(expected, plan.Candidates.Count == 1);
    }

    [Fact]
    public async Task MonochromeRoute_IsLastFallbackAndAdvancesAfterFailure()
    {
        using var http = new HttpClient();
        var router = Router([Streaming("zulu"), MonochromeProviderRegistration.CreateRegistration(new(http)), Streaming("deezer")]);
        var plan = await router.PlanAsync<IProviderStreamingCapability>(Request(ProviderCapabilityKind.Streaming, [], states:
            [new("deezer", availableQualities: [ProviderAudioQuality.Lossless]),
             new("zulu", availableQualities: [ProviderAudioQuality.Lossless]),
             new("monochrome", availableQualities: [ProviderAudioQuality.Lossless])]));
        Assert.Equal(["deezer", "zulu", "monochrome"], plan.Candidates.Select(item => item.Provider.Id));
        Assert.Equal("monochrome", router.EvaluateFallback(plan, 1, new(ProviderErrorKind.TransientFailure)).NextCandidate?.Provider.Id);
        var policy = new EffectiveProviderPolicySnapshot(
            ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty.Add(ProviderCapabilityKind.Streaming, ["deezer"]),
            ImmutableHashSet<string>.Empty, "max", 0.07);
        Assert.Equal(["deezer", "zulu", "monochrome"], policy.ApplyProviderAvailability(ProviderCapabilityKind.Streaming,
            ["monochrome", "zulu", "deezer"]));
    }
}
