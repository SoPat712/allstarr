using allstarr.Services.Spotify;
using allstarr.Models.Domain;

namespace allstarr.Tests;

public class ExternalTrackPlaybackPolicyTests
{
    [Theory]
    [InlineData("tidal")]
    public void MetadataOnlyProvidersCannotBecomePlaybackMappings(string provider)
    {
        Assert.False(ExternalTrackPlaybackPolicy.CanUseForPlayback(provider));
    }

    [Theory]
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

    [Fact]
    public void LocalSongsRemainPlayableRegardlessOfExternalProviderMetadata()
    {
        var song = new Song
        {
            Id = "local-track-id",
            IsLocal = true,
            ExternalProvider = "tidal"
        };

        Assert.True(ExternalTrackPlaybackPolicy.CanUseForPlayback(song));
    }

    [Fact]
    public void CachedMetadataOnlySongsAreRejectedWhenTheyAreNotLocal()
    {
        var song = new Song
        {
            Id = "ext-tidal-song-25",
            IsLocal = false,
            ExternalProvider = "tidal",
            ExternalId = "25"
        };

        Assert.False(ExternalTrackPlaybackPolicy.CanUseForPlayback(song));
    }
}
