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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace allstarr.Tests;

public sealed class BuiltInProviderRegistrationTests
{
    [Fact]
    public void ManagedDownloadPlacement_UsesTheKeptRootAndHonorsAnExplicitOverride()
    {
        var services = new ServiceCollection();
        services.AddProviderDownloadArtifacts(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Library:DownloadPath"] = "/downloads",
                ["Library:KeptPath"] = "/kept",
                ["FavoriteActions:Placement:RootPath"] = "/managed"
            })
            .Build());

        using var provider = services.BuildServiceProvider();
        var placement = provider.GetRequiredService<ManagedTrackPlacementOptions>();

        Assert.Equal("/managed", placement.RootPath);
        Assert.Equal(ManagedTrackPlacementOptions.DefaultRootId, placement.RootId);
    }

    [Fact]
    public void ManagedDownloadPlacement_DefaultsToTheConfiguredKeptRoot()
    {
        var services = new ServiceCollection();
        services.AddProviderDownloadArtifacts(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Library:DownloadPath"] = "/downloads",
                ["Library:KeptPath"] = "/kept"
            })
            .Build());

        using var provider = services.BuildServiceProvider();

        Assert.Equal(
            "/kept",
            provider.GetRequiredService<ManagedTrackPlacementOptions>().RootPath);
    }

    [Fact]
    public void Catalog_SeparatesAppleMusicKitAndNeverRoutesLegacyOnlyLanes()
    {
        var deezerLegacy = new Mock<IConcreteMetadataService>(MockBehavior.Strict).Object;
        var deezer = new DeezerMetadataCapabilityAdapter(deezerLegacy);
        var deezerPlaylists = new DeezerPlaylistCapabilityAdapter(deezerLegacy, deezer);
        var deezerDownload = Download("deezer");
        var deezerStreaming = Streaming("deezer");
        var qobuzDownload = Download("qobuz");
        var qobuzStreaming = Streaming("qobuz");
        var qobuzLegacy = new Mock<IConcreteMetadataService>(MockBehavior.Strict).Object;
        var qobuzMetadata = new QobuzMetadataCapabilityAdapter(qobuzLegacy);
        var qobuzPlaylists = new QobuzPlaylistCapabilityAdapter(qobuzLegacy, qobuzMetadata);
        var apple = new AppleMusicKitPlaylistCapabilityAdapter(
            new HttpClient(new Mock<HttpMessageHandler>().Object),
            new Mock<IProviderAccountSecretAccessor>(MockBehavior.Strict).Object);
        var spotify = new SpotifyPlaylistCapabilityAdapter(
            new HttpClient(new Mock<HttpMessageHandler>().Object),
            new Mock<IProviderAccountSecretAccessor>(MockBehavior.Strict).Object);
        var spotifyLyrics = Lyrics("spotify");
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
        var appleDownloadLyrics = Lyrics("apple-download");
        var appleDownloadMetadata = new AppleDownloadMetadataCapabilityAdapter(
            new Mock<IConcreteMetadataService>(MockBehavior.Strict).Object);
        var registry = new ProviderRegistry(
        [
            DeezerMetadataCapabilityAdapter.CreateRegistration(
                deezer, deezerPlaylists, deezerDownload, deezerStreaming),
            QobuzDownloadCapabilityAdapter.CreateRegistration(
                qobuzDownload, qobuzStreaming, qobuzMetadata, qobuzPlaylists),
            AppleMusicKitPlaylistCapabilityAdapter.CreateRegistration(apple),
            SpotifyPlaylistCapabilityAdapter.CreateRegistration(spotify, spotifyLyrics),
            AppleDownloadCapabilityAdapter.CreateRegistration(
                appleDownload, appleDownloadLyrics, appleDownloadStreaming, appleDownloadMetadata),
            BuiltInLyricsCapabilityRegistration.CreateRegistration("lrclib", "LRCLib", lrclib)
        ]);

        string[] expected =
        [
            "apple-download",
            "apple-musickit",
            "deezer",
            "lrclib",
            "qobuz",
            "spotify"
        ];
        Assert.Equal(expected, registry.Providers.Select(item => item.Id));
        Assert.DoesNotContain(registry.Providers,
            item => item.Id is "lastfm" or "listenbrainz" or "musicbrainz");
        Assert.Equal(
            ["apple-download", "deezer", "qobuz"],
            registry.FindByCapability(ProviderCapabilityKind.Metadata)
                .Select(item => item.Id));
        var deezerDescriptor = registry.GetRequired("deezer");
        Assert.Equal(
            [
                ProviderCapabilityKind.Metadata,
                ProviderCapabilityKind.Streaming,
                ProviderCapabilityKind.Download,
                ProviderCapabilityKind.Playlist
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
        Assert.Equal(
            ProviderCapabilitySupportState.Supported,
            deezerDescriptor.Capabilities.Single(item =>
                item.Capability == ProviderCapabilityKind.Playlist).SupportState);
        Assert.Same(deezerPlaylists, registry.GetRequiredCapability<IProviderPlaylistCapability>(
            "deezer", ProviderCapabilityKind.Playlist));
        Assert.Same(qobuzPlaylists, registry.GetRequiredCapability<IProviderPlaylistCapability>(
            "qobuz", ProviderCapabilityKind.Playlist));
        var playlistProviders = registry.FindByCapability(ProviderCapabilityKind.Playlist).ToArray();
        Assert.Equal(
            ["apple-musickit", "deezer", "qobuz", "spotify"],
            playlistProviders.Select(provider => provider.Id));
        Assert.All(playlistProviders, provider =>
        {
            var capability = provider.Capabilities.Single(item =>
                item.Capability == ProviderCapabilityKind.Playlist);
            Assert.True(capability.HasUsableImplementation);
            Assert.Contains("searchPlaylists", capability.Hooks);
            Assert.Contains("getPlaylistTracks", capability.Hooks);
        });
        Assert.Same(qobuzDownload, registry.GetRequiredCapability<IProviderDownloadCapability>(
            "qobuz", ProviderCapabilityKind.Download));
        Assert.Same(qobuzStreaming, registry.GetRequiredCapability<IProviderStreamingCapability>(
            "qobuz", ProviderCapabilityKind.Streaming));
        Assert.Same(qobuzMetadata, registry.GetRequiredCapability<IProviderMetadataCapability>(
            "qobuz", ProviderCapabilityKind.Metadata));
        Assert.Same(appleDownloadStreaming,
            registry.GetRequiredCapability<IProviderStreamingCapability>(
                "apple-download", ProviderCapabilityKind.Streaming));
        Assert.Same(appleDownloadMetadata,
            registry.GetRequiredCapability<IProviderMetadataCapability>(
                "apple-download", ProviderCapabilityKind.Metadata));
        Assert.Equal(
            ["apple-download", "lrclib", "spotify"],
            registry.FindByCapability(ProviderCapabilityKind.Lyrics).Select(item => item.Id));
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
