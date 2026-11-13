using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using allstarr.Core.ClientApiV2;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Theory]
    [InlineData("session")]
    [InlineData("tracks/native1/lyrics/sources")]
    [InlineData("tracks/native1/match")]
    [InlineData("tracks/native1/appearances")]
    [InlineData("albums/album1/similar")]
    [InlineData("playback/00000000000000000000000000000001")]
    public async Task ClientApiV2_ResourceReadsRequireHeaderAuthentication(string resource)
    {
        using var factory = ClientDetailsFactory(_ => throw new InvalidOperationException("Unexpected upstream request"));
        using var client = CreateClientApiClient(factory);
        using var anonymous = await client.GetAsync("/allstarr/v2/" + resource);
        await AssertClientApiError(anonymous, 401, "unauthorized");
        using var queryToken = await client.GetAsync("/allstarr/v2/" + resource + "?api_key=listener-token");
        await AssertClientApiError(queryToken, 401, "unauthorized");
    }

    [Fact]
    public async Task ClientApiV2_SessionReturnsTheListenerFeaturesAndTheCapabilitiesDashboard()
    {
        using var factory = ClientDetailsFactory(request => request.RequestUri!.AbsolutePath == "/Users/Me"
            ? Json(200, request.Headers.Authorization?.ToString().Contains("other-token", StringComparison.Ordinal) == true
                ? """{"Id":"other","Name":"Other listener"}""" : """{"Id":"listener","Name":"Music listener"}""")
            : Json(200, """{"Items":[]}"""));
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        await factory.Services.GetRequiredService<allstarr.Core.Settings.IDurableRuntimeSettings>()
            .ApplyBatchAsync([new("Admin:Oidc:PublicUrl", "https://music.example.test/dashboard")], "fixture");
        var session = await ReadClientLyricsJsonAsync(client, "/allstarr/v2/session");
        Assert.Equal("Music listener", session["listener"]!["name"]!.GetValue<string>());
        Assert.True(Guid.TryParse(session["listener"]!["id"]!.GetValue<string>(), out _));
        using var v1 = await client.GetAsync("/allstarr/v1/capabilities");
        v1.EnsureSuccessStatusCode();
        var capabilities = JsonNode.Parse(await v1.Content.ReadAsStringAsync())!;
        Assert.Equal(capabilities["serverVersion"]!.GetValue<string>(), session["serverVersion"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(capabilities["dashboard"], session["dashboard"]));
        foreach (var name in new[] { "tracks", "albums", "batch", "playback", "events", "artistLinks", "lyricsTranslation", "autoplay",
                     "trackFacts", "lyricsSources", "trackMatch", "appearances", "similarAlbums", "playbackStatus", "mixes", "releases",
                     "libraryAdditions", "artistFollow", "reports", "karaokeModel", "providerIcons", "history", "historyImports",
                     "historyRecheck", "stats", "sound", "taste", "preferences", "search", "lyricsSearch" })
            Assert.True(session["features"]![name]!.GetValue<bool>());
        await AssertV2ResourceRead(client, "/allstarr/v2/session", session);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "other-token");
        var other = await ReadClientLyricsJsonAsync(client, "/allstarr/v2/session");
        Assert.NotEqual(session["listener"]!["id"]!.GetValue<string>(), other["listener"]!["id"]!.GetValue<string>());
        Assert.Equal("Other listener", other["listener"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task ClientApiV2_LyricsSourcesChooseAndResetWithTheSameRevisions()
    {
        var recording = Guid.NewGuid();
        using var factory = ClientLyricsFactory(recording, new() { ["lrclib"] = ClientLyricsResult("line"), ["lyricsplus"] = ClientLyricsResult("word") });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        await SeedClientLyricsAsync(factory, recording);
        var path = $"/allstarr/v2/tracks/{V2Ids.Track(recording)}/lyrics";
        var initial = await ReadClientLyricsJsonAsync(client, path + "/sources?includeLyrics=true");
        Assert.All(initial["sources"]!.AsArray(), source => Assert.NotNull(source!["lyrics"]));
        var original = await ReadClientLyricsJsonAsync(client, $"/allstarr/v1/items/ext-allstarr-song-{recording:D}/lyrics/sources?includeLyrics=true");
        Assert.True(JsonNode.DeepEquals(original, initial));
        await AssertV2ResourceRead(client, path + "/sources?includeLyrics=true", initial);
        using var put = await client.PutAsJsonAsync(path + "/source", new { sourceId = "lrclib", expectedRevision = initial["revision"] });
        put.EnsureSuccessStatusCode();
        var chosen = JsonNode.Parse(await put.Content.ReadAsStringAsync())!;
        Assert.Equal("personal", chosen["chosen"]!["layer"]!.GetValue<string>());
        Assert.Equal("lrclib", chosen["chosen"]!["sourceId"]!.GetValue<string>());
        using var stale = await client.PutAsJsonAsync(path + "/source", new { sourceId = "lyricsplus", expectedRevision = initial["revision"] });
        await AssertClientApiError(stale, 409, "conflict");
        using var delete = new HttpRequestMessage(HttpMethod.Delete, path + "/source")
        { Content = JsonContent.Create(new { expectedRevision = chosen["revision"] }) };
        using var reset = await client.SendAsync(delete);
        reset.EnsureSuccessStatusCode();
        var cleared = JsonNode.Parse(await reset.Content.ReadAsStringAsync())!;
        Assert.Null(cleared["revision"]!["personal"]);
        Assert.Equal("automatic", cleared["chosen"]!["layer"]!.GetValue<string>());
    }

    [Fact]
    public async Task ClientApiV2_MatchCanChooseRejectAndResetAPersonalAnswer()
    {
        var recording = Guid.NewGuid();
        using var factory = ClientMatchFactory(true, recording);
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        await SeedClientMatchAsync(factory, recording);
        var path = $"/allstarr/v2/tracks/{V2Ids.Track(recording)}/match";
        var initial = await ReadClientLyricsJsonAsync(client, path);
        var v1 = await ReadClientLyricsJsonAsync(client, "/allstarr/v1/items/ext-deezer-song-match/match");
        Assert.True(JsonNode.DeepEquals(initial, v1));
        await AssertV2ResourceRead(client, path, initial);
        var candidate = initial["candidates"]!.AsArray().Single(item => item!["provider"]!["id"]!.GetValue<string>() == "deezer")!;
        using var put = await client.PutAsJsonAsync(path, new { candidateId = candidate["candidateId"]!.GetValue<string>(), expectedRevision = initial["revision"]!.GetValue<string>() });
        put.EnsureSuccessStatusCode();
        var chosen = JsonNode.Parse(await put.Content.ReadAsStringAsync())!;
        Assert.Equal("personal", chosen["authority"]!.GetValue<string>());
        using var stale = await client.PostAsJsonAsync(path + "/reject", new { expectedRevision = initial["revision"]!.GetValue<string>() });
        await AssertClientApiError(stale, 409, "conflict");
        using var reject = await client.PostAsJsonAsync(path + "/reject", new { expectedRevision = chosen["revision"]!.GetValue<string>() });
        reject.EnsureSuccessStatusCode();
        var rejected = JsonNode.Parse(await reject.Content.ReadAsStringAsync())!;
        Assert.Equal("rejected", rejected["state"]!.GetValue<string>());
        Assert.Null(rejected["current"]);
        using var reset = await client.DeleteAsync(path + "?expectedRevision=" + rejected["revision"]!.GetValue<string>());
        reset.EnsureSuccessStatusCode();
        Assert.Equal("household", JsonNode.Parse(await reset.Content.ReadAsStringAsync())!["authority"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("GET", "lyrics/sources")]
    [InlineData("PUT", "lyrics/source")]
    [InlineData("DELETE", "lyrics/source")]
    [InlineData("GET", "match")]
    [InlineData("PUT", "match")]
    [InlineData("DELETE", "match")]
    [InlineData("POST", "match/reject")]
    [InlineData("GET", "appearances")]
    public async Task ClientApiV2_TrackResourcesRejectMalformedAndForeignIds(string method, string suffix)
    {
        using var factory = ClientDetailsFactory(request => request.RequestUri!.AbsolutePath == "/Users/Me"
            ? Json(200, """{"Id":"listener"}""") : Json(404, "{}"));
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        var foreign = await SeedV2DetailsAsync(factory, "other", "foreign", personal: false);
        foreach (var (id, expected) in new[] { ("trk_s!!", 400), (V2Ids.Track(foreign), 404) })
        {
            using var request = new HttpRequestMessage(new(method), $"/allstarr/v2/tracks/{id}/{suffix}?expectedRevision=revision")
            {
                Content = method == "GET" || method == "DELETE" && suffix == "match" ? null :
                    JsonContent.Create(new { sourceId = "lrclib", candidateId = "candidate", expectedRevision = suffix.StartsWith("lyrics", StringComparison.Ordinal) ? (object)new { } : "revision" })
            };
            using var response = await client.SendAsync(request);
            Assert.Equal(expected, (int)response.StatusCode);
            if (method == "GET") Assert.Equal(expected, (await ReadV2Batch(client, $"/allstarr/v2/tracks/{id}/{suffix}"))["status"]!.GetValue<int>());
        }
    }

    [Theory]
    [InlineData("PUT", "lyrics/source", "{}")]
    [InlineData("DELETE", "lyrics/source", "{}")]
    [InlineData("PUT", "match", "{}")]
    [InlineData("POST", "match/reject", "{}")]
    [InlineData("DELETE", "match", null)]
    [InlineData("GET", "lyrics/sources?includeLyrics=invalid", null)]
    public async Task ClientApiV2_TrackResourcesValidateParameters(string method, string suffix, string? body)
    {
        using var factory = ClientDetailsFactory(_ => Json(200, """{"Id":"listener"}"""));
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        using var request = new HttpRequestMessage(new(method), "/allstarr/v2/tracks/native1/" + suffix)
        { Content = body == null ? null : new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        using var response = await client.SendAsync(request);
        await AssertClientApiError(response, 400, "invalid_request");
    }

    [Fact]
    public async Task ClientApiV2_AppearancesReturnNativeMixAndPlaylistIds()
    {
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "mix-song")).ReturnsAsync(ClientMixSong());
        using var factory = ClientMixFactory(gateway);
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        var mix = await SeedClientMixAsync(factory);
        var path = $"/allstarr/v2/tracks/{V2Ids.TrackSource("ext-deezer-song-mix-song")}/appearances";
        var result = await ReadClientLyricsJsonAsync(client, path);
        Assert.Equal(1, result["songCount"]!.GetValue<int>());
        var appearance = Assert.Single(result["appearances"]!.AsArray())!;
        Assert.Equal(V2Ids.Mix(mix.Id), appearance["id"]!.GetValue<string>());
        Assert.StartsWith("pl_", appearance["playlistId"]!.GetValue<string>());
        Assert.Equal("Saved favourites", appearance["title"]!.GetValue<string>());
        Assert.Equal(1, appearance["matchedSongCount"]!.GetValue<int>());
        await AssertV2ResourceRead(client, path, result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClientApiV2_SimilarAlbumsReturnNativeAlbumShapes(bool library)
    {
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "seed"))
            .ReturnsAsync(new Album { Id = "ext-deezer-album-seed", Title = "Seed", Artist = "Seed artist", ArtistId = "ext-deezer-artist-seed" });
        gateway.Setup(item => item.GetSimilarArtistsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "seed", It.IsAny<int>()))
            .ReturnsAsync([new Artist { Name = "Other artist", ExternalProvider = "deezer", ExternalId = "other" }]);
        gateway.Setup(item => item.GetArtistAlbumsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "other", It.IsAny<int>()))
            .ReturnsAsync([new Album { Id = "ext-deezer-album-other", Title = "Other album", Artist = "Other artist", ArtistId = "ext-deezer-artist-other", SongCount = 10, Year = 2020 }]);
        using var factory = ClientDetailsFactory(request => request.RequestUri!.AbsolutePath switch
        {
            "/Users/Me" => Json(200, """{"Id":"listener"}"""),
            "/Artists/AlbumArtists" when library => Json(200, """{"Items":[{"Id":"library-artist","Name":"Other artist"}]}"""),
            "/Users/listener/Items" when library => Json(200, """{"Items":[{"Id":"library-album","Type":"MusicAlbum","Name":"Other album","AlbumArtist":"Other artist","AlbumArtists":[{"Id":"library-artist","Name":"Other artist"}],"ProductionYear":2020,"ChildCount":10,"ImageTags":{"Primary":"albumtag"}}]}"""),
            _ => Json(200, """{"Items":[]}""")
        }, services =>
            {
                services.RemoveAll<IProtocolProviderGateway>();
                services.AddSingleton(gateway.Object);
            });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        var path = $"/allstarr/v2/albums/{V2Ids.Album("ext-deezer-album-seed")}/similar?limit=12";
        var result = await ReadClientLyricsJsonAsync(client, path);
        Assert.Equal("deezer", result["basedOn"]!.GetValue<string>());
        var album = Assert.Single(result["albums"]!.AsArray())!;
        Assert.Equal(V2Ids.Album(library ? "library-album" : "ext-deezer-album-other"), album["id"]!.GetValue<string>());
        Assert.Equal(V2Ids.Artist(library ? "library-artist" : "ext-deezer-artist-other"), album["artists"]![0]!["id"]!.GetValue<string>());
        Assert.Equal(10, album["songCount"]!.GetValue<int>());
        Assert.Equal(library ? "library" : "deezer", album["source"]!.GetValue<string>());
        Assert.Equal(!library, album["isExternal"]!.GetValue<bool>());
        Assert.StartsWith(V2ArtworkUrls.PathPrefix, album["artwork"]!["url"]!.GetValue<string>());
        await AssertV2ResourceRead(client, path, result);
    }

    [Theory]
    [InlineData("trk_s!!", "", 400)]
    [InlineData("alb_!!", "", 400)]
    [InlineData("unknown", "", 404)]
    [InlineData("hidden", "", 404)]
    [InlineData("album1", "?limit=0", 400)]
    [InlineData("album1", "?limit=25", 400)]
    [InlineData("album1", "?limit=wrong", 400)]
    public async Task ClientApiV2_SimilarAlbumsValidateIdsAndLimits(string id, string query, int expected)
    {
        using var factory = ClientDetailsFactory(request => request.RequestUri!.AbsolutePath == "/Users/Me"
            ? Json(200, """{"Id":"listener"}""") : Json(request.RequestUri.AbsolutePath.Contains("hidden", StringComparison.Ordinal) ? 403 : 404, "{}"));
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        var path = $"/allstarr/v2/albums/{id}/similar{query}";
        using var response = await client.GetAsync(path);
        Assert.Equal(expected, (int)response.StatusCode);
        Assert.Equal(expected, (await ReadV2Batch(client, path))["status"]!.GetValue<int>());
    }

    private static async Task AssertV2ResourceRead(HttpClient client, string path, JsonNode expected)
    {
        using var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        var etag = response.Headers.ETag!.ToString();
        var batch = await ReadV2Batch(client, path);
        Assert.Equal(200, batch["status"]!.GetValue<int>());
        Assert.True(JsonNode.DeepEquals(expected, batch["body"]));
        Assert.Equal(etag, batch["etag"]!.GetValue<string>());
        await AssertV2NotModified(client, path, etag);
        Assert.Equal(304, (await ReadV2Batch(client, path, etag))["status"]!.GetValue<int>());
    }
}
