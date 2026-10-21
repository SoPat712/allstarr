using System.Net;
using System.Text.Json;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Fact]
    public async Task JellyfinItemIds_DefaultLimitReturnsEveryRequestedSong()
    {
        var ids = Enumerable.Range(0, 25).Select(index => index.ToString()).ToArray();
        var gateway = ItemIdsGateway(ids);
        using var factory = ItemIdsFactory(gateway.Object, []);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/Items?api_key=fixture-key&Ids=" +
            string.Join(',', ids.Select(id => $"ext-deezer-song-{id}")));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(25, body.RootElement.GetProperty("Items").GetArrayLength());
        Assert.Equal(25, body.RootElement.GetProperty("TotalRecordCount").GetInt32());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JellyfinItemIds_ExternalOnlyResolvesWithoutBrowsing(bool userScoped)
    {
        var gateway = ItemIdsGateway("first", "second");
        var nativeRequests = new List<string>();
        using var factory = ItemIdsFactory(gateway.Object, nativeRequests);
        using var client = factory.CreateClient();
        var prefix = userScoped ? "/Users/user-1" : "";

        using var response = await client.GetAsync(
            $"{prefix}/Items?Ids=ext-deezer-song-second,ext-deezer-song-first&Fields=MediaSources&api_key=fixture-key");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var rows = body.RootElement.GetProperty("Items").EnumerateArray().ToArray();
        Assert.Equal(new[] { "ext-deezer-song-second", "ext-deezer-song-first" },
            rows.Select(row => row.GetProperty("Id").GetString()));
        Assert.Equal(2, body.RootElement.GetProperty("TotalRecordCount").GetInt32());
        Assert.All(rows, row => Assert.True(row.TryGetProperty("MediaSources", out _)));
        Assert.Empty(nativeRequests);
        gateway.VerifyAll();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JellyfinItemIds_MixedPreservesNativeRowsAndRequestedOrder(bool userScoped)
    {
        var gateway = ItemIdsGateway("first", "second");
        var nativeRequests = new List<string>();
        const string native = """{"Id":"native-one","Name":"Owned","Type":"Audio","Future":{"preserve":[1,true,"x"]},"UserData":{"PlayCount":7}}""";
        using var factory = ItemIdsFactory(gateway.Object, nativeRequests,
            $$"""{"Items":[{"Id":"native-two","Name":"Second owned","Type":"Audio"},{{native}}],"TotalRecordCount":2,"FutureEnvelope":true}""");
        using var client = factory.CreateClient();
        var prefix = userScoped ? "/Users/user-1" : "";

        using var response = await client.GetAsync(
            $"{prefix}/Items?Ids=ext-deezer-song-second,native-one,ext-deezer-song-first,native-two&Fields=MediaSources&api_key=fixture-key");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var rows = body.RootElement.GetProperty("Items").EnumerateArray().ToArray();
        Assert.Equal(new[] { "ext-deezer-song-second", "native-one", "ext-deezer-song-first", "native-two" },
            rows.Select(row => row.GetProperty("Id").GetString()));
        Assert.Equal(CanonicalJson(JsonDocument.Parse(native).RootElement), CanonicalJson(rows[1]));
        Assert.True(body.RootElement.GetProperty("FutureEnvelope").GetBoolean());
        Assert.Equal(4, body.RootElement.GetProperty("TotalRecordCount").GetInt32());
        var request = new Uri("http://fixture" + Assert.Single(nativeRequests));
        Assert.Equal($"{prefix}/Items", request.AbsolutePath);
        var query = QueryHelpers.ParseQuery(request.Query);
        Assert.Equal("native-one,native-two", query["Ids"].ToString());
        Assert.Equal("MediaSources", query["Fields"].ToString());
        gateway.VerifyAll();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JellyfinItemIds_AlbumsAndArtistsFollowRequestedOrder(bool userScoped)
    {
        var gateway = ItemIdsGateway("first");
        gateway.Setup(service => service.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "record"))
            .ReturnsAsync(new Album
            {
                Id = "ext-deezer-album-record",
                Title = "Fixture record",
                Artist = "Fixture artist",
                ExternalProvider = "deezer",
                ExternalId = "record",
                SongCount = 3
            });
        gateway.Setup(service => service.GetArtistAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "performer"))
            .ReturnsAsync(new Artist
            {
                Id = "ext-deezer-artist-performer",
                Name = "Fixture artist",
                ExternalProvider = "deezer",
                ExternalId = "performer",
                AlbumCount = 2
            });
        gateway.Setup(service => service.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "missing"))
            .ReturnsAsync((Album?)null);
        gateway.Setup(service => service.GetArtistAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "missing"))
            .ReturnsAsync((Artist?)null);
        var nativeRequests = new List<string>();
        const string native = """{"Id":"native-one","Name":"Owned","Type":"Audio","Future":{"keep":true}}""";
        using var factory = ItemIdsFactory(gateway.Object, nativeRequests,
            $$"""{"Items":[{{native}}],"TotalRecordCount":1}""");
        using var client = factory.CreateClient();
        var prefix = userScoped ? "/Users/user-1" : "";

        using var response = await client.GetAsync(
            $"{prefix}/Items?Ids=ext-deezer-artist-performer,ext-deezer-album-missing,native-one,ext-deezer-album-record,ext-deezer-song-first,ext-deezer-artist-missing&api_key=fixture-key");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var rows = body.RootElement.GetProperty("Items").EnumerateArray().ToArray();
        Assert.Equal(new[] { "ext-deezer-artist-performer", "native-one", "ext-deezer-album-record", "ext-deezer-song-first" },
            rows.Select(row => row.GetProperty("Id").GetString()));
        Assert.Equal(new[] { "MusicArtist", "Audio", "MusicAlbum", "Audio" },
            rows.Select(row => row.GetProperty("Type").GetString()));
        Assert.Equal(CanonicalJson(JsonDocument.Parse(native).RootElement), CanonicalJson(rows[1]));
        Assert.Equal(4, body.RootElement.GetProperty("TotalRecordCount").GetInt32());
        var request = new Uri("http://fixture" + Assert.Single(nativeRequests));
        Assert.Equal($"{prefix}/Items", request.AbsolutePath);
        Assert.Equal("native-one", QueryHelpers.ParseQuery(request.Query)["Ids"].ToString());
        gateway.VerifyAll();
        gateway.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("album")]
    [InlineData("artist")]
    public async Task JellyfinItemIds_UnknownNonSongReturnsEmptyWithoutBrowsing(string type)
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        if (type == "album")
            gateway.Setup(service => service.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "missing"))
                .ReturnsAsync((Album?)null);
        else
            gateway.Setup(service => service.GetArtistAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "missing"))
                .ReturnsAsync((Artist?)null);
        var nativeRequests = new List<string>();
        using var factory = ItemIdsFactory(gateway.Object, nativeRequests);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/Items?Ids=ext-deezer-{type}-missing&api_key=fixture-key");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Empty(body.RootElement.GetProperty("Items").EnumerateArray());
        Assert.Equal(0, body.RootElement.GetProperty("TotalRecordCount").GetInt32());
        Assert.Empty(nativeRequests);
        gateway.VerifyAll();
        gateway.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("ext-deezer-song-missing")]
    [InlineData("")]
    [InlineData(", ,")]
    public async Task JellyfinItemIds_UnknownOrEmptyNeverFallsBackToLibrary(string ids)
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        if (ids.Length > 0 && ids != ", ,")
            gateway.Setup(service => service.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "missing"))
                .ReturnsAsync((Song?)null);
        var nativeRequests = new List<string>();
        using var factory = ItemIdsFactory(gateway.Object, nativeRequests);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/Items?Ids={Uri.EscapeDataString(ids)}&api_key=fixture-key");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Empty(body.RootElement.GetProperty("Items").EnumerateArray());
        Assert.Equal(0, body.RootElement.GetProperty("TotalRecordCount").GetInt32());
        Assert.Empty(nativeRequests);
        gateway.VerifyAll();
    }

    [Fact]
    public async Task JellyfinItemIds_MixedPagesAfterRequestedOrder()
    {
        var gateway = ItemIdsGateway("first", "second");
        var nativeRequests = new List<string>();
        using var factory = ItemIdsFactory(gateway.Object, nativeRequests,
            """{"Items":[{"Id":"native-one","Type":"Audio","Name":"Owned"}],"TotalRecordCount":1}""");
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/Items?Ids=ext-deezer-song-second,native-one,ext-deezer-song-first&StartIndex=1&Limit=1&api_key=fixture-key");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var row = Assert.Single(body.RootElement.GetProperty("Items").EnumerateArray());
        Assert.Equal("native-one", row.GetProperty("Id").GetString());
        Assert.Equal(3, body.RootElement.GetProperty("TotalRecordCount").GetInt32());
        Assert.Equal(1, body.RootElement.GetProperty("StartIndex").GetInt32());
        var query = QueryHelpers.ParseQuery(new Uri("http://fixture" + Assert.Single(nativeRequests)).Query);
        Assert.Equal("0", query["StartIndex"].ToString());
        Assert.Equal("1", query["Limit"].ToString());
        gateway.VerifyAll();
    }

    [Theory]
    [InlineData("missing", new string[] { })]
    [InlineData("native-two,native-one", new[] { "native-two", "native-one" })]
    public async Task JellyfinItemIds_NativeRowsStayWithinRequestedSelection(string ids, string[] expected)
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        var nativeRequests = new List<string>();
        using var factory = ItemIdsFactory(gateway.Object, nativeRequests,
            """{"Items":[{"Id":"native-one","Type":"Audio","Name":"One","Future":true},{"Id":"native-two","Type":"Audio","Name":"Two"},{"Id":"unrequested","Type":"Audio","Name":"Other"}],"TotalRecordCount":3}""");
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/Items?Ids={ids}&api_key=fixture-key");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expected, body.RootElement.GetProperty("Items").EnumerateArray()
            .Select(row => row.GetProperty("Id").GetString()));
        Assert.Equal(expected.Length, body.RootElement.GetProperty("TotalRecordCount").GetInt32());
        Assert.Single(nativeRequests);
    }

    private static Mock<IProtocolProviderGateway> ItemIdsGateway(params string[] ids)
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        foreach (var id in ids)
            gateway.Setup(service => service.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", id))
                .ReturnsAsync(new Song
                {
                    Id = $"ext-deezer-song-{id}",
                    ExternalProvider = "deezer",
                    ExternalId = id,
                    Title = id,
                    Artist = "Fixture artist",
                    Duration = 180
                });
        return gateway;
    }

    private ProtocolFactory ItemIdsFactory(IProtocolProviderGateway gateway, List<string> nativeRequests,
        string native = """{"Items":[],"TotalRecordCount":0}""") =>
        new("Jellyfin", request =>
        {
            if (request.RequestUri!.AbsolutePath == "/Users/Me")
                return Json(200, """{"Id":"user-1"}""");
            Assert.True(request.RequestUri.AbsolutePath is "/Items" or "/Users/user-1/Items");
            nativeRequests.Add(request.RequestUri.PathAndQuery);
            return Json(200, native);
        }, services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway);
        });
}
