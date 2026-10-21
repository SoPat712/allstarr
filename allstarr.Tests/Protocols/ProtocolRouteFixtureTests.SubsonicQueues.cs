using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using allstarr.Core.Protocols;
using allstarr.Core.Playback;
using allstarr.Models.Settings;
using Microsoft.Extensions.Options;
using allstarr.Core.Intelligence;
using allstarr.Core.Matching;
using allstarr.Models.Domain;
using allstarr.Core.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    private const string QueueSongA = "ext-deezer-song-42";
    private const string QueueSongB = "ext-deezer-song-43";
    private static readonly string[] QueueIds = ["native", QueueSongA, QueueSongA, QueueSongB];
    private const string NativeQueueSong = """{"id":"native", "title":"Native", "isDir":false,"contentType":"audio/mpeg","future":{"value":1.00,"text":"keep"}}""";

    [Theory]
    [InlineData("json", false)]
    [InlineData("xml", false)]
    [InlineData("json", true)]
    [InlineData("xml", true)]
    public async Task SubsonicQueue_LocalSaveRemembersUpcomingOccurrenceWithoutStartingPreparation(string format, bool byIndex)
    {
        var gateway = QueueGateway();
        var audioMuse = new Mock<IAudioMuseRecommendationClient>(MockBehavior.Strict);
        audioMuse.SetupGet(service => service.IsAvailable).Returns(false);
        var prepared = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var preparation = new Mock<IUpcomingTrackPreparation>(MockBehavior.Strict);
        preparation.Setup(service => service.PrepareAsync(It.IsAny<ProtocolExecutionContext>(), QueueSongB))
            .Callback<ProtocolExecutionContext, string>((_, id) => prepared.TrySetResult(id))
            .Returns(Task.CompletedTask);
        using var upcoming = new UpcomingSongPreparation(preparation.Object,
            Options.Create(new PlaybackSettings { PrepareUpcomingSongs = true }));
        using var factory = QueueFactory(gateway, services =>
        {
            SonicServices(audioMuse.Object)(services);
            services.RemoveAll<UpcomingSongPreparation>();
            services.AddSingleton(upcoming);
        });
        using var client = factory.CreateClient();
        AssertQueueOk(await SaveQueue(client, byIndex ? "savePlayQueueByIndex" : "savePlayQueue", format, true,
            byIndex ? QueueIds : ["native", QueueSongA, QueueSongB],
            byIndex ? "currentIndex" : "current", byIndex ? "2" : QueueSongA, "0", "Feishin"), format);
        upcoming.ProcessPending();
        Assert.Empty(preparation.Invocations);
        var context = Assert.IsType<ProtocolExecutionContext>(gateway.Invocations[0].Arguments[0]);
        Assert.NotNull(context.Actor);
        upcoming.Observe(context, PlaybackTransition.Progress, QueueSongA, context.Client.DeviceId ?? context.Client.ClientId, null,
            TimeSpan.FromSeconds(10).Ticks);
        upcoming.ProcessPending();
        Assert.Equal(QueueSongB, await prepared.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        preparation.VerifyAll();
    }

    [Theory]
    [InlineData("json", false)]
    [InlineData("xml", false)]
    [InlineData("json", true)]
    [InlineData("xml", true)]
    public async Task SubsonicQueue_FeishinIndexRoundTripPreservesOccurrencesAcrossClients(string format, bool post)
    {
        var gateway = QueueGateway();
        using var factory = QueueFactory(gateway);
        using (var client = factory.CreateClient())
        {
            var save = await SaveQueue(client, "savePlayQueueByIndex", format, post,
                QueueIds, "currentIndex", "2", "12345", "Feishin");
            AssertQueueOk(save, format);
        }

        gateway.Invocations.Clear();
        using var second = factory.CreateClient();
        var read = await second.GetStringAsync(QueueUrl("getPlayQueueByIndex", format, "Tempo"));
        var queue = ReadQueue(read, format, "playQueueByIndex");
        Assert.Equal(QueueIds, queue.Ids);
        Assert.Equal("2", queue.Current);
        Assert.Equal(12345, queue.Position);
        Assert.Equal("fixture", queue.Username);
        Assert.Equal("Feishin", queue.ChangedBy);
        Assert.True(DateTimeOffset.TryParse(queue.Changed, out _));
        if (format == "json") Assert.Contains(NativeQueueSong, read, StringComparison.Ordinal);
        else Assert.Contains("future=\"keep\"", read, StringComparison.Ordinal);

        var legacy = ReadQueue(await second.GetStringAsync(QueueUrl("getPlayQueue", format, "Tempo")), format, "playQueue");
        Assert.Equal(QueueSongA, legacy.Current);
        Assert.Equal(QueueIds, legacy.Ids);
        Assert.Equal(12345, legacy.Position);
        Assert.Empty(gateway.Invocations);
        var alternate = format == "json" ? "xml" : "json";
        var crossFormat = ReadQueue(await second.GetStringAsync(QueueUrl("getPlayQueueByIndex", alternate, "Feishin")), alternate, "playQueueByIndex");
        Assert.Equal(QueueIds, crossFormat.Ids);
        Assert.Equal("2", crossFormat.Current);

        var other = ReadQueue(await second.GetStringAsync(QueueUrl("getPlayQueueByIndex", format, "Feishin", "other")), format, "playQueueByIndex");
        Assert.Empty(other.Ids);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("xml")]
    public async Task SubsonicQueue_TempoZeroPositionAndLegacyDuplicateSelectionRoundTrip(string format)
    {
        var gateway = QueueGateway();
        using var factory = QueueFactory(gateway);
        using var client = factory.CreateClient();
        AssertQueueOk(await SaveQueue(client, "savePlayQueue", format, false,
            QueueIds, "current", QueueSongA, "0", "Tempo"), format);
        var queue = ReadQueue(await client.GetStringAsync(QueueUrl("getPlayQueueByIndex", format, "Feishin")), format, "playQueueByIndex");
        Assert.Equal(QueueIds, queue.Ids);
        Assert.Equal("1", queue.Current);
        Assert.Equal(0, queue.Position);
        Assert.Equal("Tempo", queue.ChangedBy);

        AssertQueueOk(await SaveQueue(client, "savePlayQueueByIndex", format, true,
            [], null, null, null, "Feishin"), format);
        var empty = ReadQueue(await client.GetStringAsync(QueueUrl("getPlayQueue", format, "Tempo")), format, "playQueue");
        Assert.Empty(empty.Ids);
        Assert.Null(empty.Current);
        Assert.Equal(0, empty.Position);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("xml")]
    public async Task SubsonicQueue_InvalidIndexOrPositionDoesNotReplaceSavedQueue(string format)
    {
        var gateway = QueueGateway();
        using var factory = QueueFactory(gateway);
        using var client = factory.CreateClient();
        AssertQueueOk(await SaveQueue(client, "savePlayQueueByIndex", format, true,
            QueueIds, "currentIndex", "2", "12345", "Feishin"), format);
        gateway.Invocations.Clear();
        foreach (var (ids, index, position) in new (string[], string?, string?)[]
                 { (QueueIds, "4", "0"), (QueueIds, "-1", "0"), (QueueIds, null, "0"),
                     ([], "0", "0"), (QueueIds, "2", "-1"), (QueueIds, "2", "oops") })
        {
            var result = await SaveQueue(client, "savePlayQueueByIndex", format, true,
                ids, "currentIndex", index, position, "Feishin");
            if (format == "json")
            {
                using var json = JsonDocument.Parse(result);
                Assert.Equal(10, json.RootElement.GetProperty("subsonic-response").GetProperty("error").GetProperty("code").GetInt32());
            }
            else Assert.Equal("10", XDocument.Parse(result).Root!.Elements().Single(item => item.Name.LocalName == "error").Attribute("code")!.Value);
        }
        var queue = ReadQueue(await client.GetStringAsync(QueueUrl("getPlayQueueByIndex", format, "Feishin")), format, "playQueueByIndex");
        Assert.Equal(QueueIds, queue.Ids);
        Assert.Equal("2", queue.Current);
        Assert.Equal(12345, queue.Position);
        Assert.Empty(gateway.Invocations);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("xml")]
    public async Task SubsonicQueue_ExtensionsAdvertiseIndexSupportWithoutSonicSimilarity(string format)
    {
        using var factory = QueueFactory(QueueGateway());
        using var client = factory.CreateClient();
        var response = await client.GetStringAsync(QueueUrl("getOpenSubsonicExtensions", format, "Feishin"));
        if (format == "json")
        {
            using var document = JsonDocument.Parse(response);
            var extensions = document.RootElement.GetProperty("subsonic-response").GetProperty("openSubsonicExtensions").EnumerateArray().ToArray();
            Assert.Equal(1, Assert.Single(extensions, item => item.GetProperty("name").GetString() == "indexBasedQueue").GetProperty("versions")[0].GetInt32());
            Assert.Contains(extensions, item => item.GetProperty("name").GetString() == "songLyrics");
        }
        else
        {
            var extensions = XDocument.Parse(response).Root!.Elements().ToArray();
            Assert.Equal("1", Assert.Single(extensions, item => item.Attribute("name")?.Value == "indexBasedQueue").Elements().Single().Value);
            Assert.Contains(extensions, item => item.Attribute("name")?.Value == "songLyrics");
        }
    }

    [Theory]
    [InlineData("json")]
    [InlineData("xml")]
    public async Task SubsonicQueue_UnsavedNativeLegacyResponseRemainsByteForByteUnchanged(string format)
    {
        var native = format == "json" ? """{ "subsonic-response": {"status":"ok","playQueue":{"entry":[],"future":1.00}} }"""
            : "<subsonic-response xmlns=\"http://subsonic.org/restapi\" status=\"ok\"><playQueue future=\"keep\" /></subsonic-response>";
        using var factory = new ProtocolFactory("Subsonic", request => request.RequestUri!.AbsolutePath == "/rest/ping.view"
            ? Json(200, """{"subsonic-response":{"status":"ok"}}""")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(native, Encoding.UTF8, $"application/{format}") });
        using var client = factory.CreateClient();
        Assert.Equal(native, await client.GetStringAsync(QueueUrl("getPlayQueue", format, "Tempo")));
    }

    [Fact]
    public async Task SubsonicQueue_RestartReadsSavedEntriesFromReadOnlyDatabase()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var contexts = new QueueDbFactory(database.Options);
        var gateway = QueueGateway();
        void Configure(IServiceCollection services)
        {
            services.RemoveAll<IDbContextFactory<AllstarrDbContext>>();
            services.AddSingleton<IDbContextFactory<AllstarrDbContext>>(contexts);
        }
        using (var first = QueueFactory(gateway, Configure))
        using (var client = first.CreateClient())
            AssertQueueOk(await SaveQueue(client, "savePlayQueueByIndex", "json", true,
                QueueIds, "currentIndex", "2", "12345", "Feishin"), "json");
        gateway.Invocations.Clear();
        contexts.Options = new DbContextOptionsBuilder<AllstarrDbContext>().UseSqlite(
            new SqliteConnectionStringBuilder(database.ConnectionString) { Mode = SqliteOpenMode.ReadOnly }.ToString()).Options;
        using var restarted = QueueFactory(gateway, Configure);
        using var second = restarted.CreateClient();
        var queue = ReadQueue(await second.GetStringAsync(QueueUrl("getPlayQueueByIndex", "json", "Feishin")), "json", "playQueueByIndex");
        Assert.Equal(QueueIds, queue.Ids);
        Assert.Equal("2", queue.Current);
        Assert.Equal(12345, queue.Position);
        Assert.Empty(gateway.Invocations);
    }

    private sealed class QueueDbFactory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public DbContextOptions<AllstarrDbContext> Options { get; set; } = options;
        public AllstarrDbContext CreateDbContext() => new(Options);
        public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    [Theory]
    [InlineData("json")]
    [InlineData("xml")]
    public async Task SubsonicQueue_UnavailableSongDoesNotOverwriteSavedQueue(string format)
    {
        var gateway = QueueGateway();
        using var factory = QueueFactory(gateway);
        using var client = factory.CreateClient();
        AssertQueueOk(await SaveQueue(client, "savePlayQueueByIndex", format, false,
            QueueIds, "currentIndex", "2", "12345", "Feishin"), format);
        gateway.Setup(service => service.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "43"))
            .ReturnsAsync((Song?)null);
        var failed = await SaveQueue(client, "savePlayQueue", format, false,
            [QueueSongB], "current", QueueSongB, "0", "Tempo");
        Assert.Contains("failed", failed, StringComparison.Ordinal);
        gateway.Invocations.Clear();
        var queue = ReadQueue(await client.GetStringAsync(QueueUrl("getPlayQueueByIndex", format, "Feishin")), format, "playQueueByIndex");
        Assert.Equal(QueueIds, queue.Ids);
        Assert.Equal("2", queue.Current);
        Assert.Equal(12345, queue.Position);
        Assert.Empty(gateway.Invocations);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("xml")]
    public async Task SubsonicQueue_IndexMethodsAlsoSupportNativeDuplicates(string format)
    {
        var gateway = QueueGateway();
        using var factory = QueueFactory(gateway);
        using var client = factory.CreateClient();
        AssertQueueOk(await SaveQueue(client, "savePlayQueueByIndex", format, true,
            ["native", "native"], "currentIndex", "1", "12345", "Feishin"), format);
        var queue = ReadQueue(await client.GetStringAsync(QueueUrl("getPlayQueueByIndex", format, "Feishin")), format, "playQueueByIndex");
        Assert.Equal(["native", "native"], queue.Ids);
        Assert.Equal("1", queue.Current);
        Assert.Equal(12345, queue.Position);
        Assert.Empty(gateway.Invocations);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("xml")]
    public async Task SubsonicQueue_ExtensionsRemainAvailableWhenBackendDiscoveryFails(string format)
    {
        using var factory = new ProtocolFactory("Subsonic", _ => throw new HttpRequestException("Fixture unavailable"));
        using var client = factory.CreateClient();
        var body = await client.GetStringAsync($"/rest/getOpenSubsonicExtensions.view?f={format}");
        AssertQueueOk(body, format);
        Assert.Contains("indexBasedQueue", body, StringComparison.Ordinal);
        Assert.Contains("songLyrics", body, StringComparison.Ordinal);
        Assert.DoesNotContain("sonicSimilarity", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("xml")]
    public async Task SubsonicQueue_ExtensionsKeepSonicSupportWhenBackendReturnsProtocolFailure(string format)
    {
        var audioMuse = new Mock<IAudioMuseRecommendationClient>(MockBehavior.Strict);
        audioMuse.SetupGet(service => service.IsAvailable).Returns(true);
        using var factory = new ProtocolFactory("Subsonic", _ => format == "json"
            ? Json(200, """{"subsonic-response":{"status":"failed","error":{"code":70,"message":"Unknown method"}}}""")
            : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<subsonic-response xmlns='http://subsonic.org/restapi' status='failed'><error code='70' message='Unknown method' /></subsonic-response>", Encoding.UTF8, "application/xml")
            }, SonicServices(audioMuse.Object));
        using var client = factory.CreateClient();
        var body = await client.GetStringAsync($"/rest/getOpenSubsonicExtensions.view?f={format}");
        AssertQueueOk(body, format);
        Assert.Contains("indexBasedQueue", body, StringComparison.Ordinal);
        Assert.Contains("songLyrics", body, StringComparison.Ordinal);
        Assert.Contains("sonicSimilarity", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Unknown method", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("xml")]
    public async Task SubsonicQueue_IndexReadProjectsExistingNativeQueueWithoutProviderRequests(string format)
    {
        var gateway = QueueGateway();
        var native = format == "json"
            ? "{\"subsonic-response\":{\"status\":\"ok\",\"playQueue\":{\"entry\":[" + NativeQueueSong + "," + NativeQueueSong + "],\"current\":\"native\",\"position\":12345,\"changed\":\"2026-10-21T00:00:00Z\",\"changedBy\":\"Tempo\"}}}"
            : "<subsonic-response xmlns='http://subsonic.org/restapi' xmlns:future='urn:future' status='ok'><playQueue current='native' position='12345' changed='2026-10-21T00:00:00Z' changedBy='Tempo'><entry id='native' future:value='1.00'/><entry id='native' future:value='2.00'/></playQueue></subsonic-response>";
        using var factory = new ProtocolFactory("Subsonic", request =>
        {
            if (request.RequestUri!.AbsolutePath == "/rest/ping.view")
                return Json(200, """{"subsonic-response":{"status":"ok"}}""");
            Assert.Equal("/rest/getPlayQueue.view", request.RequestUri.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(native, Encoding.UTF8, $"application/{format}") };
        }, services =>
            {
                services.RemoveAll<IProtocolProviderGateway>();
                services.AddSingleton(gateway.Object);
            });
        using var client = factory.CreateClient();
        var body = await client.GetStringAsync(QueueUrl("getPlayQueueByIndex", format, "Feishin"));
        var queue = ReadQueue(body, format, "playQueueByIndex");
        Assert.Equal(["native", "native"], queue.Ids);
        Assert.Equal("0", queue.Current);
        Assert.Equal(12345, queue.Position);
        Assert.Equal("fixture", queue.Username);
        Assert.Equal("Tempo", queue.ChangedBy);
        if (format == "json") Assert.Contains(NativeQueueSong, body, StringComparison.Ordinal);
        else
        {
            var entries = XDocument.Parse(body).Descendants().Where(item => item.Name.LocalName == "entry").ToArray();
            Assert.Equal(["1.00", "2.00"], entries.Select(item => item.Attribute(XNamespace.Get("urn:future") + "value")!.Value));
        }
        Assert.Empty(gateway.Invocations);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("xml")]
    public async Task SubsonicQueue_LegacyCurrentMatchesTheProjectedPublicSongIdentity(string format)
    {
        const string canonical = "ext-allstarr-song-recording";
        var gateway = QueueGateway();
        gateway.Setup(service => service.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "42"))
            .ReturnsAsync(new Song { Id = canonical, Title = "Song", LocalPath = "fixture.flac" });
        using var factory = QueueFactory(gateway);
        using var client = factory.CreateClient();
        AssertQueueOk(await SaveQueue(client, "savePlayQueueByIndex", format, true,
            QueueIds, "currentIndex", "2", "12345", "Feishin"), format);
        var queue = ReadQueue(await client.GetStringAsync(QueueUrl("getPlayQueue", format, "Tempo")), format, "playQueue");
        Assert.Equal(["native", canonical, canonical, QueueSongB], queue.Ids);
        Assert.Equal(canonical, queue.Current);
        Assert.Equal(12345, queue.Position);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("xml")]
    public async Task SubsonicQueue_KnownNativeAliasKeepsNativeEntriesWithoutProviderRequests(string format)
    {
        var gateway = QueueGateway();
        var library = new Mock<ILibraryIndexService>(MockBehavior.Strict);
        library.Setup(service => service.ResolveKnownTrackAsync(It.IsAny<ProtocolExecutionContext>(), QueueSongA, It.IsAny<CancellationToken>()))
            .ReturnsAsync("native");
        using var factory = QueueFactory(gateway, services =>
        {
            services.RemoveAll<ILibraryIndexService>();
            services.AddSingleton(library.Object);
        });
        using var client = factory.CreateClient();
        AssertQueueOk(await SaveQueue(client, "savePlayQueueByIndex", format, true,
            [QueueSongA, QueueSongA], "currentIndex", "1", "12345", "Feishin"), format);
        var body = await client.GetStringAsync(QueueUrl("getPlayQueue", format, "Tempo"));
        var queue = ReadQueue(body, format, "playQueue");
        Assert.Equal(["native", "native"], queue.Ids);
        Assert.Equal("native", queue.Current);
        if (format == "json") Assert.Contains(NativeQueueSong, body, StringComparison.Ordinal);
        else Assert.Contains("future=\"keep\"", body, StringComparison.Ordinal);
        Assert.Empty(gateway.Invocations);
    }

    private static Mock<IProtocolProviderGateway> QueueGateway()
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        foreach (var id in new[] { "42", "43" })
            gateway.Setup(service => service.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", id))
                .ReturnsAsync(new Song
                {
                    Id = $"ext-deezer-song-{id}",
                    Title = $"Song {id}",
                    Artist = "Artist",
                    Album = "Album",
                    AlbumId = "ext-deezer-album-7",
                    Duration = 180,
                    LocalPath = "fixture.flac"
                });
        return gateway;
    }

    private static ProtocolFactory QueueFactory(Mock<IProtocolProviderGateway> gateway, Action<IServiceCollection>? configure = null) => new("Subsonic", request =>
    {
        var query = QueryHelpers.ParseQuery(request.RequestUri!.Query);
        var format = query["f"] == "xml" ? "xml" : "json";
        var path = request.RequestUri.AbsolutePath;
        if (path == "/rest/ping.view") return Json(200, """{"subsonic-response":{"status":"ok","version":"1.16.1"}}""");
        if (path == "/rest/getSong.view")
        {
            Assert.Equal("native", query["id"].ToString());
            return format == "json" ? Json(200, "{\"subsonic-response\":{\"status\":\"ok\",\"song\":" + NativeQueueSong + "}}")
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<subsonic-response xmlns=\"http://subsonic.org/restapi\" status=\"ok\"><song id=\"native\" title=\"Native\" isDir=\"false\" contentType=\"audio/mpeg\" future=\"keep\" /></subsonic-response>", Encoding.UTF8, "application/xml") };
        }
        if (path == "/rest/getPlayQueue.view")
            return format == "json" ? Json(200, """{"subsonic-response":{"status":"ok","playQueue":{"entry":[],"username":"fixture","changed":"2026-10-21T00:00:00Z","changedBy":"Native","position":0}}}""")
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<subsonic-response xmlns=\"http://subsonic.org/restapi\" status=\"ok\"><playQueue username=\"fixture\" changed=\"2026-10-21T00:00:00Z\" changedBy=\"Native\" position=\"0\" /></subsonic-response>", Encoding.UTF8, "application/xml") };
        if (path == "/rest/getOpenSubsonicExtensions.view")
            return format == "json" ? Json(200, """{"subsonic-response":{"status":"ok","openSubsonicExtensions":[{"name":"songLyrics","versions":[1]}]}}""")
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<subsonic-response xmlns=\"http://subsonic.org/restapi\" status=\"ok\"><openSubsonicExtensions name=\"songLyrics\"><versions>1</versions></openSubsonicExtensions></subsonic-response>", Encoding.UTF8, "application/xml") };
        return format == "json" ? Json(200, """{"subsonic-response":{"status":"failed","error":{"code":70,"message":"Unknown method or song"}}}""")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<subsonic-response xmlns=\"http://subsonic.org/restapi\" status=\"failed\"><error code=\"70\" message=\"Unknown method or song\" /></subsonic-response>", Encoding.UTF8, "application/xml") };
    }, services =>
    {
        services.RemoveAll<IProtocolProviderGateway>();
        services.AddSingleton(gateway.Object);
        configure?.Invoke(services);
    });

    private static string QueueUrl(string method, string format, string client, string user = "fixture") =>
        $"/rest/{method}.view?u={user}&p=fixture-password&v=1.13.0&c={client}&f={format}";

    private static async Task<string> SaveQueue(HttpClient client, string method, string format, bool post,
        string[] ids, string? currentName, string? current, string? position, string app)
    {
        var pairs = ids.Select(id => new KeyValuePair<string, string>("id", id)).ToList();
        if (currentName != null && current != null) pairs.Add(new(currentName, current));
        if (position != null) pairs.Add(new("position", position));
        using var form = new FormUrlEncodedContent(pairs);
        using var response = post ? await client.PostAsync(QueueUrl(method, format, app), form)
            : await client.GetAsync(QueueUrl(method, format, app) + "&" + await form.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    private static void AssertQueueOk(string response, string format)
    {
        if (format == "json")
        {
            using var document = JsonDocument.Parse(response);
            Assert.Equal("ok", document.RootElement.GetProperty("subsonic-response").GetProperty("status").GetString());
        }
        else Assert.Equal("ok", XDocument.Parse(response).Root!.Attribute("status")?.Value);
    }

    private static (string[] Ids, string? Current, long Position, string? Username, string? Changed, string? ChangedBy)
        ReadQueue(string response, string format, string name)
    {
        AssertQueueOk(response, format);
        var currentName = name == "playQueue" ? "current" : "currentIndex";
        if (format == "json")
        {
            using var document = JsonDocument.Parse(response);
            var queue = document.RootElement.GetProperty("subsonic-response").GetProperty(name);
            var entries = queue.GetProperty("entry").EnumerateArray().ToArray();
            foreach (var song in entries)
            {
                Assert.False(song.GetProperty("isDir").GetBoolean());
                Assert.StartsWith("audio/", song.GetProperty("contentType").GetString());
                Assert.False(string.IsNullOrWhiteSpace(song.GetProperty("title").GetString()));
            }
            return (entries.Select(item => item.GetProperty("id").GetString()!).ToArray(),
                queue.TryGetProperty(currentName, out var current) ? current.ToString() : null,
                queue.GetProperty("position").GetInt64(), queue.GetProperty("username").GetString(),
                queue.GetProperty("changed").GetString(), queue.GetProperty("changedBy").GetString());
        }
        var element = XDocument.Parse(response).Root!.Elements().Single(item => item.Name.LocalName == name);
        return (element.Elements().Select(item => item.Attribute("id")!.Value).ToArray(),
            element.Attribute(currentName)?.Value, long.Parse(element.Attribute("position")!.Value),
            element.Attribute("username")?.Value, element.Attribute("changed")?.Value, element.Attribute("changedBy")?.Value);
    }
}
