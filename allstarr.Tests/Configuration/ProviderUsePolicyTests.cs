using System.Collections.Immutable;
using allstarr.Core.Capabilities;
using allstarr.Core.Settings;

namespace allstarr.Tests;

public sealed class ProviderUsePolicyTests
{
    [Fact]
    public void ManualSourcesRemainAvailableForSelectionButNeverAutomaticWork()
    {
        var policy = new EffectiveProviderPolicySnapshot(
            ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty,
            ImmutableHashSet<string>.Empty, "lossless", 0)
        {
            ProviderModes = ImmutableDictionary<string, ProviderUseMode>.Empty
                .Add("manual", ProviderUseMode.ManualOnly)
                .Add("disabled", ProviderUseMode.Off)
        };

        Assert.Equal(["automatic", "manual"], policy.ApplyProviderAvailability(
            ProviderCapabilityKind.Streaming, ["manual", "automatic", "disabled"]));
        Assert.Equal(["automatic"], policy.ApplyAutomaticProviderAvailability(
            ProviderCapabilityKind.Streaming, ["manual", "automatic", "disabled"]));
        Assert.True(policy.IsEnabled("manual"));
        Assert.False(policy.IsAutomatic("manual"));
        Assert.False(policy.IsEnabled("disabled"));
    }

    [Fact]
    public void HouseholdDisabledSourcesCannotBeReenabledByPersonalModes()
    {
        var policy = new EffectiveProviderPolicySnapshot(
            ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty,
            ImmutableHashSet.Create("disabled"), "lossless", 0)
        {
            ProviderModes = ImmutableDictionary<string, ProviderUseMode>.Empty
                .Add("disabled", ProviderUseMode.Automatic)
        };
        Assert.Equal(ProviderUseMode.Off, policy.GetProviderMode("disabled"));
    }

    [Theory]
    [InlineData("{\"source\":\"anything\"}")]
    [InlineData("{\"source\":true}")]
    [InlineData("[]")]
    [InlineData("{\"source\":\"off\",\"source\":\"automatic\"}")]
    [InlineData("{\"https://example.test\":\"automatic\"}")]
    public void InvalidModeMapsAreRejected(string value) =>
        Assert.Throws<ArgumentException>(() => ProviderUsePolicy.ParseModes(value));

    [Fact]
    public void ModeMapsNormalizeAliasesAndRetainExplicitAutomaticOverrides()
    {
        var modes = ProviderUsePolicy.ParseModes("{\"apple-download\":\"manualOnly\",\"fixture\":\"automatic\"}");
        Assert.Equal(ProviderUseMode.ManualOnly, modes["apple-musickit"]);
        Assert.Equal(ProviderUseMode.Automatic, modes["fixture"]);
        Assert.Equal("{\"apple-musickit\":\"manualOnly\",\"fixture\":\"automatic\"}",
            ProviderUsePolicy.SerializeModes(modes));
    }
}
