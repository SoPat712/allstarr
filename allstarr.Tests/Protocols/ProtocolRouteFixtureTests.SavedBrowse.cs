using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using allstarr.Core.Favorites;
using allstarr.Core.Protocols;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Theory]
    [InlineData("getArtists", "artists", "artist", "ext-deezer-artist-related", "json", "Feishin")]
    [InlineData("getArtists", "artists", "artist", "ext-deezer-artist-related", "xml", "Tempo")]
    [InlineData("getIndexes", "indexes", "artist", "ext-deezer-artist-related", "json", "Tempo")]
    [InlineData("getIndexes", "indexes", "artist", "ext-deezer-artist-related", "xml", "Tempo")]
    [InlineData("getAlbumList2", "albumList2", "album", "ext-deezer-album-related", "json", "Feishin")]
    [InlineData("getAlbumList2", "albumList2", "album", "ext-deezer-album-related", "xml", "Tempo")]
    [InlineData("getRandomSongs", "randomSongs", "song", "ext-deezer-song-saved", "json", "Feishin")]
    [InlineData("getRandomSongs", "randomSongs", "song", "ext-deezer-song-saved", "xml", "Tempo")]
    [InlineData("getSongsByGenre", "songsByGenre", "song", "ext-deezer-song-saved", "json", "Tempo")]
    [InlineData("getSongsByGenre", "songsByGenre", "song", "ext-deezer-song-saved", "xml", "Tempo")]
    public async Task SubsonicSavedBrowseUsesFiniteMembershipAndKeepsNativeRows(string endpoint, string container,
        string element, string expected, string format, string clientName)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedSavedBrowseAsync(storage);
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        var nativeRow = """{ "id":"native-one", "name":"Native", "title":"Native", "created":"2020-01-01T00:00:00Z", "future":{"keep":9} }""";
        var nativeJson = """{"subsonic-response":{"status":"ok","CONTAINER":{ROW},"future":7}}"""
            .Replace("CONTAINER", container, StringComparison.Ordinal)
            .Replace("ROW", element == "artist" ? $"\"index\":[{{\"name\":\"N\",\"artist\":[{nativeRow}]}}]" : $"\"{element}\":[{nativeRow}]", StringComparison.Ordinal);
        var nativeXmlRow = $"<{element} id=\"native-one\" name=\"Native\" title=\"Native\" created=\"2020-01-01T00:00:00Z\"><future keep=\"9\"/></{element}>";
        var nativeXml = $"<subsonic-response xmlns=\"http://subsonic.org/restapi\" status=\"ok\"><{container}>" +
            (element == "artist" ? $"<index name=\"N\">{nativeXmlRow}</index>" : nativeXmlRow) + $"</{container}><future keep=\"7\"/></subsonic-response>";
        using var factory = new ProtocolFactory("Subsonic", request => request.RequestUri!.AbsolutePath == "/rest/ping.view"
            ? Json(200, """{"subsonic-response":{"status":"ok","version":"1.16.1"}}""")
            : CatalogNative(format, nativeJson, nativeXml), services => ConfigureFavoriteViews(services, storage, gateway.Object));
        using var client = factory.CreateClient();
        var path = $"/rest/{endpoint}.view?u=fixture&p=secret&v=1.13.0&c={clientName}&f={format}&type=alphabeticalByName&size=20&count=100&genre=Rock";
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(expected, body, StringComparison.Ordinal);
        Assert.Contains(format == "json" ? nativeRow : nativeXmlRow, body, StringComparison.Ordinal);
        Assert.DoesNotContain("foreign-track", body, StringComparison.Ordinal);
        Assert.DoesNotContain("released-track", body, StringComparison.Ordinal);
        if (format == "json")
        {
            using var document = JsonDocument.Parse(body);
            var list = document.RootElement.GetProperty("subsonic-response").GetProperty(container);
            var items = element == "artist" ? list.GetProperty("index").EnumerateArray().SelectMany(index => index.GetProperty("artist").EnumerateArray())
                : list.GetProperty(element).EnumerateArray();
            var saved = Assert.Single(items, item => item.GetProperty("id").GetString() == expected);
            if (element == "song") SubsonicSongMediaContractTests.AssertSparseMedia(SubsonicSongMediaContractTests.ReadJson(saved));
            else Assert.False(saved.TryGetProperty("starred", out _));
            if (element != "artist") Assert.Equal("2024-02-03T04:05:06.0000000+00:00", saved.GetProperty("created").GetString());
        }
        else
        {
            var root = XDocument.Parse(body).Root!;
            var saved = Assert.Single(root.Descendants(root.Name.Namespace + element), item => item.Attribute("id")?.Value == expected);
            if (element == "song") SubsonicSongMediaContractTests.AssertSparseMedia(SubsonicSongMediaContractTests.ReadXml(saved));
            else Assert.Null(saved.Attribute("starred"));
            if (element != "artist") Assert.Equal("2024-02-03T04:05:06.0000000+00:00", saved.Attribute("created")?.Value);
        }
        using var scoped = await client.GetAsync(path + "&musicFolderId=unknown-folder");
        Assert.Equal(format == "json" ? nativeJson : nativeXml, await scoped.Content.ReadAsStringAsync());
        gateway.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("newest", "", "ext-deezer-album-related")]
    [InlineData("alphabeticalByName", "", "ext-deezer-album-related")]
    [InlineData("alphabeticalByArtist", "", "ext-deezer-album-related")]
    [InlineData("byGenre", "&genre=Rock", "ext-deezer-album-related")]
    [InlineData("byYear", "&fromYear=2004&toYear=2000", "ext-deezer-album-related")]
    [InlineData("random", "", "ext-deezer-album-related")]
    [InlineData("starred", "", "ext-deezer-album-saved")]
    public async Task SubsonicSavedAlbumModesUseMembershipAndKeepFavoriteSemantics(string mode, string filter, string expected)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedSavedBrowseAsync(storage);
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        using var factory = new ProtocolFactory("Subsonic", request => request.RequestUri!.AbsolutePath == "/rest/ping.view"
            ? Json(200, """{"subsonic-response":{"status":"ok","version":"1.16.1"}}""")
            : Json(200, """{"subsonic-response":{"status":"ok","albumList2":{"album":[]}}}"""),
            services => ConfigureFavoriteViews(services, storage, gateway.Object));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/rest/getAlbumList2?u=fixture&p=secret&v=1.13.0&c=Feishin&f=json&type={mode}&size=20{filter}");
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(expected, body, StringComparison.Ordinal);
        if (mode == "starred") Assert.DoesNotContain("ext-deezer-album-related", body, StringComparison.Ordinal);
        gateway.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("json")]
    [InlineData("xml")]
    public async Task SubsonicSavedGenresCountKnownSongsAndAlbums(string format)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedSavedBrowseAsync(storage);
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        using var factory = new ProtocolFactory("Subsonic", request => request.RequestUri!.AbsolutePath == "/rest/ping.view"
            ? Json(200, """{"subsonic-response":{"status":"ok","version":"1.16.1"}}""")
            : CatalogNative(format, """{"subsonic-response":{"status":"ok","genres":{"genre":[{"value":"Rock","songCount":2,"albumCount":3,"future":9}]}}}""",
                """<subsonic-response xmlns="http://subsonic.org/restapi" status="ok"><genres><genre songCount="2" albumCount="3" future="9">Rock</genre></genres></subsonic-response>"""),
            services => ConfigureFavoriteViews(services, storage, gateway.Object));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/rest/getGenres?u=fixture&p=secret&v=1.16.1&c=Tempo&f={format}");
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        if (format == "json")
        {
            using var document = JsonDocument.Parse(body);
            var genre = Assert.Single(document.RootElement.GetProperty("subsonic-response").GetProperty("genres").GetProperty("genre").EnumerateArray());
            Assert.Equal("Rock", genre.GetProperty("value").GetString());
            Assert.Equal(3, genre.GetProperty("songCount").GetInt32());
            Assert.Equal(4, genre.GetProperty("albumCount").GetInt32());
            Assert.Equal(9, genre.GetProperty("future").GetInt32());
        }
        else
        {
            var root = XDocument.Parse(body).Root!;
            var genre = Assert.Single(root.Descendants(root.Name.Namespace + "genre"));
            Assert.Equal("3", genre.Attribute("songCount")?.Value);
            Assert.Equal("4", genre.Attribute("albumCount")?.Value);
            Assert.Equal("9", genre.Attribute("future")?.Value);
        }
        gateway.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("json")]
    [InlineData("xml")]
    public async Task SubsonicSavedAlbumPagingSortsOneCombinedSequence(string format)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedSavedBrowseAsync(storage);
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        using var factory = new ProtocolFactory("Subsonic", request =>
        {
            if (request.RequestUri!.AbsolutePath == "/rest/ping.view")
                return Json(200, """{"subsonic-response":{"status":"ok","version":"1.16.1"}}""");
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query);
            var start = int.TryParse(query["offset"], out var offset) ? offset : 0;
            var size = int.TryParse(query["size"], out var count) ? count : 10;
            var rows = new[] { new { id = "native-a", name = "Alpha" }, new { id = "native-z", name = "Zulu" } }.Skip(start).Take(size).ToArray();
            return CatalogNative(format, JsonSerializer.Serialize(new Dictionary<string, object>
            { ["subsonic-response"] = new { status = "ok", albumList2 = new { album = rows } } }),
                "<subsonic-response xmlns=\"http://subsonic.org/restapi\" status=\"ok\"><albumList2>" +
                string.Concat(rows.Select(row => $"<album id=\"{row.id}\" name=\"{row.name}\"/>")) + "</albumList2></subsonic-response>");
        }, services => ConfigureFavoriteViews(services, storage, gateway.Object));
        using var client = factory.CreateClient();
        var ids = new List<string>();
        for (var offset = 0; offset < 4; offset++)
        {
            using var response = await client.GetAsync($"/rest/getAlbumList2.view?u=fixture&p=secret&v=1.13.0&c=Feishin&f={format}&type=alphabeticalByName&offset={offset}&size=1");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            if (format == "json")
            {
                using var document = JsonDocument.Parse(body);
                ids.Add(Assert.Single(document.RootElement.GetProperty("subsonic-response").GetProperty("albumList2").GetProperty("album").EnumerateArray()).GetProperty("id").GetString()!);
            }
            else
            {
                var root = XDocument.Parse(body).Root!;
                ids.Add(Assert.Single(root.Descendants(root.Name.Namespace + "album")).Attribute("id")!.Value);
            }
        }
        Assert.Equal(new[] { "native-a", "ext-deezer-album-related", "ext-deezer-album-saved", "native-z" }, ids);
        gateway.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("json")]
    [InlineData("xml")]
    public async Task SubsonicSavedBrowsePreservesNativeDenialAndDisablesSharedValidators(string format)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedSavedBrowseAsync(storage);
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        var denied = false;
        var observations = new List<bool>();
        using var factory = new ProtocolFactory("Subsonic", request =>
        {
            if (request.RequestUri!.AbsolutePath == "/rest/ping.view")
                return Json(200, """{"subsonic-response":{"status":"ok","version":"1.16.1"}}""");
            observations.Add(request.Headers.Contains("If-None-Match"));
            var response = denied ? CatalogNative(format,
                """{"subsonic-response":{"status":"failed","error":{"code":50,"message":"Denied"}}}""",
                """<subsonic-response xmlns="http://subsonic.org/restapi" status="failed"><error code="50" message="Denied"/></subsonic-response>""")
                : CatalogNative(format, """{"subsonic-response":{"status":"ok","artists":{"index":[]}}}""",
                    """<subsonic-response xmlns="http://subsonic.org/restapi" status="ok"><artists/></subsonic-response>""");
            response.Headers.TryAddWithoutValidation("ETag", "\"native\"");
            return response;
        }, services => ConfigureFavoriteViews(services, storage, gateway.Object));
        using var client = factory.CreateClient();
        async Task<HttpResponseMessage> Read()
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"/rest/getArtists.view?u=fixture&p=secret&v=1.13.0&c=Feishin&f={format}");
            request.Headers.TryAddWithoutValidation("If-None-Match", "\"native\"");
            return await client.SendAsync(request);
        }
        using var allowed = await Read();
        Assert.Contains("ext-deezer-artist-related", await allowed.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Null(allowed.Headers.ETag);
        Assert.True(allowed.Headers.CacheControl?.NoStore);
        denied = true;
        using var forbidden = await Read();
        var body = await forbidden.Content.ReadAsStringAsync();
        Assert.Contains("Denied", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ext-deezer", body, StringComparison.Ordinal);
        Assert.All(observations, validator => Assert.False(validator));
        gateway.VerifyNoOtherCalls();
    }

    private static async Task SeedSavedBrowseAsync(FavoriteViewFactory storage)
    {
        await SeedFavoriteViewsAsync(storage, "subsonic");
        await using var db = storage.CreateDbContext();
        var snapshot = new FavoriteDisplaySnapshot(1, "ext-deezer-song-saved", "song", "Saved song", ["Related artist"],
            "Related album", 180000, 2003)
        {
            AlbumId = "ext-deezer-album-related",
            ArtistIds = ["ext-deezer-artist-related"],
            AlbumArtistIds = ["ext-deezer-artist-related"],
            Genres = ["Rock"]
        };
        var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await db.Set<FavoriteStateRecord>().Where(item => item.ItemId == snapshot.ItemId)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.DisplayMetadataJson, json));
        var added = DateTimeOffset.Parse("2024-02-03T04:05:06Z", System.Globalization.CultureInfo.InvariantCulture);
        await db.Set<FavoriteEventRecord>().Where(item => item.ItemId == snapshot.ItemId)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.CreatedAt, added));
    }
}
