using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using allstarr.Core.Matching;
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
    [InlineData("Jellyfin")]
    [InlineData("Subsonic")]
    public async Task Playback_KnownAccessibleCopyUsesNativeStreamWithOriginalAuthorization(string backend)
    {
        const string publicId = "ext-deezer-first";
        var index = new Mock<ILibraryIndexService>(MockBehavior.Strict);
        index.Setup(item => item.ResolveKnownTrackAsync(It.IsAny<ProtocolExecutionContext>(), publicId, It.IsAny<CancellationToken>()))
            .ReturnsAsync("native-track");
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        var nativeOpened = false;
        using var factory = new ProtocolFactory(backend, request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/Users/Me") return Json(200, """{"Id":"verified-user"}""");
            if (path.Contains("ping", StringComparison.Ordinal)) return Json(200, """{"subsonic-response":{"status":"ok","version":"1.16.1"}}""");
            if (backend == "Jellyfin")
            {
                Assert.Equal("/Audio/native-track/stream", path);
                Assert.Contains("fixture-key", request.RequestUri.Query, StringComparison.Ordinal);
            }
            else
            {
                Assert.Equal("/rest/stream", path);
                var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query);
                Assert.Equal("native-track", query["id"].ToString());
                Assert.Equal("listener", query["u"].ToString());
            }
            nativeOpened = true;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0x66, 0x4c, 0x61, 0x43]) };
        }, services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
            services.RemoveAll<ILibraryIndexService>();
            services.AddSingleton(index.Object);
        });
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(backend == "Jellyfin"
            ? $"/Audio/{publicId}/stream?api_key=fixture-key"
            : $"/rest/stream?u=listener&p=secret&v=1.16.1&c=fixture&id={publicId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new byte[] { 0x66, 0x4c, 0x61, 0x43 }, await response.Content.ReadAsByteArrayAsync());
        Assert.True(nativeOpened);
        gateway.VerifyNoOtherCalls();
        index.VerifyAll();
    }

    [Theory]
    [InlineData("Jellyfin", "json", false)]
    [InlineData("Jellyfin", "json", true)]
    [InlineData("Subsonic", "json", false)]
    [InlineData("Subsonic", "xml", false)]
    public async Task Search_KnownRecordingReturnsTheCompleteNativeObject(string backend, string format, bool denied)
    {
        const string externalId = "ext-deezer-song-42";
        var index = new Mock<ILibraryIndexService>(MockBehavior.Strict);
        index.Setup(item => item.FindKnownRecordingTracksAsync(It.IsAny<ProtocolExecutionContext>(),
                It.Is<IReadOnlyList<LibraryRecordingLookup>>(items => items.Count == 1 && items[0].PublicId == externalId),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string> { [externalId] = "native-track" });
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.SearchAsync(It.IsAny<ProtocolExecutionContext>(), "fixture", It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync(() => new SearchResult
            {
                Songs = [new Song { Id = externalId, ExternalId = "42", ExternalProvider = "deezer",
                Title = "Fixture", Artist = "Artist", Isrc = "USRC17607839" }]
            });
        using var factory = new ProtocolFactory(backend, request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/Users/Me") return Json(200, """{"Id":"verified-user"}""");
            if (path.Contains("ping", StringComparison.Ordinal)) return Json(200, """{"subsonic-response":{"status":"ok","version":"1.16.1"}}""");
            if (path == "/Items") return Json(200, """{"Items":[],"TotalRecordCount":0}""");
            if (path.Contains("search3", StringComparison.Ordinal)) return format == "json"
                ? Json(200, """{"subsonic-response":{"status":"ok","searchResult3":{}}}""")
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<subsonic-response xmlns=\"http://subsonic.org/restapi\" status=\"ok\"><searchResult3 /></subsonic-response>", Encoding.UTF8, "application/xml") };
            if (denied) return Json(403, """{"error":"unavailable"}""");
            if (path.EndsWith("/Items/native-track", StringComparison.Ordinal))
                return Json(200, """{"Id":"native-track","Type":"Audio","Name":"Fixture","Artists":["Artist"],"UnknownField":{"Keep":"native"}}""");
            if (path.Contains("getSong", StringComparison.Ordinal)) return format == "json"
                ? Json(200, """{"subsonic-response":{"status":"ok","song":{"id":"native-track","title":"Fixture","artist":"Artist","unknownField":"native"}}}""")
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<subsonic-response xmlns=\"http://subsonic.org/restapi\" status=\"ok\"><song id=\"native-track\" title=\"Fixture\" artist=\"Artist\" unknownField=\"native\" /></subsonic-response>", Encoding.UTF8, "application/xml") };
            throw new InvalidOperationException("Unexpected fixture route: " + path);
        }, services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
            services.RemoveAll<ILibraryIndexService>();
            services.AddSingleton(index.Object);
        }, new Dictionary<string, string?> { [$"{backend}:EnableExternalPlaylists"] = "false" });
        using var client = factory.CreateClient();
        var url = backend == "Jellyfin" ? "/Items?SearchTerm=fixture&IncludeItemTypes=Audio&api_key=fixture-key"
            : $"/rest/search3?u=listener&p=secret&v=1.16.1&c=fixture&query=fixture&f={format}";
        using var response = await client.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        if (format == "xml")
        {
            var xml = XDocument.Parse(body);
            var song = Assert.Single(xml.Descendants(), item => item.Name.LocalName == "song");
            Assert.Equal("native-track", song.Attribute("id")?.Value);
            Assert.Equal("native", song.Attribute("unknownField")?.Value);
        }
        else
        {
            using var document = JsonDocument.Parse(body);
            var items = backend == "Jellyfin" ? document.RootElement.GetProperty("Items")
                : document.RootElement.GetProperty("subsonic-response").GetProperty("searchResult3").GetProperty("song");
            var song = Assert.Single(items.EnumerateArray());
            Assert.Equal(denied ? externalId : "native-track", song.GetProperty(backend == "Jellyfin" ? "Id" : "id").GetString());
            if (!denied) Assert.Equal("native", backend == "Jellyfin"
                ? song.GetProperty("UnknownField").GetProperty("Keep").GetString() : song.GetProperty("unknownField").GetString());
        }
        index.VerifyAll();
    }
}
