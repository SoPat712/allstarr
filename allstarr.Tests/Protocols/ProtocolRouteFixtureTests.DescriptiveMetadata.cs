using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Theory]
    [InlineData("Items", "Jellyfin Web")]
    [InlineData("Albums", "Finamp")]
    public async Task AlbumSimilar_ClientRequestsRespectLimitsAndExclusions(string route, string clientName)
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(value => value.GetStoredAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "7"))
            .ReturnsAsync(new Album { Id = "ext-deezer-album-7", ArtistId = "ext-deezer-artist-2", Artist = "Artist" });
        gateway.Setup(value => value.GetArtistAlbumsAsync(It.Is<ProtocolExecutionContext>(context => context.IsReadOnly), "deezer", "2"))
            .ReturnsAsync([RelatedAlbum("7"), RelatedAlbum("8"), RelatedAlbum("9"), RelatedAlbum("9"), RelatedAlbum("10")]);
        gateway.Setup(value => value.GetSimilarArtistsAsync(It.Is<ProtocolExecutionContext>(context => context.IsReadOnly), "deezer", "2", 20))
            .ReturnsAsync([]);
        using var factory = DescriptiveFactory("Jellyfin", gateway.Object);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/{route}/ext-deezer-album-7/Similar?UserId=user-1&Limit=1&ExcludeItemIds=ext-deezer-album-8&ExcludeArtistIds=ext-deezer-artist-99&Fields=ChildCount,ProviderIds,Studios&ApiKey=fixture-key");
        request.Headers.Add("X-Emby-Authorization", $"MediaBrowser Client=\"{clientName}\", DeviceId=\"fixture\", Version=\"1\"");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        var item = Assert.Single(root.GetProperty("Items").EnumerateArray());
        Assert.Equal("ext-deezer-album-9", item.GetProperty("Id").GetString());
        Assert.Equal("MusicAlbum", item.GetProperty("Type").GetString());
        Assert.Equal(1, root.GetProperty("TotalRecordCount").GetInt32());
        Assert.Equal(0, root.GetProperty("StartIndex").GetInt32());
        Assert.False(item.GetProperty("UserData").GetProperty("Played").GetBoolean());
        gateway.VerifyAll();
        gateway.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task WebAlbumSimilar_ExcludesCurrentArtistAndUsesKnownRelatedArtistAlbums()
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(value => value.GetStoredAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "7"))
            .ReturnsAsync(new Album { Id = "ext-deezer-album-7", ArtistId = "ext-deezer-artist-2", Artist = "Host" });
        gateway.Setup(value => value.GetSimilarArtistsAsync(It.Is<ProtocolExecutionContext>(context => context.IsReadOnly), "deezer", "2", 20))
            .ReturnsAsync([new Artist { Id = "ext-deezer-artist-4", Name = "Related artist" }]);
        var related = RelatedAlbum("11");
        related.ArtistId = "ext-deezer-artist-4";
        gateway.Setup(value => value.GetArtistAlbumsAsync(It.Is<ProtocolExecutionContext>(context => context.IsReadOnly), "deezer", "4"))
            .ReturnsAsync([related]);
        using var factory = DescriptiveFactory("Jellyfin", gateway.Object);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/Items/ext-deezer-album-7/Similar?userId=user-1&limit=12&fields=PrimaryImageAspectRatio,CanDelete&ExcludeArtistIds=ext-deezer-artist-2&ApiKey=fixture-key");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ext-deezer-album-11", Assert.Single(body.RootElement.GetProperty("Items").EnumerateArray()).GetProperty("Id").GetString());
        gateway.VerifyAll();
        gateway.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AlbumSimilar_ZeroLimitDoesNotReadMetadata()
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        using var factory = DescriptiveFactory("Jellyfin", gateway.Object);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/Albums/ext-deezer-album-7/Similar?Limit=0&ApiKey=fixture-key");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Empty(body.RootElement.GetProperty("Items").EnumerateArray());
        gateway.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("json")]
    [InlineData("xml")]
    public async Task DescriptiveNativeReads_PreserveResponseBytes(string format)
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        var body = format == "json" ? """{ "subsonic-response": {"status":"ok","albumInfo":{"notes":"Native","future":true}} }"""
            : "<subsonic-response status='ok'><albumInfo><notes>Native &amp; original</notes><future /></albumInfo></subsonic-response>";
        using var factory = CatalogFactory([], gateway.Object, _ => CatalogNative(format, body, body));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/rest/getAlbumInfo2.view?u=fixture&p=secret&v=1.16.1&c=tempo&f={format}&id=native");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(body, await response.Content.ReadAsStringAsync());
        gateway.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("json")]
    [InlineData("xml")]
    public async Task FeishinTopSongs_AdvertisedArtistIdUsesStoredTracks(string format)
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(value => value.GetArtistTracksAsync(It.Is<ProtocolExecutionContext>(context => context.IsReadOnly), "deezer", "2"))
            .ReturnsAsync([DescriptiveSong(), DescriptiveSong()]);
        using var factory = DescriptiveFactory("Subsonic", gateway.Object);
        using var client = factory.CreateClient();
        var extension = await client.GetStringAsync($"/rest/getOpenSubsonicExtensions.view?u=fixture&p=secret&v=1.13.0&c=Feishin&f={format}");
        Assert.Contains("topSongsByArtistId", extension, StringComparison.Ordinal);
        using var response = await client.PostAsync("/rest/getTopSongs.view", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["u"] = "fixture",
            ["p"] = "secret",
            ["v"] = "1.13.0",
            ["c"] = "Feishin",
            ["f"] = format,
            ["id"] = "ext-deezer-artist-2",
            ["count"] = "1"
        }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var song = Assert.Single(ReadSongCollection(await response.Content.ReadAsStringAsync(), format, "topSongs", "song"));
        Assert.Equal("ext-deezer-song-42", song["id"]);
        Assert.Equal("audio/mpeg", song["contentType"]);
        Assert.Equal("mp3", song["suffix"]);
        Assert.False(song.ContainsKey("bitRate"));
        Assert.False(song.ContainsKey("size"));
        gateway.VerifyAll();
        gateway.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("json", false)]
    [InlineData("xml", false)]
    [InlineData("json", true)]
    [InlineData("xml", true)]
    public async Task TempoInfo_UsesStoredDescriptionsAndToleratesMissingMetadata(string format, bool sparse)
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(value => value.GetStoredAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "7"))
            .ReturnsAsync(sparse ? null : JsonSerializer.Deserialize<Album>("""{"Id":"ext-deezer-album-7","Notes":"Album notes & details","ProviderIds":{"MusicBrainzAlbum":"release-id"},"LastFmUrl":"https://www.last.fm/music/Fixture/Album","CoverArtUrl":"https://images.invalid/album.jpg"}"""));
        gateway.Setup(value => value.GetStoredArtistAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "2"))
            .ReturnsAsync(sparse ? null : JsonSerializer.Deserialize<Artist>("""{"Id":"ext-deezer-artist-2","Biography":"Artist biography & details","ProviderIds":{"MusicBrainzArtist":"artist-id"},"LastFmUrl":"https://www.last.fm/music/Fixture","ImageUrl":"https://images.invalid/artist.jpg"}"""));
        gateway.Setup(value => value.GetSimilarArtistsAsync(It.Is<ProtocolExecutionContext>(context => context.IsReadOnly), "deezer", "2", 20)).ReturnsAsync([]);
        using var factory = DescriptiveFactory("Subsonic", gateway.Object);
        using var client = factory.CreateClient();
        foreach (var album in new[] { true, false })
        {
            var endpoint = album ? "getAlbumInfo2" : "getArtistInfo2";
            using var response = await client.GetAsync($"/rest/{endpoint}.view?u=fixture&p=secret&v=1.16.1&c=tempo&f={format}&id=ext-deezer-{(album ? "album-7" : "artist-2")}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            var element = album ? "albumInfo" : "artistInfo2";
            var values = format == "json"
                ? JsonDocument.Parse(body).RootElement.GetProperty("subsonic-response").GetProperty(element).EnumerateObject()
                    .ToDictionary(property => property.Name, property => property.Value.ToString())
                : XDocument.Parse(body).Root!.Elements().Single(value => value.Name.LocalName == element).Elements()
                    .ToDictionary(value => value.Name.LocalName, value => value.Value);
            if (sparse) Assert.Empty(values);
            else
            {
                Assert.Equal(album ? "Album notes & details" : "Artist biography & details", values[album ? "notes" : "biography"]);
                Assert.Equal(album ? "release-id" : "artist-id", values["musicBrainzId"]);
                Assert.Equal(album ? "https://images.invalid/album.jpg" : "https://images.invalid/artist.jpg", values["largeImageUrl"]);
            }
        }
        gateway.VerifyAll();
        gateway.VerifyNoOtherCalls();
    }

    private static Album RelatedAlbum(string id) => new()
    {
        Id = $"ext-deezer-album-{id}",
        ExternalProvider = "deezer",
        ExternalId = id,
        Title = "Related",
        Artist = "Artist",
        ArtistId = "ext-deezer-artist-2"
    };

    private static Song DescriptiveSong() => JsonSerializer.Deserialize<Song>("""
        {"Id":"ext-deezer-song-42","ExternalProvider":"deezer","ExternalId":"42","Title":"Guest song",
         "Artist":"Guest","ArtistId":"ext-deezer-artist-3","AlbumArtist":"Host","LocalPath":"/fixture/song.mp3",
         "AlbumArtists":[{"Id":"ext-deezer-artist-2","Name":"Host"}],"Label":"Fixture label","Composer":"Writer",
         "Contributors":["Guest"],"ProviderIds":{"MusicBrainzTrack":"recording-id"},"ReleaseDate":"2024-02-29"}
        """)!;

    private ProtocolFactory DescriptiveFactory(string backend, IProtocolProviderGateway gateway) => new(backend, request =>
    {
        if (request.RequestUri!.AbsolutePath == "/Users/Me") return Json(200, """{"Id":"user-1"}""");
        if (request.RequestUri.AbsolutePath == "/rest/ping.view") return Json(200, """{"subsonic-response":{"status":"ok","version":"1.16.1"}}""");
        if (request.RequestUri.AbsolutePath == "/rest/getOpenSubsonicExtensions.view")
            return CatalogNative(Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query)["f"].ToString(),
                """{"subsonic-response":{"status":"ok","version":"1.16.1","openSubsonicExtensions":[{"name":"topSongsByArtistId","versions":[1]}]}}""",
                """<subsonic-response xmlns="http://subsonic.org/restapi" status="ok" version="1.16.1"><openSubsonicExtensions name="topSongsByArtistId"><versions>1</versions></openSubsonicExtensions></subsonic-response>""");
        throw new InvalidOperationException($"Unexpected native request: {request.RequestUri.AbsolutePath}");
    }, services =>
    {
        services.RemoveAll<IProtocolProviderGateway>();
        services.AddSingleton(gateway);
    });
}
