using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Models.Search;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    private const string NativeTopSongsJson =
        """{"subsonic-response":{"status":"ok","version":"1.16.1","topSongs":{"song":[{"id":"native-1","title":"Oblivion","artist":"Grimes","futureField":"kept"}]}}}""";
    private const string NativeTopSongsXml =
        """<subsonic-response xmlns="http://subsonic.org/restapi" status="ok" version="1.16.1"><topSongs><song id="native-1" title="Oblivion" artist="Grimes" futureField="kept"/></topSongs></subsonic-response>""";

    [Theory]
    [InlineData("json")]
    [InlineData("xml")]
    public async Task SubsonicTopSongs_AppendsTheArtistsProviderSongsAfterTheLibrary(string format)
    {
        var gateway = TopSongsGateway(new SearchResult
        {
            Songs =
            [
                ProviderSong("ext-deezer-song-1", "Oblivion"),
                ProviderSong("ext-deezer-song-2", "Genesis"),
                ProviderSong("ext-deezer-song-3", "Grimes Song", "Gary Grimes"),
                ProviderSong("ext-deezer-song-4", "Realiti")
            ]
        });
        using var factory = TopSongsFactory(format, gateway.Object);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            $"/rest/getTopSongs.view?u=fixture&p=secret&v=1.16.1&c=fixture&f={format}&artist=Grimes&count=2");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        if (format == "json")
        {
            using var document = JsonDocument.Parse(body);
            var songs = document.RootElement.GetProperty("subsonic-response").GetProperty("topSongs")
                .GetProperty("song").EnumerateArray().ToList();
            Assert.Equal(["native-1", "ext-deezer-song-2"], songs.Select(song => song.GetProperty("id").GetString()));
            Assert.Equal("kept", songs[0].GetProperty("futureField").GetString());
        }
        else
        {
            var songs = XDocument.Parse(body).Descendants().Where(element => element.Name.LocalName == "song").ToList();
            Assert.Equal(["native-1", "ext-deezer-song-2"], songs.Select(song => song.Attribute("id")?.Value));
            Assert.Equal("kept", songs[0].Attribute("futureField")?.Value);
        }
        gateway.Verify(item => item.SearchAsync(It.IsAny<ProtocolExecutionContext>(), "Grimes", 40, 0, 0, null), Times.Once);
    }

    [Fact]
    public async Task SubsonicTopSongs_KeepsTheNativeResponseWhenProvidersAddNothing()
    {
        var gateway = TopSongsGateway(new SearchResult { Songs = [ProviderSong("ext-deezer-song-1", "Oblivion")] });
        using var factory = TopSongsFactory("json", gateway.Object);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/rest/getTopSongs.view?u=fixture&p=secret&v=1.16.1&c=fixture&f=json&artist=Grimes");

        Assert.Equal(NativeTopSongsJson, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SubsonicTopSongs_ProviderFailureKeepsTheLibrarySongs()
    {
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.SearchAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), null))
            .ThrowsAsync(new HttpRequestException("provider down"));
        using var factory = TopSongsFactory("json", gateway.Object);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/rest/getTopSongs.view?u=fixture&p=secret&v=1.16.1&c=fixture&f=json&artist=Grimes");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(NativeTopSongsJson, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SubsonicTopSongs_WithoutAnArtistKeepsTheBackendAnswer()
    {
        const string error = """{"subsonic-response":{"status":"failed","version":"1.16.1","error":{"code":10,"message":"Required parameter is missing"}}}""";
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        using var factory = new ProtocolFactory("Subsonic", request =>
            request.RequestUri!.AbsolutePath == "/rest/ping.view"
                ? Json(200, """{"subsonic-response":{"status":"ok","version":"1.16.1"}}""")
                : Json(200, error), services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
        });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/rest/getTopSongs.view?u=fixture&p=secret&v=1.16.1&c=fixture&f=json");

        Assert.Equal(error, await response.Content.ReadAsStringAsync());
        gateway.VerifyNoOtherCalls();
    }

    private static Mock<IProtocolProviderGateway> TopSongsGateway(SearchResult result)
    {
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.SearchAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), null))
            .ReturnsAsync(result);
        return gateway;
    }

    private ProtocolFactory TopSongsFactory(string format, IProtocolProviderGateway gateway) =>
        new("Subsonic", request =>
        {
            if (request.RequestUri!.AbsolutePath == "/rest/ping.view")
                return Json(200, """{"subsonic-response":{"status":"ok","version":"1.16.1"}}""");
            Assert.Equal("/rest/getTopSongs.view", request.RequestUri.AbsolutePath);
            return format == "json"
                ? Json(200, NativeTopSongsJson)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(NativeTopSongsXml, System.Text.Encoding.UTF8, "application/xml")
                };
        }, services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway);
        });

    private static Song ProviderSong(string id, string title, string artist = "Grimes") => new()
    {
        Id = id,
        Title = title,
        Artist = artist,
        ExternalProvider = "deezer",
        ExternalId = id.Split('-').Last(),
        IsLocal = false
    };
}
