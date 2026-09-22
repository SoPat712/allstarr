using allstarr.Core.Capabilities;
using allstarr.Core.Settings;
using Moq;

namespace allstarr.Tests;

public sealed class EffectiveProviderPolicyTests
{
    [Fact]
    public async Task Resolve_BuildsOneImmutableTenantSnapshot()
    {
        var tenantId = Guid.CreateVersion7();
        var settings = new Mock<IDurableRuntimeSettings>(MockBehavior.Strict);
        settings.Setup(item => item.GetManyAsync(
                tenantId,
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, IEnumerable<string> keys, CancellationToken _) =>
                keys.ToDictionary(key => key, Setting, StringComparer.OrdinalIgnoreCase));
        var resolver = new EffectiveProviderPolicyResolver(settings.Object);

        var snapshot = await resolver.ResolveAsync(tenantId);

        Assert.Equal(tenantId, snapshot.TenantId);
        Assert.Equal(["qobuz", "deezer"],
            snapshot.GetProviderOrder(ProviderCapabilityKind.Streaming));
        Assert.Contains("unavailable", snapshot.DisabledProviders);
        Assert.Equal(AudioQualityPolicy.DefaultStep, snapshot.AudioQuality);
        Assert.Equal(0.07, snapshot.LocalPreferenceWindow);
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
