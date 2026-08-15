using allstarr.Controllers;

namespace allstarr.Tests;

public class JellyfinControllerSearchLimitTests
{
    [Theory]
    [InlineData(null, 20, true, 20, 20, 20)]
    [InlineData("MusicAlbum", 20, true, 0, 20, 0)]
    [InlineData("Audio", 20, true, 20, 0, 0)]
    [InlineData("MusicArtist", 20, true, 0, 0, 20)]
    [InlineData("Playlist", 20, true, 0, 20, 0)]
    [InlineData("Playlist", 20, false, 0, 0, 0)]
    [InlineData("Audio,MusicArtist", 15, true, 15, 0, 15)]
    [InlineData("BoxSet", 10, true, 0, 0, 0)]
    public void GetExternalSearchLimits_UsesRequestedItemTypes(
        string? includeItemTypes,
        int limit,
        bool includePlaylistsAsAlbums,
        int expectedSongLimit,
        int expectedAlbumLimit,
        int expectedArtistLimit)
    {
        var requestedTypes = string.IsNullOrWhiteSpace(includeItemTypes)
            ? null
            : includeItemTypes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var result = JellyfinController.GetExternalSearchLimits(
            requestedTypes, limit, includePlaylistsAsAlbums);

        Assert.Equal(expectedSongLimit, result.SongLimit);
        Assert.Equal(expectedAlbumLimit, result.AlbumLimit);
        Assert.Equal(expectedArtistLimit, result.ArtistLimit);
    }

    [Theory]
    [InlineData(0, 20, 20)]
    [InlineData(5, 5, 20)]
    [InlineData(20, 20, 40)]
    [InlineData(480, 50, 500)]
    [InlineData(-1, 20, 20)]
    public void IntegratedSearchFetchLimit_LoadsTheMergedPagePrefixOnce(
        int startIndex,
        int limit,
        int expected)
    {
        Assert.Equal(expected, JellyfinController.GetIntegratedSearchFetchLimit(startIndex, limit));
    }

    [Theory]
    [InlineData(22, 20, 0, 20, 22, 0, 0)]
    [InlineData(22, 2, 20, 20, 22, 0, 18)]
    [InlineData(22, 0, 40, 20, 22, 18, 20)]
    [InlineData(0, 0, 0, 20, 0, 0, 20)]
    [InlineData(24, 2, 0, 200, 2, 0, 198)]
    public void VirtualPlaylistPage_FollowsBackendRowsWithoutBreakingPaging(
        int backendTotal,
        int backendReturned,
        int startIndex,
        int limit,
        int expectedBackendTotal,
        int expectedStart,
        int expectedTake)
    {
        var result = JellyfinController.GetVirtualPlaylistPage(
            backendTotal, backendReturned, startIndex, limit);

        Assert.Equal(expectedBackendTotal, result.BackendTotal);
        Assert.Equal(expectedStart, result.Start);
        Assert.Equal(expectedTake, result.Take);
    }
}
