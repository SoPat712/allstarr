using System.Net;
using System.Net.Http.Json;
using System.Diagnostics;
using allstarr.Core.Identity;
using allstarr.Models.Settings;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using System.Text.Json.Nodes;
using System.Text.Json;
using Xunit.Abstractions;
using allstarr.Core.Capabilities;
using allstarr.Core.ClientApi;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    private readonly ITestOutputHelper _lyricsSearchOutput;
    public ProtocolRouteFixtureTests(ITestOutputHelper output) => _lyricsSearchOutput = output;

    [Fact]
    public async Task ClientApi_LyricsSearchTwentyThousandSongsMeetsResponseBudget()
    {
        var access = new Mock<IBackendLibraryAccessResolver>();
        access.Setup(service => service.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BackendLibraryAccess(true, ["music"]));
        using var factory = ClientApiFactory(request => request.RequestUri!.AbsolutePath switch
        {
            "/Users/Me" => Json(200, """{"Id":"listener"}"""),
            "/Items" => Json(200, JsonSerializer.Serialize(new
            {
                Items = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query)["ids"]
                .ToString().Split(',').Select(id => new { Id = id, Type = "Audio", Name = id, AlbumId = "library-album" })
            })),
            _ => Json(404, "{}")
        }, services =>
        {
            services.RemoveAll<IBackendLibraryAccessResolver>();
            services.AddSingleton(access.Object);
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var owner = (await factory.Services.GetRequiredService<BackendIdentityResolver>()
            .ResolveAsync(new BackendIdentityDescriptor("Jellyfin", "listener")))!.UserId;
        var context = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "primary", "listener",
            new AllstarrPrincipal(owner, "jellyfin", "primary", "listener", "Listener", false), "search-fixture", DateTimeOffset.UtcNow.AddMinutes(5), default);
        var index = factory.Services.GetRequiredService<LyricsSearchIndex>();
        var loading = Stopwatch.StartNew();
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync())
        {
            for (var number = 0; number < 20_000; number++)
            {
                var id = "native-" + number;
                var song = new Song { Id = id, IsLocal = true, Title = id, Artist = "Artist" };
                var text = "Silver moon above the water\n" + string.Join('\n', Enumerable.Range(0, 58)
                    .Select(line => $"Verse {line} tells a familiar story about song {number}")) + $"\nA unique constellation number {number}";
                await index.StoreAsync(context, song, null, new(new("backend", "Library"), "plain",
                    text.Split('\n').Select(line => new RichLyricsLine(null, null, line, "lead", [])).ToArray()), sharedLibrary: true);
                db.LibraryTracks.Add(new LibraryTrackRecord
                {
                    Id = Guid.NewGuid(),
                    OwnerUserId = owner,
                    BackendLibraryId = "music",
                    Protocol = "jellyfin",
                    BackendInstanceId = "primary",
                    BackendItemId = id,
                    FilePath = "/fixture/" + id,
                    Title = id,
                    Artist = "Artist",
                    SourceModifiedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    IndexedAt = DateTimeOffset.UtcNow,
                    Revision = 1
                });
            }
            await db.SaveChangesAsync();
        }
        var bytes = Directory.GetFiles(Path.GetDirectoryName(index.DatabasePath)!, "lyrics-search-v1.db*").Sum(path => new FileInfo(path).Length);
        Assert.True(bytes < 512L * 1024 * 1024, $"Index size was {bytes} bytes");
        _lyricsSearchOutput.WriteLine($"Indexed 20000 songs / 1200000 lines in {loading.Elapsed.TotalSeconds:F2}s; database and journal bytes: {bytes}");
        var budget = TimeSpan.FromMilliseconds(Environment.GetEnvironmentVariable("CI") != null ? 600 : 300);
        await Measure("Common phrase", "/allstarr/v1/search/lyrics?q=silver%20moon", common =>
        {
            Assert.Equal(20, common["results"]!.AsArray().Count);
            Assert.True(common["truncated"]!.GetValue<bool>());
        });
        await Measure("Specific phrase", "/allstarr/v1/search/lyrics?q=constellation%20number%2019999", specific =>
            Assert.Equal("native-19999", specific["results"]![0]!["itemId"]!.GetValue<string>()));

        async Task Measure(string name, string path, Action<JsonNode> verify)
        {
            await ReadClientLyricsJsonAsync(client, path);
            var elapsed = new TimeSpan[5];
            for (var run = 0; run < elapsed.Length; run++)
            {
                var watch = Stopwatch.StartNew();
                var response = await ReadClientLyricsJsonAsync(client, path);
                watch.Stop();
                elapsed[run] = watch.Elapsed;
                verify(response);
            }
            Array.Sort(elapsed);
            var median = elapsed[elapsed.Length / 2];
            _lyricsSearchOutput.WriteLine($"{name} full HTTP response median (5 runs): {median.TotalMilliseconds:F1}ms");
            Assert.True(median < budget, $"{name} median took {median.TotalMilliseconds:F1}ms (budget {budget.TotalMilliseconds:F0}ms)");
        }
    }

    [Fact]
    public async Task ClientApi_LyricsSearchFollowsANewBetterStoredSource()
    {
        var recording = Guid.NewGuid();
        var sources = new Dictionary<string, ProviderLyricsResult?>
        {
            ["unison"] = new(ProviderLyricsAvailabilityState.Available, "unison", ProviderLyricsFormat.PlainText, "Original quiet night"),
            ["lrclib"] = null
        };
        using var factory = ClientLyricsFactory(recording, sources);
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        await SeedClientLyricsAsync(factory, recording);
        const string item = "/allstarr/v1/items/ext-deezer-song-lyrics/lyrics";
        await ReadClientLyricsJsonAsync(client, item);
        Assert.Single((await ReadClientLyricsJsonAsync(client, "/allstarr/v1/search/lyrics?q=quiet"))["results"]!.AsArray());
        sources["lrclib"] = new(ProviderLyricsAvailabilityState.Available, "lrclib", ProviderLyricsFormat.LineTimed, "[00:01.00]A brighter morning\n[00:09.00]Last line");
        await ReadClientLyricsJsonAsync(client, item + "/sources");
        Assert.Empty((await ReadClientLyricsJsonAsync(client, "/allstarr/v1/search/lyrics?q=quiet"))["results"]!.AsArray());
        var found = Assert.Single((await ReadClientLyricsJsonAsync(client, "/allstarr/v1/search/lyrics?q=%20brighter%20morning%20"))["results"]!.AsArray())!;
        Assert.Equal("lrclib", found["source"]!["id"]!.GetValue<string>());
        Assert.Equal(1000, found["line"]!["startMs"]!.GetValue<long>());
        var rich = await ReadClientLyricsJsonAsync(client, item);
        Assert.Equal(rich["lines"]![0]!["text"]!.GetValue<string>(), found["line"]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task ClientApi_LyricsSearchUsesSelectedSourceAndUpdatesOnPinAndClear()
    {
        var recording = Guid.NewGuid();
        using var factory = ClientLyricsFactory(recording, new()
        {
            ["unison"] = new(ProviderLyricsAvailabilityState.Available, "unison", ProviderLyricsFormat.PlainText, "Quiet night\nPlain second"),
            ["lrclib"] = new(ProviderLyricsAvailabilityState.Available, "lrclib", ProviderLyricsFormat.LineTimed,
                "[00:01.00]😀 Café, I’ve heard\n[00:02.00]there’s more\n[00:03.00]Last line")
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        await SeedClientLyricsAsync(factory, recording);
        const string item = "/allstarr/v1/items/ext-deezer-song-lyrics/lyrics";
        await ReadClientLyricsJsonAsync(client, item + "/sources");
        var search = await ReadClientLyricsJsonAsync(client, "/allstarr/v1/search/lyrics?q=cafe%20ive");
        var hit = Assert.Single(search["results"]!.AsArray())!;
        Assert.Equal("ext-deezer-song-lyrics", hit["itemId"]!.GetValue<string>());
        Assert.Equal("😀 Café, I’ve heard", hit["line"]!["text"]!.GetValue<string>());
        Assert.Equal(1000, hit["line"]!["startMs"]!.GetValue<long>());
        Assert.Equal(2000, hit["line"]!["endMs"]!.GetValue<long>());
        Assert.Null(hit["line"]!["before"]);
        Assert.Equal("there’s more", hit["line"]!["after"]!.GetValue<string>());
        Assert.Equal(3, hit["highlights"]![0]!["start"]!.GetValue<int>());
        Assert.Equal("Café, I’ve", hit["line"]!["text"]!.GetValue<string>().Substring(3, hit["highlights"]![0]!["length"]!.GetValue<int>()));
        var across = await ReadClientLyricsJsonAsync(client, "/allstarr/v1/search/lyrics?q=heard%20theres%20more");
        var cross = Assert.Single(across["results"]!.AsArray())!;
        Assert.Equal("😀 Café, I’ve heard", cross["line"]!["text"]!.GetValue<string>());
        Assert.Equal(5, cross["highlights"]![0]!["length"]!.GetValue<int>());
        Assert.Equal("ext-deezer-album-lyrics", hit["albumId"]!.GetValue<string>());
        using var albumResult = await client.GetAsync("/Items/" + hit["albumId"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, albumResult.StatusCode);
        using var resolved = await client.GetAsync("/Items/" + hit["itemId"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
        Assert.Empty((await ReadClientLyricsJsonAsync(client, "/allstarr/v1/search/lyrics?q=quiet%20night"))["results"]!.AsArray());
        using var pin = await client.PutAsJsonAsync(item + "/source", new { sourceId = "unison", expectedRevision = new { } });
        Assert.Equal(HttpStatusCode.OK, pin.StatusCode);
        var pinned = JsonNode.Parse(await pin.Content.ReadAsStringAsync())!;
        var plain = Assert.Single((await ReadClientLyricsJsonAsync(client, "/allstarr/v1/search/lyrics?q=quiet%20night"))["results"]!.AsArray())!;
        Assert.Null(plain["line"]!["startMs"]);
        Assert.Null(plain["line"]!["endMs"]);
        Assert.Empty((await ReadClientLyricsJsonAsync(client, "/allstarr/v1/search/lyrics?q=cafe"))["results"]!.AsArray());
        using var clear = await client.PostAsJsonAsync(item + "/source/clear", new { expectedRevision = pinned["revision"] });
        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
        Assert.Single((await ReadClientLyricsJsonAsync(client, "/allstarr/v1/search/lyrics?q=cafe"))["results"]!.AsArray());
    }

    [Fact]
    public async Task ClientApi_LyricsSearchRanksSongsOnceLimitsAndDoesNotExposeForeignLyrics()
    {
        var recording = Guid.NewGuid();
        using var factory = ClientLyricsFactory(recording, new());
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var (owner, other) = await SeedClientLyricsAsync(factory, recording);
        var access = await factory.Services.GetRequiredService<IBackendLibraryAccessResolver>().ResolveUserAsync(owner);
        var context = access.Context!;
        var index = factory.Services.GetRequiredService<LyricsSearchIndex>();
        async Task Store(string id, string text, Guid user, string source = "unison")
        {
            var principal = context.Principal! with { UserId = user };
            var execution = new ProtocolExecutionContext(context.Protocol, context.BackendInstanceId, context.VerifiedBackendPrincipalId,
                principal, "search-fixture", DateTimeOffset.UtcNow.AddMinutes(1), default);
            await index.StoreAsync(execution, new Song
            {
                Id = "ext-deezer-song-" + id,
                ExternalProvider = "deezer",
                ExternalId = id,
                Title = id,
                Artist = "Artist"
            }, null, new(new(source, source), "plain",
                text.Split('\n').Select(line => new RichLyricsLine(null, null, line, "lead", [])).ToArray()));
        }
        await Store("phrase", "blue moon\nblue moon again", owner);
        await Store("words", "moon in a blue sky", owner);
        await Store("partial", "blue sky", owner);
        await Store("foreign", "blue moon", other, "kept");
        await Store("shared", "Public sunrise", other);
        Assert.Single((await ReadClientLyricsJsonAsync(client, "/allstarr/v1/search/lyrics?q=public%20sunrise"))["results"]!.AsArray());
        var all = await ReadClientLyricsJsonAsync(client, "/allstarr/v1/search/lyrics?q=blue%20moon");
        Assert.Equal(new[] { "phrase", "words", "partial" }, all["results"]!.AsArray().Select(hit => hit!["title"]!.GetValue<string>()));
        var limited = await ReadClientLyricsJsonAsync(client, "/allstarr/v1/search/lyrics?q=blue%20moon&limit=2");
        Assert.Equal(2, limited["results"]!.AsArray().Count);
        Assert.True(limited["truncated"]!.GetValue<bool>());
        Assert.False(all["truncated"]!.GetValue<bool>());
        var empty = await ReadClientLyricsJsonAsync(client, "/allstarr/v1/search/lyrics?q=unfindable");
        Assert.Empty(empty["results"]!.AsArray());
        Assert.False(empty["truncated"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ClientApi_LibraryLyricsSearchIndexesChangesInBackgroundAndEnforcesLibraryVisibility()
    {
        var recording = Guid.NewGuid();
        var calls = 0;
        var backend = """{"Metadata":{"IsSynced":false},"Lyrics":[{"Text":"Original library phrase"},{"Text":"Next line"}]}""";
        var libraries = new Mock<IBackendLibraryAccessResolver>();
        libraries.Setup(service => service.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BackendLibraryAccess(true, ["music"]));
        var sources = new Dictionary<string, ProviderLyricsResult?> { ["lrcred"] = null };
        using var factory = ClientLyricsFactory(recording, sources, readBackendLyrics: () => { calls++; return backend; }, configure: services =>
        {
            services.RemoveAll<IBackendLibraryAccessResolver>();
            services.AddSingleton(libraries.Object);
            services.Configure<JellyfinSettings>(options => { options.Url = "http://backend"; options.ApiKey = "fixture"; });
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var (owner, _) = await SeedClientLyricsAsync(factory, recording);
        var id = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var now = DateTimeOffset.UtcNow;
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync())
        {
            db.LibraryTracks.Add(new LibraryTrackRecord
            {
                Id = id,
                OwnerUserId = owner,
                BackendLibraryId = "music",
                Protocol = "jellyfin",
                BackendInstanceId = "primary",
                BackendItemId = "native",
                FilePath = "/fixture/song.flac",
                Title = "Fixture",
                Artist = "Artist",
                CanonicalRecordingId = recording,
                AlbumArtist = "Artist",
                DurationMilliseconds = 10000,
                DurationProvenance = "jellyfin",
                DurationRetrievedAt = now,
                SourceModifiedAt = now,
                UpdatedAt = now,
                IndexedAt = now,
                Revision = 1
            });
            db.LibraryTracks.Add(new LibraryTrackRecord
            {
                Id = Guid.Parse("00000000-0000-0000-0000-000000000001"),
                OwnerUserId = owner,
                BackendLibraryId = "music",
                Protocol = "jellyfin",
                BackendInstanceId = "primary",
                BackendItemId = "hidden",
                FilePath = "/fixture/hidden.flac",
                Title = "Hidden",
                Artist = "Artist",
                SourceModifiedAt = now,
                UpdatedAt = now,
                IndexedAt = now,
                Revision = 1
            });
            await db.SaveChangesAsync();
        }
        var worker = ActivatorUtilities.CreateInstance<LibraryLyricsIndexer>(factory.Services);
        var watch = Stopwatch.StartNew();
        await worker.SweepAsync(default);
        await worker.ProcessAsync(id, default);
        Assert.Equal(1, calls);
        var found = Assert.Single((await ReadClientLyricsJsonAsync(client, "/allstarr/v1/search/lyrics?q=original%20library"))["results"]!.AsArray())!;
        Assert.Equal("native", found["itemId"]!.GetValue<string>());
        Assert.Equal("native-album", found["albumId"]!.GetValue<string>());
        using var album = await client.GetAsync("/Items/native-album");
        Assert.Equal(HttpStatusCode.OK, album.StatusCode);
        using var resolved = await client.GetAsync("/Items/native");
        Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
        var context = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "primary", "listener",
            new AllstarrPrincipal(owner, "jellyfin", "primary", "listener", "Listener", true), "search-fixture", DateTimeOffset.UtcNow.AddMinutes(1), default);
        await factory.Services.GetRequiredService<LyricsSearchIndex>().StoreAsync(context,
            new Song { Id = "hidden", IsLocal = true, Title = "Hidden", Artist = "Artist" }, null,
            new(new("backend", "Library"), "plain", [new(null, null, "Original library phrase", "lead", [])]), sharedLibrary: true);
        Assert.Single((await ReadClientLyricsJsonAsync(client, "/allstarr/v1/search/lyrics?q=original%20library"))["results"]!.AsArray());
        libraries.Setup(service => service.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BackendLibraryAccess(true, []));
        Assert.Empty((await ReadClientLyricsJsonAsync(client, "/allstarr/v1/search/lyrics?q=original%20library"))["results"]!.AsArray());
        libraries.Setup(service => service.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BackendLibraryAccess(true, ["music"]));
        backend = """{"Metadata":{"IsSynced":false},"Lyrics":[{"Text":"Changed library phrase"}]}""";
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync())
        {
            var track = await db.LibraryTracks.SingleAsync(track => track.Id == id);
            track.UpdatedAt = now;
            track.SourceModifiedAt = now.AddSeconds(1);
            await db.SaveChangesAsync();
        }
        await worker.ProcessAsync(id, default);
        Assert.Equal(2, calls);
        Assert.True(watch.Elapsed >= LibraryLyricsIndexer.Pace * 2);
        Assert.Single((await ReadClientLyricsJsonAsync(client, "/allstarr/v1/search/lyrics?q=changed%20library"))["results"]!.AsArray());
        var old = await ReadClientLyricsJsonAsync(client, "/allstarr/v1/search/lyrics?q=original");
        Assert.Empty(old["results"]!.AsArray());
        await worker.ProcessAsync(id, default);
        Assert.Equal(2, calls);
        var cache = factory.Services.GetRequiredService<allstarr.Services.Common.IApplicationCache>();
        var key = "lyrics:v3:lrcred:" + CanonicalCatalogKeys.Hash(JsonSerializer.Serialize(new
        {
            Isrc = (string?)null,
            TrackTitle = "Fixture",
            ArtistNames = new[] { "Artist" },
            AlbumTitle = (string?)null,
            DurationSeconds = (int?)10
        }));
        var storedLyrics = new ProviderLyricsResult(ProviderLyricsAvailabilityState.Available, "lrcred",
            ProviderLyricsFormat.LineTimed, "[00:01.00]Cached sunrise", timedLines:
            [new("Cached sunrise", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(9))]);
        await cache.SetAsync(key, storedLyrics);
        sources["lrcred"] = storedLyrics;
        await worker.SweepAsync(default);
        var cached = Assert.Single((await ReadClientLyricsJsonAsync(client, "/allstarr/v1/search/lyrics?q=cached%20sunrise"))["results"]!.AsArray())!;
        Assert.Equal("lrcred", cached["source"]!["id"]!.GetValue<string>());
        Assert.Equal(1000, cached["line"]!["startMs"]!.GetValue<long>());
        Assert.Equal(10000, cached["line"]!["endMs"]!.GetValue<long>());
        Assert.Equal(2, calls);
        var rich = await ReadClientLyricsJsonAsync(client, "/allstarr/v1/items/native/lyrics");
        Assert.Equal(rich["lines"]![0]!["text"]!.GetValue<string>(), cached["line"]!["text"]!.GetValue<string>());
        Assert.Equal(rich["lines"]![0]!["endMs"]!.GetValue<long>(), cached["line"]!["endMs"]!.GetValue<long>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("?q=a")]
    [InlineData("?q=%20%20")]
    [InlineData("?q=valid&limit=0")]
    [InlineData("?q=valid&limit=51")]
    [InlineData("?q=valid&limit=1.5")]
    [InlineData("?q=valid&limit=oops")]
    public async Task ClientApi_LyricsSearchValidatesQueries(string query)
    {
        using var factory = ClientApiFactory(_ => Json(200, """{"Id":"listener"}"""));
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        using var response = await client.GetAsync("/allstarr/v1/search/lyrics" + query);
        await AssertClientApiError(response, 400, "invalid_request");
    }

    [Fact]
    public async Task ClientApi_LyricsSearchRejectsLongQueriesAndOffSwitchReturnsNotFound()
    {
        using var factory = ClientApiFactory(_ => Json(200, """{"Id":"listener"}"""));
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        using var longQuery = await client.GetAsync("/allstarr/v1/search/lyrics?q=" + new string('x', 201));
        await AssertClientApiError(longQuery, 400, "invalid_request");
        await SetClientApiEnabledAsync(factory, false);
        using var off = await client.GetAsync("/allstarr/v1/search/lyrics?q=valid");
        await AssertClientApiError(off, 404, "not_found");
    }
}
