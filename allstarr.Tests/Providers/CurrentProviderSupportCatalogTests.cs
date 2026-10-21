using allstarr.Services.Common;

namespace allstarr.Tests;

public sealed class CurrentProviderSupportCatalogTests
{
    [Fact]
    public void Matrix_DeclaresEveryCapabilityForEveryProvider()
    {
        var expectedCapabilities = new HashSet<string>(StringComparer.Ordinal)
        {
            "metadata", "streaming", "download", "playlist", "lyrics",
            "health", "scrobbling", "enrichment", "recommendation"
        };

        Assert.NotEmpty(CurrentProviderSupportCatalog.All);
        Assert.All(CurrentProviderSupportCatalog.All, provider =>
        {
            Assert.Equal(expectedCapabilities, provider.Capabilities.Select(item => item.Id).ToHashSet());
            Assert.All(provider.Capabilities, capability =>
            {
                Assert.False(string.IsNullOrWhiteSpace(capability.ProtocolLimit));
                Assert.False(string.IsNullOrWhiteSpace(capability.TestCoverage));
            });
        });
    }

    [Fact]
    public void Matrix_AdvertisesTypedPlaylistSourcesAndKeepsMissingLanesUnavailable()
    {
        AssertState("apple-musickit", "lyrics", CurrentProviderSupportCatalog.Supported);
        AssertState("apple-musickit", "streaming", CurrentProviderSupportCatalog.Supported);
        AssertState("apple-musickit", "playlist", CurrentProviderSupportCatalog.Supported);
        AssertState("apple-musickit", "metadata", CurrentProviderSupportCatalog.Supported);
        AssertState("spotify", "playlist", CurrentProviderSupportCatalog.Supported);
        AssertState("spotify", "lyrics", CurrentProviderSupportCatalog.Supported);
        AssertState("spotify", "metadata", CurrentProviderSupportCatalog.Unavailable);
        AssertState("qobuz", "metadata", CurrentProviderSupportCatalog.Supported);
        AssertState("qobuz", "streaming", CurrentProviderSupportCatalog.Supported);
        AssertState("qobuz", "download", CurrentProviderSupportCatalog.Supported);
        AssertState("monochrome", "streaming", CurrentProviderSupportCatalog.Supported);
        AssertState("monochrome", "health", CurrentProviderSupportCatalog.Supported);
        AssertState("monochrome", "metadata", CurrentProviderSupportCatalog.Unavailable);
        AssertState("monochrome", "download", CurrentProviderSupportCatalog.Unavailable);
        AssertState("deezer", "streaming", CurrentProviderSupportCatalog.Supported);
        AssertState("deezer", "download", CurrentProviderSupportCatalog.Supported);
        AssertState("musicbrainz", "metadata", CurrentProviderSupportCatalog.Unavailable);
        AssertState("musicbrainz", "recommendation", CurrentProviderSupportCatalog.Supported);
        AssertState("lastfm", "scrobbling", CurrentProviderSupportCatalog.Supported);
        AssertState("lastfm", "recommendation", CurrentProviderSupportCatalog.Supported);
        AssertState("listenbrainz", "scrobbling", CurrentProviderSupportCatalog.Supported);
        AssertState("listenbrainz", "recommendation", CurrentProviderSupportCatalog.Supported);
        AssertState("lrcred", "lyrics", CurrentProviderSupportCatalog.Supported);
        AssertState("unison", "lyrics", CurrentProviderSupportCatalog.Supported);
        AssertState("paxsenix", "lyrics", CurrentProviderSupportCatalog.Supported);
        AssertState("paxsenix", "health", CurrentProviderSupportCatalog.Supported);
        AssertState("genius", "lyrics", CurrentProviderSupportCatalog.Supported);
        AssertState("genius", "health", CurrentProviderSupportCatalog.Supported);
        AssertState("lrclib", "lyrics", CurrentProviderSupportCatalog.Supported);
        AssertState("lyricsplus", "lyrics", CurrentProviderSupportCatalog.Supported);
        AssertState("musixmatch", "lyrics", CurrentProviderSupportCatalog.Supported);
        AssertState("extensions", "metadata", CurrentProviderSupportCatalog.Supported);
        AssertState("extensions", "streaming", CurrentProviderSupportCatalog.Supported);
        AssertState("extensions", "download", CurrentProviderSupportCatalog.Supported);
        AssertState("extensions", "playlist", CurrentProviderSupportCatalog.Supported);
        AssertState("extensions", "lyrics", CurrentProviderSupportCatalog.Supported);
    }

    [Fact]
    public void Matrix_CombinesAppleCapabilitiesUnderThePersonalAccount()
    {
        Assert.DoesNotContain(CurrentProviderSupportCatalog.All, item => item.Id == "apple-download");
        var apple = Assert.Single(CurrentProviderSupportCatalog.All, item => item.Id == "apple-musickit");
        Assert.Equal("user", apple.AccountScope);
        Assert.Equal("apple-musickit", apple.RuntimeId);
        Assert.Equal("Apple Music", apple.Name);
        Assert.Contains("Media User Token", apple.Configuration);
        Assert.DoesNotContain("Developer", apple.Configuration);
    }

    private static void AssertState(string providerId, string capabilityId, string expected)
    {
        var provider = Assert.Single(CurrentProviderSupportCatalog.All, item => item.Id == providerId);
        var capability = Assert.Single(provider.Capabilities, item => item.Id == capabilityId);
        Assert.Equal(expected, capability.State);
    }
}
