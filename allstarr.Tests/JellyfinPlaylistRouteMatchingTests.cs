using System.Reflection;
using allstarr.Controllers;

namespace allstarr.Tests;

public class JellyfinPlaylistRouteMatchingTests
{
    [Theory]
    [InlineData("playlists/abc123/items", "abc123")]
    [InlineData("Playlists/abc123/Items", "abc123")]
    [InlineData("/playlists/abc123/items/", "abc123")]
    public void GetExactPlaylistItemsRequestId_ExactPlaylistItemsRoute_ReturnsPlaylistId(string path, string expectedPlaylistId)
    {
        var playlistId = InvokePrivateStatic<string?>("GetExactPlaylistItemsRequestId", path);

        Assert.Equal(expectedPlaylistId, playlistId);
    }

    [Theory]
    [InlineData("playlists/abc123/items/extra")]
    [InlineData("users/user-1/playlists/abc123/items")]
    [InlineData("items/abc123")]
    [InlineData("playlists")]
    public void GetExactPlaylistItemsRequestId_NonExactRoute_ReturnsNull(string path)
    {
        var playlistId = InvokePrivateStatic<string?>("GetExactPlaylistItemsRequestId", path);

        Assert.Null(playlistId);
    }

    private static T InvokePrivateStatic<T>(string methodName, params object?[] args)
    {
        var method = typeof(JellyfinController).GetMethod(
            methodName,
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        var result = method!.Invoke(null, args);
        return (T)result!;
    }
}
