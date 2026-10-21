using allstarr.Core.Capabilities;
using allstarr.Core.Settings;
using Moq;

namespace allstarr.Tests;

public sealed class EffectiveProviderPolicyTests
{
    [Theory]
    [InlineData("automatic", "manualOnly", ProviderUseMode.ManualOnly)]
    [InlineData("manualOnly", "automatic", ProviderUseMode.Automatic)]
    [InlineData("off", "automatic", ProviderUseMode.Off)]
    [InlineData("off", "manualOnly", ProviderUseMode.Off)]
    public async Task PersonalModesOverrideHouseholdUnlessHouseholdIsOff(
        string household, string personal, ProviderUseMode expected)
    {
        var user = Guid.CreateVersion7();
        var settings = new Mock<IDurableRuntimeSettings>(MockBehavior.Strict);
        settings.Setup(item => item.GetManyAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<string> keys, CancellationToken _) =>
                keys.ToDictionary(key => key, Setting, StringComparer.OrdinalIgnoreCase));
        settings.Setup(item => item.GetPreferencesAsync(user, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PersonalListeningPreferences(
                new(ProviderModes: new Dictionary<string, string> { ["source"] = personal }),
                new(ProviderModes: new Dictionary<string, string> { ["source"] = household }), false, "revision"));
        var policy = await new EffectiveProviderPolicyResolver(settings.Object).ResolveForUserAsync(user);
        Assert.Equal(expected, policy.GetProviderMode("source"));
    }

    [Theory]
    [InlineData(null, ProviderUseMode.ManualOnly)]
    [InlineData("automatic", ProviderUseMode.Automatic)]
    [InlineData("off", ProviderUseMode.Off)]
    public async Task ArtistGridDefaultAndSavedHouseholdModesRemainSelectable(string? mode, ProviderUseMode expected)
    {
        var settings = new Mock<IDurableRuntimeSettings>(MockBehavior.Strict);
        settings.Setup(item => item.GetManyAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<string> keys, CancellationToken _) =>
                keys.ToDictionary(key => key, Setting, StringComparer.OrdinalIgnoreCase));
        var configured = new Dictionary<string, string> { ["soundcloud"] = "manualOnly" };
        if (mode != null) configured["artistgrid"] = mode;
        var preferences = new ListeningPreferences(ProviderModes: configured);
        settings.Setup(item => item.GetPreferencesAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PersonalListeningPreferences(preferences, preferences, true, "revision"));
        var registry = new Mock<IProviderRegistry>();
        registry.SetupGet(item => item.Providers).Returns([
            new("artistgrid", "ArtistGrid", "Source", ProviderOrigin.BuiltIn, "1", "1", [], new())
            { DefaultUseMode = ProviderUseMode.ManualOnly },
            new("soundcloud", "SoundCloud", "Source", ProviderOrigin.BuiltIn, "1", "1", [], new())
        ]);
        var policy = await new EffectiveProviderPolicyResolver(settings.Object, registry.Object).ResolveAsync();
        Assert.Equal(expected, policy.GetProviderMode("artistgrid"));
        Assert.Equal(ProviderUseMode.ManualOnly, policy.GetProviderMode("soundcloud"));
    }

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

    [Fact]
    public void LyricsDefaultAppendsPublicSourcesAndSavedOrdersAppendAvailableSources()
    {
        Assert.Equal("backend,kept,spotify,apple-download,lrcred,lyricsplus,musixmatch,lrclib,unison,paxsenix,genius",
            RuntimeSettingCatalog.Require("Providers:LyricsOrder").DefaultValue);
        var snapshot = new EffectiveProviderPolicySnapshot(
            System.Collections.Immutable.ImmutableDictionary<ProviderCapabilityKind, System.Collections.Immutable.ImmutableArray<string>>.Empty
                .Add(ProviderCapabilityKind.Lyrics, ["lrclib", "lyricsplus"]),
            System.Collections.Immutable.ImmutableHashSet<string>.Empty, "lossless", 0);
        Assert.Equal(new[] { "lrclib", "lyricsplus", "lrcred", "paxsenix" },
            snapshot.ApplyProviderAvailability(ProviderCapabilityKind.Lyrics, ["paxsenix", "lrcred", "lyricsplus", "lrclib"]));
    }

    [Fact]
    public void RemovedLyricsSourceCannotParticipateFromAnOldAvailabilityList()
    {
        var snapshot = new EffectiveProviderPolicySnapshot(
            System.Collections.Immutable.ImmutableDictionary<ProviderCapabilityKind, System.Collections.Immutable.ImmutableArray<string>>.Empty
                .Add(ProviderCapabilityKind.Lyrics, ["binilyrics", "lrclib"]),
            System.Collections.Immutable.ImmutableHashSet<string>.Empty, "lossless", 0);
        Assert.False(snapshot.IsEnabled("binilyrics"));
        Assert.False(snapshot.IsAutomatic("binilyrics"));
        Assert.Equal(new[] { "lrclib", "lrcred" },
            snapshot.ApplyProviderAvailability(ProviderCapabilityKind.Lyrics, ["binilyrics", "lrcred", "lrclib"]));
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
