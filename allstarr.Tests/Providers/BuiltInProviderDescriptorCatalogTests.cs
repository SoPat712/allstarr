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
using allstarr.Core.Providers.Lyrics;
using allstarr.Models.Lyrics;
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
        var deezerStreaming = Streaming("deezer");
        var qobuzDownload = Download("qobuz");
        var qobuzStreaming = Streaming("qobuz");
        var qobuzMetadata = new QobuzMetadataCapabilityAdapter(
            new Mock<IConcreteMetadataService>(MockBehavior.Strict).Object);
        var apple = new AppleMusicKitPlaylistCapabilityAdapter(
            new HttpClient(new Mock<HttpMessageHandler>().Object),
            new Mock<IProviderAccountSecretAccessor>(MockBehavior.Strict).Object);
        var spotify = new SpotifyPlaylistCapabilityAdapter(
            new HttpClient(new Mock<HttpMessageHandler>().Object),
            new Mock<IProviderAccountSecretAccessor>(MockBehavior.Strict).Object);
        var spotifyLyrics = Lyrics("spotify");
        var lyricsPlus = Lyrics("lyricsplus");
        var lrclib = Lyrics("lrclib");
        var appleDownload = new AppleDownloadCapabilityAdapter(
            new HttpClient(new Mock<HttpMessageHandler>().Object),
            new AppleDownloadSettings(),
            new Mock<IAppleDownloadEndpointDiscovery>(MockBehavior.Strict).Object,
            new ProviderDownloadArtifactResolver(
                new Mock<IProviderDownloadArtifactStore>(MockBehavior.Strict).Object,
                new ProviderDownloadWorkspaceOptions { RootPath = Path.GetTempPath() }),
            1024);
        var appleDownloadStreaming = new AppleDownloadStreamingCapabilityAdapter(
            new HttpClient(new Mock<HttpMessageHandler>().Object),
            new AppleDownloadSettings(),
            new Mock<IAppleDownloadEndpointDiscovery>(MockBehavior.Strict).Object);
        var registry = new ProviderRegistry(
        [
            DeezerMetadataCapabilityAdapter.CreateRegistration(
                deezer, deezerDownload, deezerStreaming),
            QobuzDownloadCapabilityAdapter.CreateRegistration(
                qobuzDownload, qobuzStreaming, qobuzMetadata),
            AppleMusicKitPlaylistCapabilityAdapter.CreateRegistration(apple),
            SpotifyPlaylistCapabilityAdapter.CreateRegistration(spotify, spotifyLyrics),
            AppleDownloadCapabilityAdapter.CreateRegistration(
                appleDownload, streaming: appleDownloadStreaming),
            BuiltInLyricsCapabilityRegistration.CreateRegistration("lyricsplus", "LyricsPlus", lyricsPlus),
            BuiltInLyricsCapabilityRegistration.CreateRegistration("lrclib", "LRCLib", lrclib),
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
            ["deezer", "qobuz"],
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
            deezerDescriptor.Capabilities.Where(item => item.Capability is
                ProviderCapabilityKind.Metadata or
                ProviderCapabilityKind.Streaming or
                ProviderCapabilityKind.Download),
            capability => Assert.True(capability.HasUsableImplementation));
        Assert.All(
            deezerDescriptor.Capabilities.Where(item =>
                item.Capability is ProviderCapabilityKind.Playlist or ProviderCapabilityKind.Health),
            capability => Assert.Equal(
                ProviderCapabilitySupportState.ConfiguredOnly,
                capability.SupportState));
        Assert.Contains(
            registry.FindByCapability(ProviderCapabilityKind.Playlist),
            item => item.Id == "apple-musickit");
        Assert.Same(qobuzDownload, registry.GetRequiredCapability<IProviderDownloadCapability>(
            "qobuz", ProviderCapabilityKind.Download));
        Assert.Same(qobuzStreaming, registry.GetRequiredCapability<IProviderStreamingCapability>(
            "qobuz", ProviderCapabilityKind.Streaming));
        Assert.Same(qobuzMetadata, registry.GetRequiredCapability<IProviderMetadataCapability>(
            "qobuz", ProviderCapabilityKind.Metadata));
        Assert.Same(appleDownloadStreaming,
            registry.GetRequiredCapability<IProviderStreamingCapability>(
                "apple-download", ProviderCapabilityKind.Streaming));
        Assert.Equal(
            ["lrclib", "lyricsplus", "spotify"],
            registry.FindByCapability(ProviderCapabilityKind.Lyrics).Select(item => item.Id));
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

    private static IProviderStreamingCapability Streaming(string providerId)
    {
        var mock = new Mock<IProviderStreamingCapability>(MockBehavior.Strict);
        mock.SetupGet(item => item.ProviderId).Returns(providerId);
        mock.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Streaming);
        return mock.Object;
    }

    private static BuiltInLyricsCapabilityAdapter Lyrics(string providerId) => new(
        providerId,
        (_, _) => Task.FromResult<LyricsInfo?>(null));
}
