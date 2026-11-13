using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using allstarr.Core.Caching;
using allstarr.Core.Capabilities;
using allstarr.Core.ClientApi;
using allstarr.Core.ClientApiV2;
using allstarr.Core.Enrichment;
using allstarr.Core.Identity;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Core.Releases;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Fact]
    public void ClientApiV2_LibraryEndpointNamesAreUniqueAcrossVersions()
    {
        var names = typeof(allstarr.Controllers.ClientApiV2.MixesController).Assembly.GetTypes()
            .Where(type => type.Namespace is "allstarr.Controllers.ClientApi" or "allstarr.Controllers.ClientApiV2")
            .SelectMany(type => type.GetMethods()).SelectMany(method => method.GetCustomAttributes(false))
            .OfType<IEndpointNameMetadata>().Select(metadata => metadata.EndpointName).ToArray();
        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task ClientApiV2_MixesReturnPublicationAndConditionalReads()
    {
        using var factory = ClientMixFactory(new());
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        var mix = await SeedClientMixAsync(factory);
        await factory.Services.GetRequiredService<IDurableRuntimeSettings>().ApplyBatchAsync([new(PersonalMixService.PublishKey, "true")], "fixture");
        using var response = await client.GetAsync("/allstarr/v2/mixes?timeZoneId=UTC");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var card = Assert.Single(body.GetProperty("mixes").EnumerateArray(), item => item.GetProperty("id").GetString() == V2Ids.Mix(mix.Id));
        Assert.True(card.GetProperty("inApps").GetBoolean());
        Assert.StartsWith("pl_", card.GetProperty("playlistId").GetString());
        Assert.Equal("ready", card.GetProperty("state").GetString());
        Assert.Equal(2, card.GetProperty("itemCount").GetInt32());
        Assert.StartsWith($"/allstarr/v2/mixes/{V2Ids.Mix(mix.Id)}/cover?v=", card.GetProperty("coverUrl").GetString());
        Assert.False(card.TryGetProperty("mixId", out _));
        await AssertV2LibraryConditionalAsync(client, "/allstarr/v2/mixes?timeZoneId=UTC", response);
        await AssertV2LibraryBatchAsync(client, "/allstarr/v2/mixes?timeZoneId=UTC", response);
    }

    [Fact]
    public async Task ClientApiV2_MixTracksReturnNativeShapesAndHideOtherListeners()
    {
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(service => service.GetStoredSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "mix-song"))
            .ReturnsAsync(ClientMixSong());
        using var factory = ClientMixFactory(gateway);
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        var mix = await SeedClientMixAsync(factory);
        var principal = (await factory.Services.GetRequiredService<BackendIdentityResolver>().ResolveAsync(new("Jellyfin", "listener")))!;
        var actor = new ProviderActorContext(ProviderActorKind.User, principal.UserId, new("jellyfin", "primary", "listener"));
        await factory.Services.GetRequiredService<IApplicationCache>().SetStringAsync(
            ProtocolProviderGateway.MetadataCacheKey(actor, "deezer", ProviderResourceKind.Track, "mix-song"), JsonSerializer.Serialize(ClientMixSong()));
        var path = $"/allstarr/v2/mixes/{V2Ids.Mix(mix.Id)}/tracks";
        using var tracks = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, tracks.StatusCode);
        var page = await tracks.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(V2Ids.Mix(mix.Id), page.GetProperty("id").GetString());
        var songs = page.GetProperty("tracks").EnumerateArray().ToArray();
        Assert.Equal(2, songs.Length);
        Assert.All(songs, song =>
        {
            Assert.StartsWith("trk_", song.GetProperty("id").GetString());
            Assert.True(song.TryGetProperty("aliases", out _));
            Assert.True(song.TryGetProperty("keep", out _));
            Assert.False(song.TryGetProperty("itemId", out _));
        });
        Assert.Equal(["Service song", "Library song"], songs.Select(song => song.GetProperty("title").GetString()));
        await AssertV2LibraryBatchAsync(client, path, tracks);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "other-token");
        using var denied = await client.GetAsync(path);
        await AssertClientApiError(denied, 404, "not_found");
        using var foreignBatch = await client.PostAsJsonAsync("/allstarr/v2/batch", new { requests = new[] { new { id = "mix", path } } });
        Assert.Equal(404, (await foreignBatch.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("responses").GetProperty("mix").GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task ClientApiV2_MixCoversValidateAfterCheckingListenerAccess()
    {
        using var factory = ClientMixFactory(new());
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        var mix = await SeedClientMixAsync(factory);
        var path = $"/allstarr/v2/mixes/{V2Ids.Mix(mix.Id)}/cover?v={MixCoverImageService.Version(MixCoverInput.From(mix))}";
        using var cover = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, cover.StatusCode);
        Assert.Equal("image/jpeg", cover.Content.Headers.ContentType?.MediaType);
        await AssertV2LibraryConditionalAsync(client, path, cover);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "other-token");
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.IfNoneMatch.Add(cover.Headers.ETag!);
        using var hidden = await client.SendAsync(request);
        await AssertClientApiError(hidden, 404, "not_found");
    }

    [Fact]
    public async Task ClientApiV2_ReleasesAndCoversStayListenerScoped()
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(service => service.GetStoredAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "release-album"))
            .ReturnsAsync(new Album { CoverArtUrl = "https://covers.example.test/release.png" });
        byte[] art = [137, 80, 78, 71, 13, 10, 26, 10];
        using var factory = ClientReleaseFactory(ClientReleaseMetadata(), gateway, _ => new(HttpStatusCode.OK)
        { Content = new ByteArrayContent(art) { Headers = { ContentType = new("image/png") } } });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        var owner = (await factory.Services.GetRequiredService<BackendIdentityResolver>().ResolveAsync(new("Jellyfin", "listener")))!.UserId;
        var releases = factory.Services.GetRequiredService<NewReleasesService>();
        await releases.FollowAsync(owner, new("deezer", "release-artist", "Release artist"));
        await releases.PollAsync(owner, Guid.NewGuid(), default);
        using var response = await client.GetAsync("/allstarr/v2/releases");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var release = Assert.Single(body.GetProperty("releases").EnumerateArray());
        Assert.Equal(V2Ids.Album("ext-deezer-album-release-album"), release.GetProperty("albumId").GetString());
        Assert.Equal(V2Ids.Artist("ext-deezer-artist-release-artist"), release.GetProperty("artist").GetProperty("id").GetString());
        Assert.False(release.TryGetProperty("releaseId", out _));
        var coverUrl = release.GetProperty("coverUrl").GetString()!;
        Assert.Equal($"/allstarr/v2/releases/{release.GetProperty("id").GetString()}/cover", coverUrl);
        await AssertV2LibraryConditionalAsync(client, "/allstarr/v2/releases", response);
        await AssertV2LibraryBatchAsync(client, "/allstarr/v2/releases", response);
        using var cover = await client.GetAsync(coverUrl);
        Assert.Equal(HttpStatusCode.OK, cover.StatusCode);
        Assert.Equal(art, await cover.Content.ReadAsByteArrayAsync());
        await AssertV2LibraryConditionalAsync(client, coverUrl, cover);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "other-token");
        using var other = await client.GetAsync("/allstarr/v2/releases");
        Assert.Empty((await other.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("releases").EnumerateArray());
        using var hidden = await client.GetAsync(coverUrl);
        await AssertClientApiError(hidden, 404, "not_found");
    }

    [Theory]
    [InlineData("ext-deezer-artist-release-artist")]
    [InlineData("native-release-artist")]
    public async Task ClientApiV2_FollowAndUnfollowUseTypedArtistsAndPersonalState(string source)
    {
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(service => service.GetArtistAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "release-artist"))
            .ReturnsAsync(new Artist { Name = "Release artist" });
        using var factory = ClientReleaseFactory(ClientReleaseMetadata(), gateway);
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        var resolver = factory.Services.GetRequiredService<BackendIdentityResolver>();
        var owner = (await resolver.ResolveAsync(new("Jellyfin", "listener")))!.UserId;
        var other = (await resolver.ResolveAsync(new("Jellyfin", "other")))!.UserId;
        var releases = factory.Services.GetRequiredService<NewReleasesService>();
        await releases.FollowAsync(other, new("deezer", "release-artist", "Release artist"));
        foreach (var followed in new[] { true, true, false, false })
        {
            using var request = new HttpRequestMessage(followed ? HttpMethod.Put : HttpMethod.Delete, $"/allstarr/v2/artists/{V2Ids.Artist(source)}/follow");
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(followed, body.GetProperty("followed").GetBoolean());
            Assert.Equal(V2Ids.Artist(source), body.GetProperty("artist").GetProperty("id").GetString());
            Assert.Equal(followed ? 1 : 0, (await releases.ListAsync(owner)).Count);
            Assert.Single(await releases.ListAsync(other));
        }
        client.DefaultRequestHeaders.Authorization = new("Bearer", "other-token");
        foreach (var method in new[] { HttpMethod.Put, HttpMethod.Delete })
        {
            using var request = new HttpRequestMessage(method, $"/allstarr/v2/artists/{V2Ids.Artist("native-release-artist")}/follow");
            using var hidden = await client.SendAsync(request);
            await AssertClientApiError(hidden, 404, "not_found");
        }
    }

    [Fact]
    public async Task ClientApiV2_AdditionsReplayListAndCancelOnlyTheListenersRequests()
    {
        using var factory = ClientMeFactory();
        using var client = CreateClientApiClient(factory);
        var (owner, _) = await SeedClientMeAsync(factory, false);
        var id = Guid.CreateVersion7();
        var recording = Guid.NewGuid();
        const string source = "ext-spotify-song-track";
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync())
        {
            db.PromotionRequests.Add(new()
            {
                Id = id,
                RequesterUserId = owner,
                RecordingId = recording,
                IdentityKey = "retry",
                Unit = "song",
                RequestJson = JsonSerializer.Serialize(new PromoteSongRequest(recording, Guid.Empty, Guid.Empty, "music", "retry")
                { Unit = "song", RequestedItemId = source }),
                CreatedAt = DateTimeOffset.UtcNow
            });
            db.AuditEvents.Add(new()
            {
                Id = Guid.CreateVersion7(),
                ActorUserId = owner,
                Action = "library-addition.accepted",
                CorrelationId = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("tap"))),
                SubjectId = id.ToString("D"),
                DetailsJson = JsonSerializer.Serialize(new AddClientLibraryItem(source, "song")),
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        client.DefaultRequestHeaders.Add("Idempotency-Key", "tap");
        using var replay = await client.PostAsJsonAsync("/allstarr/v2/library/additions", new { itemId = V2Ids.TrackSource(source), unit = "song" });
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var addition = await replay.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(id, addition.GetProperty("id").GetGuid());
        Assert.Equal(V2Ids.Track(recording), addition.GetProperty("itemId").GetString());
        Assert.Equal(V2Ids.TrackSource(source), addition.GetProperty("requestedItemId").GetString());
        Assert.Equal("waitingForApproval", addition.GetProperty("state").GetString());
        using var changed = await client.PostAsJsonAsync("/allstarr/v2/library/additions", new { itemId = source, unit = "album" });
        await AssertClientApiError(changed, 409, "conflict");
        using var list = await client.GetAsync("/allstarr/v2/library/additions");
        Assert.Equal(id, Assert.Single((await list.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray()).GetProperty("id").GetGuid());
        await AssertV2LibraryConditionalAsync(client, "/allstarr/v2/library/additions", list);
        await AssertV2LibraryBatchAsync(client, "/allstarr/v2/library/additions", list);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "other-token");
        using var other = await client.GetAsync("/allstarr/v2/library/additions");
        Assert.Empty((await other.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray());
        using var denied = await client.DeleteAsync($"/allstarr/v2/library/additions/{id}");
        await AssertClientApiError(denied, 404, "not_found");
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        using var cancel = await client.DeleteAsync($"/allstarr/v2/library/additions/{id}");
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        Assert.Equal("cancelled", (await cancel.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());
    }

    [Fact]
    public async Task ClientApiV2_ReportsCreateReceiptsAndHideOtherListenersPlayback()
    {
        using var factory = ClientPlaybackFactory();
        using var client = CreateClientApiClient(factory);
        var id = await SeedClientPlaybackAsync(factory, "valid");
        foreach (var user in new[] { "listener", "other" })
        {
            using var request = ClientPlaybackRequest("/allstarr/v2/reports", user);
            request.Method = HttpMethod.Post;
            request.Content = JsonContent.Create(new { kind = "wont-play", note = "Fixture report", itemId = V2Ids.TrackSource("ext-deezer-song-42"), playbackRequestId = id });
            using var response = await client.SendAsync(request);
            if (user == "other") await AssertClientApiError(response, 404, "not_found");
            else
            {
                Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                var receipt = await response.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Single(receipt.EnumerateObject());
                Assert.NotEqual(Guid.Empty, receipt.GetProperty("id").GetGuid());
            }
        }
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync();
        Assert.Equal("playback", (await db.ProblemReports.SingleAsync()).Kind);
    }

    [Theory]
    [InlineData("GET", "/allstarr/v2/mixes?timeZoneId=not-a-zone", null)]
    [InlineData("GET", "/allstarr/v2/mixes/mix_!/tracks", null)]
    [InlineData("GET", "/allstarr/v2/mixes/mix_!/cover", null)]
    [InlineData("GET", "/allstarr/v2/releases/not%20valid/cover", null)]
    [InlineData("DELETE", "/allstarr/v2/library/additions/not-a-guid", null)]
    [InlineData("PUT", "/allstarr/v2/artists/art_!/follow", null)]
    [InlineData("DELETE", "/allstarr/v2/artists/alb_YWJj/follow", null)]
    [InlineData("POST", "/allstarr/v2/library/additions", "{\"itemId\":\"trk_!\",\"unit\":\"song\"}")]
    [InlineData("POST", "/allstarr/v2/library/additions", "{\"itemId\":\"native1\",\"unit\":\"playlist\"}")]
    [InlineData("POST", "/allstarr/v2/reports", "{\"kind\":\"unknown\",\"note\":\"Fixture\"}")]
    [InlineData("POST", "/allstarr/v2/reports", "{\"kind\":\"other\",\"note\":\"Fixture\",\"itemId\":\"trk_!\"}")]
    [InlineData("POST", "/allstarr/v2/reports", "{\"kind\":\"other\",\"note\":\"Fixture\",\"playbackRequestId\":\"invalid\"}")]
    [InlineData("POST", "/allstarr/v2/reports", "{\"kind\":\"other\",\"note\":\"Fixture\",\"subjectUserId\":\"other\"}")]
    public async Task ClientApiV2_LibraryResourcesRejectInvalidInput(string method, string path, string? body)
    {
        using var factory = ClientMixFactory(new());
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        using var request = new HttpRequestMessage(new(method), path);
        if (body != null) request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request);
        await AssertClientApiError(response, 400, "invalid_request");
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("long")]
    public async Task ClientApiV2_AdditionsRejectInvalidRetryKeys(string key)
    {
        using var factory = ClientMeFactory();
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/allstarr/v2/library/additions");
        request.Headers.TryAddWithoutValidation("Idempotency-Key", key == "long" ? new string('x', 301) : key);
        request.Content = JsonContent.Create(new { itemId = "ext-deezer-song-known", unit = "song" });
        using var response = await client.SendAsync(request);
        await AssertClientApiError(response, 400, "invalid_request");
    }

    [Fact]
    public async Task ClientApiV2_AdditionsRejectAnEmptyRetryHeader()
    {
        using var factory = ClientMeFactory();
        using var client = CreateClientApiClient(factory);
        using var body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("""{"itemId":"ext-deezer-song-known","unit":"song"}"""));
        var response = await factory.Server.SendAsync(http =>
        {
            http.Connection.LocalPort = 8080;
            http.Request.Method = HttpMethods.Post;
            http.Request.Scheme = "http";
            http.Request.Host = new("localhost");
            http.Request.Path = "/allstarr/v2/library/additions";
            http.Request.Headers.Authorization = "Bearer listener-token";
            http.Request.Headers["Idempotency-Key"] = "";
            http.Request.ContentType = "application/json";
            http.Request.ContentLength = body.Length;
            http.Request.Body = body;
        });
        Assert.True(response.Request.Headers.ContainsKey("Idempotency-Key"));
        Assert.Equal(400, response.Response.StatusCode);
    }

    [Theory]
    [InlineData("GET", "mixes")]
    [InlineData("GET", "mixes/mix_11111111111141118111111111111111/tracks")]
    [InlineData("GET", "mixes/mix_11111111111141118111111111111111/cover")]
    [InlineData("GET", "releases")]
    [InlineData("GET", "releases/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/cover")]
    [InlineData("GET", "library/additions")]
    [InlineData("POST", "library/additions")]
    [InlineData("DELETE", "library/additions/11111111-1111-4111-8111-111111111111")]
    [InlineData("PUT", "artists/native1/follow")]
    [InlineData("DELETE", "artists/native1/follow")]
    [InlineData("POST", "reports")]
    [InlineData("GET", "models/karaoke")]
    [InlineData("GET", "models/karaoke/model-1/file")]
    [InlineData("GET", "providers/deezer/icon")]
    public async Task ClientApiV2_LibraryResourcesRequireHeaderAuthenticationAndMusicPort(string method, string resource)
    {
        using var factory = ClientApiFactory(_ => Json(200, """{"Id":"listener"}"""));
        using var client = CreateClientApiClient(factory);
        foreach (var path in new[] { "/allstarr/v2/" + resource, "/allstarr/v2/" + resource + "?api_key=listener-token" })
        {
            using var request = new HttpRequestMessage(new(method), path);
            using var denied = await client.SendAsync(request);
            await AssertClientApiError(denied, 401, "unauthorized");
        }
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        using var dashboard = new HttpRequestMessage(new(method), "http://localhost:5275/allstarr/v2/" + resource);
        using var hidden = await client.SendAsync(dashboard);
        await AssertClientApiError(hidden, 404, "not_found");
    }

    [Theory]
    [InlineData("mixes?timeZoneId=not-a-zone", 400)]
    [InlineData("mixes?timeZoneId=UTC&timeZoneId=UTC", 400)]
    [InlineData("mixes?include=tracks", 400)]
    [InlineData("mixes/mix_!/tracks", 400)]
    [InlineData("mixes/mix_11111111111141118111111111111111/tracks?views=lyrics", 400)]
    [InlineData("releases?include=tracks", 400)]
    [InlineData("library/additions?cursor=bad", 400)]
    [InlineData("models/karaoke?version=bad", 400)]
    [InlineData("mixes/mix_11111111111141118111111111111111/cover", 404)]
    [InlineData("releases/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/cover", 404)]
    [InlineData("providers/deezer/icon", 404)]
    [InlineData("models/karaoke/model-1/file", 404)]
    public async Task ClientApiV2_LibraryBatchRejectsBadQueriesAndFileReads(string resource, int status)
    {
        using var factory = ClientMixFactory(new());
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        using var response = await client.PostAsJsonAsync("/allstarr/v2/batch", new { requests = new[] { new { id = "read", path = "/allstarr/v2/" + resource } } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(status, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("responses").GetProperty("read").GetProperty("status").GetInt32());
    }

    private static async Task AssertV2LibraryConditionalAsync(HttpClient client, string path, HttpResponseMessage original)
    {
        Assert.NotNull(original.Headers.ETag);
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.IfNoneMatch.Add(original.Headers.ETag!);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    private static async Task AssertV2LibraryBatchAsync(HttpClient client, string path, HttpResponseMessage original)
    {
        using var response = await client.PostAsJsonAsync("/allstarr/v2/batch", new { requests = new object[] { new { id = "read", path }, new { id = "cached", path, ifNoneMatch = original.Headers.ETag!.Tag } } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["responses"]!;
        Assert.Equal(200, body["read"]!["status"]!.GetValue<int>());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(await original.Content.ReadAsStringAsync()), body["read"]!["body"]));
        Assert.Equal(304, body["cached"]!["status"]!.GetValue<int>());
        Assert.Null(body["cached"]!["body"]);
    }
}
