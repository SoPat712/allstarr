using allstarr.Core.Capabilities;
using allstarr.Core.Settings;
using Moq;

namespace allstarr.Tests;

public sealed class EffectiveProviderPolicyTests
{
    [Fact]
    public async Task Resolve_BuildsOneImmutableHouseholdSnapshot()
    {
        var settings = new Mock<IDurableRuntimeSettings>(MockBehavior.Strict);
        settings.Setup(item => item.GetManyAsync(
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<string> keys, CancellationToken _) =>
                keys.ToDictionary(key => key, Setting, StringComparer.OrdinalIgnoreCase));
        settings.Setup(item => item.GetPreferencesAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PersonalListeningPreferences(new(), new(), true, "household-revision"));
        var resolver = new EffectiveProviderPolicyResolver(settings.Object);

        var snapshot = await resolver.ResolveAsync();

        Assert.Null(snapshot.UserId);
        Assert.Equal(["qobuz", "deezer"],
            snapshot.GetProviderOrder(ProviderCapabilityKind.Streaming));
        Assert.Contains("unavailable", snapshot.DisabledProviders);
        Assert.Equal(AudioQualityPolicy.DefaultStep, snapshot.AudioQuality);
        Assert.Equal(0.07, snapshot.LocalPreferenceWindow);
    }

    [Fact]
    public async Task ResolveForUser_KeepsPersonalFiltersIsolatedAndIncludesUnknownExplicitState()
    {
        var userA = Guid.CreateVersion7();
        var userB = Guid.CreateVersion7();
        var settings = new Mock<IDurableRuntimeSettings>(MockBehavior.Strict);
        settings.Setup(item => item.GetManyAsync(
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<string> keys, CancellationToken _) =>
                keys.ToDictionary(key => key, Setting, StringComparer.OrdinalIgnoreCase));
        settings.Setup(item => item.GetPreferencesAsync(userA, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PersonalListeningPreferences(
                new("CleanOnly", false, false), new(), false, "user-a-revision"));
        settings.Setup(item => item.GetPreferencesAsync(userB, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PersonalListeningPreferences(
                new("All", true, true), new(), false, "user-b-revision"));
        var resolver = new EffectiveProviderPolicyResolver(settings.Object);

        var policyA = await resolver.ResolveForUserAsync(userA);
        var policyB = await resolver.ResolveForUserAsync(userB);

        Assert.Equal(userA, policyA.UserId);
        Assert.Equal(userB, policyB.UserId);
        Assert.False(policyA.Includes(1));
        Assert.True(policyB.Includes(1));
        Assert.True(policyA.Includes((int?)null));
        Assert.True(policyB.Includes((int?)null));
        Assert.Equal("user-a-revision", policyA.PreferenceRevision);
        Assert.Equal("user-b-revision", policyB.PreferenceRevision);
    }

    [Fact]
    public void Catalog_OwnsProviderOrderDefaults()
    {
        Assert.Equal("apple-download,deezer,qobuz",
            RuntimeSettingCatalog.Require("Providers:StreamingOrder").DefaultValue);
        Assert.Equal("spotify,apple-download,deezer,qobuz",
            RuntimeSettingCatalog.Require("Providers:PlaylistOrder").DefaultValue);
    }

    private static EffectiveRuntimeSetting Setting(string key)
    {
        object value = key switch
        {
            "Providers:StreamingOrder" => new[] { "qobuz", "deezer" },
            "Providers:Disabled" => new[] { "unavailable" },
            AudioQualityPolicy.SettingKey => AudioQualityPolicy.DefaultStep,
            "Matching:LocalPreferencePercent" => 7,
            _ => Array.Empty<string>()
        };
        var type = value switch
        {
            string[] => RuntimeSettingValueType.StringList,
            int => RuntimeSettingValueType.Integer,
            _ => RuntimeSettingValueType.String
        };
        var normalized = value is string[] items ? string.Join(',', items) : value.ToString()!;
        return new EffectiveRuntimeSetting(
            key,
            type,
            value,
            normalized,
            RuntimeSettingOrigin.Durable,
            1,
            "test",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
    }
}
