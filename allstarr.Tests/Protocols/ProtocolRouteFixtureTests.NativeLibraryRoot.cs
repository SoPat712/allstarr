using System.Net;
using System.Text.Json;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Services.Jellyfin;
using Microsoft.AspNetCore.WebUtilities;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Theory]
    [InlineData("/Items", "/Items")]
    [InlineData("/Items?Recursive=false&Fields=Name%2CCollectionType", "/Items?Recursive=false&Fields=Name%2CCollectionType")]
    [InlineData("/Users/user-1/Items", "/Users/user-1/Items")]
    [InlineData("/Users/user-1/Items?Recursive=false&Fields=Name%2CCollectionType", "/Users/user-1/Items?Recursive=false&Fields=Name%2CCollectionType")]
    public async Task JellyfinUntypedItemsRootReturnsOnlyNativeMusicCollectionsAndPreservesQuery(
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
            return Json(200, NativeLibraryRootResponse());
        });
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, requestPath);
        request.Headers.TryAddWithoutValidation("Authorization", "MediaBrowser fixture");

        using var response = await client.SendAsync(request);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expectedUpstreamPath, Assert.Single(observed));
        var items = body.RootElement.GetProperty("Items").EnumerateArray().ToArray();
        var music = Assert.Single(items);
        Assert.Equal("music-root", music.GetProperty("Id").GetString());
        Assert.Equal("CollectionFolder", music.GetProperty("Type").GetString());
        Assert.Equal("music", music.GetProperty("CollectionType").GetString());
        Assert.True(music.GetProperty("Future").GetProperty("kept").GetBoolean());
        Assert.Equal(1, body.RootElement.GetProperty("TotalRecordCount").GetInt32());
        Assert.True(body.RootElement.GetProperty("FutureEnvelope").GetProperty("kept").GetBoolean());
    }

    [Theory]
    [InlineData(0, "music-root")]
    [InlineData(1, "second-music-root")]
    [InlineData(2, null)]
    public async Task JellyfinUntypedItemsRootPagesMusicLibrariesAfterFiltering(int startIndex, string? expectedId)
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
                "{\"Id\":\"music-root\",\"Type\":\"CollectionFolder\",\"CollectionType\":\"music\",\"Future\":true}," +
                "{\"Id\":\"second-music-root\",\"Type\":\"CollectionFolder\",\"CollectionType\":\"music\",\"Future\":true}]," +
                "\"TotalRecordCount\":3,\"StartIndex\":0}");
        });
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/Items?Recursive=false&StartIndex={startIndex}&Limit=1&Fields=Name%2CCollectionType");
        request.Headers.TryAddWithoutValidation("Authorization", "MediaBrowser fixture");

        using var response = await client.SendAsync(request);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(observed);
        Assert.Equal(2, body.RootElement.GetProperty("TotalRecordCount").GetInt32());
        Assert.Equal(startIndex, body.RootElement.GetProperty("StartIndex").GetInt32());
        var items = body.RootElement.GetProperty("Items").EnumerateArray();
        if (expectedId == null)
            Assert.Empty(items);
        else
        {
            var item = Assert.Single(items);
            Assert.Equal(expectedId, item.GetProperty("Id").GetString());
            Assert.True(item.GetProperty("Future").GetBoolean());
        }
    }

    [Fact]
    public async Task JellyfinUntypedItemsRootDoesNotReplaceNativeFoldersWithSavedMusicRows()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedFavoriteViewsAsync(storage, "jellyfin");
        var observed = new List<string>();
        using var factory = new ProtocolFactory("Jellyfin", request =>
        {
            if (request.RequestUri!.AbsolutePath == "/Users/Me")
                return Json(200, "{\"Id\":\"user-1\"}");

            observed.Add(request.RequestUri.PathAndQuery);
            Assert.DoesNotContain("IncludeItemTypes", request.RequestUri.Query, StringComparison.OrdinalIgnoreCase);
            return Json(200, NativeLibraryRootResponse());
        }, services => ConfigureFavoriteViews(
            services,
            storage,
            new Mock<IProtocolProviderGateway>(MockBehavior.Strict).Object));
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/Items?Recursive=false");
        request.Headers.TryAddWithoutValidation("Authorization", "MediaBrowser fixture");

        using var response = await client.SendAsync(request);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/Items?Recursive=false", Assert.Single(observed));
        Assert.Equal("music-root", Assert.Single(body.RootElement.GetProperty("Items").EnumerateArray())
            .GetProperty("Id").GetString());
        Assert.Equal(1, body.RootElement.GetProperty("TotalRecordCount").GetInt32());
    }

    [Theory]
    [InlineData("/Items?Recursive=true")]
    [InlineData("/Items?ParentId=library-1&Recursive=false")]
    public async Task JellyfinTypedOrParentBrowseDoesNotUseLibraryRootDiscovery(string requestPath)
    {
        var observed = new List<string>();
        using var factory = new ProtocolFactory("Jellyfin", request =>
        {
            if (request.RequestUri!.AbsolutePath == "/Users/Me")
                return Json(200, "{\"Id\":\"user-1\"}");

            observed.Add(request.RequestUri.PathAndQuery);
            var query = QueryHelpers.ParseQuery(request.RequestUri.Query);
            Assert.Equal(
                JellyfinMusicEndpointPolicy.DefaultMusicItemTypes,
                query["IncludeItemTypes"].ToString());
            return Json(200, "{\"Items\":[{\"Id\":\"native-audio\",\"Type\":\"Audio\",\"Future\":true}],\"TotalRecordCount\":1}");
        });
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, requestPath);
        request.Headers.TryAddWithoutValidation("Authorization", "MediaBrowser fixture");

        using var response = await client.SendAsync(request);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(observed);
        Assert.Equal("native-audio", body.RootElement.GetProperty("Items")[0].GetProperty("Id").GetString());
    }

    private static string NativeLibraryRootResponse() =>
        "{\"Items\":[" +
        "{\"Id\":\"music-root\",\"Name\":\"Music\",\"Type\":\"CollectionFolder\",\"CollectionType\":\"music\",\"Future\":{\"kept\":true}}," +
        "{\"Id\":\"movie-root\",\"Name\":\"Movies\",\"Type\":\"CollectionFolder\",\"CollectionType\":\"movies\"}," +
        "{\"Id\":\"tv-root\",\"Name\":\"TV\",\"Type\":\"CollectionFolder\",\"CollectionType\":\"tvshows\"}]," +
        "\"TotalRecordCount\":3,\"StartIndex\":0,\"FutureEnvelope\":{\"kept\":true}}";
}
