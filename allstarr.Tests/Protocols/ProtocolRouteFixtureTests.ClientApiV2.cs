using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using allstarr.Core.Capabilities;
using allstarr.Core.ClientApiV2;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    private const string V2LibrarySong = """
        {"Items":[{"Id":"native1","Type":"Audio","Name":"Library song","ArtistItems":[{"Name":"Library artist","Id":"artist1"}],
        "AlbumArtists":[{"Name":"Library artist","Id":"artist1"}],"Album":"Library album","AlbumId":"album1","AlbumPrimaryImageTag":"albumtag",
        "ImageTags":{"Primary":"songtag"},"RunTimeTicks":2000000000,"ProductionYear":2020,"IndexNumber":3,"ParentIndexNumber":1,
        "NormalizationGain":-7.5,"ProviderIds":{"MusicBrainzTrack":"mbid-1"},"UserData":{"IsFavorite":true},
        "MediaSources":[{"Container":"flac","Bitrate":2000000,"MediaStreams":[{"Type":"Audio","Codec":"flac","SampleRate":96000,"BitDepth":24,"Channels":2}]}]}]}
        """;

    [Fact]
    public async Task ClientApiV2_TracksReadLibrarySongsWithABearerToken()
    {
        var authorizations = new List<string>();
        using var factory = ClientDetailsFactory(request =>
        {
            authorizations.Add(request.Headers.TryGetValues("Authorization", out var values) ? values.Single() : "");
            if (request.RequestUri!.AbsolutePath == "/Users/Me") return Json(200, """{"Id":"listener"}""");
            Assert.Equal("/Users/listener/Items", request.RequestUri.AbsolutePath);
            return Json(200, V2LibrarySong);
        });
        using var fixtures = new ProviderIconFiles();
        using var client = CreateClientApiClient(factory);
        fixtures.Use(factory.Services);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        using var response = await client.GetAsync("/allstarr/v2/tracks?ids=native1,unknown,trk_s!!&include=album");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using var json = JsonDocument.Parse(body);
        var track = Assert.Single(json.RootElement.GetProperty("tracks").EnumerateArray());
        Assert.Equal(V2Ids.TrackSource("native1"), track.GetProperty("id").GetString());
        Assert.Equal(["native1"], track.GetProperty("aliases").EnumerateArray().Select(value => value.GetString()!).ToArray());
        Assert.False(track.GetProperty("isExternal").GetBoolean());
        Assert.Equal("native1", track.GetProperty("libraryItemId").GetString());
        AssertLibraryProvider(track.GetProperty("provider"));
        Assert.Equal("Library song", track.GetProperty("title").GetString());
        var artist = Assert.Single(track.GetProperty("artists").EnumerateArray());
        Assert.Equal(V2Ids.Artist("artist1"), artist.GetProperty("id").GetString());
        Assert.Equal("Library artist", artist.GetProperty("name").GetString());
        Assert.Empty(track.GetProperty("featuring").EnumerateArray());
        Assert.Equal(V2Ids.Album("album1"), track.GetProperty("album").GetProperty("id").GetString());
        Assert.Equal(1, track.GetProperty("disc").GetInt32());
        Assert.Equal(3, track.GetProperty("track").GetInt32());
        Assert.Equal(200000, track.GetProperty("durationMs").GetInt64());
        Assert.Equal(2020, track.GetProperty("year").GetInt32());
        Assert.Equal(-7.5, track.GetProperty("trackGainDb").GetDouble());
        Assert.True(track.GetProperty("saved").GetBoolean());
        Assert.Equal("mbid-1", track.GetProperty("externalIds").GetProperty("musicbrainzRecording").GetString());
        Assert.Equal(V2ArtworkUrls.For("native1", "songtag")!.Url, track.GetProperty("artwork").GetProperty("url").GetString());
        var format = track.GetProperty("format");
        Assert.Equal("flac", format.GetProperty("container").GetString());
        Assert.Equal(24, format.GetProperty("bitDepth").GetInt32());
        Assert.Equal(96000, format.GetProperty("sampleRate").GetInt32());
        Assert.True(format.GetProperty("isLossless").GetBoolean());
        var album = json.RootElement.GetProperty("albums").GetProperty(V2Ids.Album("album1"));
        Assert.Equal("Library album", album.GetProperty("title").GetString());
        Assert.Equal(V2ArtworkUrls.For("album1", "albumtag")!.Url, album.GetProperty("artwork").GetProperty("url").GetString());
        Assert.Equal(["unknown", "trk_s!!"], json.RootElement.GetProperty("missing").EnumerateArray().Select(value => value.GetString()!).ToArray());
        Assert.NotEmpty(authorizations);
        Assert.All(authorizations, value => Assert.Equal("MediaBrowser Token=\"listener-token\"", value));
    }

    [Fact]
    public async Task ClientApiV2_TracksUseTheRecordingIdForEverySpellingOfAServiceSong()
    {
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(service => service.GetPlayableProviderOrderAsync(It.IsAny<ProviderActorContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["deezer"]);
        gateway.Setup(service => service.GetStoredSongAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(() => new Song
            {
                Id = "ext-deezer-song-known",
                Title = "Stored song",
                Artist = "Stored artist",
                Artists = ["Stored artist", "Guest"],
                ArtistId = "ext-deezer-artist-9",
                ArtistIds = ["ext-deezer-artist-9", "ext-deezer-artist-10"],
                Album = "Stored album",
                AlbumId = "ext-deezer-album-7",
                Duration = 200,
                Year = 2019,
                ExternalProvider = "deezer",
                ExternalId = "known",
                ReplayGain = new() { TrackGain = -6.25 }
            });
        using var factory = ClientDetailsFactory(request => request.RequestUri!.AbsolutePath == "/Users/Me"
            ? Json(200, """{"Id":"listener"}""") : throw new InvalidOperationException("Unexpected upstream request"), services =>
            {
                services.RemoveAll<IProtocolProviderGateway>();
                services.AddSingleton(gateway.Object);
            });
        using var fixtures = new ProviderIconFiles();
        using var client = CreateClientApiClient(factory);
        fixtures.Use(factory.Services);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        var recording = await SeedClientDetailsAsync(factory, "listener", "known", personal: true);
        var wrapped = V2Ids.TrackSource("ext-deezer-song-known");
        using var response = await client.GetAsync($"/allstarr/v2/tracks?ids=ext-deezer-song-known,{V2Ids.Track(recording)},{wrapped}&include=album");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        using var json = JsonDocument.Parse(body);
        var track = Assert.Single(json.RootElement.GetProperty("tracks").EnumerateArray());
        Assert.Equal(V2Ids.Track(recording), track.GetProperty("id").GetString());
        var aliases = track.GetProperty("aliases").EnumerateArray().Select(value => value.GetString()!).ToArray();
        Assert.Contains("ext-deezer-song-known", aliases);
        Assert.Contains(CanonicalRecordingRecord.DefaultPublicId(recording), aliases);
        Assert.Contains(wrapped, aliases);
        Assert.DoesNotContain(V2Ids.Track(recording), aliases);
        Assert.True(track.GetProperty("isExternal").GetBoolean());
        Assert.Equal(JsonValueKind.Null, track.GetProperty("libraryItemId").ValueKind);
        Assert.Equal("deezer", track.GetProperty("provider").GetProperty("id").GetString());
        Assert.Equal("Stored song", track.GetProperty("title").GetString());
        Assert.Equal([V2Ids.Artist("ext-deezer-artist-9"), V2Ids.Artist("ext-deezer-artist-10")],
            track.GetProperty("artists").EnumerateArray().Select(artist => artist.GetProperty("id").GetString()!).ToArray());
        var featured = Assert.Single(track.GetProperty("featuring").EnumerateArray());
        Assert.Equal(V2Ids.Artist("ext-deezer-artist-10"), featured.GetProperty("id").GetString());
        Assert.Equal("Guest", featured.GetProperty("name").GetString());
        Assert.Equal(V2Ids.Album("ext-deezer-album-7"), track.GetProperty("album").GetProperty("id").GetString());
        Assert.Equal(200000, track.GetProperty("durationMs").GetInt64());
        Assert.Equal(-6.25, track.GetProperty("trackGainDb").GetDouble());
        Assert.True(track.GetProperty("explicit").GetBoolean());
        Assert.True(track.GetProperty("saved").GetBoolean());
        Assert.Equal("kept", track.GetProperty("keep").GetProperty("state").GetString());
        Assert.Equal("pinned", track.GetProperty("match").GetProperty("state").GetString());
        Assert.True(track.GetProperty("lyrics").GetProperty("available").GetBoolean());
        Assert.Equal("USABC1200001", track.GetProperty("externalIds").GetProperty("isrc").GetString());
        Assert.Equal("known", track.GetProperty("externalIds").GetProperty("deezer").GetString());
        Assert.Equal("flac", track.GetProperty("format").GetProperty("codec").GetString());
        Assert.StartsWith(V2ArtworkUrls.PathPrefix, track.GetProperty("artwork").GetProperty("url").GetString());
        Assert.True(json.RootElement.GetProperty("albums").GetProperty(V2Ids.Album("ext-deezer-album-7")).GetProperty("isExternal").GetBoolean());
        Assert.Empty(json.RootElement.GetProperty("missing").EnumerateArray());

        using var single = await client.GetAsync($"/allstarr/v2/tracks/{V2Ids.Track(recording)}");
        var singleBody = await single.Content.ReadAsStringAsync();
        Assert.True(single.StatusCode == HttpStatusCode.OK, singleBody);
        using var singleJson = JsonDocument.Parse(singleBody);
        Assert.Equal(V2Ids.Track(recording), singleJson.RootElement.GetProperty("track").GetProperty("id").GetString());
        Assert.False(singleJson.RootElement.TryGetProperty("albums", out _));
        using var unknown = await client.GetAsync($"/allstarr/v2/tracks/{V2Ids.Track(Guid.NewGuid())}");
        await AssertClientApiError(unknown, 404, "not_found");
    }

    [Theory]
    [InlineData("/allstarr/v2/tracks")]
    [InlineData("/allstarr/v2/tracks?ids=")]
    [InlineData("/allstarr/v2/tracks?ids=native1,,native2")]
    [InlineData("/allstarr/v2/tracks?ids=native1&include=albums")]
    [InlineData("/allstarr/v2/tracks?ids=native1&include=album,album")]
    [InlineData("/allstarr/v2/tracks/native1?include=artist")]
    public async Task ClientApiV2_TracksRejectInvalidRequests(string path)
    {
        using var factory = ClientDetailsFactory(request => request.RequestUri!.AbsolutePath == "/Users/Me"
            ? Json(200, """{"Id":"listener"}""") : throw new InvalidOperationException("Unexpected upstream request"));
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        using var response = await client.GetAsync(path);
        await AssertClientApiError(response, 400, "invalid_request");
    }

    [Fact]
    public async Task ClientApiV2_TracksRejectTooManyIds()
    {
        using var factory = ClientDetailsFactory(request => request.RequestUri!.AbsolutePath == "/Users/Me"
            ? Json(200, """{"Id":"listener"}""") : throw new InvalidOperationException("Unexpected upstream request"));
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        using var response = await client.GetAsync("/allstarr/v2/tracks?ids=" + string.Join(',', Enumerable.Range(0, 101).Select(index => $"native{index}")));
        await AssertClientApiError(response, 400, "invalid_request");
    }

    [Theory]
    [InlineData("/allstarr/v2/tracks?ids=native1&api_key=listener-token", null)]
    [InlineData("/allstarr/v2/tracks?ids=native1&ApiKey=listener-token", null)]
    [InlineData("/allstarr/v2/tracks?ids=native1", "listener token")]
    [InlineData("/allstarr/v2/tracks?ids=native1", "listener\"token")]
    [InlineData("/allstarr/v2/tracks?ids=native1", null)]
    public async Task ClientApiV2_TokensTravelOnlyInTheAuthorizationHeader(string path, string? bearer)
    {
        var requests = 0;
        using var factory = ClientDetailsFactory(_ =>
        {
            requests++;
            return Json(200, """{"Id":"listener"}""");
        });
        using var client = CreateClientApiClient(factory);
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (bearer != null) request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bearer);
        using var response = await client.SendAsync(request);
        await AssertClientApiError(response, 401, "unauthorized");
        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task ClientApiV2_ArtworkIsServedByTheImageRoute()
    {
        var images = new List<Uri>();
        var imageAuthorizations = new List<string?>();
        using var factory = ClientDetailsFactory(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/Users/Me") return Json(200, """{"Id":"listener"}""");
            // The relay first confirms the signed-in listener can see the item.
            if (request.RequestUri.AbsolutePath == "/Items")
                return Json(200, """{"Items":[{"Id":"native1","Type":"Audio"}],"TotalRecordCount":1}""");
            images.Add(request.RequestUri);
            imageAuthorizations.Add(request.Headers.TryGetValues("Authorization", out var values) ? values.Single() : null);
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent([0x89, 0x50, 0x4e, 0x47]) { Headers = { ContentType = new("image/png") } } };
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        var url = V2ArtworkUrls.For("native1", "songtag")!.Url;
        using var response = await client.GetAsync(url + "?w=300");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        var image = Assert.Single(images);
        Assert.Equal("/Items/native1/Images/Primary", image.AbsolutePath);
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(image.Query);
        Assert.Equal("songtag", query["tag"].ToString());
        Assert.Equal("300", query["maxWidth"].ToString());

        using var invalid = await client.GetAsync(V2ArtworkUrls.PathPrefix + "bmF0aXZlMQ");
        await AssertClientApiError(invalid, 404, "not_found");
        using var nested = await client.GetAsync(url + "/extra");
        await AssertClientApiError(nested, 404, "not_found");
        using var posted = await client.PostAsync(url, null);
        await AssertClientApiError(posted, 404, "not_found");
        Assert.Single(images);

        // Lock screens, cast receivers and widgets load artwork without a token, as they do the media server's image urls.
        using var anonymous = factory.CreateClient();
        using var unsigned = await anonymous.GetAsync(url + "?w=120");
        Assert.Equal(HttpStatusCode.OK, unsigned.StatusCode);
        Assert.Equal(2, images.Count);
        Assert.Equal("120", Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(images[1].Query)["maxWidth"].ToString());
        Assert.DoesNotContain("listener-token", imageAuthorizations[1] ?? "");
    }

    [Fact]
    public async Task ClientApiV2_TrackViewsUseTheSameReadersAsTheItemRoutes()
    {
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(service => service.GetPlayableProviderOrderAsync(It.IsAny<ProviderActorContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["deezer"]);
        gateway.Setup(service => service.GetStoredSongAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(() => new Song { Id = "ext-deezer-song-known", Title = "Stored song", Artist = "Stored artist", ExternalProvider = "deezer", ExternalId = "known" });
        using var factory = ClientDetailsFactory(request => request.RequestUri!.AbsolutePath == "/Users/Me"
            ? Json(200, """{"Id":"listener"}""") : throw new InvalidOperationException("Unexpected upstream request"), services =>
            {
                services.RemoveAll<IProtocolProviderGateway>();
                services.AddSingleton(gateway.Object);
            });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        var recording = await SeedClientDetailsAsync(factory, "listener", "known", personal: true);
        using var response = await client.GetAsync("/allstarr/v2/tracks/ext-deezer-song-known?views=match,lyrics,lyricsSources");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        var json = JsonNode.Parse(body)!;
        Assert.Equal(V2Ids.Track(recording), json["track"]!["id"]!.GetValue<string>());
        var status = json["viewStatus"]!.AsObject();
        Assert.Equal(["lyrics", "lyricsSources", "match"], status.Select(item => item.Key).Order().ToArray());
        // Each view answers exactly as its v1 item route does for the same song.
        using var v1 = CreateClientApiClient(factory);
        v1.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        foreach (var (view, path) in new[] { ("lyrics", "lyrics"), ("lyricsSources", "lyrics/sources"), ("match", "match") })
        {
            using var item = await v1.GetAsync($"/allstarr/v1/items/ext-deezer-song-known/{path}");
            var itemBody = await item.Content.ReadAsStringAsync();
            if (item.StatusCode == HttpStatusCode.OK)
            {
                Assert.Equal("ok", status[view]!.GetValue<string>());
                Assert.True(JsonNode.DeepEquals(JsonNode.Parse(itemBody), json["views"]![view]), $"{view}: {itemBody} vs {json["views"]![view]}");
            }
            else
            {
                Assert.Equal(HttpStatusCode.NotFound, item.StatusCode);
                Assert.Equal("none", status[view]!.GetValue<string>());
                Assert.Null(json["views"]![view]);
            }
        }
        Assert.True(status.Any(item => item.Value!.GetValue<string>() == "ok"), status.ToJsonString());

        using var plain = await client.GetAsync("/allstarr/v2/tracks/ext-deezer-song-known");
        using var plainJson = JsonDocument.Parse(await plain.Content.ReadAsStringAsync());
        Assert.False(plainJson.RootElement.TryGetProperty("views", out _));
        Assert.False(plainJson.RootElement.TryGetProperty("viewStatus", out _));
        foreach (var invalid in new[] { "lyric", "lyrics,lyrics", "playbacks" })
        {
            using var rejected = await client.GetAsync($"/allstarr/v2/tracks/ext-deezer-song-known?views={invalid}");
            await AssertClientApiError(rejected, 400, "invalid_request");
        }
    }

    [Fact]
    public async Task ClientApiV2_BatchRunsOnlyV2ReadsAndKeepsFailuresSeparate()
    {
        using var factory = ClientDetailsFactory(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/Users/Me") return Json(200, """{"Id":"listener"}""");
            Assert.Equal("/Users/listener/Items", request.RequestUri.AbsolutePath);
            return Json(200, V2LibrarySong);
        });
        using var fixtures = new ProviderIconFiles();
        using var client = CreateClientApiClient(factory);
        fixtures.Use(factory.Services);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        var requests = new[]
        {
            new { id = "list", path = "/allstarr/v2/tracks?ids=native1&include=album" },
            new { id = "one", path = "/allstarr/v2/tracks/native1" },
            new { id = "badView", path = "/allstarr/v2/tracks/native1?views=bogus" },
            new { id = "token", path = "/allstarr/v2/tracks/native1?api_key=listener-token" },
            new { id = "repeated", path = "/allstarr/v2/tracks?ids=native1&ids=native2" },
            new { id = "unknown", path = "/allstarr/v2/tracks/unknown" },
            new { id = "artwork", path = "/allstarr/v2/artwork/bmF0aXZlMQ" },
            new { id = "v1", path = "/allstarr/v1/capabilities" },
            new { id = "escape", path = "/allstarr/v2/tracks/a%2Fb" },
            new { id = "outside", path = "https://example.test/allstarr/v2/tracks/native1" }
        };
        using var response = await client.PostAsJsonAsync("/allstarr/v2/batch", new { requests });
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using var json = JsonDocument.Parse(body);
        var responses = json.RootElement.GetProperty("responses");
        Assert.Equal(requests.Select(item => item.id).Order(), responses.EnumerateObject().Select(item => item.Name).Order());
        Assert.Equal(200, responses.GetProperty("list").GetProperty("status").GetInt32());
        var listed = responses.GetProperty("list").GetProperty("body");
        Assert.Equal(V2Ids.TrackSource("native1"), Assert.Single(listed.GetProperty("tracks").EnumerateArray()).GetProperty("id").GetString());
        Assert.True(listed.GetProperty("albums").TryGetProperty(V2Ids.Album("album1"), out _));
        Assert.Equal("Library song", responses.GetProperty("one").GetProperty("body").GetProperty("track").GetProperty("title").GetString());
        foreach (var (id, status, code) in new[] { ("badView", 400, "invalid_request"), ("token", 400, "invalid_request"), ("repeated", 400, "invalid_request"),
                     ("unknown", 404, "not_found"), ("artwork", 404, "not_found"), ("v1", 404, "not_found"), ("escape", 404, "not_found"), ("outside", 404, "not_found") })
        {
            var result = responses.GetProperty(id);
            Assert.Equal(status, result.GetProperty("status").GetInt32());
            Assert.Equal(code, result.GetProperty("body").GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task ClientApiV2_BatchReadsRunSideBySideWithTheirOwnRequestState()
    {
        using var factory = ClientDetailsFactory(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/Users/Me") return Json(200, """{"Id":"listener"}""");
            return Json(200, V2LibrarySong);
        });
        using var fixtures = new ProviderIconFiles();
        using var client = CreateClientApiClient(factory);
        fixtures.Use(factory.Services);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        var requests = Enumerable.Range(0, V2Contract.MaximumBatchRequests).Select(index => new { id = $"r{index}", path = "/allstarr/v2/tracks/native1" }).ToArray();
        using var response = await client.PostAsJsonAsync("/allstarr/v2/batch", new { requests });
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        using var json = JsonDocument.Parse(body);
        foreach (var result in json.RootElement.GetProperty("responses").EnumerateObject())
        {
            Assert.Equal(200, result.Value.GetProperty("status").GetInt32());
            Assert.True(result.Value.GetProperty("body").GetProperty("track").GetProperty("saved").GetBoolean());
        }
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(21, false)]
    [InlineData(2, true)]
    public async Task ClientApiV2_BatchRejectsEmptyOversizedAndDuplicateRequests(int count, bool duplicate)
    {
        using var factory = ClientDetailsFactory(request => request.RequestUri!.AbsolutePath == "/Users/Me"
            ? Json(200, """{"Id":"listener"}""") : throw new InvalidOperationException("Unexpected upstream request"));
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        var requests = Enumerable.Range(0, count).Select(index => new { id = duplicate ? "same" : $"r{index}", path = "/allstarr/v2/tracks/native1" });
        using var response = await client.PostAsJsonAsync("/allstarr/v2/batch", new { requests });
        await AssertClientApiError(response, 400, "invalid_request");
    }

    [Theory]
    [InlineData("")]
    [InlineData("?documentName=other")]
    public async Task ClientApiV2_OpenApiMatchesTheCheckedInContract(string query)
    {
        using var factory = ClientApiFactory(_ => throw new InvalidOperationException("Unexpected upstream request"));
        using var client = CreateClientApiClient(factory);
        using var response = await client.GetAsync("/allstarr/v2/openapi.json" + query);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Expected the client document, received {(int)response.StatusCode}: {body}");
        var document = JsonNode.Parse(body)!;
        var paths = document["paths"]!.AsObject().Select(path => path.Key).ToArray();
        Assert.NotEmpty(paths);
        Assert.All(paths, path => Assert.StartsWith("/allstarr/v2/", path));
        var path = Path.Combine(FindClientApiRepositoryRoot(), "apis", "specifications", "allstarr", "client-v2.json");
        var json = document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
        if (Environment.GetEnvironmentVariable("ALLSTARR_UPDATE_CLIENT_SPEC") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, json);
        }
        else
        {
            Assert.True(File.Exists(path) && JsonNode.DeepEquals(JsonNode.Parse(await File.ReadAllTextAsync(path)), document),
                "The client contract changed. Generated document:\n" + json);
        }
    }
}
