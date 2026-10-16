using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using allstarr.Core.Favorites;
using allstarr.Core.Identity;
using allstarr.Core.Jobs;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    private static readonly Guid FavoriteViewer = Guid.Parse("018f1f6e-8e9c-77f5-9a79-3d8a494d60cd");

    [Theory]
    [Trait("Lane", "ReleaseCritical")]
    [InlineData("getStarred", "json")]
    [InlineData("getStarred", "xml")]
    [InlineData("getStarred2", "json")]
    [InlineData("getStarred2", "xml")]
    public async Task SubsonicStarredReadsIncludeSavedProviderFavoritesAndPreserveNativeNodes(string endpoint, string format)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedFavoriteViewsAsync(storage, "subsonic");
        var container = endpoint == "getStarred" ? "starred" : "starred2";
        var nativeJson = """{"subsonic-response":{"status":"ok","version":"1.16.1","future":{"keep":7},"CONTAINER":{"song":[{"id":"native-one","title":"Native favorite","future":{"value":9}}],"album":[],"artist":[]}}}"""
            .Replace("CONTAINER", container, StringComparison.Ordinal);
        var nativeXml = $"<subsonic-response xmlns=\"http://subsonic.org/restapi\" status=\"ok\" version=\"1.16.1\" future=\"keep\"><{container}><song id=\"native-one\" title=\"Native favorite\" future=\"value\"><future value=\"9\"/></song></{container}><future keep=\"7\"/></subsonic-response>";
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        var observed = new List<ObservedRequest>();
        using var factory = new ProtocolFactory("Subsonic", request =>
        {
            if (request.RequestUri!.AbsolutePath == "/rest/ping.view")
                return Json(200, """{"subsonic-response":{"status":"ok","version":"1.16.1"}}""");
            observed.Add(Observe(request));
            return CatalogNative(format, nativeJson, nativeXml);
        }, services => ConfigureFavoriteViews(services, storage, gateway.Object));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/rest/{endpoint}.view?u=fixture&p=secret&v=1.16.1&c=fixture&f={format}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        if (format == "json")
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement.GetProperty("subsonic-response");
            Assert.Equal(7, root.GetProperty("future").GetProperty("keep").GetInt32());
            var starred = root.GetProperty(container);
            var native = Assert.Single(starred.GetProperty("song").EnumerateArray(), item => item.GetProperty("id").GetString() == "native-one");
            Assert.Equal(9, native.GetProperty("future").GetProperty("value").GetInt32());
            Assert.Contains(starred.GetProperty("song").EnumerateArray(), item => item.GetProperty("id").GetString() == "ext-deezer-song-saved");
            Assert.Contains(starred.GetProperty("album").EnumerateArray(), item => item.GetProperty("id").GetString() == "ext-deezer-album-saved");
            Assert.Contains(starred.GetProperty("artist").EnumerateArray(), item => item.GetProperty("id").GetString() == "ext-deezer-artist-saved");
        }
        else
        {
            var root = XDocument.Parse(body).Root!;
            XNamespace ns = root.Name.Namespace;
            Assert.Equal("keep", root.Attribute("future")?.Value);
            Assert.Equal("7", root.Element(ns + "future")?.Attribute("keep")?.Value);
            var starred = root.Element(ns + container)!;
            var native = Assert.Single(starred.Elements(ns + "song"), item => item.Attribute("id")?.Value == "native-one");
            Assert.Equal("value", native.Attribute("future")?.Value);
            Assert.Equal("9", native.Element(ns + "future")?.Attribute("value")?.Value);
            Assert.Contains(starred.Elements(ns + "song"), item => item.Attribute("id")?.Value == "ext-deezer-song-saved");
            Assert.Contains(starred.Elements(ns + "album"), item => item.Attribute("id")?.Value == "ext-deezer-album-saved");
            Assert.Contains(starred.Elements(ns + "artist"), item => item.Attribute("id")?.Value == "ext-deezer-artist-saved");
        }
        Assert.DoesNotContain("foreign-track", body, StringComparison.Ordinal);
        Assert.DoesNotContain("released-track", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ext-deezer-playlist-saved", body, StringComparison.Ordinal);
        Assert.Single(observed);
        gateway.VerifyNoOtherCalls();
    }

    [Theory]
    [Trait("Lane", "ReleaseCritical")]
    [InlineData("IsFavorite=true")]
    [InlineData("Filters=IsFavorite")]
    public async Task JellyfinFavoriteBrowseIncludesSavedTracksWhileKeepingNativePayloads(string favoriteQuery)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedFavoriteViewsAsync(storage, "jellyfin");
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        using var factory = new ProtocolFactory("Jellyfin", request =>
            request.RequestUri!.AbsolutePath == "/Users/Me"
                ? Json(200, """{"Id":"user-1","Name":"Fixture user","Policy":{"IsAdministrator":false}}""")
                : Json(200, """{"Items":[{"Id":"native-one","Name":"Native favorite","Type":"Audio","UserData":{"IsFavorite":true,"future":9}}],"TotalRecordCount":1,"StartIndex":0,"future":{"keep":7}}"""),
            services => ConfigureFavoriteViews(services, storage, gateway.Object));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/Items?ApiKey=fixture&{favoriteQuery}&IncludeItemTypes=Audio&Recursive=true&StartIndex=0&Limit=20");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var items = root.GetProperty("Items").EnumerateArray().ToArray();
        Assert.Equal(9, Assert.Single(items, item => item.GetProperty("Id").GetString() == "native-one")
            .GetProperty("UserData").GetProperty("future").GetInt32());
        var saved = Assert.Single(items, item => item.GetProperty("Id").GetString() == "ext-deezer-song-saved");
        Assert.True(saved.GetProperty("UserData").GetProperty("IsFavorite").GetBoolean());
        Assert.Equal(2, root.GetProperty("TotalRecordCount").GetInt32());
        Assert.Equal(7, root.GetProperty("future").GetProperty("keep").GetInt32());
        Assert.DoesNotContain("foreign-track", body, StringComparison.Ordinal);
        Assert.DoesNotContain("released-track", body, StringComparison.Ordinal);
        gateway.VerifyNoOtherCalls();
    }

    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task FavoriteViewUsesAcceptedDisplayMetadataWhenTheProviderLaterFails()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedFavoriteViewsAsync(storage, "jellyfin");
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "snapshot"))
            .ReturnsAsync(new allstarr.Models.Domain.Song
            {
                Id = "ext-deezer-song-snapshot",
                ExternalProvider = "deezer",
                ExternalId = "snapshot",
                Title = "Accepted favorite",
                Artist = "Accepted artist",
                Album = "Accepted album",
                LocalPath = "/untrusted/fixture-source.flac"
            });
        using var pipelineServices = new ServiceCollection().AddSingleton(gateway.Object).BuildServiceProvider();
        var options = new DurableJobOptions();
        var clock = new SystemPlatformClock();
        var jobs = new DurableJobQueue(storage, options, new JobPayloadPolicy(options), clock);
        var pipeline = ActivatorUtilities.CreateInstance<FavoriteActionPipeline>(pipelineServices, storage, jobs, clock);
        using var factory = new ProtocolFactory("Jellyfin", request =>
            request.RequestUri!.AbsolutePath == "/Users/Me"
                ? Json(200, """{"Id":"user-1","Name":"Fixture user","Policy":{"IsAdministrator":false}}""")
                : Json(200, """{"Items":[],"TotalRecordCount":0,"StartIndex":0}"""), services =>
        {
            ConfigureFavoriteViews(services, storage, gateway.Object);
            services.RemoveAll<IFavoriteActionPipeline>();
            services.AddSingleton<IFavoriteActionPipeline>(pipeline);
        });
        using var client = factory.CreateClient();
        using var accepted = await client.PostAsync("/UserFavoriteItems/ext-deezer-song-snapshot?ApiKey=fixture",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        gateway.Verify(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "snapshot"), Times.Once);
        gateway.Setup(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "snapshot"))
            .ThrowsAsync(new HttpRequestException("Provider unavailable"));

        using var response = await client.GetAsync("/Items?ApiKey=fixture&IsFavorite=true&IncludeItemTypes=Audio&Limit=20");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var saved = Assert.Single(document.RootElement.GetProperty("Items").EnumerateArray(),
            item => item.GetProperty("Id").GetString() == "ext-deezer-song-snapshot");
        Assert.Equal("Accepted favorite [A]", saved.GetProperty("Name").GetString());
        Assert.Equal("Accepted artist [D]", saved.GetProperty("Artists")[0].GetString());
        Assert.DoesNotContain("/untrusted/fixture", body, StringComparison.Ordinal);
        gateway.Verify(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "snapshot"), Times.Once);
    }

    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task FavoriteDisplayMetadataLookupDoesNotHoldTheFavoriteWriter()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedFavoriteViewsAsync(storage, "jellyfin");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "slow"))
            .Returns(async () =>
            {
                entered.TrySetResult();
                await release.Task;
                return new allstarr.Models.Domain.Song
                { Id = "ext-deezer-song-slow", ExternalProvider = "deezer", ExternalId = "slow", Title = "Slow favorite", Artist = "Artist" };
            });
        using var pipelineServices = new ServiceCollection().AddSingleton(gateway.Object).BuildServiceProvider();
        var options = new DurableJobOptions();
        var clock = new SystemPlatformClock();
        var jobs = new DurableJobQueue(storage, options, new JobPayloadPolicy(options), clock);
        var pipeline = ActivatorUtilities.CreateInstance<FavoriteActionPipeline>(pipelineServices, storage, jobs, clock);
        var execution = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "primary", "user-1",
            new AllstarrPrincipal(FavoriteViewer, "jellyfin", "primary", "user-1", "Favorite viewer", false),
            "favorite-display", DateTimeOffset.UtcNow.AddMinutes(1), default);
        var recording = pipeline.RecordAsync(new(execution, "ext-deezer-song-slow", FavoriteOperation.Favorite, "display"));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await using var independent = storage.CreateDbContext();
            Assert.Equal(1, await independent.Users.Where(item => item.Id == FavoriteViewer)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.DisplayName, "Independent writer"))
                .WaitAsync(TimeSpan.FromSeconds(30)));
        }
        finally { release.TrySetResult(); await recording; }
    }

    [Theory]
    [Trait("Lane", "ReleaseCritical")]
    [InlineData("", 0, "native-0", "native-1")]
    [InlineData("", 2, "native-2", "ext-deezer-song-saved")]
    [InlineData("", 3, "ext-deezer-song-saved", null)]
    [InlineData("&SortBy=SortName&SortOrder=Ascending", 1, "ext-deezer-song-saved", "native-1")]
    [InlineData("&SortBy=SortName&SortOrder=Descending", 1, "native-1", "ext-deezer-song-saved")]
    public async Task JellyfinFavoritePagingUsesOneCombinedResult(string sort, int start, string first, string? second)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedFavoriteViewsAsync(storage, "jellyfin");
        await using (var db = storage.CreateDbContext())
            await db.Set<FavoriteStateRecord>().Where(item => item.ItemId == "ext-deezer-song-saved")
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.DisplayMetadataJson,
                    "{\"version\":1,\"itemId\":\"ext-deezer-song-saved\",\"kind\":\"song\",\"title\":\"Bravo\",\"artists\":[]}"));
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        using var factory = new ProtocolFactory("Jellyfin", request =>
        {
            if (request.RequestUri!.AbsolutePath == "/Users/Me")
                return Json(200, """{"Id":"user-1","Name":"Fixture user","Policy":{"IsAdministrator":false}}""");
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query);
            var prefix = int.TryParse(query.GetValueOrDefault("Limit").ToString(), out var requested) ? requested : 3;
            var descending = query.GetValueOrDefault("SortOrder") == "Descending";
            var rows = new[] { new { Id = "native-0", Name = "Alpha", Type = "Audio" },
                new { Id = "native-1", Name = "Charlie", Type = "Audio" },
                new { Id = "native-2", Name = "Delta", Type = "Audio" } };
            var page = (descending ? rows.Reverse() : rows).Take(prefix);
            return Json(200, JsonSerializer.Serialize(new { Items = page, TotalRecordCount = 3, StartIndex = 0 }));
        }, services => ConfigureFavoriteViews(services, storage, gateway.Object));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/Items?ApiKey=fixture&IsFavorite=true&IncludeItemTypes=Audio&StartIndex={start}&Limit=2{sort}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal(4, root.GetProperty("TotalRecordCount").GetInt32());
        Assert.Equal(start, root.GetProperty("StartIndex").GetInt32());
        Assert.Equal(second == null ? [first] : new[] { first, second },
            root.GetProperty("Items").EnumerateArray().Select(item => item.GetProperty("Id").GetString()));
        gateway.VerifyNoOtherCalls();
    }

    [Theory]
    [Trait("Lane", "ReleaseCritical")]
    [InlineData("MusicAlbum", "ext-deezer-album-saved")]
    [InlineData("MusicArtist", "ext-deezer-artist-saved")]
    [InlineData("Playlist", "ext-deezer-playlist-saved")]
    public async Task JellyfinFavoriteTypesKeepTheirOwnShape(string type, string id)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedFavoriteViewsAsync(storage, "jellyfin");
        using var factory = new ProtocolFactory("Jellyfin", request =>
            request.RequestUri!.AbsolutePath == "/Users/Me"
                ? Json(200, """{"Id":"user-1","Name":"Fixture user","Policy":{"IsAdministrator":false}}""")
                : Json(200, """{"Items":[],"TotalRecordCount":0,"StartIndex":0}"""),
            services => ConfigureFavoriteViews(services, storage, new Mock<IProtocolProviderGateway>(MockBehavior.Strict).Object));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/Items?ApiKey=fixture&IsFavorite=true&IncludeItemTypes={type}&Limit=20");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var item = Assert.Single(document.RootElement.GetProperty("Items").EnumerateArray());
        Assert.Equal(id, item.GetProperty("Id").GetString());
        Assert.Equal(type, item.GetProperty("Type").GetString());
        Assert.True(item.GetProperty("UserData").GetProperty("IsFavorite").GetBoolean());
    }

    [Theory]
    [Trait("Lane", "ReleaseCritical")]
    [InlineData("&Filters=IsFavorite,IsPlayed")]
    [InlineData("&ArtistIds=native-artist")]
    [InlineData("&ParentId=native-folder")]
    [InlineData("&SearchTerm=no-such-title")]
    [InlineData("&Ids=native-only")]
    public async Task JellyfinFavoriteScopedReadsNeverInjectUnrelatedRows(string filter)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedFavoriteViewsAsync(storage, "jellyfin");
        const string native = """{"Items":[{"Id":"native-only","Type":"Audio","future":7}],"TotalRecordCount":1,"StartIndex":0,"future":9}""";
        using var factory = new ProtocolFactory("Jellyfin", request => request.RequestUri!.AbsolutePath == "/Users/Me"
            ? Json(200, """{"Id":"user-1","Name":"Fixture user","Policy":{"IsAdministrator":false}}""") : Json(200, native),
            services => ConfigureFavoriteViews(services, storage, new Mock<IProtocolProviderGateway>(MockBehavior.Strict).Object));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/Items?ApiKey=fixture&IsFavorite=true&IncludeItemTypes=Audio&Limit=20{filter}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(native, await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [Trait("Lane", "ReleaseCritical")]
    [InlineData("json", "{\"subsonic-response\":{\"status\":\"failed\",\"error\":{\"code\":70,\"message\":\"Denied\"}}}")]
    [InlineData("xml", "<subsonic-response status=\"failed\"><error code=\"70\" message=\"Denied\"/></subsonic-response>")]
    [InlineData("json", "not-json")]
    public async Task SubsonicStarredReadsPreserveNativeFailures(string format, string body)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedFavoriteViewsAsync(storage, "subsonic");
        using var factory = new ProtocolFactory("Subsonic", request => request.RequestUri!.AbsolutePath == "/rest/ping.view"
            ? Json(200, """{"subsonic-response":{"status":"ok","version":"1.16.1"}}""") : CatalogNative(format, body, body),
            services => ConfigureFavoriteViews(services, storage, new Mock<IProtocolProviderGateway>(MockBehavior.Strict).Object));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/rest/getStarred2.view?u=fixture&p=secret&v=1.16.1&c=fixture&f={format}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(body, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task FavoriteDisplayUpgradePreservesLegacyStateAndDatabaseGuards()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedFavoriteViewsAsync(storage, "jellyfin");
        await using var db = storage.CreateDbContext();
        var before = await db.Set<FavoriteStateRecord>().AsNoTracking().OrderBy(item => item.Id)
            .Select(item => new { item.Id, item.OwnerUserId, item.ItemId, item.IsFavorite, item.Revision, item.LastEventId }).ToArrayAsync();
        var triggers = await db.Database.SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type='trigger' ORDER BY name").ToArrayAsync();
        var migrator = db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
        await migrator.MigrateAsync("20261015165744_SupersedeDownloadArtifacts");
        await db.Database.MigrateAsync();
        Assert.Equal(before, await db.Set<FavoriteStateRecord>().AsNoTracking().OrderBy(item => item.Id)
            .Select(item => new { item.Id, item.OwnerUserId, item.ItemId, item.IsFavorite, item.Revision, item.LastEventId }).ToArrayAsync());
        Assert.All(await db.Set<FavoriteStateRecord>().AsNoTracking().ToArrayAsync(), item => Assert.Null(item.DisplayMetadataJson));
        Assert.Equal(triggers, await db.Database.SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type='trigger' ORDER BY name").ToArrayAsync());
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM pragma_foreign_key_check").SingleAsync());
    }

    [Theory]
    [Trait("Lane", "ReleaseCritical")]
    [InlineData("jellyfin", "disabled")]
    [InlineData("jellyfin", "backend")]
    [InlineData("subsonic", "principal")]
    [InlineData("subsonic", "unlinked")]
    public async Task SavedFavoriteReaderRequiresTheCurrentExactListener(string protocol, string mismatch)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedFavoriteViewsAsync(storage, protocol);
        var principal = protocol == "jellyfin" ? "user-1" : "fixture";
        if (mismatch == "disabled")
        {
            await using var db = storage.CreateDbContext();
            await db.Users.Where(item => item.Id == FavoriteViewer)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.Enabled, false));
        }
        var backend = mismatch == "backend" ? "other-instance" : "primary";
        var verified = mismatch == "principal" ? "other-principal" : principal;
        var execution = new ProtocolExecutionContext(protocol == "jellyfin" ? ProtocolKind.Jellyfin : ProtocolKind.Subsonic,
            backend, verified,
            mismatch == "unlinked" ? null : new AllstarrPrincipal(FavoriteViewer, protocol, backend, verified, "Viewer", false),
            "favorite-scope", DateTimeOffset.UtcNow.AddMinutes(1), default);
        Assert.Empty(await new FavoriteViewReader(storage).ReadAsync(execution));
    }

    [Theory]
    [Trait("Lane", "ReleaseCritical")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SubsonicStarredFolderScopeAndConditionalHeadersRemainHonest(bool scoped)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var storage = new FavoriteViewFactory(database.Options);
        await SeedFavoriteViewsAsync(storage, "subsonic");
        const string native = """{"subsonic-response":{"status":"ok","starred2":{"song":[]}}}""";
        bool? observedValidator = null;
        using var factory = new ProtocolFactory("Subsonic", request =>
        {
            if (request.RequestUri!.AbsolutePath == "/rest/ping.view")
                return Json(200, """{"subsonic-response":{"status":"ok","version":"1.16.1"}}""");
            observedValidator = request.Headers.Contains("If-None-Match");
            var response = Json(200, native);
            response.Headers.TryAddWithoutValidation("ETag", "\"native-version\"");
            return response;
        }, services => ConfigureFavoriteViews(services, storage, new Mock<IProtocolProviderGateway>(MockBehavior.Strict).Object));
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "/rest/getStarred2.view?u=fixture&p=secret&v=1.16.1&c=fixture&f=json" + (scoped ? "&musicFolderId=folder" : ""));
        request.Headers.TryAddWithoutValidation("If-None-Match", "\"native-version\"");
        using var result = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal(scoped, observedValidator);
        var body = await result.Content.ReadAsStringAsync();
        if (scoped) Assert.Equal(native, body);
        else
        {
            Assert.Contains("ext-deezer-song-saved", body, StringComparison.Ordinal);
            Assert.Null(result.Headers.ETag);
            Assert.True(result.Headers.CacheControl?.NoStore);
            using var json = JsonDocument.Parse(body);
            Assert.All(json.RootElement.GetProperty("subsonic-response").GetProperty("starred2").GetProperty("song").EnumerateArray(),
                item => Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("starred").GetString())));
        }
    }

    private static void ConfigureFavoriteViews(IServiceCollection services, FavoriteViewFactory storage,
        IProtocolProviderGateway gateway)
    {
        services.AddSingleton<IStartupFilter, SonicPrincipalStartupFilter>();
        services.RemoveAll<IDbContextFactory<AllstarrDbContext>>();
        services.AddSingleton<IDbContextFactory<AllstarrDbContext>>(storage);
        services.RemoveAll<IProtocolProviderGateway>();
        services.AddSingleton(gateway);
    }

    private static async Task SeedFavoriteViewsAsync(FavoriteViewFactory storage, string protocol)
    {
        var now = DateTimeOffset.UtcNow;
        var other = Guid.CreateVersion7();
        await using (var db = storage.CreateDbContext())
        {
            foreach (var user in new[] { FavoriteViewer, other })
                db.Users.Add(new()
                {
                    Id = user,
                    BackendType = protocol,
                    BackendInstanceId = "primary",
                    BackendPrincipalId = user == FavoriteViewer ? protocol == "jellyfin" ? "user-1" : "fixture" : "foreign",
                    DisplayName = "Favorite viewer",
                    Enabled = true,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            await db.SaveChangesAsync();
        }
        var options = new DurableJobOptions();
        var clock = new SystemPlatformClock();
        var jobs = new DurableJobQueue(storage, options, new JobPayloadPolicy(options), clock);
        var pipeline = new FavoriteActionPipeline(storage, jobs, clock);
        async Task Record(Guid user, string item, FavoriteOperation operation, string revision)
        {
            var principal = user == FavoriteViewer ? protocol == "jellyfin" ? "user-1" : "fixture" : "foreign";
            var execution = new ProtocolExecutionContext(protocol == "jellyfin" ? ProtocolKind.Jellyfin : ProtocolKind.Subsonic,
                "primary", principal, new AllstarrPrincipal(user, protocol, "primary", principal, "Favorite viewer", false),
                "saved-favorites", now.AddMinutes(5), default);
            await pipeline.RecordAsync(new(execution, item, operation, revision));
        }
        foreach (var item in new[] { "ext-deezer-song-saved", "ext-deezer-album-saved", "ext-deezer-artist-saved", "ext-deezer-playlist-saved" })
            await Record(FavoriteViewer, item, FavoriteOperation.Favorite, "saved");
        await Record(other, "ext-deezer-song-foreign-track", FavoriteOperation.Favorite, "foreign");
        await Record(FavoriteViewer, "ext-deezer-song-released-track", FavoriteOperation.Favorite, "first");
        await Record(FavoriteViewer, "ext-deezer-song-released-track", FavoriteOperation.Unfavorite, "second");
        await using var failed = storage.CreateDbContext();
        await failed.Set<FavoriteEventRecord>().Where(item => item.ItemId == "ext-deezer-song-saved")
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.State, FavoriteEventState.Failed));
    }

    private sealed class FavoriteViewFactory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
        public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
