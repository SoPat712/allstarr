using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using allstarr.Core.Artwork;
using allstarr.Core.Capabilities;
using allstarr.Core.ClientApiV2;
using allstarr.Core.Identity;
using allstarr.Core.Intelligence;
using allstarr.Core.Music;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Theory]
    [InlineData("/allstarr/v1/events")]
    [InlineData("/allstarr/v1/me/plays")]
    [InlineData("/allstarr/v1/me/plays/00000000-0000-0000-0000-000000000001")]
    [InlineData("/allstarr/v1/me/history/imports/00000000-0000-0000-0000-000000000001")]
    public async Task ClientApiV2_RetiredListenerRoutesAreAbsent(string path)
    {
        using var factory = ClientApiFactory(_ => throw new InvalidOperationException("Unexpected upstream request"));
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        using var response = await client.GetAsync(path);
        await AssertClientApiError(response, 404, "not_found");
    }

    [Theory]
    [InlineData("history")]
    [InlineData("history/imports")]
    [InlineData("history/imports/recheck")]
    [InlineData("stats")]
    [InlineData("stats/art?artist=Artist")]
    [InlineData("sound")]
    [InlineData("taste")]
    [InlineData("preferences")]
    public async Task ClientApiV2_ListenerReadsRequireHeaderAuthenticationAndTheMusicPort(string resource)
    {
        using var factory = ClientMeFactory();
        using var client = CreateClientApiClient(factory);
        var path = "/allstarr/v2/me/" + resource;
        using var missing = await client.GetAsync(path);
        await AssertClientApiError(missing, 401, "unauthorized");
        using var query = await client.GetAsync(path + (path.Contains('?') ? "&" : "?") + "api_key=listener-token");
        await AssertClientApiError(query, 401, "unauthorized");
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        using var dashboard = await client.GetAsync("http://localhost:5275" + path);
        await AssertClientApiError(dashboard, 404, "not_found");
    }

    [Fact]
    public async Task ClientApiV2_HistoryKeepsRecordedFactsAndUsesNativeTracksWithPaging()
    {
        using var factory = ClientDetailsFactory(request => request.RequestUri!.AbsolutePath == "/Users/Me"
            ? Json(200, request.Headers.ToString().Contains("other-token", StringComparison.Ordinal)
                ? """{"Id":"other"}""" : """{"Id":"listener"}""")
            : Json(200, V2LibrarySong));
        using var client = CreateClientApiClient(factory);
        var resolver = factory.Services.GetRequiredService<BackendIdentityResolver>();
        var owner = (await resolver.ResolveAsync(new("Jellyfin", "listener")))!.UserId;
        var other = (await resolver.ResolveAsync(new("Jellyfin", "other")))!.UserId;
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync())
        {
            foreach (var entry in new[] { (Owner: owner, Title: "Recorded title", Reference: "native1"),
                (Owner: owner, Title: "Unresolved import", Reference: "archive:missing"),
                (Owner: other, Title: "Private other play", Reference: "native1") })
                db.ListeningEvents.Add(new()
                {
                    Id = Guid.CreateVersion7(),
                    OwnerUserId = entry.Owner,
                    Protocol = "jellyfin",
                    BackendInstanceId = "primary",
                    OccurrenceKey = Guid.NewGuid().ToString("N"),
                    State = ListeningEventState.Completed,
                    ListenedAt = DateTimeOffset.Parse("2026-01-02T03:04:05Z"),
                    UpdatedAt = DateTimeOffset.UtcNow,
                    SourceKind = entry.Reference == "native1" ? "protocol" : "import",
                    TrackReference = entry.Reference,
                    Title = entry.Title,
                    Artist = "Recorded artist",
                    Album = "Recorded album",
                    DurationMilliseconds = 120000
                });
            await db.SaveChangesAsync();
        }
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        var plays = new List<JsonElement>();
        string? before = null;
        do
        {
            using var response = await client.GetAsync("/allstarr/v2/me/history?limit=1" + (before == null ? "" : "&before=" + Uri.EscapeDataString(before)));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            Assert.NotNull(response.Headers.ETag);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            plays.AddRange(body.RootElement.GetProperty("plays").EnumerateArray().Select(item => item.Clone()));
            before = body.RootElement.GetProperty("next").GetString();
            Assert.True(plays.Count <= 2);
        } while (before != null);
        Assert.Equal(2, plays.Count);
        var resolved = plays.Single(item => item.GetProperty("title").GetString() == "Recorded title");
        Assert.Equal(V2Ids.TrackSource("native1"), resolved.GetProperty("track").GetProperty("id").GetString());
        Assert.Equal("Library song", resolved.GetProperty("track").GetProperty("title").GetString());
        Assert.False(resolved.GetProperty("track").TryGetProperty("Name", out _));
        Assert.Equal(120000, resolved.GetProperty("durationMs").GetInt64());
        Assert.Equal(JsonValueKind.Null, plays.Single(item => item.GetProperty("title").GetString() == "Unresolved import").GetProperty("track").ValueKind);
        Assert.All(plays, play => Assert.StartsWith("/allstarr/v2/me/stats/art?", play.GetProperty("artwork").GetProperty("url").GetString()));
        using var stats = await client.GetAsync("/allstarr/v2/me/stats?period=all");
        Assert.Equal(HttpStatusCode.OK, stats.StatusCode);
        using var statsBody = JsonDocument.Parse(await stats.Content.ReadAsStringAsync());
        var song = statsBody.RootElement.GetProperty("topSongs").EnumerateArray().Single(item => item.GetProperty("title").GetString() == "Recorded title");
        Assert.Equal(V2Ids.TrackSource("native1"), song.GetProperty("track").GetProperty("id").GetString());
        client.DefaultRequestHeaders.Authorization = new("Bearer", "other-token");
        using var otherResponse = await client.GetAsync("/allstarr/v2/me/history");
        var otherBody = await otherResponse.Content.ReadAsStringAsync();
        Assert.Contains("Private other play", otherBody);
        Assert.DoesNotContain("Unresolved import", otherBody);
        Assert.DoesNotContain("Recorded title", otherBody);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClientApiV2_StatsAndTasteKeepListenerScopeAndEmbedArtwork(bool administrator)
    {
        using var factory = ClientMeFactory(administrator);
        using var client = CreateClientApiClient(factory);
        var (owner, other) = await SeedClientMeAsync(factory, administrator);
        var taste = factory.Services.GetRequiredService<TasteService>();
        await taste.PutNudgeAsync(owner, new("artist", "Own artist", "more"));
        await taste.PutNudgeAsync(other, new("artist", "Other artist", "never"));
        foreach (var listener in new[] { (Token: "listener-token", Artist: "Own artist"), (Token: "other-token", Artist: "Other artist") })
        {
            client.DefaultRequestHeaders.Authorization = new("Bearer", listener.Token);
            using var stats = await client.GetAsync("/allstarr/v2/me/stats?year=2025&timeZoneId=America/New_York");
            Assert.Equal(HttpStatusCode.OK, stats.StatusCode);
            using var body = JsonDocument.Parse(await stats.Content.ReadAsStringAsync());
            Assert.Equal(1, body.RootElement.GetProperty("playCount").GetInt64());
            Assert.Equal("America/New_York", body.RootElement.GetProperty("period").GetProperty("timeZoneId").GetString());
            var song = Assert.Single(body.RootElement.GetProperty("topSongs").EnumerateArray());
            Assert.Equal(listener.Artist, song.GetProperty("artist").GetString());
            Assert.Equal(JsonValueKind.Null, song.GetProperty("track").ValueKind);
            Assert.StartsWith("/allstarr/v2/me/stats/art?", song.GetProperty("artwork").GetProperty("url").GetString());
            Assert.DoesNotContain("/allstarr/v1/", body.RootElement.GetRawText());
            Assert.DoesNotContain(listener.Artist == "Own artist" ? "Other artist" : "Own artist", body.RootElement.GetRawText());
            using var profile = await client.GetAsync("/allstarr/v2/me/taste");
            Assert.Equal(HttpStatusCode.OK, profile.StatusCode);
            using var profileBody = JsonDocument.Parse(await profile.Content.ReadAsStringAsync());
            Assert.Equal(listener.Artist, Assert.Single(profileBody.RootElement.GetProperty("nudges").EnumerateArray()).GetProperty("name").GetString());
            using var unchanged = new HttpRequestMessage(HttpMethod.Get, "/allstarr/v2/me/taste");
            unchanged.Headers.IfNoneMatch.Add(profile.Headers.ETag!);
            using var conditional = await client.SendAsync(unchanged);
            Assert.Equal(HttpStatusCode.NotModified, conditional.StatusCode);
        }
    }

    [Fact]
    public async Task ClientApiV2_ImportsRecheckAndBatchArePersonalReads()
    {
        var access = new Mock<IBackendLibraryAccessResolver>();
        access.Setup(service => service.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BackendLibraryAccess(true, []));
        using var factory = ClientDetailsFactory(request => request.RequestUri!.AbsolutePath == "/Users/Me"
            ? Json(200, request.Headers.ToString().Contains("other-token", StringComparison.Ordinal)
                ? """{"Id":"other"}""" : """{"Id":"listener"}""")
            : Json(200, """{"Items":[]}"""), services =>
        {
            services.RemoveAll<IBackendLibraryAccessResolver>();
            services.AddSingleton(access.Object);
        });
        using var client = CreateClientApiClient(factory);
        var (owner, other) = await SeedClientMeAsync(factory, false);
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync())
        {
            foreach (var listener in new[] { (Owner: owner, File: "Own.json"), (Owner: other, File: "Other.json") })
                db.ListeningHistoryImports.Add(new()
                {
                    Id = Guid.NewGuid(),
                    OwnerUserId = listener.Owner,
                    Protocol = "jellyfin",
                    BackendInstanceId = "primary",
                    DisplayFileName = listener.File,
                    ContentSha256 = Guid.NewGuid().ToString("N"),
                    SizeBytes = 1,
                    PreviewJson = "{}",
                    PreviewRevision = "fixture",
                    Revision = 1,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    ExpiresAt = DateTimeOffset.UtcNow.AddDays(1)
                });
            await db.SaveChangesAsync();
        }
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        var paths = new[] { "history", "history/imports", "history/imports/recheck", "stats?period=all", "taste", "preferences", "sound" };
        using var batch = await client.PostAsJsonAsync("/allstarr/v2/batch", new
        {
            requests = paths.Select((path, index) => new { id = index.ToString(), path = "/allstarr/v2/me/" + path }).ToArray()
        });
        Assert.Equal(HttpStatusCode.OK, batch.StatusCode);
        using var body = JsonDocument.Parse(await batch.Content.ReadAsStringAsync());
        var results = body.RootElement.GetProperty("responses");
        for (var index = 0; index < paths.Length; index++)
        {
            var part = results.GetProperty(index.ToString());
            Assert.Equal(paths[index] == "sound" ? 404 : 200, part.GetProperty("status").GetInt32());
            if (paths[index] == "sound") continue;
            using var standalone = await client.GetAsync("/allstarr/v2/me/" + paths[index]);
            Assert.Equal(HttpStatusCode.OK, standalone.StatusCode);
            Assert.Equal(standalone.Headers.ETag!.ToString(), part.GetProperty("etag").GetString());
            using var standaloneBody = JsonDocument.Parse(await standalone.Content.ReadAsStringAsync());
            Assert.True(JsonElement.DeepEquals(standaloneBody.RootElement, part.GetProperty("body")));
        }
        Assert.Contains("Own.json", results.GetProperty("1").GetRawText());
        Assert.DoesNotContain("Other.json", body.RootElement.GetRawText());
        using var excluded = await client.PostAsJsonAsync("/allstarr/v2/batch", new
        {
            requests = new[] { new { id = "art", path = "/allstarr/v2/me/stats/art?artist=Artist" } }
        });
        using var excludedBody = JsonDocument.Parse(await excluded.Content.ReadAsStringAsync());
        Assert.Equal(404, excludedBody.RootElement.GetProperty("responses").GetProperty("art").GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task ClientApiV2_HistoryRecheckExamplesContainNativeTracksWithoutChangingMatches()
    {
        var access = new Mock<IBackendLibraryAccessResolver>();
        access.Setup(service => service.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BackendLibraryAccess(true, []));
        access.Setup(service => service.ResolveUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid user, CancellationToken _) => new BackendLibraryAccessContext(new(ProtocolKind.Jellyfin, "primary", "listener",
                new(user, "jellyfin", "primary", "listener", "Listener", false), "fixture", DateTimeOffset.UtcNow.AddMinutes(5), default), new(true, [])));
        var gateway = V2BatchGateway();
        gateway.Setup(service => service.GetStoredSongAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(V2BatchSong("known"));
        gateway.Setup(service => service.GetStreamingProviderAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync("deezer");
        var index = new Mock<ILibraryIndexService>();
        index.Setup(service => service.FindKnownRecordingTracksAsync(It.IsAny<ProtocolExecutionContext>(),
                It.IsAny<IReadOnlyList<LibraryRecordingLookup>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string>());
        using var factory = ClientDetailsFactory(request => request.RequestUri!.AbsolutePath == "/Users/Me"
            ? Json(200, """{"Id":"listener"}""") : Json(200, """{"Items":[]}"""), services =>
        {
            services.RemoveAll<IBackendLibraryAccessResolver>();
            services.AddSingleton(access.Object);
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
            services.RemoveAll<ILibraryIndexService>();
            services.AddSingleton(index.Object);
        });
        using var client = CreateClientApiClient(factory);
        var recording = await SeedClientDetailsAsync(factory, "listener", "known", personal: false);
        var owner = (await factory.Services.GetRequiredService<BackendIdentityResolver>().ResolveAsync(new("Jellyfin", "listener")))!.UserId;
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync())
        {
            db.ListeningEvents.Add(new()
            {
                Id = Guid.NewGuid(),
                OwnerUserId = owner,
                Protocol = "jellyfin",
                BackendInstanceId = "primary",
                OccurrenceKey = "doubtful-import",
                CanonicalRecordingId = recording,
                SourceKind = "import",
                Title = "Completely different recording",
                Artist = "Recorded artist",
                State = ListeningEventState.Completed,
                ListenedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        using var response = await client.GetAsync("/allstarr/v2/me/history/imports/recheck");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, body.RootElement.GetProperty("doubtful").GetInt32());
        var example = Assert.Single(body.RootElement.GetProperty("examples").EnumerateArray());
        Assert.Equal(V2Ids.Track(recording), example.GetProperty("recordingId").GetString());
        Assert.Equal(V2Ids.Track(recording), example.GetProperty("track").GetProperty("id").GetString());
        Assert.Equal("Completely different recording", example.GetProperty("title").GetString());
        await using var check = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync();
        Assert.Equal(recording, (await check.ListeningEvents.SingleAsync()).CanonicalRecordingId);
    }

    [Fact]
    public async Task ClientApiV2_PreferencesPatchOnlyChangesTheCurrentListener()
    {
        using var factory = ClientMeFactory();
        using var client = CreateClientApiClient(factory);
        using var anonymous = await client.PatchAsJsonAsync("/allstarr/v2/me/preferences", new { heartAction = "heart" });
        await AssertClientApiError(anonymous, 401, "unauthorized");
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        foreach (var action in new[] { "heart", "keep", "addToLibrary" })
        {
            using var patch = await client.PatchAsJsonAsync("/allstarr/v2/me/preferences", new { heartAction = action });
            Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
            using var response = await client.GetAsync("/allstarr/v2/me/preferences");
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(action, body.RootElement.GetProperty("heartAction").GetString());
        }
        foreach (var request in new object[] { new { heartAction = "delete" }, new { heartAction = "" }, new { unknown = "heart" } })
        {
            using var invalid = await client.PatchAsJsonAsync("/allstarr/v2/me/preferences", request);
            await AssertClientApiError(invalid, 400, "invalid_request");
        }
        client.DefaultRequestHeaders.Authorization = new("Bearer", "other-token");
        using var other = await client.GetAsync("/allstarr/v2/me/preferences");
        using var otherBody = JsonDocument.Parse(await other.Content.ReadAsStringAsync());
        Assert.Equal("keep", otherBody.RootElement.GetProperty("heartAction").GetString());
    }

    [Theory]
    [InlineData("history?limit=0")]
    [InlineData("history?limit=201")]
    [InlineData("history?limit=bad")]
    [InlineData("history?before=invalid")]
    [InlineData("stats?period=bad")]
    [InlineData("stats?period=7d&year=2025")]
    [InlineData("stats?year=0")]
    [InlineData("stats?year=bad")]
    [InlineData("stats?timeZoneId=Nowhere/Invalid")]
    [InlineData("stats/art")]
    [InlineData("stats/art?artist=Artist&libraryArtistId=art_!!")]
    public async Task ClientApiV2_ListenerReadsRejectInvalidParameters(string path)
    {
        using var factory = ClientMeFactory();
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        using var response = await client.GetAsync("/allstarr/v2/me/" + path);
        await AssertClientApiError(response, 400, "invalid_request");
    }

    [Fact]
    public async Task ClientApiV2_StatsArtworkUsesTheListenerAndReturnsImageBytes()
    {
        var owners = new List<Guid?>();
        var media = new Mock<IMediaAssetResolver>();
        media.Setup(service => service.ResolveAsync(It.IsAny<MediaAssetIdentity>(), It.IsAny<Func<CancellationToken, Task<MediaAssetSource?>>>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MediaAssetIdentity identity, Func<CancellationToken, Task<MediaAssetSource?>> _, int _, CancellationToken _) =>
            {
                owners.Add(identity.UserId);
                return new ResolvedMediaAsset(new byte[1024], "image/png", "fixture", null, null, false);
            });
        using var factory = ClientMeFactory(configure: services =>
        {
            services.RemoveAll<IMediaAssetResolver>();
            services.AddSingleton(media.Object);
        });
        using var client = CreateClientApiClient(factory);
        var (owner, _) = await SeedClientMeAsync(factory, false);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        using var response = await client.GetAsync("/allstarr/v2/me/stats/art?artist=Artist&title=Song");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(1024, (await response.Content.ReadAsByteArrayAsync()).Length);
        Assert.Equal(owner, Assert.Single(owners));
    }

    [Fact]
    public async Task ClientApiV2_StatsArtworkHidesAnotherListenersArtist()
    {
        using var factory = ClientApiFactory(request => request.RequestUri!.AbsolutePath == "/Users/Me"
            ? Json(200, """{"Id":"listener"}""") : Json(404, "{}"));
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        using var hidden = await client.GetAsync("/allstarr/v2/me/stats/art?artist=Artist&libraryArtistId=" + V2Ids.Artist("private-artist"));
        await AssertClientApiError(hidden, 404, "not_found");
    }
}
