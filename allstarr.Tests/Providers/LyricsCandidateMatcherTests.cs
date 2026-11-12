using allstarr.Core.Providers.Lyrics;

namespace allstarr.Tests;

public sealed class LyricsCandidateMatcherTests
{
    [Theory]
    [InlineData("Part 2", "Part 3", false)]
    [InlineData("Song 1", "Song 2", false)]
    [InlineData("22", "23", false)]
    [InlineData("Song", "Song 1", false)]
    [InlineData("Part 2", "Part 2", true)]
    [InlineData("Artist - Title", "Title", true)]
    [InlineData("Title - Album Version", "Title", true)]
    public void TitlesKeepNumbersAndSupportDashReadings(string requested, string candidate, bool expected)
    {
        Assert.Equal(expected, LyricsCandidateMatcher.Matches(requested, ["Artist"], null, candidate, "Artist", null));
    }

    [Theory]
    [InlineData("Song (Instrumental)", "Album")]
    [InlineData("Song", "Karaoke")]
    [InlineData("Song", "Backing Track")]
    public void VocalRequestsRejectInstrumentalCandidates(string title, string album)
    {
        Assert.False(LyricsCandidateMatcher.Matches("Song", ["Artist"], null, title, "Artist", album));
        Assert.True(LyricsCandidateMatcher.Matches(title, ["Artist"], album, title, "Artist", album));
    }

    [Theory]
    [InlineData("abcde", "abcdf", "artist", "artist", true)]
    [InlineData("abcde", "abcfg", "artist", "artist", false)]
    [InlineData("Title", "Title", "abcde", "abcdf", true)]
    [InlineData("Title", "Title", "abcde", "abcfg", false)]
    [InlineData("Café", "CAFE!", "Beyoncé", "Beyonce", true)]
    [InlineData("Title", "Other - Title", "Artist", "Artist", false)]
    public void TitleAndArtistEachNeedEightyPercent(string requestedTitle, string title, string requestedArtist, string artist, bool expected)
    {
        Assert.Equal(expected, LyricsCandidateMatcher.Matches(requestedTitle, [requestedArtist], null, title, artist, null));
    }
}
