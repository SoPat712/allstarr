using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using allstarr.Core.Capabilities;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Providers.Lyrics;
using allstarr.Models.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Theory]
    [InlineData("Jellyfin", "json", "syllable")]
    [InlineData("Jellyfin", "json", "line")]
    [InlineData("Jellyfin", "json", "static")]
    [InlineData("Subsonic", "json", "syllable")]
    [InlineData("Subsonic", "json", "line")]
    [InlineData("Subsonic", "json", "static")]
    [InlineData("Subsonic", "xml", "syllable")]
    [InlineData("Subsonic", "xml", "line")]
    [InlineData("Subsonic", "xml", "static")]
    public async Task SpicyRealAdapterOutputReachesBothProtocolRoutes(string backend, string format, string fixture)
    {
        using var handler = new SpicyLyricsTestHandler(SpicyLyricsTestData.Payload(fixture));
        using var upstream = new HttpClient(handler);
        var source = new SpicyLyricsCapabilityAdapter(upstream, new SpicyLyricsTestSecrets());
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Lyrics)).Returns(["spicylyrics"]);
        gateway.Setup(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "42"))
            .ReturnsAsync(new Song
            {
                Id = "ext-deezer-song-42",
                ExternalProvider = "deezer",
                ExternalId = "42",
                SpotifyId = SpicyLyricsTestData.TrackId,
                Title = "Fixture",
                Artist = "Artist",
                Album = "Album",
                Duration = 123
            });
        gateway.Setup(item => item.GetLyricsAsync(It.IsAny<ProtocolExecutionContext>(), "spicylyrics", SpicyLyricsTestData.TrackId,
                ProviderLyricsFormat.LineTimed, "Fixture", It.IsAny<IReadOnlyList<string>>(), "Album", 123, null))
            .Returns(async (ProtocolExecutionContext context, string _, string id, ProviderLyricsFormat? preferred, string? title,
                IReadOnlyList<string>? artists, string? album, int? duration, string? isrc) =>
                (await source.FetchLyricsAsync(SpicyLyricsTestData.Context(token: context.CancellationToken),
                    SpicyLyricsTestData.Request(id, preferred: preferred))).RequireValue());
        var resolver = new ProtocolLyricsResolver(gateway.Object, NullLogger<ProtocolLyricsResolver>.Instance);
        using var factory = new ProtocolFactory(backend, request => request.RequestUri!.AbsolutePath switch
        {
            "/Users/Me" => Json(200, """{"Id":"user-1","Name":"Listener"}"""),
            "/rest/ping.view" => Json(200, """{"subsonic-response":{"status":"ok"}}"""),
            _ => throw new InvalidOperationException("Only backend authentication is needed")
        }, services => ReplaceLyricsClientServices(services, gateway.Object, resolver));
        using var client = factory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(10);
        using var request = backend == "Jellyfin" ? new HttpRequestMessage(HttpMethod.Get, "/Audio/ext-deezer-song-42/Lyrics?api_key=fixture-key") :
            LyricsClientRequest("getLyricsBySongId", "Feishin", format, false, "&id=ext-deezer-song-42");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(1, handler.Calls);
        var firstText = fixture == "syllable" ? "Hello world" : fixture == "line" ? "Start" : "First plain line";
        if (backend == "Jellyfin")
        {
            using var document = JsonDocument.Parse(body);
            var lines = document.RootElement.GetProperty("Lyrics");
            Assert.Equal(firstText, lines[0].GetProperty("Text").GetString());
            if (fixture == "static") Assert.False(lines[0].TryGetProperty("Start", out _));
            else
            {
                Assert.Equal(fixture == "line" ? 0 : 10_000_000, lines[0].GetProperty("Start").GetInt64());
                if (fixture == "syllable")
                {
                    var cues = lines[0].GetProperty("Cues");
                    Assert.Equal(3, cues.GetArrayLength());
                    Assert.Equal(12_000_000, cues[0].GetProperty("End").GetInt64());
                    Assert.Equal(14_000_000, cues[1].GetProperty("Start").GetInt64());
                    Assert.Equal(23_000_000, cues[2].GetProperty("End").GetInt64());
                    Assert.Equal("oh yes", lines[1].GetProperty("Text").GetString());
                }
                else Assert.Equal(0, lines[0].GetProperty("Cues").GetArrayLength());
            }
        }
        else if (format == "json")
        {
            using var document = JsonDocument.Parse(body);
            var lyrics = document.RootElement.GetProperty("subsonic-response").GetProperty("lyricsList").GetProperty("structuredLyrics")[0];
            Assert.Equal(fixture != "static", lyrics.GetProperty("synced").GetBoolean());
            var line = lyrics.GetProperty("line")[0];
            Assert.Equal(firstText, line.GetProperty("value").GetString());
            if (fixture == "static") Assert.False(line.TryGetProperty("start", out _));
            else Assert.Equal(fixture == "line" ? 0 : 1000, line.GetProperty("start").GetInt64());
            if (fixture == "syllable") Assert.Equal("oh yes", lyrics.GetProperty("line")[1].GetProperty("value").GetString());
        }
        else
        {
            var document = XDocument.Parse(body);
            var ns = document.Root!.Name.Namespace;
            var lyrics = Assert.Single(document.Descendants(ns + "structuredLyrics"));
            var line = lyrics.Elements(ns + "line").First();
            Assert.Equal(firstText, line.Value);
            Assert.Equal(fixture == "static" ? null : fixture == "line" ? "0" : "1000", line.Attribute("start")?.Value);
        }
        gateway.VerifyAll();
    }

    [Theory]
    [InlineData("Jellyfin")]
    [InlineData("Subsonic")]
    public async Task SpicyUnavailableOrUnknownNativeIdentityPreservesBackendLyricsBytes(string backend)
    {
        const string jellyfin = """{"Metadata":{"IsSynced":false,"Custom":"keep"},"Lyrics":[{"Text":"Native words","Future":7}]}""";
        const string subsonic = """{"subsonic-response":{"status":"ok","lyricsList":{"structuredLyrics":[{"synced":false,"line":[{"value":"Native words"}],"future":7}]}}}""";
        using var handler = new SpicyLyricsTestHandler("unused") { Status = HttpStatusCode.NotFound };
        using var upstream = new HttpClient(handler);
        var source = new SpicyLyricsCapabilityAdapter(upstream, new SpicyLyricsTestSecrets());
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Lyrics)).Returns(["spicylyrics", "backend"]);
        gateway.Setup(item => item.GetLyricsAsync(It.IsAny<ProtocolExecutionContext>(), "spicylyrics", SpicyLyricsTestData.TrackId,
                ProviderLyricsFormat.LineTimed, "Fixture", It.IsAny<IReadOnlyList<string>>(), "", 0, null))
            .Returns(async () => (await source.FetchLyricsAsync(SpicyLyricsTestData.Context(), SpicyLyricsTestData.Request())).RequireValue());
        var resolver = new ProtocolLyricsResolver(gateway.Object, NullLogger<ProtocolLyricsResolver>.Instance);
        using var factory = new ProtocolFactory(backend, request => request.RequestUri!.AbsolutePath switch
        {
            "/Users/Me" => Json(200, """{"Id":"user-1"}"""),
            "/Items" => ItemLookup("""{"Id":"native-song","Type":"Audio","Name":"Fixture","AlbumArtist":"Artist","ProviderIds":{"Spotify":"AbCdEfGhIjKlMnOpQrStUv"}}"""),
            "/Audio/native-song/Lyrics" => Json(200, jellyfin),
            "/rest/ping.view" => Json(200, """{"subsonic-response":{"status":"ok"}}"""),
            "/rest/getSong" => Json(200, """{"subsonic-response":{"status":"ok","song":{"id":"native-song","title":"Fixture","artist":"Artist"}}}"""),
            "/rest/getLyricsBySongId" => Json(200, subsonic),
            _ => throw new InvalidOperationException("Unexpected backend route")
        }, services => ReplaceLyricsClientServices(services, gateway.Object, resolver));
        using var client = factory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(10);
        using var request = backend == "Jellyfin" ? new HttpRequestMessage(HttpMethod.Get, "/Audio/native-song/Lyrics?api_key=fixture-key") :
            LyricsClientRequest("getLyricsBySongId", "Feishin", "json", false, "&id=native-song");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(backend == "Jellyfin" ? jellyfin : subsonic, await response.Content.ReadAsStringAsync());
        Assert.Equal(backend == "Jellyfin" ? 1 : 0, handler.Calls);
    }
}
