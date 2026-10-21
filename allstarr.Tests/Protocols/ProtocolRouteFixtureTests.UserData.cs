using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using allstarr.Core.Identity;
using allstarr.Core.Intelligence;
using allstarr.Core.Protocols;
using allstarr.Filters;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Theory]
    [InlineData("json", "Feishin", "song")]
    [InlineData("xml", "Tempo", "song")]
    [InlineData("json", "Tempo", "album")]
    [InlineData("xml", "Tempo", "artist")]
    public async Task UserData_RatingSurvivesRefreshAndIsViewerScoped(string format, string clientName, string kind)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedFavoriteViewsAsync(storage, "subsonic");
        using var factory = HeartFactory("Subsonic", storage, HeartGateway().Object);
        using var client = factory.CreateClient();
        var id = $"ext-deezer-{kind}-heart";
        foreach (var rating in new[] { 4, 0 })
        {
            using var mutation = await client.PostAsync("/rest/setRating.view", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["u"] = "fixture",
                ["p"] = "fixture",
                ["v"] = "1.13.0",
                ["c"] = clientName,
                ["f"] = format,
                ["id"] = id,
                ["rating"] = rating.ToString()
            }));
            Assert.Equal(HttpStatusCode.OK, mutation.StatusCode);
            foreach (var viewer in new[] { "fixture", "foreign" })
            {
                var method = kind == "song" ? "getSong" : kind == "album" ? "getAlbum" : "getArtist";
                using var detail = await client.GetAsync($"/rest/{method}?u={viewer}&p=fixture&v=1.13.0&c={clientName}&f={format}&id={id}");
                var body = await detail.Content.ReadAsStringAsync();
                var actual = format == "json"
                    ? JsonDocument.Parse(body).RootElement.GetProperty("subsonic-response").GetProperty(kind).GetProperty("userRating").GetInt32()
                    : int.Parse(XDocument.Parse(body).Descendants().Single(item => item.Name.LocalName == kind && item.Attribute("id")?.Value == id).Attribute("userRating")!.Value);
                Assert.Equal(viewer == "fixture" ? rating : 0, actual);
            }
        }
    }

    [Theory]
    [InlineData("/UserPlayedItems/", "?UserId=user-1&ApiKey=fixture", "Finamp")]
    [InlineData("/Users/user-1/PlayedItems/", "?ApiKey=fixture", "Jellyfin Web")]
    public async Task UserData_PlayedMutationAndDetailReturnTheSameDto(string prefix, string query, string clientName)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedFavoriteViewsAsync(storage, "jellyfin");
        using var factory = HeartFactory("Jellyfin", storage, HeartGateway().Object);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Emby-Authorization", $"MediaBrowser Client=\"{clientName}\", DeviceId=\"fixture\", Token=\"fixture\"");
        foreach (var played in new[] { true, false })
        {
            using var response = await client.SendAsync(new HttpRequestMessage(played ? HttpMethod.Post : HttpMethod.Delete, prefix + HeartSong + query));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var mutation = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            using var detail = await client.GetAsync($"/Users/user-1/Items/{HeartSong}?ApiKey=fixture");
            using var document = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
            foreach (var data in new[] { mutation.RootElement, document.RootElement.GetProperty("UserData") })
            {
                Assert.Equal(played, data.GetProperty("Played").GetBoolean());
                Assert.Equal(played ? 1 : 0, data.GetProperty("PlayCount").GetInt32());
                Assert.Equal(0, data.GetProperty("PlaybackPositionTicks").GetInt64());
                Assert.False(data.GetProperty("IsFavorite").GetBoolean());
                Assert.Equal(HeartSong, data.GetProperty("ItemId").GetString());
                Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("Key").GetString()));
            }
        }
    }

    [Theory]
    [InlineData("ParentId", "PlayCount")]
    [InlineData("ParentId", "DatePlayed")]
    [InlineData("AlbumIds", "PlayCount")]
    [InlineData("AlbumIds", "DatePlayed")]
    public async Task UserData_HistorySortRunsBeforePaging(string scopeName, string sort)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedFavoriteViewsAsync(storage, "jellyfin");
        await SeedUserDataHistory(storage, "jellyfin");
        var gateway = HeartGateway();
        gateway.Setup(item => item.GetAlbumAsync(Moq.It.IsAny<ProtocolExecutionContext>(), "deezer", "heart"))
            .ReturnsAsync(new allstarr.Models.Domain.Album
            {
                Id = HeartAlbum,
                Title = "History album",
                Songs =
                [
                    new() { Id = "ext-deezer-song-unplayed", Title = "A new song", ExternalProvider = "deezer" },
                    new() { Id = HeartSong, Title = "Heart song", ExternalProvider = "deezer" }
                ]
            });
        using var factory = HeartFactory("Jellyfin", storage, gateway.Object);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/Users/user-1/Items?ApiKey=fixture&IncludeItemTypes=Audio&{scopeName}={HeartAlbum}&SortBy={sort}&SortOrder=Descending&StartIndex=0&Limit=1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var item = Assert.Single(document.RootElement.GetProperty("Items").EnumerateArray());
        Assert.Equal(HeartSong, item.GetProperty("Id").GetString());
        Assert.Equal(2, item.GetProperty("UserData").GetProperty("PlayCount").GetInt32());
        Assert.Equal(2, document.RootElement.GetProperty("TotalRecordCount").GetInt32());
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("6")]
    [InlineData("bad")]
    public async Task UserData_InvalidProviderRatingReturnsProtocolError(string rating)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedFavoriteViewsAsync(storage, "subsonic");
        using var factory = HeartFactory("Subsonic", storage, HeartGateway().Object);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/rest/setRating?u=fixture&p=fixture&v=1.13.0&c=Feishin&f=json&id={HeartSong}&rating={rating}");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("failed", document.RootElement.GetProperty("subsonic-response").GetProperty("status").GetString());
    }

    [Fact]
    public async Task UserData_PlayedMutationCannotTargetAnotherViewer()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedFavoriteViewsAsync(storage, "jellyfin");
        using var factory = HeartFactory("Jellyfin", storage, HeartGateway().Object);
        using var client = factory.CreateClient();
        using var response = await client.PostAsync($"/UserPlayedItems/{HeartSong}?UserId=foreign&ApiKey=fixture", null);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task UserData_JellyfinResumeTracksBackwardSeekAndClearsOnCompletion()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedFavoriteViewsAsync(storage, "jellyfin");
        var now = DateTimeOffset.UtcNow;
        await using (var db = storage.CreateDbContext())
        {
            db.IntelligencePolicies.Add(new()
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = FavoriteViewer,
                Protocol = "jellyfin",
                BackendInstanceId = "primary",
                Enabled = true,
                CreatedAt = now,
                UpdatedAt = now
            });
            await db.SaveChangesAsync();
        }
        var options = new allstarr.Core.Jobs.DurableJobOptions();
        var jobs = new allstarr.Core.Jobs.DurableJobQueue(storage, options, new allstarr.Core.Jobs.JobPayloadPolicy(options), new allstarr.Core.Operations.SystemPlatformClock());
        var pipeline = new allstarr.Core.Playback.PlaybackSignalPipeline(jobs);
        var scrobbles = new Mock<allstarr.Core.Playback.IScopedPlaybackScrobbleDelivery>();
        scrobbles.Setup(item => item.DeliverAsync(It.IsAny<allstarr.Core.Playback.PlaybackSignalPayload>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var lyrics = new Mock<allstarr.Core.Playback.IPlaybackLyricsPrefetch>();
        lyrics.Setup(item => item.PrefetchAsync(It.IsAny<allstarr.Core.Playback.PlaybackSignalPayload>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var writer = new Mock<IRecommendationSignalWriter>();
        writer.Setup(item => item.WriteAsync(It.IsAny<IntelligenceScope>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var handler = new allstarr.Core.Playback.PlaybackSignalJobHandler(writer.Object, scrobbles.Object, lyrics.Object, storage);
        var execution = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "primary", "user-1",
            new AllstarrPrincipal(FavoriteViewer, "jellyfin", "primary", "user-1", "Viewer", false), "resume", now.AddHours(1), default);
        using var factory = HeartFactory("Jellyfin", storage, HeartGateway().Object);
        using var client = factory.CreateClient();
        var index = 0;
        foreach (var (transition, position, expected) in new[]
        {
            (allstarr.Core.Playback.PlaybackTransition.Progress, 50.0, 50.0),
            (allstarr.Core.Playback.PlaybackTransition.Progress, 12.345, 12.345),
            (allstarr.Core.Playback.PlaybackTransition.Submission, 180.0, 0.0)
        })
        {
            await pipeline.RecordAsync(new(execution, transition, HeartSong, "fixture", "resume-session",
                TimeSpan.FromSeconds(position).Ticks, now.AddSeconds(++index),
                new(null, null, HeartSong, "Heart song", "Heart artist", "Heart album", 180000)));
            var claim = await jobs.ClaimNextAsync("resume", [allstarr.Core.Playback.PlaybackSignalPipeline.JobType]);
            Assert.NotNull(claim);
            using var services = new ServiceCollection().BuildServiceProvider();
            await handler.ExecuteAsync(new(claim!, services), default);
            using var response = await client.GetAsync($"/Users/user-1/Items/{HeartSong}?ApiKey=fixture");
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(TimeSpan.FromSeconds(expected).Ticks,
                document.RootElement.GetProperty("UserData").GetProperty("PlaybackPositionTicks").GetInt64());
        }
    }

    [Fact]
    public async Task UserData_NewListenAfterUnplayedIsVisibleAfterOldHistoryExpires()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedFavoriteViewsAsync(storage, "jellyfin");
        await SeedUserDataHistory(storage, "jellyfin");
        using var factory = HeartFactory("Jellyfin", storage, HeartGateway().Object);
        using var client = factory.CreateClient();
        using var mutation = await client.DeleteAsync($"/UserPlayedItems/{HeartSong}?UserId=user-1&ApiKey=fixture");
        Assert.Equal(HttpStatusCode.OK, mutation.StatusCode);
        await using (var db = storage.CreateDbContext())
        {
            await db.ListeningEvents.ExecuteDeleteAsync();
            var now = DateTimeOffset.UtcNow.AddSeconds(1);
            db.ListeningEvents.Add(new()
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = FavoriteViewer,
                Protocol = "jellyfin",
                BackendInstanceId = "primary",
                TrackReference = HeartSong,
                OccurrenceKey = "fresh-listen",
                State = ListeningEventState.Completed,
                ListenedAt = now,
                UpdatedAt = now
            });
            await db.SaveChangesAsync();
        }
        using var response = await client.GetAsync($"/Users/user-1/Items/{HeartSong}?ApiKey=fixture");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = document.RootElement.GetProperty("UserData");
        Assert.Equal(1, data.GetProperty("PlayCount").GetInt32());
        Assert.True(data.GetProperty("Played").GetBoolean());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UserData_ManualPlayedStateClearsEarlierResume(bool played)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedFavoriteViewsAsync(storage, "jellyfin");
        await SeedUserDataHistory(storage, "jellyfin");
        await using (var db = storage.CreateDbContext())
        {
            await db.ListeningEvents.Where(item => item.State != ListeningEventState.Completed)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.ResumePositionTicks, TimeSpan.FromSeconds(20).Ticks));
        }
        using var factory = HeartFactory("Jellyfin", storage, HeartGateway().Object);
        using var client = factory.CreateClient();
        using var mutation = await client.SendAsync(new HttpRequestMessage(played ? HttpMethod.Post : HttpMethod.Delete,
            $"/UserPlayedItems/{HeartSong}?UserId=user-1&ApiKey=fixture"));
        Assert.Equal(HttpStatusCode.OK, mutation.StatusCode);
        using var response = await client.GetAsync($"/Users/user-1/Items/{HeartSong}?ApiKey=fixture");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, document.RootElement.GetProperty("UserData").GetProperty("PlaybackPositionTicks").GetInt64());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UserData_SearchAndFavoritesSortBothDirectionsBeforePaging(bool favorites)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedFavoriteViewsAsync(storage, "jellyfin");
        await SeedUserDataHistory(storage, "jellyfin");
        var gateway = HeartGateway();
        gateway.Setup(item => item.SearchAsync(It.IsAny<ProtocolExecutionContext>(), "heart", It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync(new allstarr.Models.Search.SearchResult
            {
                Songs =
            [
                new() { Id = HeartSong, Title = "Heart song", Artist = "Heart artist", ExternalProvider = "deezer" },
                new() { Id = "ext-deezer-song-unplayed", Title = "Heart new", Artist = "Heart artist", ExternalProvider = "deezer" }
            ]
            });
        using var factory = HeartFactory("Jellyfin", storage, gateway.Object);
        using var client = factory.CreateClient();
        if (favorites)
        {
            using var mutation = await client.PostAsync($"/UserFavoriteItems/{HeartSong}?ApiKey=fixture", null);
            Assert.Equal(HttpStatusCode.OK, mutation.StatusCode);
        }
        foreach (var descending in new[] { true, false })
        {
            var scope = favorites ? "IsFavorite=true" : "SearchTerm=heart";
            using var response = await client.GetAsync($"/Users/user-1/Items?ApiKey=fixture&IncludeItemTypes=Audio&{scope}&SortBy=PlayCount&SortOrder={(descending ? "Descending" : "Ascending")}&StartIndex=0&Limit=1");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var item = Assert.Single(document.RootElement.GetProperty("Items").EnumerateArray());
            Assert.Equal(descending ? HeartSong : favorites ? "ext-deezer-song-saved" : "ext-deezer-song-unplayed", item.GetProperty("Id").GetString());
            Assert.Equal(descending ? 2 : 0, item.GetProperty("UserData").GetProperty("PlayCount").GetInt32());
        }
    }

    public static IEnumerable<object[]> UserDataCollections() =>
        from format in new[] { "json", "xml" }
        from collection in new[] { "searchResult3", "album", "playlist", "starred2", "randomSongs", "similarSongs2", "topSongs", "playQueue", "nowPlaying" }
        select new object[] { format, collection };

    [Theory]
    [MemberData(nameof(UserDataCollections))]
    public async Task UserData_EverySubsonicCollectionProjectsCompletedOccurrencesWithoutChangingNativeItems(string format, string collection)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedFavoriteViewsAsync(storage, "subsonic");
        await SeedUserDataHistory(storage, "subsonic");
        using var factory = HeartFactory("Subsonic", storage, HeartGateway().Object);
        using var scope = factory.Services.CreateScope();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        http.Items[ProtocolExecutionContextFactory.HttpContextItemKey] = new ProtocolExecutionContext(
            ProtocolKind.Subsonic, "primary", "fixture", new AllstarrPrincipal(FavoriteViewer, "subsonic", "primary", "fixture", "Viewer", false),
            "user-data", DateTimeOffset.UtcNow.AddMinutes(5), default);
        var entry = collection is "playlist" or "playQueue" or "nowPlaying" ? "entry" : "song";
        const string nativeJson = "{ \"id\":\"native\", \"playCount\":27, \"future\":true }";
        var nativeXml = $"<{entry} id='native' playCount='27' future='true'/>";
        var result = new ContentResult
        {
            ContentType = "application/" + format + "; charset=utf-8",
            Content = format == "json"
                ? $"{{\"subsonic-response\":{{\"status\":\"ok\",\"{collection}\":{{\"{entry}\":[{nativeJson},{{\"id\":\"{HeartSong}\",\"contentType\":\"audio/flac\",\"suffix\":\"flac\"}}]}}}}}}"
                : $"<subsonic-response xmlns='http://subsonic.org/restapi' status='ok'><{collection}>{nativeXml}<{entry} id='{HeartSong}' contentType='audio/flac' suffix='flac'/></{collection}></subsonic-response>"
        };
        var action = new ActionContext(http, new RouteData(), new ActionDescriptor());
        var context = new ResultExecutingContext(action, [], result, new object());
        var filter = ActivatorUtilities.CreateInstance<SubsonicFavoriteResponseFilter>(scope.ServiceProvider);
        await filter.OnResultExecutionAsync(context, () => Task.FromResult(new ResultExecutedContext(action, [], result, new object())));
        var body = result.Content!;
        Assert.Contains(format == "json" ? nativeJson : nativeXml, body, StringComparison.Ordinal);
        if (format == "json")
        {
            using var document = JsonDocument.Parse(body);
            var song = document.RootElement.GetProperty("subsonic-response").GetProperty(collection).GetProperty(entry)[1];
            Assert.Equal(2, song.GetProperty("playCount").GetInt32());
            Assert.True(DateTimeOffset.TryParse(song.GetProperty("played").GetString(), out _));
            Assert.Equal("audio/flac", song.GetProperty("contentType").GetString());
            Assert.False(song.TryGetProperty("bitRate", out _));
            Assert.False(song.TryGetProperty("size", out _));
        }
        else
        {
            var song = XDocument.Parse(body).Descendants().Single(item => item.Attribute("id")?.Value == HeartSong);
            Assert.Equal("2", song.Attribute("playCount")?.Value);
            Assert.True(DateTimeOffset.TryParse(song.Attribute("played")?.Value, out _));
            Assert.Equal("audio/flac", song.Attribute("contentType")?.Value);
            Assert.Null(song.Attribute("bitRate"));
            Assert.Null(song.Attribute("size"));
        }
        await using var db = storage.CreateDbContext();
        Assert.Equal(4, db.ListeningEvents.Count());
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT count(*) AS Value FROM provider_user_data").SingleAsync());
    }

    private static async Task SeedUserDataHistory(FavoriteViewFactory storage, string protocol)
    {
        await using var db = storage.CreateDbContext();
        var now = DateTimeOffset.Parse("2026-10-16T12:00:00Z");
        foreach (var (state, index) in new[] { ListeningEventState.Completed, ListeningEventState.Completed, ListeningEventState.Playing, ListeningEventState.Skipped }.Select((state, index) => (state, index)))
            db.ListeningEvents.Add(new()
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = FavoriteViewer,
                Protocol = protocol,
                BackendInstanceId = "primary",
                TrackReference = HeartSong,
                OccurrenceKey = "history-" + index,
                State = state,
                ListenedAt = state == ListeningEventState.Completed ? now.AddDays(index) : null,
                UpdatedAt = now.AddDays(index),
                PositionTicks = TimeSpan.FromSeconds(20).Ticks
            });
        await db.SaveChangesAsync();
    }
}
