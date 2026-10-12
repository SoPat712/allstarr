using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Models.Search;
using allstarr.Models.Subsonic;
using allstarr.Services.Common;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Theory]
    [InlineData("json", "album")]
    [InlineData("xml", "album")]
    [InlineData("json", "artist")]
    [InlineData("xml", "artist")]
    [InlineData("json", "playlist")]
    [InlineData("xml", "playlist")]
    public async Task SubsonicMusicDirectory_RendersProviderChildrenWithoutNativeRelay(string format, string kind)
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        var song = new Song { Id = "ext-deezer-song-1", Title = "Track", Artist = "Artist", ExternalProvider = "deezer", ExternalId = "1" };
        var id = $"ext-deezer-{kind}-7";
        if (kind == "album")
            gateway.Setup(item => item.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "7"))
                .ReturnsAsync(new Album { Id = id, Title = "Album", Songs = [song] });
        else if (kind == "artist")
        {
            gateway.Setup(item => item.GetArtistAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "7"))
                .ReturnsAsync(new Artist { Id = id, Name = "Artist" });
            gateway.Setup(item => item.GetArtistAlbumsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "7"))
                .ReturnsAsync([CatalogAlbum("1", "Album")]);
        }
        else
        {
            id = PlaylistIdHelper.CreatePlaylistId("deezer", "7");
            gateway.Setup(item => item.GetPlaylistAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "7"))
                .ReturnsAsync(new ExternalPlaylist { Id = "7", Name = "Playlist", Provider = "deezer" });
            gateway.Setup(item => item.GetPlaylistTracksAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "7"))
                .ReturnsAsync([song]);
        }
        var observed = new List<ObservedRequest>();
        using var factory = CatalogFactory(observed, gateway.Object, _ => throw new InvalidOperationException("Unexpected native relay"));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/rest/getMusicDirectory.view?u=fixture&p=secret&v=1.16.1&c=fixture&f={format}&id={id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var expectedChild = kind == "artist" ? "ext-deezer-album-1" : song.Id;
        if (format == "json")
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement.GetProperty("subsonic-response");
            Assert.Equal("ok", root.GetProperty("status").GetString());
            var directory = root.GetProperty("directory");
            Assert.Equal(id, directory.GetProperty("id").GetString());
            var child = Assert.Single(directory.GetProperty("child").EnumerateArray());
            Assert.Equal(expectedChild, child.GetProperty("id").GetString());
            Assert.Equal(kind == "artist", child.GetProperty("isDir").GetBoolean());
        }
        else
        {
            var root = XDocument.Parse(body).Root!;
            Assert.Equal("ok", root.Attribute("status")?.Value);
            var directory = Assert.Single(root.Elements());
            Assert.Equal("directory", directory.Name.LocalName);
            Assert.Equal(id, directory.Attribute("id")?.Value);
            var child = Assert.Single(directory.Elements());
            Assert.Equal("child", child.Name.LocalName);
            Assert.Equal(expectedChild, child.Attribute("id")?.Value);
            Assert.Equal(kind == "artist" ? "true" : "false", child.Attribute("isDir")?.Value);
        }
        Assert.Empty(observed);
        gateway.VerifyAll();
        gateway.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("json")]
    [InlineData("xml")]
    public async Task SubsonicMusicDirectory_NativeResponseStaysByteIdentical(string format)
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        const string json = """{ "subsonic-response": {"status":"ok","directory":{"id":"native","future":"kept"}} }""";
        const string xml = """<subsonic-response xmlns="http://subsonic.org/restapi" status="ok"><directory id="native" future="kept" /></subsonic-response>""";
        var observed = new List<ObservedRequest>();
        using var factory = CatalogFactory(observed, gateway.Object, _ => CatalogNative(format, json, xml));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/rest/getMusicDirectory.view?u=fixture&p=secret&v=1.16.1&c=fixture&f={format}&id=native");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(format == "json" ? json : xml, await response.Content.ReadAsStringAsync());
        Assert.StartsWith("/rest/getMusicDirectory.view?", Assert.Single(observed).PathAndQuery);
        gateway.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("json")]
    [InlineData("xml")]
    public async Task SubsonicSearch2_UsesTheMatchingBackendMethodAndDirectoryAlbums(string format)
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.SearchAsync(It.IsAny<ProtocolExecutionContext>(), "Album", 0, 2, 0, null))
            .ReturnsAsync(new SearchResult { Albums = [CatalogAlbum("1", "Album")] });
        gateway.Setup(item => item.SearchPlaylistsAsync(It.IsAny<ProtocolExecutionContext>(), "Album", 2))
            .ReturnsAsync([]);
        var observed = new List<ObservedRequest>();
        using var factory = CatalogFactory(observed, gateway.Object, _ => CatalogNative(format,
            """{"subsonic-response":{"status":"ok","version":"1.16.1","searchResult2":{"album":[{"id":"native","title":"Album","isDir":true,"future":"kept"}]}}}""",
            """<subsonic-response xmlns="http://subsonic.org/restapi" status="ok" version="1.16.1"><searchResult2><album id="native" title="Album" isDir="true" future="kept"/></searchResult2></subsonic-response>"""));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/rest/search2.view?u=fixture&p=secret&v=1.16.1&c=fixture&f={format}&query=Album&songCount=0&artistCount=0&albumCount=2");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        if (format == "json")
        {
            using var document = JsonDocument.Parse(body);
            var albums = document.RootElement.GetProperty("subsonic-response").GetProperty("searchResult2").GetProperty("album").EnumerateArray().ToArray();
            Assert.Equal(2, albums.Length);
            Assert.Equal("kept", albums.Single(item => item.GetProperty("id").GetString() == "native").GetProperty("future").GetString());
            var album = albums.Single(item => item.GetProperty("id").GetString() == "ext-deezer-album-1");
            Assert.True(album.GetProperty("isDir").GetBoolean());
            Assert.Equal("Album", album.GetProperty("title").GetString());
        }
        else
        {
            var root = XDocument.Parse(body).Root!;
            var result = Assert.Single(root.Elements());
            Assert.Equal("searchResult2", result.Name.LocalName);
            Assert.Equal(2, result.Elements().Count());
            Assert.Equal("kept", result.Elements().Single(item => item.Attribute("id")?.Value == "native").Attribute("future")?.Value);
            var album = result.Elements().Single(item => item.Attribute("id")?.Value == "ext-deezer-album-1");
            Assert.Equal("true", album.Attribute("isDir")?.Value);
            Assert.Equal("Album", album.Attribute("title")?.Value);
        }
        Assert.StartsWith("/rest/search2?", Assert.Single(observed).PathAndQuery);
        gateway.Verify(item => item.SearchAsync(It.IsAny<ProtocolExecutionContext>(), "Album", 0, 2, 0, null), Times.Once);
    }
}
