using System.Reflection;
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

        var method = typeof(JellyfinController).GetMethod(
            "GetExternalSearchLimits",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);

        var result = ((int SongLimit, int AlbumLimit, int ArtistLimit))method!.Invoke(
            null,
            new object?[] { requestedTypes, limit, includePlaylistsAsAlbums })!;

        Assert.Equal(expectedSongLimit, result.SongLimit);
        Assert.Equal(expectedAlbumLimit, result.AlbumLimit);
        Assert.Equal(expectedArtistLimit, result.ArtistLimit);
    }
}
