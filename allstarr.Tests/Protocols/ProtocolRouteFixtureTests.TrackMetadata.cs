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
    [Theory]
    [InlineData("json", true)]
    [InlineData("xml", true)]
    [InlineData("json", false)]
    [InlineData("xml", false)]
    public async Task SubsonicMetadataIsConsistentOnSongSearchAndAlbum(string format, bool known)
    {
        var song = MetadataSong(known);
        var gateway = MetadataGateway(song);
        using var factory = new ProtocolFactory("Subsonic", request =>
        {
            var search = request.RequestUri!.AbsolutePath.Contains("search3", StringComparison.OrdinalIgnoreCase);
            if (Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query)["f"] == "xml")
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                    "<subsonic-response xmlns=\"http://subsonic.org/restapi\" status=\"ok\" version=\"1.16.1\">" +
                    (search ? "<searchResult3 />" : "") + "</subsonic-response>", System.Text.Encoding.UTF8, "application/xml")
                };
            return Json(200, search
                ? """{"subsonic-response":{"status":"ok","version":"1.16.1","searchResult3":{}}}"""
                : """{"subsonic-response":{"status":"ok","version":"1.16.1"}}""");
        }, services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
        });
        using var client = factory.CreateClient();
        foreach (var request in new[] { "getSong&id=" + song.Id, "getAlbum&id=" + song.AlbumId, "search3&query=Song" })
        {
            using var response = await client.GetAsync($"/rest/{request.Split('&')[0]}?{request[(request.IndexOf('&') + 1)..]}&u=fixture&p=fixture&v=1.16.1&c=Tempo&f={format}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            if (format == "json")
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement.GetProperty("subsonic-response");
                var item = request.StartsWith("getSong", StringComparison.Ordinal) ? root.GetProperty("song") :
                    request.StartsWith("getAlbum", StringComparison.Ordinal) ? root.GetProperty("album").GetProperty("song")[0] :
                    root.GetProperty("searchResult3").GetProperty("song")[0];
                Assert.Equal(known, item.TryGetProperty("size", out var size));
                Assert.Equal(known, item.TryGetProperty("replayGain", out var gain));
                Assert.Equal(known, item.TryGetProperty("year", out _));
                if (known)
                {
                    Assert.Equal(30645098, size.GetInt64());
                    Assert.Equal(0, gain.GetProperty("trackGain").GetDouble());
                    Assert.Equal(44100, item.GetProperty("samplingRate").GetInt32());
                    Assert.Equal("wav", item.GetProperty("suffix").GetString());
                }
            }
            else
            {
                var item = XDocument.Parse(body).Descendants().Single(element =>
                    element.Name.LocalName == "song" && element.Attribute("id")?.Value == song.Id);
                Assert.Equal(known, item.Attribute("size") != null);
                Assert.Equal(known, item.Element(item.Name.Namespace + "replayGain") != null);
                Assert.Equal(known, item.Attribute("year") != null);
                if (known)
                {
                    Assert.Equal("30645098", item.Attribute("size")!.Value);
                    Assert.Equal("0", item.Element(item.Name.Namespace + "replayGain")!.Attribute("trackGain")!.Value);
                    Assert.Equal("44100", item.Attribute("samplingRate")!.Value);
                }
            }
        }
    }

    [Theory]
    [InlineData("Jellyfin", "song")]
    [InlineData("Jellyfin", "album")]
    [InlineData("Subsonic", "song")]
    [InlineData("Subsonic", "album")]
    public async Task ArtistGridCoversUseRasterImagesAndConditionalRequests(string backend, string kind)
    {
        var song = MetadataSong(true);
        var gateway = MetadataGateway(song);
        using var factory = new ProtocolFactory(backend, request => request.RequestUri!.AbsolutePath == "/Users/Me"
            ? Json(200, """{"Id":"user-1"}""")
            : Json(200, """{"subsonic-response":{"status":"ok","version":"1.16.1"}}"""), services =>
        {
            if (backend == "Jellyfin") AddArtworkPrincipal(services, "user-1");
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
        });
        using var client = factory.CreateClient();
        var id = kind == "song" ? song.Id : song.AlbumId;
        var url = backend == "Jellyfin" ? $"/Items/{id}/Images/Primary?api_key=fixture-key" :
            $"/rest/getCoverArt?id={id}&u=fixture&p=fixture&v=1.16.1&c=Tempo";
        using var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, bytes[..8]);
        using var bitmap = SkiaSharp.SKBitmap.Decode(bytes);
        Assert.Equal(600, bitmap.Width);
        Assert.Equal(600, bitmap.Height);
        Assert.NotNull(response.Headers.ETag);
        Assert.NotNull(response.Headers.CacheControl);
        using var conditional = new HttpRequestMessage(HttpMethod.Get, url);
        conditional.Headers.IfNoneMatch.Add(response.Headers.ETag!);
        using var repeated = await client.SendAsync(conditional);
        Assert.Equal(HttpStatusCode.NotModified, repeated.StatusCode);
        Assert.Empty(await repeated.Content.ReadAsByteArrayAsync());
    }

    private static Song MetadataSong(bool known) => new()
    {
        Id = "ext-artistgrid-song-metadata",
        ExternalProvider = "artistgrid",
        ExternalId = "metadata",
        Title = "Song",
        Artist = "Artist",
        Album = "Era",
        AlbumId = "ext-artistgrid-album-era",
        CoverArtUrl = "/images/providers/artistgrid-cover.png",
        Duration = 115,
        Container = known ? "wav" : null,
        Codec = known ? "pcm_s24le" : null,
        Size = known ? 30645098 : null,
        Bitrate = known ? 2116800 : null,
        SampleRate = known ? 44100 : null,
        BitDepth = known ? 24 : null,
        Channels = known ? 2 : null,
        ReleaseDate = known ? "2020-07-17" : null,
        ReplayGain = known ? new() { TrackGain = 0, TrackPeak = 0.98 } : null
    };

    private static Mock<IProtocolProviderGateway> MetadataGateway(Song song)
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "artistgrid", "metadata")).ReturnsAsync(song);
        gateway.Setup(item => item.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "artistgrid", "era")).ReturnsAsync(new Album
        {
            Id = song.AlbumId!,
            ExternalProvider = "artistgrid",
            ExternalId = "era",
            Title = "Era",
            Artist = "Artist",
            CoverArtUrl = song.CoverArtUrl,
            Songs = [song]
        });
        gateway.Setup(item => item.SearchAsync(It.IsAny<ProtocolExecutionContext>(), "Song", It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync(new SearchResult { Songs = [song] });
        gateway.Setup(item => item.GetPublicArtworkUriAsync("artistgrid", It.IsAny<allstarr.Core.Capabilities.ProviderResourceKind>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((Uri?)null);
        return gateway;
    }
}
