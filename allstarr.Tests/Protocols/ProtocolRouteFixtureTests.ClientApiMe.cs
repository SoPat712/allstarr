using System.Net;
using System.Text.Json;
using allstarr.Core.AudioMuse;
using allstarr.Core.Identity;
using allstarr.Core.Intelligence;
using allstarr.Core.Music;
using allstarr.Core.Playback;
using allstarr.Core.Protocols;
using allstarr.Core.Secrets;
using allstarr.Core.Storage;
using allstarr.Models.Search;
using allstarr.Services.Common;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Theory]
    [InlineData(19, false)]
    [InlineData(20, false)]
    [InlineData(20, true)]
    public async Task ClientApi_MeSoundUsesOnlyTheListenersFinishedSongsAndExistingMinimum(int finished, bool administrator)
    {
        var access = new Mock<IBackendLibraryAccessResolver>();
        access.Setup(service => service.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BackendLibraryAccess(true, ["music"]));
        using var factory = ClientApiFactory(request =>
        {
            var id = QueryHelpers.ParseQuery(request.RequestUri!.Query).GetValueOrDefault("id").ToString();
            var sample = id.Contains("sample", StringComparison.Ordinal);
            return request.RequestUri.AbsolutePath switch
            {
                "/Users/Me" => Json(200, request.Headers.ToString().Contains("other-token", StringComparison.Ordinal)
                    ? """{"Id":"other"}""" : JsonSerializer.Serialize(new { Id = "listener", Policy = new { IsAdministrator = administrator } })),
                "/api/health" => Json(200, """{"status":"ok"}"""),
                "/api/version" => Json(200, """{"app_version":"v3.6.4"}"""),
                "/api/servers" => Json(200, """{"servers":[{"server_id":"fixture-server","server_type":"jellyfin","creds":{"url":"http://backend.example.test"}}]}"""),
                "/api/models" => Json(200, """{"server_id":"fixture-server","models":{"musicnn":{"enabled":true,"local":{"count":80,"total":80}}}}"""),
                "/external/get_embedding" => Json(200, JsonSerializer.Serialize(new { item_id = id, embedding = Enumerable.Repeat(.1, 200).ToArray() })),
                "/external/get_score" => Json(200, JsonSerializer.Serialize(new
                {
                    item_id = id,
                    energy = sample ? .3 + int.Parse(id.Split('-').Last()) * .01 : id.StartsWith("own", StringComparison.Ordinal) ? .8 : .1,
                    tempo = sample ? 90 : id.StartsWith("own", StringComparison.Ordinal) ? 120 : 160
                })),
                _ => throw new InvalidOperationException("Unexpected upstream request")
            };
        }, services =>
        {
            services.RemoveAll<IBackendLibraryAccessResolver>();
            services.AddSingleton(access.Object);
        });
        using var client = CreateClientApiClient(factory);
        var (owner, other) = await SeedClientMeAsync(factory, administrator);
        await factory.Services.GetRequiredService<BackendIdentityResolver>().ResolveAsync(new("Jellyfin", "other", IsAdministrator: true));
        await factory.Services.GetRequiredService<FileSecretKeyRingProvider>().CreateIfMissingAsync(false);
        await factory.Services.GetRequiredService<AudioMuseSettings>().ApplyAsync(new AdminAuthSession
        {
            SessionId = "fixture",
            UserId = "other",
            UserName = "Other",
            IsAdministrator = true,
            AllstarrUserId = other,
            BackendType = "Jellyfin",
            BackendInstanceId = "primary",
            JellyfinAccessToken = "fixture",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        }, "http://analysis.example.test", "fixture-key", null, default);
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync())
        {
            foreach (var listener in new[] { (Owner: owner, Prefix: "own", Finished: finished), (Owner: other, Prefix: "other", Finished: 20) })
                for (var index = 0; index < listener.Finished + 20; index++)
                {
                    var track = new LibraryTrackRecord
                    {
                        Id = Guid.NewGuid(),
                        OwnerUserId = listener.Owner,
                        Protocol = "jellyfin",
                        BackendInstanceId = "primary",
                        BackendLibraryId = "music",
                        BackendItemId = $"{listener.Prefix}-{(index < listener.Finished ? "done" : "sample")}-{index}",
                        Title = "Song",
                        Artist = listener.Prefix
                    };
                    db.LibraryTracks.Add(track);
                    if (index < listener.Finished) db.ListeningEvents.Add(new()
                    {
                        Id = Guid.NewGuid(),
                        OwnerUserId = listener.Owner,
                        Protocol = "jellyfin",
                        BackendInstanceId = "primary",
                        OccurrenceKey = track.BackendItemId,
                        TrackReference = track.BackendItemId,
                        LibraryTrackId = track.Id,
                        State = ListeningEventState.Completed,
                        ListenedAt = DateTimeOffset.UtcNow,
                        UpdatedAt = DateTimeOffset.UtcNow,
                        SourceKind = "protocol"
                    });
                }
            await db.SaveChangesAsync();
        }
        foreach (var listener in new[] { (Token: "listener-token", Tempo: 120), (Token: "other-token", Tempo: 160) })
        {
            client.DefaultRequestHeaders.Remove("X-Emby-Token");
            client.DefaultRequestHeaders.Add("X-Emby-Token", listener.Token);
            using var response = await client.GetAsync("/allstarr/v1/me/sound");
            var expected = listener.Token == "listener-token" && finished == 19 ? HttpStatusCode.NotFound : HttpStatusCode.OK;
            Assert.True(response.StatusCode == expected,
                $"Expected {expected}, received {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
            Assert.True(response.Headers.CacheControl?.NoStore);
            if (listener.Token == "listener-token" && finished == 19)
            {
                await AssertClientApiError(response, 404, "not_found");
                continue;
            }
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = body.RootElement;
            Assert.Equal(["at", "librarySongs", "libraryTempo", "skippedSongs", "songs", "tempo", "traits"],
                root.EnumerateObject().Select(item => item.Name).Order().ToArray());
            Assert.Equal(20, root.GetProperty("songs").GetInt32());
            Assert.Equal(0, root.GetProperty("skippedSongs").GetInt32());
            Assert.Equal(20, root.GetProperty("librarySongs").GetInt32());
            Assert.Equal(listener.Tempo, root.GetProperty("tempo").GetInt32());
            Assert.Equal(90, root.GetProperty("libraryTempo").GetInt32());
            var trait = Assert.Single(root.GetProperty("traits").EnumerateArray());
            Assert.Equal("Energetic", trait.GetProperty("name").GetString());
            Assert.Equal(listener.Tempo == 120, trait.GetProperty("difference").GetInt32() > 0);
            Assert.Equal(JsonValueKind.Null, trait.GetProperty("lean").ValueKind);
            Assert.NotEqual(default, root.GetProperty("at").GetDateTimeOffset());
        }
    }

    [Fact]
    public async Task ClientApi_MeCapabilitiesReflectRegisteredServices()
    {
        using var factory = ClientMeFactory(configure: services =>
        {
            services.RemoveAll<ListeningStatsService>();
            services.RemoveAll<TasteService>();
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        using var response = await client.GetAsync("/allstarr/v1/capabilities");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(body.RootElement.GetProperty("features").GetProperty("stats").GetBoolean());
        Assert.False(body.RootElement.GetProperty("features").GetProperty("taste").GetBoolean());
        Assert.Equal("jellyfin", body.RootElement.GetProperty("library").GetProperty("id").GetString());
    }

    [Theory]
    [InlineData("stats")]
    [InlineData("stats/art?artist=Artist")]
    [InlineData("taste")]
    [InlineData("sound")]
    [InlineData("history")]
    [InlineData("plays")]
    [InlineData("plays/00000000-0000-0000-0000-000000000001")]
    [InlineData("history/imports")]
    [InlineData("history/imports/00000000-0000-0000-0000-000000000001")]
    [InlineData("history/imports/recheck")]
    public async Task ClientApi_MeRequiresAListenerAndTheMusicPort(string path)
    {
        using var factory = ClientMeFactory();
        using var client = CreateClientApiClient(factory);
        using var missingToken = await client.GetAsync("/allstarr/v1/me/" + path);
        await AssertClientApiError(missingToken, 401, "unauthorized");
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        using var dashboardPort = await client.GetAsync("http://localhost:5275/allstarr/v1/me/" + path);
        await AssertClientApiError(dashboardPort, 404, "not_found");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClientApi_MeStatsAndTasteArePersonalForEveryListener(bool administrator)
    {
        using var factory = ClientMeFactory(administrator);
        using var client = CreateClientApiClient(factory);
        var (owner, other) = await SeedClientMeAsync(factory, administrator);
        var taste = factory.Services.GetRequiredService<TasteService>();
        await taste.PutNudgeAsync(owner, new("artist", "Own artist", "more"));
        await taste.PutNudgeAsync(other, new("artist", "Other artist", "never"));
        await taste.SetUseImportedHistoryAsync(owner, false);

        foreach (var listener in new[] { (Token: "listener-token", Artist: "Own artist"), (Token: "other-token", Artist: "Other artist") })
        {
            client.DefaultRequestHeaders.Remove("X-Emby-Token");
            client.DefaultRequestHeaders.Add("X-Emby-Token", listener.Token);
            using var stats = await client.GetAsync("/allstarr/v1/me/stats?year=2025&timeZoneId=America/New_York");
            Assert.Equal(HttpStatusCode.OK, stats.StatusCode);
            Assert.True(stats.Headers.CacheControl?.NoStore);
            using var body = JsonDocument.Parse(await stats.Content.ReadAsStringAsync());
            var root = body.RootElement;
            Assert.Equal(1, root.GetProperty("playCount").GetInt64());
            Assert.Equal("America/New_York", root.GetProperty("period").GetProperty("timeZoneId").GetString());
            var song = Assert.Single(root.GetProperty("topSongs").EnumerateArray());
            Assert.Equal("Song", song.GetProperty("title").GetString());
            Assert.Equal(listener.Artist, song.GetProperty("artist").GetString());
            Assert.Equal("Album", song.GetProperty("album").GetString());
            var artist = Assert.Single(root.GetProperty("topArtists").EnumerateArray());
            Assert.Equal(listener.Artist, artist.GetProperty("name").GetString());
            var album = Assert.Single(root.GetProperty("topAlbums").EnumerateArray());
            Assert.Equal("Album", album.GetProperty("title").GetString());
            Assert.Equal(listener.Artist, album.GetProperty("artist").GetString());
            foreach (var item in new[] { song, artist, album, Assert.Single(root.GetProperty("newArtists").EnumerateArray()) })
            {
                Assert.Equal(1, item.GetProperty("playCount").GetInt64());
                Assert.StartsWith("/allstarr/v1/me/stats/art?artist=", item.GetProperty("artworkUrl").GetString());
            }
            Assert.DoesNotContain(listener.Artist == "Own artist" ? "Other artist" : "Own artist", root.GetRawText());

            using var response = await client.GetAsync("/allstarr/v1/me/taste");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            using var profile = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var tasteRoot = profile.RootElement;
            Assert.Equal(["lately", "latelyWeight", "nudges", "paused", "useImportedHistory", "usually"],
                tasteRoot.EnumerateObject().Select(item => item.Name).Order().ToArray());
            var nudge = Assert.Single(tasteRoot.GetProperty("nudges").EnumerateArray());
            Assert.Equal(["expiresAt", "kind", "mode", "name"], nudge.EnumerateObject().Select(item => item.Name).Order().ToArray());
            Assert.Equal(listener.Artist, nudge.GetProperty("name").GetString());
            Assert.Equal("artist", nudge.GetProperty("kind").GetString());
            Assert.Equal(listener.Token == "other-token" ? "never" : "more", nudge.GetProperty("mode").GetString());
            Assert.Equal(JsonValueKind.Null, nudge.GetProperty("expiresAt").ValueKind);
            Assert.Equal(listener.Token == "listener-token", tasteRoot.GetProperty("paused").GetBoolean());
            Assert.Equal(listener.Token == "other-token", tasteRoot.GetProperty("useImportedHistory").GetBoolean());
            foreach (var layer in new[] { "lately", "usually" })
            {
                var item = Assert.Single(tasteRoot.GetProperty(layer).GetProperty("artists").EnumerateArray());
                Assert.Equal(["name", "strength"], item.EnumerateObject().Select(value => value.Name).Order().ToArray());
                Assert.Equal(listener.Artist, item.GetProperty("name").GetString());
                Assert.InRange(item.GetProperty("strength").GetInt32(), 0, 100);
                var tag = Assert.Single(tasteRoot.GetProperty(layer).GetProperty("tags").EnumerateArray());
                Assert.Equal(["name", "strength"], tag.EnumerateObject().Select(value => value.Name).Order().ToArray());
                Assert.Equal(listener.Token == "listener-token" ? "Rock" : "Jazz", tag.GetProperty("name").GetString());
                Assert.InRange(tag.GetProperty("strength").GetInt32(), 0, 100);
            }
            Assert.DoesNotContain(listener.Artist == "Own artist" ? "Other artist" : "Own artist", tasteRoot.GetRawText());
        }
    }

    [Theory]
    [InlineData("period=bad")]
    [InlineData("period=7d&year=2025")]
    [InlineData("year=0")]
    [InlineData("year=9999")]
    [InlineData("timeZoneId=Nowhere/Invalid")]
    public async Task ClientApi_MeStatsRejectsTheDashboardInvalidPeriods(string query)
    {
        using var factory = ClientMeFactory();
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        using var response = await client.GetAsync("/allstarr/v1/me/stats?" + query);
        await AssertClientApiError(response, 400, "invalid_request");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Use period=7d, 30d, year or all, or year=1 through 9998.", body.RootElement.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("period=7d")]
    [InlineData("period=30d")]
    [InlineData("period=year")]
    [InlineData("period=all")]
    public async Task ClientApi_MeStatsAcceptsDashboardPeriodsAndEmptyHistory(string query)
    {
        using var factory = ClientMeFactory();
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        using var response = await client.GetAsync("/allstarr/v1/me/stats?" + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, body.RootElement.GetProperty("playCount").GetInt64());
        Assert.Empty(body.RootElement.GetProperty("topSongs").EnumerateArray());
    }

    [Theory]
    [InlineData("title=Song", 8, 0, 0)]
    [InlineData("album=Album", 0, 8, 0)]
    [InlineData("", 0, 0, 0)]
    public async Task ClientApi_MeStatsArtworkUsesThePersonalScopeAndMusicOrigin(string query, int songs, int albums, int artists)
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        var scopes = new List<ProtocolExecutionContext>();
        gateway.Setup(service => service.SearchAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), songs, albums, artists, It.IsAny<string?>()))
            .Callback<ProtocolExecutionContext, string, int, int, int, string?>((context, _, _, _, _, _) => scopes.Add(context))
            .ReturnsAsync(new SearchResult());
        var media = new Mock<IMediaAssetResolver>(MockBehavior.Strict);
        var owners = new List<Guid?>();
        media.Setup(service => service.ResolveAsync(It.IsAny<MediaAssetIdentity>(), It.IsAny<Func<CancellationToken, Task<MediaAssetSource?>>>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(async (MediaAssetIdentity identity, Func<CancellationToken, Task<MediaAssetSource?>> fetch, int _, CancellationToken token) =>
            {
                owners.Add(identity.UserId);
                Assert.Null(await fetch(token));
                return new ResolvedMediaAsset(new byte[1024], "image/png", "fixture", null, null, false);
            });
        using var factory = ClientMeFactory(true, services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
            services.RemoveAll<IMediaAssetResolver>();
            services.AddSingleton(media.Object);
        });
        using var client = CreateClientApiClient(factory);
        var (owner, other) = await SeedClientMeAsync(factory, true);
        foreach (var listener in new[] { (Token: "listener-token", Owner: owner), (Token: "other-token", Owner: other) })
        {
            client.DefaultRequestHeaders.Remove("X-Emby-Token");
            client.DefaultRequestHeaders.Add("X-Emby-Token", listener.Token);
            using var response = await client.GetAsync("http://localhost:8080/allstarr/v1/me/stats/art?artist=Own%20artist&" + query);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
            Assert.True(response.Headers.CacheControl?.Private);
            Assert.Equal(listener.Owner, owners.Last());
            Assert.All(scopes, context => Assert.False(context.Principal!.IsAdministrator));
            if (query.Length > 0) Assert.Equal(listener.Owner, scopes.Last().Principal!.UserId);
            else Assert.Empty(scopes);
        }
        using var dashboardPort = await client.GetAsync("http://localhost:5275/allstarr/v1/me/stats/art?artist=Own%20artist");
        await AssertClientApiError(dashboardPort, 404, "not_found");
    }

    [Fact]
    public async Task ClientApi_MeStatsArtworkMirrorsMissingArtworkAndNameValidation()
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(service => service.SearchAsync(It.IsAny<ProtocolExecutionContext>(), "Artist", 0, 0, 5, It.IsAny<string?>()))
            .ReturnsAsync(new SearchResult());
        using var factory = ClientMeFactory(configure: services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        using var missing = await client.GetAsync("/allstarr/v1/me/stats/art?artist=Artist");
        Assert.Equal(HttpStatusCode.NoContent, missing.StatusCode);
        Assert.True(missing.Headers.CacheControl?.Private);
        Assert.Equal(TimeSpan.FromHours(1), missing.Headers.CacheControl?.MaxAge);
        using var absentName = await client.GetAsync("/allstarr/v1/me/stats/art");
        await AssertClientApiError(absentName, 400, "invalid_request");
        using var longName = await client.GetAsync("/allstarr/v1/me/stats/art?artist=" + new string('x', 501));
        await AssertClientApiError(longName, 400, "invalid_request");
    }

    private static ProtocolFactory ClientMeFactory(bool administrator = false, Action<IServiceCollection>? configure = null) =>
        ClientApiFactory(request => request.RequestUri!.AbsolutePath == "/Users/Me"
            ? Json(200, request.Headers.ToString().Contains("other-token", StringComparison.Ordinal)
                ? """{"Id":"other"}"""
                : JsonSerializer.Serialize(new { Id = "listener", Policy = new { IsAdministrator = administrator } }))
            : throw new InvalidOperationException("Unexpected upstream request"), configure);

    private static async Task<(Guid Owner, Guid Other)> SeedClientMeAsync(ProtocolFactory factory, bool administrator)
    {
        var resolver = factory.Services.GetRequiredService<BackendIdentityResolver>();
        var owner = (await resolver.ResolveAsync(new("Jellyfin", "listener", IsAdministrator: administrator)))!.UserId;
        var other = (await resolver.ResolveAsync(new("Jellyfin", "other")))!.UserId;
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync();
        foreach (var listener in new[] { (Id: owner, Artist: "Own artist"), (Id: other, Artist: "Other artist") })
        {
            var at = DateTimeOffset.Parse("2025-06-01T12:00:00Z");
            db.ListeningEvents.Add(new()
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = listener.Id,
                Protocol = "jellyfin",
                BackendInstanceId = "primary",
                OccurrenceKey = Guid.NewGuid().ToString("N"),
                State = ListeningEventState.Completed,
                ListenedAt = at,
                UpdatedAt = at,
                SourceKind = "protocol",
                TrackReference = listener.Artist,
                Title = "Song",
                Artist = listener.Artist,
                Album = "Album",
                DurationMilliseconds = 120000
            });
            db.ListeningEvents.Add(new()
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = listener.Id,
                Protocol = "jellyfin",
                BackendInstanceId = "primary",
                OccurrenceKey = Guid.NewGuid().ToString("N"),
                State = ListeningEventState.Completed,
                ListenedAt = at.AddYears(-1),
                UpdatedAt = at.AddYears(-1),
                SourceKind = "protocol",
                TrackReference = "earlier",
                Title = "Earlier song",
                Artist = "Earlier artist"
            });
            db.TasteProfiles.Add(new()
            {
                OwnerUserId = listener.Id,
                PayloadJson = JsonSerializer.Serialize(new TasteDocument
                {
                    Paused = listener.Id == owner,
                    Learned = new()
                    {
                        ["artist:" + listener.Artist.ToLowerInvariant()] = new(listener.Artist, 3, 3, DateTimeOffset.UtcNow),
                        [listener.Id == owner ? "tag:rock" : "tag:jazz"] = new(listener.Id == owner ? "Rock" : "Jazz", 3, 3, DateTimeOffset.UtcNow)
                    }
                })
            });
        }
        await db.SaveChangesAsync();
        return (owner, other);
    }
}
