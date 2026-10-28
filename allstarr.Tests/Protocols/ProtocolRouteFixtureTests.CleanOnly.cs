using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Protocols;
using allstarr.Core.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Theory]
    [InlineData("/Items?IncludeItemTypes=Audio")]
    [InlineData("/Users/user-1/Items?IncludeItemTypes=Audio")]
    [InlineData("/Items?ParentId=native-album&IncludeItemTypes=Audio")]
    [InlineData("/Items/native-artist/InstantMix")]
    [InlineData("/Playlists/native-playlist/Items")]
    public async Task JellyfinCleanOnlyFiltersNativeCollections(string path)
    {
        var requests = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var factory = new ProtocolFactory("Jellyfin", request =>
        {
            requests.Enqueue(request.RequestUri!.PathAndQuery);
            return request.RequestUri.AbsolutePath == "/Users/Me"
                ? Json(200, """{"Id":"user-1"}""")
                : request.RequestUri.Query.Contains("ids=", StringComparison.OrdinalIgnoreCase) && IsItemLookup(request, "native-album") ? ItemLookup("""{"Id":"native-album","Type":"MusicAlbum"}""")
                : Json(200, """{"Items":[{"Id":"clean","Type":"Audio","Name":"Clean","OfficialRating":"Clean"},{"Id":"explicit","Type":"Audio","Name":"Explicit","OfficialRating":"Explicit"},{"Id":"unknown","Type":"Audio","Name":"Unknown"}],"TotalRecordCount":3}""");
        }, CleanOnlyServices("Jellyfin"));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(path + (path.Contains('?') ? "&" : "?") + "api_key=fixture");
        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}; requests: {string.Join(" | ", requests)}");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("clean", Assert.Single(body.RootElement.GetProperty("Items").EnumerateArray()).GetProperty("Id").GetString());
        Assert.Equal(1, body.RootElement.GetProperty("TotalRecordCount").GetInt32());
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
    }

    [Theory]
    [InlineData("getAlbumList2", "json")]
    [InlineData("getAlbumList2", "xml")]
    [InlineData("getAlbum", "json")]
    [InlineData("getPlaylist", "json")]
    [InlineData("getRandomSongs", "json")]
    [InlineData("getSimilarSongs2", "json")]
    [InlineData("getAlbum", "xml")]
    [InlineData("getPlaylist", "xml")]
    [InlineData("getRandomSongs", "xml")]
    [InlineData("getSimilarSongs2", "xml")]
    public async Task SubsonicCleanOnlyFiltersNativeCollections(string method, string format)
    {
        var container = method switch { "getAlbumList2" => "albumList2", "getAlbum" => "album", "getPlaylist" => "playlist", "getRandomSongs" => "randomSongs", _ => "similarSongs2" };
        var row = method switch { "getPlaylist" => "entry", "getAlbumList2" => "album", _ => "song" };
        using var factory = new ProtocolFactory("Subsonic", request => request.RequestUri!.AbsolutePath == "/rest/ping.view"
            ? Json(200, """{"subsonic-response":{"status":"ok","version":"1.16.1"}}""")
            : format == "json"
                ? Json(200, """{"subsonic-response":{"status":"ok","version":"1.16.1","CONTAINER":{"ROW":[{"id":"clean","title":"Clean","explicitStatus":"clean"},{"id":"explicit","title":"Explicit","explicitStatus":"explicit"},{"id":"unknown","title":"Unknown"}]}}}""".Replace("CONTAINER", container).Replace("ROW", row))
                : Xml($"<subsonic-response xmlns=\"http://subsonic.org/restapi\" status=\"ok\"><{container}><{row} id=\"clean\" title=\"Clean\" explicitStatus=\"clean\"/><{row} id=\"explicit\" title=\"Explicit\" explicitStatus=\"explicit\"/><{row} id=\"unknown\" title=\"Unknown\"/></{container}></subsonic-response>"),
            CleanOnlyServices("Subsonic"));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/rest/{method}.view?u=fixture&p=secret&v=1.16.1&c=fixture&f={format}&id=native");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        if (format == "json")
        {
            using var body = JsonDocument.Parse(text);
            Assert.Equal("clean", Assert.Single(body.RootElement.GetProperty("subsonic-response").GetProperty(container).GetProperty(row).EnumerateArray()).GetProperty("id").GetString());
        }
        else
            Assert.Equal("clean", Assert.Single(XDocument.Parse(text).Descendants(), element => element.Name.LocalName == row).Attribute("id")!.Value);
    }

    [Theory]
    [InlineData("Jellyfin", "/Audio/native/stream?api_key=fixture", null)]
    [InlineData("Jellyfin", "/Items/native/Download?api_key=fixture", "Explicit")]
    [InlineData("Jellyfin", "/Items/native/File?api_key=fixture", null)]
    [InlineData("Jellyfin", "/Audio/native/main.m3u8?api_key=fixture", null)]
    [InlineData("Jellyfin", "/Audio/native/hls/old/0.mp4?api_key=fixture", null)]
    [InlineData("Jellyfin", "/Items/native/Lyrics?api_key=fixture", "Explicit")]
    [InlineData("Subsonic", "/rest/getTranscodeStream.view?u=fixture&p=secret&v=1.16.1&c=fixture&id=native&transcodeParams=opaque&f=json", "clean")]
    [InlineData("Subsonic", "/rest/getTranscodeDecision.view?u=fixture&p=secret&v=1.16.1&c=fixture&mediaId=native&f=json", null)]
    [InlineData("Subsonic", "/rest/stream.view?u=fixture&p=secret&v=1.16.1&c=fixture&id=native&f=json", null)]
    [InlineData("Subsonic", "/rest/download.view?u=fixture&p=secret&v=1.16.1&c=fixture&id=native&f=json", "explicit")]
    [InlineData("Subsonic", "/rest/getLyricsBySongId.view?u=fixture&p=secret&v=1.16.1&c=fixture&id=native&f=json", null)]
    public async Task CleanOnlyRefusesUnverifiedDirectNativeMediaBeforeOpeningBytes(string backend, string path, string? rating)
    {
        var opened = false;
        using var factory = new ProtocolFactory(backend, request =>
        {
            var target = request.RequestUri!.AbsolutePath;
            if (target == "/Users/Me") return Json(200, """{"Id":"user-1"}""");
            if (IsItemLookup(request, "native")) return ItemLookup(JsonSerializer.Serialize(new { Id = "native", Type = "Audio", OfficialRating = rating }));
            if (target == "/rest/ping.view") return Json(200, """{"subsonic-response":{"status":"ok","version":"1.16.1"}}""");
            if (target == "/Users/user-1/Items/native") return Json(200, JsonSerializer.Serialize(new { Id = "native", Type = "Audio", OfficialRating = rating }));
            if (target.StartsWith("/rest/getSong", StringComparison.Ordinal))
                return Json(200, JsonSerializer.Serialize(new Dictionary<string, object> { ["subsonic-response"] = new { status = "ok", song = new { id = "native", explicitStatus = rating } } }));
            opened = true;
            return Json(200, "{}");
        }, CleanOnlyServices(backend));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(opened);
    }

    [Theory]
    [InlineData("Jellyfin", true)]
    [InlineData("Jellyfin", false)]
    [InlineData("Subsonic", true)]
    [InlineData("Subsonic", false)]
    [InlineData("Jellyfin", null)]
    [InlineData("Subsonic", null)]
    public async Task CleanOnlyNativePlaylistReplacesExplicitRowsOnlyWithVerifiedCounterparts(string backend, bool? available)
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.SearchAsync(It.IsAny<ProtocolExecutionContext>(), "Track Artist", 20, 0, 0, null))
            .ReturnsAsync(new allstarr.Models.Search.SearchResult
            {
                Songs = available == true ? [new allstarr.Models.Domain.Song
                { Id = "ext-deezer-song-clean", Title = "Track (Clean)", Artist = "Artist", Album = "Album", Duration = 180,
                    ExternalProvider = "deezer", ExternalId = "clean", ExplicitContentLyrics = 3 }] : []
            });
        if (available == null)
            gateway.Setup(item => item.SearchAsync(It.IsAny<ProtocolExecutionContext>(), "Track Artist", 20, 0, 0, null)).ThrowsAsync(new HttpRequestException("unavailable"));
        using var factory = new ProtocolFactory(backend, request => request.RequestUri!.AbsolutePath switch
        {
            "/Users/Me" => Json(200, """{"Id":"user-1"}"""),
            "/rest/ping.view" => Json(200, """{"subsonic-response":{"status":"ok","version":"1.16.1"}}"""),
            _ => backend == "Jellyfin"
                ? Json(200, """{"Items":[{"Id":"explicit","Type":"Audio","Name":"Track","Artists":["Artist"],"Album":"Album","RunTimeTicks":1800000000,"OfficialRating":"Explicit"},{"Id":"explicit","Type":"Audio","Name":"Track","Artists":["Artist"],"Album":"Album","RunTimeTicks":1800000000,"OfficialRating":"Explicit"},{"Id":"unknown","Type":"Audio","Name":"Unknown"}],"TotalRecordCount":3}""")
                : Json(200, """{"subsonic-response":{"status":"ok","version":"1.16.1","playlist":{"entry":[{"id":"explicit","title":"Track","artist":"Artist","album":"Album","duration":180,"explicitStatus":"explicit"},{"id":"explicit","title":"Track","artist":"Artist","album":"Album","duration":180,"explicitStatus":"explicit"},{"id":"unknown","title":"Unknown"}]}}}""")
        }, services =>
        {
            CleanOnlyServices(backend)(services);
            services.RemoveAll<IProtocolProviderGateway>(); services.AddSingleton(gateway.Object);
        });
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(backend == "Jellyfin"
            ? "/Playlists/native/Items?api_key=fixture"
            : "/rest/getPlaylist.view?u=fixture&p=secret&v=1.16.1&c=fixture&id=native&f=json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var rows = backend == "Jellyfin" ? json.RootElement.GetProperty("Items") : json.RootElement.GetProperty("subsonic-response").GetProperty("playlist").GetProperty("entry");
        Assert.Equal(available == true ? 2 : 0, rows.GetArrayLength());
        if (available == true)
        {
            Assert.Equal("ext-deezer-song-clean", rows[0].GetProperty(backend == "Jellyfin" ? "Id" : "id").GetString());
            Assert.Equal("explicit", rows[0].GetProperty(backend == "Jellyfin" ? "OriginalItemId" : "originalItemId").GetString());
        }
        gateway.Verify(item => item.SearchAsync(It.IsAny<ProtocolExecutionContext>(), "Track Artist", 20, 0, 0, null), Times.Once);
    }

    [Theory]
    [InlineData("/Audio/native/stream?api_key=fixture", "GET")]
    [InlineData("/Items/native/File?api_key=fixture", "HEAD")]
    public async Task JellyfinCleanOnlyKeepsVerifiedNativeStreamingAndHeadSemantics(string path, string method)
    {
        using var factory = new ProtocolFactory("Jellyfin", request => request.RequestUri!.AbsolutePath switch
        {
            "/Users/Me" => Json(200, """{"Id":"user-1"}"""),
            "/Items" when IsItemLookup(request, "native") => ItemLookup("""{"Id":"native","Type":"Audio","OfficialRating":"Clean"}"""),
            "/Users/user-1/Items/native" => CleanNativeItemResponse(request),
            _ => NativeCleanAudioResponse()
        }, CleanOnlyServices("Jellyfin"));
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(4, response.Content.Headers.ContentLength);
        Assert.Equal(method == "HEAD" ? [] : new byte[] { 1, 2, 3, 4 }, await response.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("Jellyfin")]
    [InlineData("Subsonic")]
    public async Task CleanOnlyVerifiedProviderPlaybackDoesNotPreferAnUnknownNativeCopy(string backend)
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "clean"))
            .ReturnsAsync(new allstarr.Models.Domain.Song { Id = "ext-deezer-song-clean", ExplicitContentLyrics = 0 });
        gateway.Setup(item => item.OpenStreamAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "clean", It.IsAny<ProviderAudioQuality>(), null, false))
            .ReturnsAsync((ProtocolProviderStream?)null);
        var index = new Mock<allstarr.Core.Matching.ILibraryIndexService>(MockBehavior.Strict);
        index.Setup(item => item.ResolveKnownTrackAsync(It.IsAny<ProtocolExecutionContext>(), "ext-deezer-song-clean", It.IsAny<CancellationToken>()))
            .ReturnsAsync("native");
        using var factory = new ProtocolFactory(backend, request => request.RequestUri!.AbsolutePath switch
        {
            "/Users/Me" => Json(200, """{"Id":"user-1"}"""),
            "/rest/ping.view" => Json(200, """{"subsonic-response":{"status":"ok","version":"1.16.1"}}"""),
            "/Items" when IsItemLookup(request, "native") => ItemLookup("""{"Id":"native","Type":"Audio"}"""),
            "/Users/user-1/Items/native" => Json(200, """{"Id":"native","Type":"Audio"}"""),
            "/rest/getSong.view" => Json(200, """{"subsonic-response":{"status":"ok","song":{"id":"native"}}}"""),
            _ => throw new InvalidOperationException("Native audio must not open.")
        }, services =>
        {
            CleanOnlyServices(backend)(services);
            services.RemoveAll<IProtocolProviderGateway>(); services.AddSingleton(gateway.Object);
            services.RemoveAll<allstarr.Core.Matching.ILibraryIndexService>(); services.AddSingleton(index.Object);
        });
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(backend == "Jellyfin"
            ? "/Audio/ext-deezer-song-clean/stream?api_key=fixture"
            : "/rest/stream.view?u=fixture&p=secret&v=1.16.1&c=fixture&id=ext-deezer-song-clean&f=json");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        gateway.Verify(item => item.OpenStreamAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "clean", It.IsAny<ProviderAudioQuality>(), null, false), Times.Once);
    }

    [Fact]
    public async Task SubsonicCleanOnlyDirectoryKeepsFoldersAndVerifiedSongs()
    {
        using var factory = new ProtocolFactory("Subsonic", request => request.RequestUri!.AbsolutePath == "/rest/ping.view"
            ? Json(200, """{"subsonic-response":{"status":"ok","version":"1.16.1"}}""")
            : Json(200, """{"subsonic-response":{"status":"ok","version":"1.16.1","directory":{"id":"root","name":"Music","child":[{"id":"folder","isDir":true,"title":"Folder"},{"id":"clean","isDir":false,"title":"Clean","explicitStatus":"clean"},{"id":"unknown","isDir":false,"title":"Unknown"}]}}}"""), CleanOnlyServices("Subsonic"));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/rest/getMusicDirectory.view?u=fixture&p=secret&v=1.16.1&c=fixture&f=json&id=root");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "folder", "clean" }, json.RootElement.GetProperty("subsonic-response").GetProperty("directory").GetProperty("child").EnumerateArray().Select(item => item.GetProperty("id").GetString()));
    }

    [Theory]
    [InlineData("Jellyfin")]
    [InlineData("Subsonic")]
    public async Task CleanOnlyMetadataDoesNotReuseAnUnrestrictedConditionalResponse(string backend)
    {
        using var factory = new ProtocolFactory(backend, request =>
        {
            if (request.RequestUri!.AbsolutePath == "/Users/Me") return Json(200, """{"Id":"user-1"}""");
            if (request.RequestUri.AbsolutePath == "/rest/ping.view") return Json(200, """{"subsonic-response":{"status":"ok","version":"1.16.1"}}""");
            Assert.Empty(request.Headers.IfNoneMatch);
            Assert.Null(request.Headers.IfModifiedSince);
            return backend == "Jellyfin"
                ? Json(200, """{"Items":[{"Id":"clean","Type":"Audio","OfficialRating":"Clean"}],"TotalRecordCount":1}""")
                : Json(200, """{"subsonic-response":{"status":"ok","version":"1.16.1","randomSongs":{"song":[{"id":"clean","explicitStatus":"clean"}]}}}""");
        }, CleanOnlyServices(backend));
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, backend == "Jellyfin"
            ? "/Users/user-1/Items?IncludeItemTypes=Audio&api_key=fixture"
            : "/rest/getRandomSongs.view?u=fixture&p=secret&v=1.16.1&c=fixture&f=json");
        request.Headers.IfNoneMatch.ParseAdd("\"old-view\"");
        request.Headers.IfModifiedSince = DateTimeOffset.UtcNow.AddDays(-1);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task SubsonicCleanOnlyDoesNotTrustSongEvidenceFromAFailedResponse()
    {
        var opened = false;
        using var factory = new ProtocolFactory("Subsonic", request =>
        {
            if (request.RequestUri!.AbsolutePath == "/rest/ping.view") return Json(200, """{"subsonic-response":{"status":"ok","version":"1.16.1"}}""");
            if (request.RequestUri.AbsolutePath == "/rest/getSong.view") return Json(200, """{"subsonic-response":{"status":"failed","song":{"id":"native","explicitStatus":"clean"}}}""");
            opened = true;
            return NativeCleanAudioResponse();
        }, CleanOnlyServices("Subsonic"));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/rest/stream.view?u=fixture&p=secret&v=1.16.1&c=fixture&id=native&f=json");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(opened);
    }

    private static HttpResponseMessage CleanNativeItemResponse(HttpRequestMessage request)
    {
        Assert.Equal("fixture", Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri!.Query)["ApiKey"].ToString());
        return Json(200, """{"Id":"native","Type":"Audio","OfficialRating":"Clean"}""");
    }

    private static HttpResponseMessage NativeCleanAudioResponse()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3, 4]) };
        response.Content.Headers.ContentLength = 4;
        return response;
    }

    private static Action<IServiceCollection> CleanOnlyServices(string backend) => services =>
    {
        var user = Guid.Parse("018f1f6e-8e9c-77f5-9a79-3d8a494d60cd");
        services.AddSingleton<IStartupFilter>(new ArtworkPrincipalStartupFilter(new AllstarrPrincipal(user,
            backend.ToLowerInvariant(), "primary", backend == "Jellyfin" ? "user-1" : "fixture", "Listener", false)));
        var policies = new Mock<IEffectiveProviderPolicyResolver>(MockBehavior.Strict);
        policies.Setup(service => service.ResolveForUserAsync(user, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EffectiveProviderPolicySnapshot(ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty,
                ImmutableHashSet<string>.Empty, AudioQualityPolicy.DefaultStep, 0.07)
            { UserId = user, Preferences = new("CleanOnly"), PreferenceRevision = "clean" });
        services.RemoveAll<IEffectiveProviderPolicyResolver>();
        services.AddSingleton(policies.Object);
    };
}
