using allstarr.Core.Providers.Spotify;

namespace allstarr.Tests;

public sealed class SpotifyTrackReferenceTests
{
    [Theory]
    [InlineData("AbCdEfGhIjKlMnOpQrStUv")]
    [InlineData("spotify:track:AbCdEfGhIjKlMnOpQrStUv")]
    [InlineData("https://open.spotify.com/track/AbCdEfGhIjKlMnOpQrStUv?si=fixture")]
    [InlineData("https://open.spotify.com/intl-en/track/AbCdEfGhIjKlMnOpQrStUv")]
    public void SpicyTrackReferencePreservesExactCase(string value)
    {
        Assert.True(SpotifyTrackReference.TryNormalize(value, out var id));
        Assert.Equal("AbCdEfGhIjKlMnOpQrStUv", id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("short")]
    [InlineData("spotify:album:AbCdEfGhIjKlMnOpQrStUv")]
    [InlineData("http://open.spotify.com/track/AbCdEfGhIjKlMnOpQrStUv")]
    [InlineData("https://open.spotify.com.evil.test/track/AbCdEfGhIjKlMnOpQrStUv")]
    [InlineData("https://user@open.spotify.com/track/AbCdEfGhIjKlMnOpQrStUv")]
    [InlineData("https://open.spotify.com:444/track/AbCdEfGhIjKlMnOpQrStUv")]
    [InlineData("https://open.spotify.com/album/AbCdEfGhIjKlMnOpQrStUv")]
    [InlineData("https://open.spotify.com/track/AbCdEfGhIjKlMnOpQrStUv/more")]
    [InlineData("https://open.spotify.com/track/AbCdEfGhIjKlMnOpQrStU%76")]
    [InlineData("АbCdEfGhIjKlMnOpQrStUv")]
    public void SpicyTrackReferenceRejectsOtherResources(string? value) =>
        Assert.False(SpotifyTrackReference.TryNormalize(value, out _));
}
