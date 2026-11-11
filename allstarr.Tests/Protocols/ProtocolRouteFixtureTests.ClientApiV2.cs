using System.Net;
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
                Artists = ["Stored artist"],
                ArtistId = "ext-deezer-artist-9",
                ArtistIds = ["ext-deezer-artist-9"],
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
        Assert.Equal(V2Ids.Artist("ext-deezer-artist-9"), Assert.Single(track.GetProperty("artists").EnumerateArray()).GetProperty("id").GetString());
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
        using var factory = ClientDetailsFactory(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/Users/Me") return Json(200, """{"Id":"listener"}""");
            // The relay first confirms the signed-in listener can see the item.
            if (request.RequestUri.AbsolutePath == "/Items")
                return Json(200, """{"Items":[{"Id":"native1","Type":"Audio"}],"TotalRecordCount":1}""");
            images.Add(request.RequestUri);
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
