using allstarr.Core.Capabilities;
using allstarr.Core.Providers;
using allstarr.Core.Providers.Deezer;
using allstarr.Core.Providers.AppleMusicKit;
using allstarr.Core.Providers.AppleDownload;
using allstarr.Core.Providers.Qobuz;
using allstarr.Core.Downloads;
using allstarr.Models.Settings;
using allstarr.Services.AppleMusic;
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
        var deezerDownload = Download("deezer");
        var qobuzDownload = Download("qobuz");
        var apple = new AppleMusicKitPlaylistCapabilityAdapter(
            new HttpClient(new Mock<HttpMessageHandler>().Object),
            new Mock<IProviderAccountSecretAccessor>(MockBehavior.Strict).Object);
        var spotify = new SpotifyPlaylistCapabilityAdapter(
            new HttpClient(new Mock<HttpMessageHandler>().Object),
            new Mock<IProviderAccountSecretAccessor>(MockBehavior.Strict).Object);
        var appleDownload = new AppleDownloadCapabilityAdapter(
            new HttpClient(new Mock<HttpMessageHandler>().Object),
            new AppleDownloadSettings(),
            new Mock<IAppleDownloadEndpointDiscovery>(MockBehavior.Strict).Object,
            new ProviderDownloadArtifactResolver(
                new Mock<IProviderDownloadArtifactStore>(MockBehavior.Strict).Object,
                new ProviderDownloadWorkspaceOptions { RootPath = Path.GetTempPath() }),
            1024);
        var registry = new ProviderRegistry(
        [
            DeezerMetadataCapabilityAdapter.CreateRegistration(deezer, deezerDownload),
            QobuzDownloadCapabilityAdapter.CreateRegistration(qobuzDownload),
            AppleMusicKitPlaylistCapabilityAdapter.CreateRegistration(apple),
            SpotifyPlaylistCapabilityAdapter.CreateRegistration(spotify),
            AppleDownloadCapabilityAdapter.CreateRegistration(appleDownload),
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
        Assert.True(deezerDescriptor.Capabilities.Single(item =>
            item.Capability == ProviderCapabilityKind.Download).HasUsableImplementation);
        Assert.All(
            deezerDescriptor.Capabilities.Where(item =>
                item.Capability is not (ProviderCapabilityKind.Metadata or ProviderCapabilityKind.Download)),
            capability => Assert.Equal(
                ProviderCapabilitySupportState.ConfiguredOnly,
                capability.SupportState));
        Assert.Contains(
            registry.FindByCapability(ProviderCapabilityKind.Playlist),
            item => item.Id == "apple-musickit");
        Assert.Same(qobuzDownload, registry.GetRequiredCapability<IProviderDownloadCapability>(
            "qobuz", ProviderCapabilityKind.Download));
        Assert.All(
            BuiltInProviderDescriptorCatalog.LegacyRegistrations,
            registration => Assert.All(
                registration.Descriptor.Capabilities,
                capability => Assert.False(capability.HasUsableImplementation)));
    }

    private static IProviderDownloadCapability Download(string providerId)
    {
        var mock = new Mock<IProviderDownloadCapability>(MockBehavior.Strict);
        mock.SetupGet(item => item.ProviderId).Returns(providerId);
        mock.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Download);
        return mock.Object;
    }
}
