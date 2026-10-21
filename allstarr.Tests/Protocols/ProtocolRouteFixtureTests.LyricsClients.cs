using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using allstarr.Core.Capabilities;
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
    [InlineData("json")]
    [InlineData("xml")]
    public async Task LyricsClients_MergeDiscoveryWithoutPromisingEnhancedProjection(string format)
    {
        using var factory = new ProtocolFactory("Subsonic", _ => LyricsBackendResponse(format,
            """{"subsonic-response":{"status":"ok","version":"1.16.1","type":"Fixture","serverVersion":"2.3.4","custom":7,"openSubsonicExtensions":[{"name":"songLyrics","versions":[1,2]},{"name":"future","versions":[7],"custom":true},{"name":"songLyrics","versions":[2]}]}}""",
            """<subsonic-response xmlns="http://subsonic.org/restapi" status="ok" version="1.16.1" type="Fixture" serverVersion="2.3.4" custom="7"><openSubsonicExtensions name="songLyrics"><versions>1</versions><versions>2</versions></openSubsonicExtensions><openSubsonicExtensions name="future" custom="true"><versions>7</versions></openSubsonicExtensions><openSubsonicExtensions name="songLyrics"><versions>2</versions></openSubsonicExtensions></subsonic-response>"""));
        using var client = factory.CreateClient();
        using var request = LyricsClientRequest("getOpenSubsonicExtensions", "Feishin", format, false);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        if (format == "json")
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement.GetProperty("subsonic-response");
            Assert.Equal("2.3.4", root.GetProperty("serverVersion").GetString());
            var extensions = root.GetProperty("openSubsonicExtensions");
            var lyrics = Assert.Single(extensions.EnumerateArray(), item => item.GetProperty("name").GetString() == "songLyrics");
            Assert.Equal([1], lyrics.GetProperty("versions").EnumerateArray().Select(item => item.GetInt32()));
            Assert.True(Assert.Single(extensions.EnumerateArray(), item => item.GetProperty("name").GetString() == "future")
                .GetProperty("custom").GetBoolean());
        }
        else
        {
            var root = XDocument.Parse(body).Root!;
            Assert.Equal("2.3.4", root.Attribute("serverVersion")!.Value);
            var extensions = root.Elements(root.Name.Namespace + "openSubsonicExtensions");
            var lyrics = Assert.Single(extensions, item => item.Attribute("name")!.Value == "songLyrics");
            Assert.Equal(["1"], lyrics.Elements(root.Name.Namespace + "versions").Select(item => item.Value));
            Assert.Equal("true", Assert.Single(extensions, item => item.Attribute("name")!.Value == "future").Attribute("custom")!.Value);
        }
    }

    [Theory]
    [InlineData("json")]
    [InlineData("xml")]
    public async Task LyricsClients_LegacyArtistTitleRemainsBackendOnly(string format)
    {
        var native = format == "json"
            ? """{ "subsonic-response":{"status":"ok","lyrics":{"artist":"Fixture Artist","title":"Fixture Song","value":"native text"}} }"""
            : """<subsonic-response status="ok"><lyrics artist="Fixture Artist" title="Fixture Song">native text</lyrics></subsonic-response>""";
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        using var factory = new ProtocolFactory("Subsonic", request =>
            request.RequestUri!.AbsolutePath.Contains("ping", StringComparison.Ordinal)
                ? Json(200, """{"subsonic-response":{"status":"ok"}}""")
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(native, Encoding.UTF8, $"application/{format}") },
            services =>
            {
                services.RemoveAll<IProtocolProviderGateway>();
                services.AddSingleton(gateway.Object);
            });
        using var client = factory.CreateClient();
        using var request = LyricsClientRequest("getLyrics", "Tempo", format, false, "&artist=Fixture%20Artist&title=Fixture%20Song");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(native, await response.Content.ReadAsStringAsync());
        gateway.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("Feishin", "json", false)]
    [InlineData("Feishin", "xml", true)]
    [InlineData("Tempo", "json", true)]
    [InlineData("Tempo", "xml", false)]
    public async Task LyricsClients_DiscoverProviderLyricsWithoutBackendSupport(string clientName, string format, bool post)
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        using var factory = new ProtocolFactory("Subsonic", request =>
        {
            var ping = request.RequestUri!.AbsolutePath.Contains("ping", StringComparison.Ordinal);
            return LyricsBackendResponse(format, ping
                ? """{"subsonic-response":{"status":"ok","version":"1.16.1","type":"Fixture","custom":7}}"""
                : """{"subsonic-response":{"status":"failed","version":"1.16.1","error":{"code":0,"message":"Unknown method"}}}""",
                ping
                    ? """<subsonic-response xmlns="http://subsonic.org/restapi" status="ok" version="1.16.1" type="Fixture" custom="7"/>"""
                    : """<subsonic-response xmlns="http://subsonic.org/restapi" status="failed" version="1.16.1"><error code="0" message="Unknown method"/></subsonic-response>""");
        }, services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
        });
        using var client = factory.CreateClient();
        foreach (var endpoint in new[] { "ping", "getOpenSubsonicExtensions" })
        {
            using var request = LyricsClientRequest(endpoint, clientName, format, post);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            if (format == "json")
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement.GetProperty("subsonic-response");
                Assert.Equal("ok", root.GetProperty("status").GetString());
                Assert.True(root.GetProperty("openSubsonic").GetBoolean());
                Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("serverVersion").GetString()));
                Assert.False(root.TryGetProperty("error", out _));
                if (endpoint == "ping") Assert.Equal(7, root.GetProperty("custom").GetInt32());
                else
                {
                    var extension = Assert.Single(root.GetProperty("openSubsonicExtensions").EnumerateArray(),
                        item => item.GetProperty("name").GetString() == "songLyrics");
                    Assert.Contains(1, extension.GetProperty("versions").EnumerateArray().Select(item => item.GetInt32()));
                }
            }
            else
            {
                var root = XDocument.Parse(body).Root!;
                Assert.Equal("ok", root.Attribute("status")!.Value);
                Assert.Equal("true", root.Attribute("openSubsonic")!.Value);
                Assert.False(string.IsNullOrWhiteSpace(root.Attribute("serverVersion")!.Value));
                Assert.Empty(root.Elements(root.Name.Namespace + "error"));
                if (endpoint == "ping") Assert.Equal("7", root.Attribute("custom")!.Value);
                else
                {
                    var extension = Assert.Single(root.Elements(root.Name.Namespace + "openSubsonicExtensions"),
                        item => item.Attribute("name")!.Value == "songLyrics");
                    Assert.Contains(extension.Elements(root.Name.Namespace + "versions"), item => item.Value == "1");
                }
            }
        }
        gateway.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("json", 200)]
    [InlineData("xml", 200)]
    [InlineData("json", 401)]
    [InlineData("xml", 403)]
    public async Task LyricsClients_FailedPingKeepsBackendStatusAndBytes(string format, int status)
    {
        var native = format == "json"
            ? """{ "subsonic-response": {"status":"failed","error":{"code":40,"message":"Denied"}} }"""
            : """<subsonic-response status="failed"><error code="40" message="Denied" /></subsonic-response>""";
        using var factory = new ProtocolFactory("Subsonic", _ => new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent(native, Encoding.UTF8, $"application/{format}")
        });
        using var client = factory.CreateClient();
        using var request = LyricsClientRequest("ping", "Feishin", format, false);
        using var response = await client.SendAsync(request);
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(native, await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("Feishin", "json", "synced", true)]
    [InlineData("Feishin", "xml", "synced", false)]
    [InlineData("Tempo", "json", "plain", false)]
    [InlineData("Tempo", "xml", "plain", true)]
    [InlineData("Feishin", "json", "missing", false)]
    [InlineData("Tempo", "json", "missing", false)]
    [InlineData("Tempo", "xml", "missing", false)]
    public async Task LyricsClients_RenderStructuredLyrics(string clientName, string format, string kind, bool post)
    {
        var gateway = LyricsClientGateway();
        var resolver = LyricsClientResolver(kind);
        using var factory = new ProtocolFactory("Subsonic", request =>
        {
            Assert.Contains("ping", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
            return Json(200, """{"subsonic-response":{"status":"ok","version":"1.16.1"}}""");
        }, services => ReplaceLyricsClientServices(services, gateway.Object, resolver.Object));
        using var client = factory.CreateClient();
        using var request = LyricsClientRequest("getLyricsBySongId", clientName, format, post, "&id=ext-deezer-song-42");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        if (format == "json")
        {
            using var document = JsonDocument.Parse(body);
            var list = document.RootElement.GetProperty("subsonic-response").GetProperty("lyricsList");
            // Tempo checks for null before accessing the first result, but does not check an empty array.
            if (kind == "missing") Assert.False(list.TryGetProperty("structuredLyrics", out _));
            else
            {
                var lyrics = Assert.Single(list.GetProperty("structuredLyrics").EnumerateArray());
                Assert.Equal(kind == "synced", lyrics.GetProperty("synced").GetBoolean());
                Assert.Equal(0, lyrics.GetProperty("offset").GetInt64());
                Assert.Equal("first & <line>", lyrics.GetProperty("line")[0].GetProperty("value").GetString());
                if (kind == "synced") Assert.Equal(1234, lyrics.GetProperty("line")[0].GetProperty("start").GetInt64());
                else Assert.False(lyrics.GetProperty("line")[0].TryGetProperty("start", out _));
            }
        }
        else
        {
            var root = XDocument.Parse(body).Root!;
            var structured = root.Element(root.Name.Namespace + "lyricsList")!.Elements(root.Name.Namespace + "structuredLyrics");
            if (kind == "missing") Assert.Empty(structured);
            else
            {
                var lyrics = Assert.Single(structured);
                Assert.Equal((kind == "synced").ToString().ToLowerInvariant(), lyrics.Attribute("synced")!.Value);
                var line = lyrics.Elements(root.Name.Namespace + "line").First();
                Assert.Equal("first & <line>", line.Value);
                Assert.Equal(kind == "synced" ? "1234" : null, line.Attribute("start")?.Value);
            }
        }
        gateway.VerifyAll();
        gateway.VerifyNoOtherCalls();
        resolver.VerifyAll();
        resolver.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("Finamp", "synced")]
    [InlineData("Jellyfin Web", "plain")]
    [InlineData("Feishin", "synced")]
    public async Task LyricsClients_JellyfinMarkersEnableFetchAndCacheThenAllowNotFound(string clientName, string kind)
    {
        var gateway = LyricsClientGateway();
        gateway.Setup(service => service.OpenStreamAsync(
                It.Is<ProtocolExecutionContext>(context => context.IsReadOnly), "deezer", "42",
                ProviderAudioQuality.Any, null, true))
            .ReturnsAsync(() => new ProtocolProviderStream(new HttpResponseMessage(HttpStatusCode.OK),
                new ProviderStreamLease("fixture", new Uri("https://fixture.invalid/audio"), DateTimeOffset.UtcNow.AddMinutes(1),
                    true, true, new("audio/flac", "flac", "flac"), ProviderStreamRetryBehavior.DoNotRetry), "deezer", "42"));
        var resolver = LyricsClientResolver(kind);
        using var factory = new ProtocolFactory("Jellyfin", request =>
            request.RequestUri!.AbsolutePath == "/Users/Me"
                ? Json(200, """{"Id":"user-1","Name":"Fixture User"}""")
                : throw new InvalidOperationException("Only authentication may reach the backend"),
            services => ReplaceLyricsClientServices(services, gateway.Object, resolver.Object));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Emby-Authorization",
            $"MediaBrowser Client=\"{clientName}\", Device=\"Tests\", DeviceId=\"lyrics\", Version=\"1\"");
        using var itemResponse = await client.GetAsync("/Users/user-1/Items/ext-deezer-song-42?Fields=MediaSources,MediaStreams&api_key=fixture-key");
        Assert.Equal(HttpStatusCode.OK, itemResponse.StatusCode);
        using var item = JsonDocument.Parse(await itemResponse.Content.ReadAsStringAsync());
        Assert.True(item.RootElement.GetProperty("HasLyrics").GetBoolean());
        var streams = item.RootElement.GetProperty("MediaSources")[0].GetProperty("MediaStreams");
        var marker = Assert.Single(streams.EnumerateArray(), stream => stream.GetProperty("Type").GetString() == "Lyric");
        Assert.Equal(1, marker.GetProperty("Index").GetInt32());
        Assert.True(marker.GetProperty("IsExternal").GetBoolean());
        foreach (var field in new[] { "IsInterlaced", "IsDefault", "IsForced", "IsTextSubtitleStream", "SupportsExternalStream" })
            Assert.False(marker.GetProperty(field).GetBoolean());
        Assert.Equal(streams.GetRawText(), item.RootElement.GetProperty("MediaStreams").GetRawText());
        using var playbackResponse = await client.PostAsync("/Items/ext-deezer-song-42/PlaybackInfo?UserId=user-1&api_key=fixture-key", null);
        Assert.Equal(HttpStatusCode.OK, playbackResponse.StatusCode);
        using var playback = JsonDocument.Parse(await playbackResponse.Content.ReadAsStringAsync());
        Assert.Single(playback.RootElement.GetProperty("MediaSources")[0].GetProperty("MediaStreams").EnumerateArray(),
            stream => stream.GetProperty("Type").GetString() == "Lyric");
        resolver.VerifyNoOtherCalls();

        using var lyricsResponse = await client.GetAsync("/Audio/ext-deezer-song-42/Lyrics?api_key=fixture-key");
        Assert.Equal(HttpStatusCode.OK, lyricsResponse.StatusCode);
        var cached = await lyricsResponse.Content.ReadAsStringAsync();
        var dto = JsonSerializer.Deserialize<LyricResponseContract>(cached)!;
        Assert.Equal(1_230_000_000, dto.Metadata!.Length);
        Assert.Equal("first & <line>", dto.Lyrics![0].Text);
        Assert.Equal(kind == "synced" ? 12_340_000L : null, dto.Lyrics[0].Start);
        using var lyrics = JsonDocument.Parse(cached);
        Assert.Equal(kind == "synced", lyrics.RootElement.GetProperty("Metadata").GetProperty("IsSynced").GetBoolean());
        var line = lyrics.RootElement.GetProperty("Lyrics")[0];
        Assert.Equal("first & <line>", line.GetProperty("Text").GetString());
        Assert.Equal(JsonValueKind.Array, line.GetProperty("Cues").ValueKind);
        if (kind == "synced") Assert.Equal(12_340_000, line.GetProperty("Start").GetInt64());
        else Assert.False(line.TryGetProperty("Start", out _));

        resolver.Reset();
        SetupLyricsClientResolver(resolver, null);
        using var missing = await client.GetAsync("/Audio/ext-deezer-song-42/Lyrics?api_key=fixture-key");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Contains("first & <line>", lyrics.RootElement.GetProperty("Lyrics")[0].GetProperty("Text").GetString()!);
        gateway.VerifyAll();
        gateway.VerifyNoOtherCalls();
        resolver.VerifyAll();
        resolver.VerifyNoOtherCalls();
    }

    private static HttpRequestMessage LyricsClientRequest(string endpoint, string clientName, string format, bool post, string extra = "")
    {
        var version = clientName == "Tempo" ? "1.15.0" : "1.13.0";
        var suffix = clientName == "Tempo" ? "" : ".view";
        var parameters = $"u=listener&t=fixture-token&s=fixture-salt&v={version}&c={Uri.EscapeDataString(clientName)}&f={format}{extra}";
        return new HttpRequestMessage(post ? HttpMethod.Post : HttpMethod.Get, $"/rest/{endpoint}{suffix}{(post ? "" : "?" + parameters)}")
        {
            Content = post ? new StringContent(parameters, Encoding.UTF8, "application/x-www-form-urlencoded") : null
        };
    }

    private static HttpResponseMessage LyricsBackendResponse(string format, string json, string xml) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(format == "json" ? json : xml, Encoding.UTF8, $"application/{format}")
    };

    private static Mock<IProtocolProviderGateway> LyricsClientGateway()
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(service => service.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "42"))
            .ReturnsAsync(new Song
            {
                Id = "ext-deezer-song-42",
                ExternalProvider = "deezer",
                ExternalId = "42",
                Title = "Fixture Song",
                Artist = "Fixture Artist",
                Album = "Fixture Album",
                Duration = 123
            });
        return gateway;
    }

    private static Mock<IProtocolLyricsResolver> LyricsClientResolver(string kind)
    {
        var resolver = new Mock<IProtocolLyricsResolver>(MockBehavior.Strict);
        SetupLyricsClientResolver(resolver, kind == "missing" ? null : new LyricsInfo
        {
            TrackName = "Fixture Song",
            ArtistName = "Fixture Artist",
            AlbumName = "Fixture Album",
            Duration = 123,
            SyncedLyrics = kind == "synced" ? "[offset:100]\n[00:01.334]first & <line>\n[00:02.5]second" : null,
            PlainLyrics = "first & <line>\nsecond"
        });
        return resolver;
    }

    private static void SetupLyricsClientResolver(Mock<IProtocolLyricsResolver> resolver, LyricsInfo? lyrics) =>
        resolver.Setup(service => service.FindAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<Song>(),
                "deezer:42", "deezer", "42", null))
            .ReturnsAsync(lyrics);

    private static void ReplaceLyricsClientServices(IServiceCollection services, IProtocolProviderGateway gateway, IProtocolLyricsResolver resolver)
    {
        services.RemoveAll<IProtocolProviderGateway>();
        services.AddSingleton(gateway);
        services.RemoveAll<IProtocolLyricsResolver>();
        services.AddSingleton(resolver);
        services.RemoveAll<ILibraryIndexService>();
        services.AddSingleton(Mock.Of<ILibraryIndexService>());
    }

    private sealed record LyricResponseContract(LyricMetadataContract? Metadata, LyricLineContract[]? Lyrics);
    private sealed record LyricMetadataContract(string? Artist, string? Album, string? Title, long? Length, bool? IsSynced);
    private sealed record LyricLineContract(string? Text, long? Start, LyricCueContract[]? Cues);
    private sealed record LyricCueContract(int Position, long Start, int? EndPosition, long? End);
}
