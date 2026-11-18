using System.Net;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task JellyfinHeartDuringPlayback_IsSentAgainAfterTheStopSavesThePlay(bool duringPlayback)
    {
        const string userId = "fixture-user";
        const string deviceId = "fixture-device";
        var favorite = false;
        bool? favoriteAtStart = null;
        var favoriteWrites = 0;
        using var factory = new ProtocolFactory(
            "Jellyfin",
            request =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (path == "/Users/Me")
                    return Json(200, $$"""{"Id":"{{userId}}","Name":"Fixture User"}""");
                if (path == "/Items/native-song")
                    return Json(200, """{"Id":"native-song","Type":"Audio","Name":"Native song"}""");
                if (path == "/Items")
                    return Json(200, """{"Items":[{"Id":"native-song","Type":"Audio","Name":"Native song"}],"TotalRecordCount":1}""");
                if (path == "/UserFavoriteItems/native-song")
                {
                    favoriteWrites++;
                    favorite = request.Method == HttpMethod.Post;
                    return Json(200, $$"""{"IsFavorite":{{(favorite ? "true" : "false")}}}""");
                }
                // Like Jellyfin, the stop saves the play with the favorite flag read when the song started.
                if (path == "/Sessions/Playing")
                {
                    favoriteAtStart = favorite;
                    return Json(204, "{}");
                }
                if (path == "/Sessions/Playing/Stopped")
                {
                    favorite = favoriteAtStart ?? favorite;
                    return Json(204, "{}");
                }
                if (path.StartsWith("/Sessions/", StringComparison.Ordinal)) return Json(204, "{}");
                return Json(404, "{}");
            });
        await PreparePlaybackSessionAsync(factory, userId, deviceId);
        using var client = factory.CreateClient();
        async Task SendAsync(string path, string body)
        {
            using var response = await client.SendAsync(PlaybackRequest(path, body, deviceId));
            Assert.True(response.IsSuccessStatusCode, $"{path} answered {(int)response.StatusCode}");
        }

        if (!duringPlayback) await SendAsync("/UserFavoriteItems/native-song", "{}");
        await SendAsync("/Sessions/Playing", """{"ItemId":"native-song","PositionTicks":0}""");
        if (duringPlayback) await SendAsync("/UserFavoriteItems/native-song", "{}");
        await SendAsync("/Sessions/Playing/Stopped", """{"ItemId":"native-song","PositionTicks":300000000}""");

        Assert.True(favorite);
        Assert.Equal(duringPlayback ? 2 : 1, favoriteWrites);
    }
}
