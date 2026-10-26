using System.Net;
using System.Text.Json;
using allstarr.Services.Jellyfin;
using Microsoft.AspNetCore.WebUtilities;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Theory]
    [InlineData("/Items", "/Items")]
    [InlineData("/Users/user-1/Items", "/Users/user-1/Items")]
    public async Task JellyfinUntypedItemsRoot_PreservesMusicAndNativePlaylistFolders(
        string requestPath,
        string expectedUpstreamPath)
    {
        var observed = new List<string>();
        using var factory = new ProtocolFactory("Jellyfin", request =>
        {
            if (request.RequestUri!.AbsolutePath == "/Users/Me")
                return Json(200, "{\"Id\":\"user-1\"}");

            observed.Add(request.RequestUri.PathAndQuery);
            Assert.DoesNotContain("IncludeItemTypes", request.RequestUri.Query, StringComparison.OrdinalIgnoreCase);
            return Json(200, NativePlaylistRootResponse());
        });
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, requestPath);
        request.Headers.TryAddWithoutValidation("Authorization", "MediaBrowser fixture");

        using var response = await client.SendAsync(request);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expectedUpstreamPath, Assert.Single(observed));
        var items = body.RootElement.GetProperty("Items").EnumerateArray().ToArray();
        Assert.Equal(["manual-playlists", "music-root", "playlists-folder"],
            items.Select(item => item.GetProperty("Id").GetString()));
        Assert.Equal(["ManualPlaylistsFolder", "CollectionFolder", "PlaylistsFolder"],
            items.Select(item => item.GetProperty("Type").GetString()));
        using var original = JsonDocument.Parse(NativePlaylistRootResponse());
        var nativeItems = original.RootElement.GetProperty("Items").EnumerateArray().ToArray();
        Assert.True(JsonElement.DeepEquals(nativeItems[1], items[0]));
        Assert.True(JsonElement.DeepEquals(nativeItems[2], items[1]));
        Assert.True(JsonElement.DeepEquals(nativeItems[4], items[2]));
        Assert.Equal(3, body.RootElement.GetProperty("TotalRecordCount").GetInt32());
        Assert.True(body.RootElement.GetProperty("FutureEnvelope").GetProperty("kept").GetBoolean());
        Assert.Equal("manual", items[0].GetProperty("Future").GetProperty("source").GetString());
        Assert.True(items[1].GetProperty("Future").GetProperty("kept").GetBoolean());
        Assert.Equal("playlists", items[2].GetProperty("Future").GetProperty("source").GetString());
    }

    [Fact]
    public async Task JellyfinUntypedItemsRoot_PaginatesMusicAndPlaylistFoldersAfterFiltering()
    {
        var observed = new List<string>();
        using var factory = new ProtocolFactory("Jellyfin", request =>
        {
            if (request.RequestUri!.AbsolutePath == "/Users/Me")
                return Json(200, "{\"Id\":\"user-1\"}");

            observed.Add(request.RequestUri.PathAndQuery);
            var query = QueryHelpers.ParseQuery(request.RequestUri.Query);
            Assert.False(query.ContainsKey("StartIndex"));
            Assert.False(query.ContainsKey("Limit"));
            Assert.False(query.ContainsKey("IncludeItemTypes"));
            Assert.Equal("Name,CollectionType", query["Fields"].ToString());
            return Json(200,
                "{\"Items\":[" +
                "{\"Id\":\"movie-root\",\"Type\":\"CollectionFolder\",\"CollectionType\":\"movies\"}," +
                "{\"Id\":\"manual-playlists\",\"Type\":\"ManualPlaylistsFolder\",\"Future\":{\"source\":\"manual\"}}," +
                "{\"Id\":\"music-root\",\"Type\":\"CollectionFolder\",\"CollectionType\":\"music\",\"Future\":{\"source\":\"music\"}}," +
                "{\"Id\":\"tv-root\",\"Type\":\"CollectionFolder\",\"CollectionType\":\"tvshows\"}," +
                "{\"Id\":\"playlists-folder\",\"Type\":\"PlaylistsFolder\",\"Future\":{\"source\":\"playlists\"}}," +
                "{\"Id\":\"boxset-root\",\"Type\":\"CollectionFolder\",\"CollectionType\":\"boxsets\"}]," +
                "\"TotalRecordCount\":6,\"StartIndex\":0}");
        });
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/Items?Recursive=false&StartIndex=1&Limit=2&Fields=Name%2CCollectionType");
        request.Headers.TryAddWithoutValidation("Authorization", "MediaBrowser fixture");

        using var response = await client.SendAsync(request);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(observed);
        Assert.Equal(3, body.RootElement.GetProperty("TotalRecordCount").GetInt32());
        Assert.Equal(1, body.RootElement.GetProperty("StartIndex").GetInt32());
        Assert.Equal(["music-root", "playlists-folder"],
            body.RootElement.GetProperty("Items").EnumerateArray()
                .Select(item => item.GetProperty("Id").GetString()));
    }

    [Fact]
    public async Task JellyfinUserViews_StillExposesOnlyMusicCollectionFolders()
    {
        var observed = new List<string>();
        using var factory = new ProtocolFactory("Jellyfin", request =>
        {
            if (request.RequestUri!.AbsolutePath == "/Users/Me")
                return Json(200, "{\"Id\":\"user-1\"}");

            observed.Add(request.RequestUri.PathAndQuery);
            Assert.Equal("/UserViews", request.RequestUri.AbsolutePath);
            return Json(200,
                "{\"Items\":[" +
                "{\"Id\":\"music-root\",\"Type\":\"CollectionFolder\",\"CollectionType\":\"music\",\"Future\":{\"kept\":true}}," +
                "{\"Id\":\"manual-playlists\",\"Type\":\"ManualPlaylistsFolder\",\"CollectionType\":\"playlists\"}," +
                "{\"Id\":\"playlists-folder\",\"Type\":\"PlaylistsFolder\"}," +
                "{\"Id\":\"movie-root\",\"Type\":\"CollectionFolder\",\"CollectionType\":\"movies\"}]," +
                "\"TotalRecordCount\":4}");
        });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/UserViews?UserId=user-1&api_key=fixture-key");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/UserViews?UserId=user-1&ApiKey=fixture-key", Assert.Single(observed));
        var item = Assert.Single(body.RootElement.GetProperty("Items").EnumerateArray());
        Assert.Equal("music-root", item.GetProperty("Id").GetString());
        Assert.True(item.GetProperty("Future").GetProperty("kept").GetBoolean());
        Assert.Equal(1, body.RootElement.GetProperty("TotalRecordCount").GetInt32());
    }

    [Theory]
    [InlineData("/Items?ParentId=manual-playlists&Recursive=false", false)]
    [InlineData("/Users/user-1/Items?ParentId=manual-playlists&Recursive=false&IncludeItemTypes=Playlist", true)]
    public async Task JellyfinPlaylistFolderParentBrowse_PreservesOpaqueParentAndMusicConstraint(
        string requestPath,
        bool typed)
    {
        var observed = new List<string>();
        using var factory = new ProtocolFactory("Jellyfin", request =>
        {
            if (request.RequestUri!.AbsolutePath == "/Users/Me")
                return Json(200, "{\"Id\":\"user-1\"}");

            observed.Add(request.RequestUri.PathAndQuery);
            var query = QueryHelpers.ParseQuery(request.RequestUri.Query);
            Assert.Equal("manual-playlists", query["ParentId"].ToString());
            Assert.Equal("false", query["Recursive"].ToString());
            var requestedTypes = query["IncludeItemTypes"].ToString();
            Assert.Equal(
                typed ? "Playlist" : JellyfinMusicEndpointPolicy.DefaultMusicItemTypes,
                requestedTypes);
            Assert.DoesNotContain("Movie", requestedTypes, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Series", requestedTypes, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Episode", requestedTypes, StringComparison.OrdinalIgnoreCase);
            return Json(200,
                "{\"Items\":[{\"Id\":\"native-playlist\",\"Name\":\"Native Playlist\",\"Type\":\"Playlist\",\"Future\":{\"keep\":true}}]," +
                "\"TotalRecordCount\":1,\"StartIndex\":0,\"FutureEnvelope\":{\"keep\":true}}");
        });
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, requestPath);
        request.Headers.TryAddWithoutValidation("Authorization", "MediaBrowser fixture");

        using var response = await client.SendAsync(request);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(observed);
        var item = Assert.Single(body.RootElement.GetProperty("Items").EnumerateArray());
        Assert.Equal("native-playlist", item.GetProperty("Id").GetString());
        Assert.Equal("Playlist", item.GetProperty("Type").GetString());
        Assert.True(item.GetProperty("Future").GetProperty("keep").GetBoolean());
        Assert.True(body.RootElement.GetProperty("FutureEnvelope").GetProperty("keep").GetBoolean());
    }

    [Fact]
    public async Task JellyfinNativePlaylistItems_FilterMixedVideoRowsWithoutReshapingMusicRows()
    {
        const string playlistId = "0123456789abcdef0123456789abcdef";
        var observed = new List<string>();
        using var factory = new ProtocolFactory("Jellyfin", request =>
        {
            if (request.RequestUri!.AbsolutePath == "/Users/Me")
                return Json(200, "{\"Id\":\"user-1\"}");

            if (request.RequestUri.AbsolutePath == "/Items")
            {
                var query = QueryHelpers.ParseQuery(request.RequestUri.Query);
                Assert.Equal(playlistId, query["ids"].ToString());
                return ItemLookup($$"""{"Id":"{{playlistId}}","Type":"Playlist"}""");
            }

            observed.Add(request.RequestUri.PathAndQuery);
            Assert.Equal($"/Playlists/{playlistId}/Items", request.RequestUri.AbsolutePath);
            return Json(200,
                "{\"Items\":[" +
                "{\"Id\":\"audio-1\",\"Type\":\"Audio\",\"Future\":{\"kind\":\"audio\"}}," +
                "{\"Id\":\"playlist-2\",\"Type\":\"Playlist\",\"Future\":{\"kind\":\"playlist\"}}," +
                "{\"Id\":\"movie-1\",\"Type\":\"Movie\"}," +
                "{\"Id\":\"episode-1\",\"Type\":\"Episode\"}]," +
                "\"TotalRecordCount\":4,\"StartIndex\":0,\"FutureEnvelope\":{\"keep\":true}}");
        });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/Playlists/{playlistId}/Items?Limit=20&api_key=fixture-key");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, body.RootElement.GetProperty("TotalRecordCount").GetInt32());
        Assert.Equal(["audio-1", "playlist-2"],
            body.RootElement.GetProperty("Items").EnumerateArray()
                .Select(item => item.GetProperty("Id").GetString()));
        var items = body.RootElement.GetProperty("Items").EnumerateArray().ToArray();
        Assert.Equal("audio", items[0].GetProperty("Future").GetProperty("kind").GetString());
        Assert.Equal("playlist", items[1].GetProperty("Future").GetProperty("kind").GetString());
        Assert.True(body.RootElement.GetProperty("FutureEnvelope").GetProperty("keep").GetBoolean());
        Assert.Contains(observed, path => path.StartsWith($"/Playlists/{playlistId}/Items?", StringComparison.Ordinal));
    }

    private static string NativePlaylistRootResponse() =>
        "{\"Items\":[" +
        "{\"Id\":\"movie-root\",\"Name\":\"Movies\",\"Type\":\"CollectionFolder\",\"CollectionType\":\"movies\"}," +
        "{\"Id\":\"manual-playlists\",\"Name\":\"Playlists\",\"Type\":\"ManualPlaylistsFolder\",\"CollectionType\":\"playlists\",\"Future\":{\"source\":\"manual\"}}," +
        "{\"Id\":\"music-root\",\"Name\":\"Music\",\"Type\":\"CollectionFolder\",\"CollectionType\":\"music\",\"Future\":{\"kept\":true}}," +
        "{\"Id\":\"tv-root\",\"Name\":\"TV\",\"Type\":\"CollectionFolder\",\"CollectionType\":\"tvshows\"}," +
        "{\"Id\":\"playlists-folder\",\"Name\":\"All Playlists\",\"Type\":\"PlaylistsFolder\",\"Future\":{\"source\":\"playlists\"}}]," +
        "\"TotalRecordCount\":5,\"StartIndex\":0,\"FutureEnvelope\":{\"kept\":true}}";
}
