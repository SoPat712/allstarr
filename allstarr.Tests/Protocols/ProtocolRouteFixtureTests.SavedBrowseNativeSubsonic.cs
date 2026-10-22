using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using allstarr.Core.Favorites;
using allstarr.Core.Protocols;
using allstarr.Core.Protocols.Subsonic;
using allstarr.Services.Subsonic;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Theory]
    [InlineData("getArtists", "artists", "json")]
    [InlineData("getArtists", "artists", "xml")]
    [InlineData("getIndexes", "indexes", "json")]
    [InlineData("getIndexes", "indexes", "xml")]
    public async Task SubsonicSavedArtistBrowseRetainsEveryNativeRowAndOrdersIndexes(string endpoint, string container, string format)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedNativeSubsonicBrowseAsync(storage);
        var native = NativeSubsonicBrowseRows("artist", 517);
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        var observations = new List<string>();
        using var factory = new ProtocolFactory("Subsonic", request =>
        {
            if (request.RequestUri!.AbsolutePath == "/rest/ping.view") return NativeSubsonicPing();
            observations.Add(Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query)["ifModifiedSince"].ToString());
            return NativeSubsonicBrowseReply(format, container, "artist", native);
        }, services => ConfigureFavoriteViews(services, storage, gateway.Object));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(NativeSubsonicBrowsePath(endpoint, format) + "&ifModifiedSince=123");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var ids = NativeSubsonicBrowseIds(body, format, container, "artist");
        var expected = native.Take(260).Select(row => row.Id)
            .Concat(["ext-deezer-artist-related", "ext-deezer-artist-saved"])
            .Concat(native.Skip(260).Select(row => row.Id)).ToArray();
        Assert.Equal(expected, ids);
        Assert.Equal(519, ids.Distinct(StringComparer.Ordinal).Count());
        foreach (var row in native) Assert.Contains(format == "json" ? row.Json : row.Xml, body, StringComparison.Ordinal);
        if (format == "json")
        {
            using var document = JsonDocument.Parse(body);
            var indexes = document.RootElement.GetProperty("subsonic-response").GetProperty(container).GetProperty("index");
            Assert.Equal(new[] { "A", "R", "S", "Z" }, indexes.EnumerateArray().Select(index => index.GetProperty("name").GetString()));
            Assert.Equal(9, indexes[0].GetProperty("futureIndex").GetInt32());
        }
        else
        {
            var root = XDocument.Parse(body).Root!;
            var indexes = root.Element(root.Name.Namespace + container)!.Elements(root.Name.Namespace + "index").ToArray();
            Assert.Equal(new[] { "A", "R", "S", "Z" }, indexes.Select(index => index.Attribute("name")?.Value));
            Assert.Equal("9", indexes[0].Attribute("futureIndex")?.Value);
        }
        Assert.Single(observations);
        if (endpoint == "getIndexes") Assert.Equal("0", observations[0]);
        gateway.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("newest", "json")]
    [InlineData("newest", "xml")]
    [InlineData("alphabeticalByName", "json")]
    [InlineData("alphabeticalByName", "xml")]
    [InlineData("alphabeticalByArtist", "json")]
    [InlineData("alphabeticalByArtist", "xml")]
    [InlineData("byGenre", "json")]
    [InlineData("byGenre", "xml")]
    [InlineData("byYear", "json")]
    [InlineData("byYear", "xml")]
    [InlineData("starred", "json")]
    [InlineData("starred", "xml")]
    public async Task SubsonicSavedAlbumPagesRetainTheCompleteNativeSequence(string mode, string format)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedNativeSubsonicBrowseAsync(storage);
        var native = NativeSubsonicBrowseRows("album", 517);
        var expected = mode switch
        {
            "newest" => new[] { "ext-deezer-album-related", "ext-deezer-album-saved" }.Concat(native.Select(row => row.Id)),
            "starred" => new[] { "ext-deezer-album-saved" }.Concat(native.Select(row => row.Id)),
            "byYear" => native.Select(row => row.Id).Concat(["ext-deezer-album-saved", "ext-deezer-album-related"]),
            _ => native.Take(260).Select(row => row.Id).Concat(["ext-deezer-album-related", "ext-deezer-album-saved"])
                .Concat(native.Skip(260).Select(row => row.Id))
        };
        var expectedIds = expected.ToArray();
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        var observations = new List<(int Offset, int Size)>();
        using var factory = new ProtocolFactory("Subsonic", request =>
        {
            if (request.RequestUri!.AbsolutePath == "/rest/ping.view") return NativeSubsonicPing();
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query);
            var offset = int.Parse(query["offset"].ToString(), CultureInfo.InvariantCulture);
            var size = int.Parse(query["size"].ToString(), CultureInfo.InvariantCulture);
            observations.Add((offset, size));
            return NativeSubsonicBrowseReply(format, "albumList2", "album", native.Skip(offset).Take(size));
        }, services => ConfigureFavoriteViews(services, storage, gateway.Object));
        using var client = factory.CreateClient();
        var received = new List<string>();
        for (var offset = 0; offset <= expectedIds.Length + 137; offset += 137)
        {
            using var response = await client.GetAsync(NativeSubsonicBrowsePath("getAlbumList2", format) +
                $"&type={mode}&genre=Rock&fromYear=2000&toYear=2004&offset={offset}&size=137");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            var ids = NativeSubsonicBrowseIds(body, format, "albumList2", "album");
            Assert.Equal(expectedIds.Skip(offset).Take(137), ids);
            foreach (var row in native.Where(row => ids.Contains(row.Id, StringComparer.Ordinal)))
                Assert.Contains(format == "json" ? row.Json : row.Xml, body, StringComparison.Ordinal);
            received.AddRange(ids);
        }
        Assert.Equal(expectedIds, received);
        Assert.Equal(expectedIds.Length, received.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(observations, page => page.Offset == 500);
        Assert.All(observations, page => Assert.InRange(page.Size, 1, 500));
        gateway.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("json")]
    [InlineData("xml")]
    public async Task SubsonicSavedGenreSongPagesRetainAllNativeRowsBeforeSavedSongs(string format)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedNativeSubsonicBrowseAsync(storage);
        var native = NativeSubsonicBrowseRows("song", 517);
        var expected = native.Select(row => row.Id).Append("ext-deezer-song-saved").ToArray();
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        var observations = new List<(int Offset, int Count)>();
        using var factory = new ProtocolFactory("Subsonic", request =>
        {
            if (request.RequestUri!.AbsolutePath == "/rest/ping.view") return NativeSubsonicPing();
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query);
            var offset = int.Parse(query["offset"].ToString(), CultureInfo.InvariantCulture);
            var count = int.Parse(query["count"].ToString(), CultureInfo.InvariantCulture);
            Assert.Equal("Rock", query["genre"].ToString());
            observations.Add((offset, count));
            return NativeSubsonicBrowseReply(format, "songsByGenre", "song", native.Skip(offset).Take(count));
        }, services => ConfigureFavoriteViews(services, storage, gateway.Object));
        using var client = factory.CreateClient();
        var received = new List<string>();
        for (var offset = 0; offset <= expected.Length + 137; offset += 137)
        {
            using var response = await client.GetAsync(NativeSubsonicBrowsePath("getSongsByGenre", format) + $"&genre=Rock&offset={offset}&count=137");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            var ids = NativeSubsonicBrowseIds(body, format, "songsByGenre", "song");
            Assert.Equal(expected.Skip(offset).Take(137), ids);
            foreach (var row in native.Where(row => ids.Contains(row.Id, StringComparer.Ordinal)))
                Assert.Contains(format == "json" ? row.Json : row.Xml, body, StringComparison.Ordinal);
            received.AddRange(ids);
        }
        Assert.Equal(expected, received);
        Assert.Equal(518, received.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(observations, page => page.Offset == 500);
        Assert.All(observations, page => Assert.InRange(page.Count, 1, 500));
        gateway.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("getRandomSongs", "randomSongs", "song", "json")]
    [InlineData("getRandomSongs", "randomSongs", "song", "xml")]
    [InlineData("getAlbumList2", "albumList2", "album", "json")]
    [InlineData("getAlbumList2", "albumList2", "album", "xml")]
    public async Task SubsonicSavedRandomBrowseRetainsEveryNativeRowInACompleteWindow(string endpoint, string container, string element, string format)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedNativeSubsonicBrowseAsync(storage);
        var native = NativeSubsonicBrowseRows(element, 59);
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        using var factory = new ProtocolFactory("Subsonic", request => request.RequestUri!.AbsolutePath == "/rest/ping.view"
            ? NativeSubsonicPing() : NativeSubsonicBrowseReply(format, container, element, native),
            services => ConfigureFavoriteViews(services, storage, gateway.Object));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(NativeSubsonicBrowsePath(endpoint, format) + "&type=random&genre=Rock&fromYear=2000&toYear=2004&size=500");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var ids = NativeSubsonicBrowseIds(body, format, container, element);
        var saved = element == "song" ? new[] { "ext-deezer-song-saved" }
            : new[] { "ext-deezer-album-related", "ext-deezer-album-saved" };
        var expected = native.Select(row => row.Id).Concat(saved).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, ids.Order(StringComparer.Ordinal));
        Assert.Equal(expected.Length, ids.Distinct(StringComparer.Ordinal).Count());
        foreach (var row in native) Assert.Contains(format == "json" ? row.Json : row.Xml, body, StringComparison.Ordinal);
        gateway.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("json")]
    [InlineData("xml")]
    public async Task SubsonicSavedGenresPreserveNativeRowsExceptForKnownMembershipCounts(string format)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedNativeSubsonicBrowseAsync(storage);
        var nativeJson = Enumerable.Range(0, 516).Select(index =>
            $"{{ \"value\" : \"Genre {index:D4}\", \"songCount\" : {index}, \"albumCount\" : 3, \"future\" : {{ \"value\" : 4.2500 }} }}").ToArray();
        var nativeXml = Enumerable.Range(0, 516).Select(index =>
            $"<genre songCount='{index}' albumCount='3' future='keep'>Genre {index:D4}<future value='4.2500'/></genre>").ToArray();
        const string rockJson = "{ \"value\" : \"Rock\", \"songCount\" : 10, \"albumCount\" : 20, \"future\" : { \"value\" : 4.2500 } }";
        const string rockXml = "<genre songCount='10' albumCount='20' future='keep'>Rock</genre>";
        var json = "{\"subsonic-response\":{\"status\":\"ok\",\"genres\":{\"genre\":[" + string.Join(',', nativeJson.Append(rockJson)) + "]}}}";
        var xml = "<subsonic-response xmlns=\"http://subsonic.org/restapi\" status=\"ok\"><genres>" +
            string.Concat(nativeXml.Append(rockXml)) + "</genres></subsonic-response>";
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        using var factory = new ProtocolFactory("Subsonic", request => request.RequestUri!.AbsolutePath == "/rest/ping.view"
            ? NativeSubsonicPing() : CatalogNative(format, json, xml),
            services => ConfigureFavoriteViews(services, storage, gateway.Object));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(NativeSubsonicBrowsePath("getGenres", format));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        if (format == "json")
        {
            using var document = JsonDocument.Parse(body);
            var genres = document.RootElement.GetProperty("subsonic-response").GetProperty("genres").GetProperty("genre").EnumerateArray().ToArray();
            Assert.Equal(517, genres.Length);
            Assert.Equal(Enumerable.Range(0, 516).Select(index => $"Genre {index:D4}").Append("Rock"), genres.Select(row => row.GetProperty("value").GetString()));
            var rock = genres[^1];
            Assert.Equal(11, rock.GetProperty("songCount").GetInt32());
            Assert.Equal(22, rock.GetProperty("albumCount").GetInt32());
            Assert.Equal(4.25m, rock.GetProperty("future").GetProperty("value").GetDecimal());
            foreach (var row in nativeJson) Assert.Contains(row, body, StringComparison.Ordinal);
        }
        else
        {
            var root = XDocument.Parse(body).Root!;
            var genres = root.Element(root.Name.Namespace + "genres")!.Elements(root.Name.Namespace + "genre").ToArray();
            Assert.Equal(517, genres.Length);
            Assert.Equal(Enumerable.Range(0, 516).Select(index => $"Genre {index:D4}").Append("Rock"), genres.Select(row => row.Nodes().OfType<XText>().First().Value));
            var rock = genres[^1];
            Assert.Equal("11", rock.Attribute("songCount")?.Value);
            Assert.Equal("22", rock.Attribute("albumCount")?.Value);
            Assert.Equal("keep", rock.Attribute("future")?.Value);
            foreach (var row in nativeXml) Assert.Contains(row, body, StringComparison.Ordinal);
        }
        gateway.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("highest", "json")]
    [InlineData("highest", "xml")]
    [InlineData("frequent", "json")]
    [InlineData("frequent", "xml")]
    [InlineData("recent", "json")]
    [InlineData("recent", "xml")]
    [InlineData("unknown", "json")]
    [InlineData("unknown", "xml")]
    [InlineData("", "json")]
    [InlineData("", "xml")]
    public async Task SubsonicAlbumModesWithoutSavedOrderingRelayTheNativePageUnchanged(string mode, string format)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedNativeSubsonicBrowseAsync(storage);
        var native = NativeSubsonicBrowseRows("album", 23);
        using var payload = NativeSubsonicBrowseReply(format, "albumList2", "album", native);
        var expected = await payload.Content.ReadAsStringAsync();
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        var observations = new List<(string Mode, string Offset, string Size)>();
        using var factory = new ProtocolFactory("Subsonic", request =>
        {
            if (request.RequestUri!.AbsolutePath == "/rest/ping.view") return NativeSubsonicPing();
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query);
            observations.Add((query.GetValueOrDefault("type").ToString(), query["offset"].ToString(), query["size"].ToString()));
            return NativeSubsonicBrowseReply(format, "albumList2", "album", native);
        }, services => ConfigureFavoriteViews(services, storage, gateway.Object));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(NativeSubsonicBrowsePath("getAlbumList2", format) +
            $"&offset=17&size=23" + (mode.Length == 0 ? "" : $"&type={mode}"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, await response.Content.ReadAsStringAsync());
        Assert.Equal((mode, "17", "23"), Assert.Single(observations));
        gateway.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("json")]
    [InlineData("xml")]
    public async Task SubsonicSavedBrowseDeduplicatesSavedIdsWhilePreservingTheNativeRow(string format)
    {
        const string id = "ext-deezer-album-saved";
        var row = NativeSubsonicBrowseRows("album", 1)[0] with { Id = id };
        row = row with
        {
            Json = row.Json.Replace("native-album-0000", id, StringComparison.Ordinal),
            Xml = row.Xml.Replace("native-album-0000", id, StringComparison.Ordinal)
        };
        using var payload = NativeSubsonicBrowseReply(format, "albumList2", "album", [row]);
        var body = await payload.Content.ReadAsStringAsync();
        var response = new SubsonicProxyResponse(Encoding.UTF8.GetBytes(body), "application/" + format, HttpStatusCode.OK,
            new Dictionary<string, string[]>());
        var display = new FavoriteDisplaySnapshot(1, id, "album", "Saved replacement", []);
        var saved = new FavoriteViewItem(id, FavoriteDisplaySnapshot.Resource(id)!, DateTimeOffset.Parse("2024-02-03T04:05:06Z", CultureInfo.InvariantCulture), display);
        var parameters = SubsonicRequestParameters.FromDictionary(new Dictionary<string, string>
        { ["type"] = "alphabeticalByName", ["size"] = "20" });
        var merged = SubsonicSavedBrowseAdapter.Merge([response], [saved, saved], "albumList2", format, parameters, new SubsonicResponseBuilder());
        var result = Encoding.UTF8.GetString(merged.Body);
        Assert.Equal(new[] { id }, NativeSubsonicBrowseIds(result, format, "albumList2", "album"));
        Assert.Contains(format == "json" ? row.Json : row.Xml, result, StringComparison.Ordinal);
        Assert.DoesNotContain("Saved replacement", result, StringComparison.Ordinal);
    }

    private static async Task SeedNativeSubsonicBrowseAsync(FavoriteViewFactory storage)
    {
        await SeedSavedBrowseAsync(storage);
        await using var db = storage.CreateDbContext();
        var added = DateTimeOffset.Parse("2024-02-03T04:05:06Z", CultureInfo.InvariantCulture);
        await db.Set<FavoriteEventRecord>().Where(item => item.OwnerUserId == FavoriteViewer)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.CreatedAt, added));
        var album = new FavoriteDisplaySnapshot(1, "ext-deezer-album-saved", "album", "Saved album", ["Unknown artist"], Year: 2002)
        { Genres = ["Rock"] };
        var json = JsonSerializer.Serialize(album, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await db.Set<FavoriteStateRecord>().Where(item => item.ItemId == album.ItemId)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.DisplayMetadataJson, json));
    }

    private static HttpResponseMessage NativeSubsonicPing() => Json(200, """{"subsonic-response":{"status":"ok","version":"1.16.1"}}""");

    private static string NativeSubsonicBrowsePath(string endpoint, string format) =>
        $"/rest/{endpoint}.view?u=fixture&p=secret&v=1.16.1&c=fixture&f={format}";

    private sealed record NativeSubsonicBrowseRow(string Id, string Json, string Xml);

    private static NativeSubsonicBrowseRow[] NativeSubsonicBrowseRows(string element, int count) => Enumerable.Range(0, count).Select(index =>
    {
        var id = $"native-{element}-{index:D4}";
        var prefix = index < 260 ? "A" : "Z";
        var title = $"{prefix} {index:D4}";
        var json = $"{{ \"id\" : \"{id}\", \"name\" : \"{title}\", \"title\" : \"{title}\", \"artist\" : \"{prefix} artist\", \"genre\" : \"Rock\", \"year\" : 2001, \"created\" : \"2020-01-01T00:00:00Z\", \"starred\" : \"2020-01-01T00:00:00Z\", \"future\" : {{ \"number\" : 4.2500, \"text\" : \"keep\\u0020this\" }} }}";
        var xml = $"<{element} id='{id}' name='{title}' title='{title}' artist='{prefix} artist' genre='Rock' year='2001' created='2020-01-01T00:00:00Z' starred='2020-01-01T00:00:00Z'><future number='4.2500'>keep &amp; this</future></{element}>";
        return new NativeSubsonicBrowseRow(id, json, xml);
    }).ToArray();

    private static HttpResponseMessage NativeSubsonicBrowseReply(string format, string container, string element, IEnumerable<NativeSubsonicBrowseRow> source)
    {
        var rows = source.ToArray();
        var jsonRows = element == "artist"
            ? "\"index\":[" + string.Join(',', rows.GroupBy(row =>
                    int.Parse(row.Id[^4..], CultureInfo.InvariantCulture) < 260 ? "A" : "Z").Select(group =>
                    "{\"name\":\"" + group.Key + "\",\"futureIndex\":9,\"artist\":[" + string.Join(',', group.Select(row => row.Json)) + "]}")) + "]"
            : "\"" + element + "\":[" + string.Join(',', rows.Select(row => row.Json)) + "]";
        var xmlRows = element == "artist"
            ? string.Concat(rows.GroupBy(row => int.Parse(row.Id[^4..], CultureInfo.InvariantCulture) < 260 ? "A" : "Z")
                .Select(group => "<index name=\"" + group.Key + "\" futureIndex=\"9\">" + string.Concat(group.Select(row => row.Xml)) + "</index>"))
            : string.Concat(rows.Select(row => row.Xml));
        return CatalogNative(format, "{\"subsonic-response\":{\"status\":\"ok\",\"" + container + "\":{" + jsonRows + "},\"futureRoot\":9}}",
            "<subsonic-response xmlns=\"http://subsonic.org/restapi\" status=\"ok\"><" + container + ">" + xmlRows + "</" + container + "><futureRoot value=\"9\"/></subsonic-response>");
    }

    private static string[] NativeSubsonicBrowseIds(string body, string format, string container, string element)
    {
        if (format == "json")
        {
            using var document = JsonDocument.Parse(body);
            var list = document.RootElement.GetProperty("subsonic-response").GetProperty(container);
            var rows = element == "artist" ? list.GetProperty("index").EnumerateArray().SelectMany(index => index.GetProperty("artist").EnumerateArray())
                : list.GetProperty(element).EnumerateArray();
            return rows.Select(row => row.GetProperty("id").GetString()!).ToArray();
        }
        var root = XDocument.Parse(body).Root!;
        return root.Element(root.Name.Namespace + container)!.Descendants(root.Name.Namespace + element)
            .Select(row => row.Attribute("id")!.Value).ToArray();
    }
}
