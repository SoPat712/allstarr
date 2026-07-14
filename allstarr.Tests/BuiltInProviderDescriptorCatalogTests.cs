using allstarr.Core.Capabilities;
using allstarr.Core.Providers;
using allstarr.Core.Providers.Deezer;
using allstarr.Core.Providers.AppleMusicKit;
using allstarr.Core.Providers.Spotify;
using allstarr.Services;
using Moq;

namespace allstarr.Tests;

public sealed class BuiltInProviderDescriptorCatalogTests
{
    [Fact]
    public void Catalog_SeparatesAppleMusicKitAndNeverRoutesLegacyOnlyLanes()
    {
        var deezer = new DeezerMetadataCapabilityAdapter(
            new Mock<IConcreteMetadataService>(MockBehavior.Strict).Object);
        var apple = new AppleMusicKitPlaylistCapabilityAdapter(
            new HttpClient(new Mock<HttpMessageHandler>().Object),
            new Mock<IProviderAccountSecretAccessor>(MockBehavior.Strict).Object);
        var spotify = new SpotifyPlaylistCapabilityAdapter(
            new HttpClient(new Mock<HttpMessageHandler>().Object),
            new Mock<IProviderAccountSecretAccessor>(MockBehavior.Strict).Object);
        var registry = new ProviderRegistry(
        [
            DeezerMetadataCapabilityAdapter.CreateRegistration(deezer),
            AppleMusicKitPlaylistCapabilityAdapter.CreateRegistration(apple),
            SpotifyPlaylistCapabilityAdapter.CreateRegistration(spotify),
            .. BuiltInProviderDescriptorCatalog.LegacyRegistrations
        ]);

        string[] expected =
        [
            "apple-download",
            "apple-musickit",
            "deezer",
            "lastfm",
            "listenbrainz",
            "lrclib",
            "lyricsplus",
            "musicbrainz",
            "qobuz",
            "spotify",
            "squidwtf"
        ];
        Assert.Equal(expected, registry.Providers.Select(item => item.Id));
        Assert.Equal(
            ["deezer"],
            registry.FindByCapability(ProviderCapabilityKind.Metadata)
                .Select(item => item.Id));
        var deezerDescriptor = registry.GetRequired("deezer");
        Assert.Equal(
            [
                ProviderCapabilityKind.Metadata,
                ProviderCapabilityKind.Streaming,
                ProviderCapabilityKind.Download,
                ProviderCapabilityKind.Playlist,
                ProviderCapabilityKind.Health
            ],
            deezerDescriptor.Capabilities.Select(item => item.Capability));
        Assert.True(deezerDescriptor.Capabilities.Single(item =>
            item.Capability == ProviderCapabilityKind.Metadata).HasUsableImplementation);
        Assert.All(
            deezerDescriptor.Capabilities.Where(item =>
                item.Capability != ProviderCapabilityKind.Metadata),
            capability => Assert.Equal(
                ProviderCapabilitySupportState.ConfiguredOnly,
                capability.SupportState));
        Assert.Contains(
            registry.FindByCapability(ProviderCapabilityKind.Playlist),
            item => item.Id == "apple-musickit");
        Assert.False(registry.TryGetCapability<IProviderMetadataCapability>(
            "qobuz",
            ProviderCapabilityKind.Metadata,
            out _));
        Assert.All(
            BuiltInProviderDescriptorCatalog.LegacyRegistrations,
            registration => Assert.All(
                registration.Descriptor.Capabilities,
                capability => Assert.False(capability.HasUsableImplementation)));
    }
}
