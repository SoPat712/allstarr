using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Models.Search;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Theory]
    [InlineData("json", true)]
    [InlineData("xml", true)]
    [InlineData("json", false)]
    [InlineData("xml", false)]
    public async Task SubsonicExternalArtist_IncludesOwnedAlbumsOnlyWithASharedRelease(string format, bool sharedRelease)
    {
        var gateway = OwnedArtistGateway();
        var observed = new List<ObservedRequest>();
        using var factory = CatalogFactory(observed, gateway.Object, request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("search3", StringComparison.Ordinal))
                return CatalogNative(format,
                    """{"subsonic-response":{"status":"ok","searchResult3":{"artist":[{"id":"namesake","name":"Grimes","albumCount":40},{"id":"real","name":"Grimes","albumCount":2}]}}}""",
                    """<subsonic-response xmlns="http://subsonic.org/restapi" status="ok"><searchResult3><artist id="namesake" name="Grimes" albumCount="40"/><artist id="real" name="Grimes" albumCount="2"/></searchResult3></subsonic-response>""");
            if (request.RequestUri.Query.Contains("id=namesake", StringComparison.Ordinal) || !sharedRelease)
                return CatalogNative(format,
                    """{"subsonic-response":{"status":"ok","artist":{"id":"namesake","name":"Grimes","album":[{"id":"wrong","name":"Unrelated"}]}}}""",
                    """<subsonic-response xmlns="http://subsonic.org/restapi" status="ok"><artist id="namesake" name="Grimes"><album id="wrong" name="Unrelated"/></artist></subsonic-response>""");
            Assert.Contains("id=real", request.RequestUri.Query, StringComparison.Ordinal);
            return CatalogNative(format,
                """{"subsonic-response":{"status":"ok","artist":{"id":"real","name":"Grimes","album":[{"id":"owned-visions","name":"Visions","upc":"012345678905","coverArt":"native-cover","futureField":"kept"},{"id":"owned-halfaxa","name":"Halfaxa","futureField":"kept"}]}}}""",
                """<subsonic-response xmlns="http://subsonic.org/restapi" status="ok"><artist id="real" name="Grimes"><album id="owned-visions" name="Visions" upc="012345678905" coverArt="native-cover" futureField="kept"/><album id="owned-halfaxa" name="Halfaxa" futureField="kept"/></artist></subsonic-response>""");
        });
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(
            $"/rest/getArtist.view?u=fixture&p=secret&v=1.16.1&c=fixture&f={format}&id=ext-deezer-artist-real");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var rows = ReadMergedCatalogRows(body, format, "artist", "album");
        string[] expected = sharedRelease
            ? ["owned-visions", "owned-halfaxa", "ext-deezer-album-r2"]
            : ["ext-deezer-album-r1", "ext-deezer-album-r2"];
        Assert.Equal(expected, rows.Select(row => row["id"]));
        Assert.DoesNotContain(rows, row => row["id"] == "wrong");
        if (sharedRelease)
        {
            Assert.Equal("kept", rows[0]["futureField"]);
            Assert.Equal("native-cover", rows[0]["coverArt"]);
            Assert.Equal("kept", rows[1]["futureField"]);
            Assert.Equal(3, observed.Count);
        }
        gateway.VerifyAll();
    }

    [Theory]
    [InlineData("json")]
    [InlineData("xml")]
    public async Task SubsonicExternalArtist_KeepsServiceAlbumsWhenLibraryCannotBeRead(string format)
    {
        var gateway = OwnedArtistGateway();
        using var factory = CatalogFactory([], gateway.Object, _ => throw new HttpRequestException("Library unavailable"));
        using var client = factory.CreateClient();
        var body = await client.GetStringAsync(
            $"/rest/getArtist.view?u=fixture&p=secret&v=1.16.1&c=fixture&f={format}&id=ext-deezer-artist-real");
        var rows = ReadMergedCatalogRows(body, format, "artist", "album");
        Assert.Equal(["ext-deezer-album-r1", "ext-deezer-album-r2"], rows.Select(row => row["id"]));
        gateway.VerifyAll();
    }

    [Theory]
    [InlineData("json", null)]
    [InlineData("xml", null)]
    [InlineData("json", 1)]
    [InlineData("xml", 1)]
    public async Task SubsonicMergedAlbum_OrdersTracksWithUnknownDiscBesideDiscOne(string format, int? firstDisc)
    {
        var first = ProviderSong("ext-deezer-song-first", "First");
        first.DiscNumber = firstDisc;
        first.Track = 1;
        var last = ProviderSong("ext-deezer-song-last", "Last");
        last.DiscNumber = 2;
        last.Track = 2;
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(service => service.SearchAsync(It.IsAny<ProtocolExecutionContext>(), "Grimes Visions", 0, 10, 0, null))
            .ReturnsAsync(new SearchResult { Albums = [CatalogAlbum("r1", "Visions")] });
        gateway.Setup(service => service.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "r1"))
            .ReturnsAsync(new Album { Title = "Visions", Artist = "Grimes", Songs = [last, first] });
        using var factory = CatalogFactory([], gateway.Object, _ => CatalogNative(format,
            """{"subsonic-response":{"status":"ok","album":{"id":"native-album","name":"Visions","artist":"Grimes","song":[{"id":"disc-two","title":"Third","discNumber":2,"track":1,"futureField":"kept"},{"id":"disc-one","title":"Second","track":2,"futureField":"kept"}]}}}""",
            """<subsonic-response xmlns="http://subsonic.org/restapi" status="ok"><album id="native-album" name="Visions" artist="Grimes"><song id="disc-two" title="Third" discNumber="2" track="1" futureField="kept"/><song id="disc-one" title="Second" track="2" futureField="kept"/></album></subsonic-response>"""));
        using var client = factory.CreateClient();
        var body = await client.GetStringAsync(
            $"/rest/getAlbum.view?u=fixture&p=secret&v=1.16.1&c=fixture&f={format}&id=native-album");
        var rows = ReadMergedCatalogRows(body, format, "album", "song");
        Assert.Equal(["ext-deezer-song-first", "disc-one", "disc-two", "ext-deezer-song-last"], rows.Select(row => row["id"]));
        Assert.Equal("kept", rows[1]["futureField"]);
        Assert.Equal("kept", rows[2]["futureField"]);
        Assert.False(rows[1].ContainsKey("discNumber"));
        if (firstDisc.HasValue) Assert.Equal("1", rows[0]["discNumber"]);
        else Assert.False(rows[0].ContainsKey("discNumber"));
        gateway.VerifyAll();
    }

    private static Mock<IProtocolProviderGateway> OwnedArtistGateway()
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(service => service.GetArtistAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "real"))
            .ReturnsAsync(CatalogArtist("real", 2));
        gateway.Setup(service => service.GetArtistAlbumsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "real"))
            .ReturnsAsync([CatalogAlbum("r1", "Visions"), CatalogAlbum("r2", "Art Angels")]);
        gateway.Setup(service => service.SearchAsync(It.IsAny<ProtocolExecutionContext>(), "Grimes", 0, 0, 20, null))
            .ReturnsAsync(new SearchResult());
        return gateway;
    }

    private static Dictionary<string, string?>[] ReadMergedCatalogRows(string body, string format, string parent, string child)
    {
        if (format == "json")
        {
            using var document = JsonDocument.Parse(body);
            var container = document.RootElement.GetProperty("subsonic-response").GetProperty(parent);
            var jsonRows = container.GetProperty(child)
                .EnumerateArray().Select(SubsonicSongMediaContractTests.ReadJson).ToArray();
            Assert.Equal(jsonRows.Length, container.GetProperty(parent == "artist" ? "albumCount" : "songCount").GetInt32());
            return jsonRows;
        }
        var root = XDocument.Parse(body).Root!;
        var element = root.Element(root.Name.Namespace + parent)!;
        var rows = element.Elements(root.Name.Namespace + child).ToArray();
        Assert.Equal(rows.Length.ToString(), element.Attribute(parent == "artist" ? "albumCount" : "songCount")?.Value);
        Assert.All(rows, row => Assert.Equal(root.Name.Namespace, row.Name.Namespace));
        return rows.Select(SubsonicSongMediaContractTests.ReadXml).ToArray();
    }
}
