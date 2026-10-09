using allstarr.Services.Common;

namespace allstarr.Tests;

public sealed class OdesliServiceTests
{
    [Theory]
    [InlineData("qobuz", "qobuz", "42", "https://open.qobuz.com/track/42")]
    [InlineData("spotiflac-deezer", "deezer", "3135556", "https://www.deezer.com/track/3135556")]
    [InlineData("spotiflac-apple-music", "apple-music", "2037093408", "https://music.apple.com/us/song/2037093408")]
    public void OdesliTrackUrl_UsesProviderTrackRoutes(string provider, string normalized, string trackId, string expected)
    {
        Assert.Equal(normalized, OdesliService.NormalizeProviderId(provider));
        Assert.Equal(expected, OdesliService.BuildTrackUrl(provider, trackId));
    }

}
