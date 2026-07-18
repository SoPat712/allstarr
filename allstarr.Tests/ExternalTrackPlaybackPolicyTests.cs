using allstarr.Services.Spotify;

namespace allstarr.Tests;

public class ExternalTrackPlaybackPolicyTests
{
    [Theory]
    [InlineData("squidwtf")]
    [InlineData("SquidWTF")]
    [InlineData("squid-wtf")]
    [InlineData("squid_wtf")]
    [InlineData("tidal")]
    public void MetadataOnlyProvidersCannotBecomePlaybackMappings(string provider)
    {
        Assert.False(ExternalTrackPlaybackPolicy.CanUseForPlayback(provider));
    }

    [Theory]
    [InlineData("ext-squidwtf-song-25")]
    [InlineData("ext-tidal-song-25")]
    public void LegacyTrackIdsCannotHideMetadataOnlyProvider(string trackId)
    {
        Assert.False(ExternalTrackPlaybackPolicy.CanUseForPlayback("unknown", trackId));
    }

    [Theory]
    [InlineData("deezer")]
    [InlineData("qobuz")]
    [InlineData("applemusic")]
    [InlineData("custom-extension")]
    public void AudioProvidersCanBecomePlaybackMappings(string provider)
    {
        Assert.True(ExternalTrackPlaybackPolicy.CanUseForPlayback(provider));
    }
}
