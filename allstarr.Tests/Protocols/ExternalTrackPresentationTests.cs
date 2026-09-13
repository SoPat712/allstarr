using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Services.Jellyfin;
using allstarr.Services.Subsonic;

namespace allstarr.Tests;

public sealed class ExternalTrackPresentationTests
{
    [Theory]
    [InlineData(true, 1, "Original title")]
    [InlineData(true, 0, "Original title")]
    [InlineData(false, 1, "Original title [A]/[E]")]
    [InlineData(false, 0, "Original title [A]")]
    [InlineData(false, 2, "Original title [A]")]
    public void TitlesAreConsistentWithoutChangingTheDomainSong(bool local, int explicitness, string expected)
    {
        var song = new Song
        {
            Id = local ? "native" : "ext-deezer-song-123",
            Title = "Original title",
            IsLocal = local,
            ExternalProvider = local ? null : "deezer",
            ExternalId = local ? null : "123",
            ExplicitContentLyrics = explicitness
        };
        var subsonic = new SubsonicResponseBuilder();
        Assert.Equal(expected, ExternalTrackPresentation.Title(song));
        Assert.Equal(expected, subsonic.ConvertSongToJson(song)["title"]);
        Assert.Equal(expected, subsonic.ConvertSongToXml(song, "http://subsonic.org/restapi").Attribute("title")!.Value);
        Assert.Equal(expected, new JellyfinResponseBuilder().ConvertSongToJellyfinItem(song)["Name"]);
        Assert.Equal("Original title", song.Title);
    }
}
