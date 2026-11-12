using System.Net;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Protocols;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Theory]
    [InlineData("Jellyfin")]
    [InlineData("Subsonic")]
    public async Task NativeLyricsCleanupRemovesMarkersWithoutMovingTheRemainingLine(string backend)
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Lyrics)).Returns(["backend"]);
        using var factory = new ProtocolFactory(backend, request => request.RequestUri!.AbsolutePath switch
        {
            "/Users/Me" => Json(200, """{"Id":"user-1","Name":"Fixture User"}"""),
            "/Items" => ItemLookup("""{"Id":"native-song","Type":"Audio","Name":"Song","AlbumArtist":"Artist"}"""),
            "/Audio/native-song/Lyrics" => Json(200, """{"Metadata":{"IsSynced":true},"Lyrics":[{"Start":10000000,"Text":"[Chorus]"},{"Start":20000000,"Text":"Нello"}]}"""),
            "/rest/ping.view" => Json(200, """{"subsonic-response":{"status":"ok","version":"1.16.1"}}"""),
            "/rest/getSong" => Json(200, """{"subsonic-response":{"status":"ok","song":{"id":"native-song","title":"Song","artist":"Artist"}}}"""),
            "/rest/getLyricsBySongId" => Json(200, """{"subsonic-response":{"status":"ok","lyricsList":{"structuredLyrics":[{"synced":true,"line":[{"start":1000,"value":"[Chorus]"},{"start":2000,"value":"Нello"}]}]}}}"""),
            _ => throw new InvalidOperationException("Unexpected lyrics fixture request")
        }, services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
        }, new Dictionary<string, string?> { ["MULTI_PROVIDER_LYRICS_ORDER"] = "backend" });
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(backend == "Jellyfin"
            ? "/Audio/native-song/Lyrics?api_key=fixture-key"
            : "/rest/getLyricsBySongId.view?id=native-song&u=listener&p=fixture&v=1.16.1&c=fixture&f=json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var line = Assert.Single(backend == "Jellyfin" ? body.RootElement.GetProperty("Lyrics").EnumerateArray()
            : body.RootElement.GetProperty("subsonic-response").GetProperty("lyricsList").GetProperty("structuredLyrics")[0]
                .GetProperty("line").EnumerateArray());
        Assert.Equal("Hello", line.GetProperty(backend == "Jellyfin" ? "Text" : "value").GetString());
        Assert.Equal(backend == "Jellyfin" ? 20000000 : 2000, line.GetProperty(backend == "Jellyfin" ? "Start" : "start").GetInt64());
    }
}
