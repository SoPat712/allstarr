using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Models.Lyrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Theory]
    [InlineData("Jellyfin", "json")]
    [InlineData("Subsonic", "json")]
    [InlineData("Subsonic", "xml")]
    public async Task NativeKeptLyricsUseIndexedIdentityAndProjectOffsetsOnce(string backend, string format)
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Lyrics)).Returns(["kept", "backend"]);
        var index = new Mock<ILibraryIndexService>(MockBehavior.Strict);
        index.Setup(item => item.FindByBackendItemAsync(It.IsAny<ProtocolExecutionContext>(), "native-song", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IndexedLibraryTrack(Guid.CreateVersion7(), "native-song", "/backend/path/never-used.flac",
                "Fixture Song", "Fixture Artist", "Fixture Album", null, null, null, null, null, null, null,
                new Dictionary<string, string> { ["spotify"] = "exact-source" }, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0));
        var kept = new Mock<IKeptLyricsReader>(MockBehavior.Strict);
        kept.Setup(item => item.ReadAsync(It.IsAny<ProtocolExecutionContext>(),
                It.Is<Song>(song => song.Id == "native-song" && song.IsLocal && song.LocalPath == null),
                It.Is<IReadOnlyList<KeptLyricsIdentity>>(ids => ids.Count == 1 && ids[0] == new KeptLyricsIdentity("spotify", "exact-source"))))
            .ReturnsAsync(new LyricsInfo
            {
                Source = "kept",
                TrackName = "Fixture Song",
                ArtistName = "Fixture Artist",
                SyncedLyrics = "[offset:250]\n[00:01.234]Owned line"
            });
        using var factory = new ProtocolFactory(backend, request => request.RequestUri!.AbsolutePath switch
        {
            "/Users/Me" => Json(200, """{"Id":"user-1","Name":"Fixture User"}"""),
            "/Items" => ItemLookup("""{"Id":"native-song","Type":"Audio","Name":"Fixture Song","AlbumArtist":"Fixture Artist"}"""),
            "/rest/ping.view" => Json(200, """{"subsonic-response":{"status":"ok","version":"1.16.1"}}"""),
            "/rest/getSong" => Json(200, """{"subsonic-response":{"status":"ok","song":{"id":"native-song","title":"Fixture Song","artist":"Fixture Artist"}}}"""),
            _ => throw new InvalidOperationException("Kept lyrics must not read a provider or backend lyric body")
        }, services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
            services.RemoveAll<ILibraryIndexService>();
            services.AddSingleton(index.Object);
            services.RemoveAll<IKeptLyricsReader>();
            services.AddSingleton(kept.Object);
        }, new Dictionary<string, string?> { ["MULTI_PROVIDER_LYRICS_ORDER"] = "kept,backend" });
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(backend == "Jellyfin"
            ? "/Audio/native-song/Lyrics?api_key=fixture-key"
            : $"/rest/getLyricsBySongId.view?id=native-song&u=listener&p=fixture&v=1.16.1&c=fixture&f={format}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        if (backend == "Jellyfin")
        {
            using var document = JsonDocument.Parse(body);
            Assert.Equal(9_840_000, document.RootElement.GetProperty("Lyrics")[0].GetProperty("Start").GetInt64());
        }
        else if (format == "json")
        {
            using var document = JsonDocument.Parse(body);
            var lyrics = document.RootElement.GetProperty("subsonic-response").GetProperty("lyricsList").GetProperty("structuredLyrics")[0];
            Assert.Equal(0, lyrics.GetProperty("offset").GetInt64());
            Assert.Equal(984, lyrics.GetProperty("line")[0].GetProperty("start").GetInt64());
        }
        else
        {
            var document = XDocument.Parse(body);
            var ns = document.Root!.Name.Namespace;
            var lyrics = Assert.Single(document.Descendants(ns + "structuredLyrics"));
            Assert.Equal("0", lyrics.Attribute("offset")!.Value);
            Assert.Equal("984", Assert.Single(lyrics.Elements(ns + "line")).Attribute("start")!.Value);
        }
        Assert.Contains("Owned line", body, StringComparison.Ordinal);
        kept.VerifyAll();
        index.VerifyAll();
        gateway.Verify(item => item.GetProviderOrder(ProviderCapabilityKind.Lyrics), Times.Once);
        gateway.VerifyNoOtherCalls();
    }
}
