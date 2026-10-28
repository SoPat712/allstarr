using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Models.Search;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    private const string NativeArtistJson =
        """{"subsonic-response":{"status":"ok","version":"1.16.1","artist":{"id":"native-artist","name":"Grimes","albumCount":1,"futureField":"kept","album":[{"id":"native-album-1","name":"Visions","upc":"012345678905","futureField":"kept"}]}}}""";
    private const string NativeArtistXml =
        """<subsonic-response xmlns="http://subsonic.org/restapi" status="ok" version="1.16.1"><artist id="native-artist" name="Grimes" albumCount="1" futureField="kept"><album id="native-album-1" name="Visions" upc="012345678905" futureField="kept"/></artist></subsonic-response>""";
    private const string NativeAlbumJson =
        """{"subsonic-response":{"status":"ok","version":"1.16.1","album":{"id":"native-album-1","name":"Visions","artist":"Grimes","songCount":1,"futureField":"kept","song":[{"id":"native-song-1","title":"Oblivion","futureField":"kept"}]}}}""";
    private const string NativeAlbumXml =
        """<subsonic-response xmlns="http://subsonic.org/restapi" status="ok" version="1.16.1"><album id="native-album-1" name="Visions" artist="Grimes" songCount="1" futureField="kept"><song id="native-song-1" title="Oblivion" futureField="kept"/></album></subsonic-response>""";

    [Theory]
    [InlineData("json")]
    [InlineData("xml")]
    public async Task SubsonicNativeArtist_AppendsProviderAlbumsFromTheArtistThatSharesAnAlbum(string format)
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.SearchAsync(It.IsAny<ProtocolExecutionContext>(), "Grimes", 0, 0, 20, null))
            .ReturnsAsync(new SearchResult
            {
                Artists =
                [
                    CatalogArtist("namesake", 40),
                    CatalogArtist("real", 8)
                ]
            });
        gateway.Setup(item => item.GetArtistAlbumsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "namesake"))
            .ReturnsAsync([CatalogAlbum("n1", "Grimes Plays the Hits")]);
        gateway.Setup(item => item.GetArtistAlbumsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "real"))
            .ReturnsAsync([CatalogAlbum("r1", "Visions"), CatalogAlbum("r2", "Art Angels"), CatalogAlbum("r3", "Miss Anthropocene")]);
        var observed = new List<ObservedRequest>();
        using var factory = CatalogFactory(observed, gateway.Object, _ => CatalogNative(format, NativeArtistJson, NativeArtistXml));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            $"/rest/getArtist.view?u=fixture&p=secret&v=1.16.1&c=fixture&f={format}&id=native-artist");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        string[] expected = ["native-album-1", "ext-deezer-album-r2", "ext-deezer-album-r3"];
        if (format == "json")
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement.GetProperty("subsonic-response");
            Assert.Equal("ok", root.GetProperty("status").GetString());
            var artist = root.GetProperty("artist");
            Assert.Equal("native-artist", artist.GetProperty("id").GetString());
            Assert.Equal("Grimes", artist.GetProperty("name").GetString());
            Assert.Equal("kept", artist.GetProperty("futureField").GetString());
            Assert.Equal(3, artist.GetProperty("albumCount").GetInt32());
            var albums = artist.GetProperty("album").EnumerateArray().ToList();
            Assert.Equal(expected, albums.Select(album => album.GetProperty("id").GetString()));
            Assert.Equal("kept", albums[0].GetProperty("futureField").GetString());
            Assert.All(albums.Skip(1), album => Assert.Equal("native-artist", album.GetProperty("artistId").GetString()));
            Assert.Equal(["Art Angels [A]", "Miss Anthropocene [A]"], albums.Skip(1).Select(album => album.GetProperty("name").GetString()));
        }
        else
        {
            var document = XDocument.Parse(body);
            Assert.Equal("ok", document.Root!.Attribute("status")?.Value);
            var artist = Assert.Single(document.Root.Elements(), element => element.Name.LocalName == "artist");
            Assert.Equal("native-artist", artist.Attribute("id")?.Value);
            Assert.Equal("kept", artist.Attribute("futureField")?.Value);
            Assert.Equal("3", artist.Attribute("albumCount")?.Value);
            var albums = artist.Elements().ToList();
            Assert.All(albums, album => Assert.Equal(XName.Get("album", "http://subsonic.org/restapi"), album.Name));
            Assert.Equal(expected, albums.Select(album => album.Attribute("id")?.Value));
            Assert.Equal("kept", albums[0].Attribute("futureField")?.Value);
            Assert.All(albums.Skip(1), album => Assert.Equal("native-artist", album.Attribute("artistId")?.Value));
            Assert.Equal(["Art Angels [A]", "Miss Anthropocene [A]"], albums.Skip(1).Select(album => album.Attribute("name")?.Value));
        }
        Assert.Equal(
            $"/rest/getArtist.view?u=fixture&p=secret&v=1.16.1&c=fixture&f={format}&id=native-artist",
            Assert.Single(observed).PathAndQuery);
        gateway.VerifyAll();
    }

    [Theory]
    [InlineData("json")]
    [InlineData("xml")]
    public async Task SubsonicNativeAlbum_AppendsTheMissingSongsOfTheSameProviderAlbum(string format)
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.SearchAsync(It.IsAny<ProtocolExecutionContext>(), "Grimes Visions", 0, 10, 0, null))
            .ReturnsAsync(new SearchResult
            {
                Albums =
                [
                    CatalogAlbum("other", "Starhand Visions"),
                    CatalogAlbum("9", "Visions (Deluxe Edition)")
                ]
            });
        gateway.Setup(item => item.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "9"))
            .ReturnsAsync(new Album
            {
                Id = "ext-deezer-album-9",
                Title = "Visions (Deluxe Edition)",
                Artist = "Grimes",
                Songs = [ProviderSong("ext-deezer-song-1", "Oblivion"), ProviderSong("ext-deezer-song-2", "Genesis")]
            });
        var observed = new List<ObservedRequest>();
        using var factory = CatalogFactory(observed, gateway.Object, _ => CatalogNative(format, NativeAlbumJson, NativeAlbumXml));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            $"/rest/getAlbum.view?u=fixture&p=secret&v=1.16.1&c=fixture&f={format}&id=native-album-1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        string[] expected = ["native-song-1", "ext-deezer-song-2"];
        if (format == "json")
        {
            using var document = JsonDocument.Parse(body);
            var album = document.RootElement.GetProperty("subsonic-response").GetProperty("album");
            Assert.Equal("native-album-1", album.GetProperty("id").GetString());
            Assert.Equal("kept", album.GetProperty("futureField").GetString());
            Assert.Equal(2, album.GetProperty("songCount").GetInt32());
            var songs = album.GetProperty("song").EnumerateArray().ToList();
            Assert.Equal(expected, songs.Select(song => song.GetProperty("id").GetString()));
            Assert.Equal("kept", songs[0].GetProperty("futureField").GetString());
        }
        else
        {
            var album = Assert.Single(XDocument.Parse(body).Root!.Elements(), element => element.Name.LocalName == "album");
            Assert.Equal("kept", album.Attribute("futureField")?.Value);
            Assert.Equal("2", album.Attribute("songCount")?.Value);
            var songs = album.Elements().ToList();
            Assert.All(songs, song => Assert.Equal(XName.Get("song", "http://subsonic.org/restapi"), song.Name));
            Assert.Equal(expected, songs.Select(song => song.Attribute("id")?.Value));
            Assert.Equal("kept", songs[0].Attribute("futureField")?.Value);
        }
        Assert.Single(observed);
        gateway.VerifyAll();
        gateway.Verify(item => item.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "other"), Times.Never);
    }

    [Theory]
    [InlineData("getArtist", "native-artist", "json")]
    [InlineData("getArtist", "native-artist", "xml")]
    [InlineData("getAlbum", "native-album-1", "json")]
    [InlineData("getAlbum", "native-album-1", "xml")]
    public async Task SubsonicNativeArtistAndAlbum_PassThroughWhenProvidersAddNothing(string endpoint, string id, string format)
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.SearchAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), null))
            .ReturnsAsync(new SearchResult());
        var native = NativeCatalogBody(endpoint, format);
        var observed = new List<ObservedRequest>();
        using var factory = CatalogFactory(observed, gateway.Object,
            _ => CatalogNative(format, native, native));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            $"/rest/{endpoint}.view?u=fixture&p=secret&v=1.16.1&c=fixture&f={format}&id={id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(native, await response.Content.ReadAsStringAsync());
        Assert.Single(observed);
        gateway.Verify(item => item.SearchAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(),
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), null), Times.Once);
        gateway.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("getArtist", "native-artist")]
    [InlineData("getAlbum", "native-album-1")]
    public async Task SubsonicNativeArtistAndAlbum_PassThroughWhenProvidersFail(string endpoint, string id)
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.SearchAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), null))
            .ThrowsAsync(new HttpRequestException("provider down"));
        var native = NativeCatalogBody(endpoint, "json");
        var observed = new List<ObservedRequest>();
        using var factory = CatalogFactory(observed, gateway.Object, _ => Json(200, native));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            $"/rest/{endpoint}.view?u=fixture&p=secret&v=1.16.1&c=fixture&f=json&id={id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(native, await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("getSimilarSongs", "ext-deezer-song-42", "similarSongs", "json")]
    [InlineData("getSimilarSongs", "ext-deezer-song-42", "similarSongs", "xml")]
    [InlineData("getSimilarSongs2", "ext-deezer-artist-7", "similarSongs2", "json")]
    [InlineData("getSimilarSongs2", "ext-deezer-artist-7", "similarSongs2", "xml")]
    public async Task SubsonicSimilarSongs_ProviderSeedsAnswerFromTheSharedMix(
        string endpoint, string id, string element, string format)
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "42"))
            .ReturnsAsync(ProviderSong("ext-deezer-song-42", "Seed"));
        gateway.Setup(item => item.GetSimilarSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "42", 2))
            .ReturnsAsync([ProviderSong("ext-deezer-song-43", "Genesis"), ProviderSong("ext-deezer-song-44", "Realiti")]);
        gateway.Setup(item => item.GetArtistAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "7"))
            .ReturnsAsync(new Artist { Id = "ext-deezer-artist-7", Name = "Grimes", ExternalProvider = "deezer", ExternalId = "7" });
        gateway.Setup(item => item.GetArtistAlbumsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "7"))
            .ReturnsAsync([CatalogAlbum("a1", "Visions")]);
        gateway.Setup(item => item.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "a1"))
            .ReturnsAsync(new Album
            {
                Id = "ext-deezer-album-a1",
                Title = "Visions",
                Artist = "Grimes",
                Songs = [ProviderSong("ext-deezer-song-43", "Genesis"), ProviderSong("ext-deezer-song-44", "Realiti")]
            });
        var observed = new List<ObservedRequest>();
        using var factory = CatalogFactory(observed, gateway.Object,
            request => throw new InvalidOperationException($"Unexpected upstream request: {request.RequestUri}"));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            $"/rest/{endpoint}.view?u=fixture&p=secret&v=1.16.1&c=fixture&f={format}&id={id}&count=2");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var expected = new[] { "ext-deezer-song-43", "ext-deezer-song-44" }
            .OrderBy(songId => MusicMixService.StableOrder(id, songId), StringComparer.Ordinal);
        if (format == "json")
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement.GetProperty("subsonic-response");
            Assert.Equal("ok", root.GetProperty("status").GetString());
            Assert.Equal(expected, root.GetProperty(element).GetProperty("song").EnumerateArray()
                .Select(song => song.GetProperty("id").GetString()));
        }
        else
        {
            var root = XDocument.Parse(body).Root!;
            Assert.Equal(XName.Get("subsonic-response", "http://subsonic.org/restapi"), root.Name);
            Assert.Equal("ok", root.Attribute("status")?.Value);
            var list = Assert.Single(root.Elements());
            Assert.Equal(XName.Get(element, "http://subsonic.org/restapi"), list.Name);
            Assert.Equal(expected, list.Elements(XName.Get("song", "http://subsonic.org/restapi"))
                .Select(song => song.Attribute("id")?.Value));
        }
        Assert.Empty(observed);
    }

    [Theory]
    [InlineData("getSimilarSongs", "native-song-1")]
    [InlineData("getSimilarSongs2", "native-artist-1")]
    [InlineData("getArtistInfo2", "native-artist-1")]
    public async Task SubsonicSimilarSongsAndArtistInfo_RelayNativeIdsUnchanged(string endpoint, string id)
    {
        const string native = """{"subsonic-response":{"status":"ok","version":"1.16.1","nativeAnswer":{"futureField":"kept"}}}""";
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        var observed = new List<ObservedRequest>();
        using var factory = CatalogFactory(observed, gateway.Object, _ => Json(200, native));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            $"/rest/{endpoint}.view?u=fixture&p=secret&v=1.16.1&c=fixture&f=json&id={id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(native, await response.Content.ReadAsStringAsync());
        Assert.Equal(
            $"/rest/{endpoint}.view?u=fixture&p=secret&v=1.16.1&c=fixture&f=json&id={id}",
            Assert.Single(observed).PathAndQuery);
        gateway.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("json", false)]
    [InlineData("json", true)]
    [InlineData("xml", false)]
    [InlineData("xml", true)]
    public async Task SubsonicArtistInfo2_ProviderArtistGetsAnEmptyValidAnswer(string format, bool nullResult)
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.GetSimilarArtistsAsync(
                It.IsAny<ProtocolExecutionContext>(), "deezer", "7", 20))
            .ReturnsAsync(nullResult ? null! : []);
        gateway.Setup(item => item.GetStoredArtistAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "7"))
            .ReturnsAsync((Artist?)null);
        var observed = new List<ObservedRequest>();
        using var factory = CatalogFactory(observed, gateway.Object,
            request => throw new InvalidOperationException($"Unexpected upstream request: {request.RequestUri}"));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            $"/rest/getArtistInfo2.view?u=fixture&p=secret&v=1.16.1&c=fixture&f={format}&id=ext-deezer-artist-7");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        if (format == "json")
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement.GetProperty("subsonic-response");
            Assert.Equal("ok", root.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Object, root.GetProperty("artistInfo2").ValueKind);
        }
        else
        {
            var root = XDocument.Parse(body).Root!;
            Assert.Equal("ok", root.Attribute("status")?.Value);
            Assert.Equal(XName.Get("artistInfo2", "http://subsonic.org/restapi"), Assert.Single(root.Elements()).Name);
        }
        Assert.Empty(observed);
        gateway.VerifyAll();
        gateway.VerifyNoOtherCalls();
    }

    private static string NativeCatalogBody(string endpoint, string format) => (endpoint, format) switch
    {
        ("getArtist", "json") => NativeArtistJson,
        ("getArtist", _) => NativeArtistXml,
        (_, "json") => NativeAlbumJson,
        _ => NativeAlbumXml
    };

    private static HttpResponseMessage CatalogNative(string format, string json, string xml) =>
        format == "json"
            ? Json(200, json)
            : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(xml, System.Text.Encoding.UTF8, "application/xml")
            };

    private ProtocolFactory CatalogFactory(
        List<ObservedRequest> observed,
        IProtocolProviderGateway gateway,
        Func<HttpRequestMessage, HttpResponseMessage> native) =>
        new("Subsonic", request =>
        {
            if (request.RequestUri!.AbsolutePath == "/rest/ping.view")
                return Json(200, """{"subsonic-response":{"status":"ok","version":"1.16.1"}}""");
            observed.Add(Observe(request));
            return native(request);
        }, services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway);
        });

    private static Artist CatalogArtist(string id, int albumCount) => new()
    {
        Id = $"ext-deezer-artist-{id}",
        Name = "Grimes",
        AlbumCount = albumCount,
        ExternalProvider = "deezer",
        ExternalId = id
    };

    private static Album CatalogAlbum(string id, string title) => new()
    {
        Id = $"ext-deezer-album-{id}",
        Title = title,
        Upc = title == "Visions" ? "012345678905" : null,
        Artist = "Grimes",
        ExternalProvider = "deezer",
        ExternalId = id
    };
}
