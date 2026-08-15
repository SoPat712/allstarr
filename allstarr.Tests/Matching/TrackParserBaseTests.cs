using allstarr.Services.Common;
using Xunit;

namespace allstarr.Tests;

public class TrackParserBaseTests
{
    [Fact]
    public void TrackParserBaseHelpers_ShouldBuildConsistentIdsAndYears()
    {
        Assert.Equal("ext-deezer-song-123", TrackParserProbe.SongId("deezer", "123"));
        Assert.Equal("ext-qobuz-album-555", TrackParserProbe.AlbumId("qobuz", "555"));
        Assert.Equal("ext-soundcloud-artist-77", TrackParserProbe.ArtistId("soundcloud", "77"));

        Assert.Equal(2024, TrackParserProbe.Year("2024-11-03"));
        Assert.Null(TrackParserProbe.Year(""));
        Assert.Null(TrackParserProbe.Year("abc"));
    }

    private sealed class TrackParserProbe : TrackParserBase
    {
        public static string SongId(string provider, string externalId) => BuildExternalSongId(provider, externalId);
        public static string AlbumId(string provider, string externalId) => BuildExternalAlbumId(provider, externalId);
        public static string ArtistId(string provider, string externalId) => BuildExternalArtistId(provider, externalId);
        public static int? Year(string? dateString) => ParseYearFromDateString(dateString);
    }
}
