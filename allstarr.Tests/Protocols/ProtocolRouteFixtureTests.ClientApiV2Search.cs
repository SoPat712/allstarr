using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using allstarr.Core.Capabilities;
using allstarr.Core.ClientApi;
using allstarr.Core.ClientApiV2;
using allstarr.Core.Identity;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Models.Search;
using allstarr.Models.Subsonic;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Fact]
    public async Task ClientApiV2_SearchBuildsFreshTracksWithoutStoredMetadataOrDetailRequests()
    {
        var gateway = SearchV2Gateway(new() { Songs = [V2BatchSong("fresh")] });
        using var factory = SearchV2Factory(gateway, _ => Json(200, """{"Items":[]}"""));
        using var icons = new ProviderIconFiles();
        using var client = CreateClientApiClient(factory);
        icons.Use(factory.Services);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        using var response = await client.GetAsync("/allstarr/v2/search?q=stored&types=songs");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var final = SearchV2Lines(await response.Content.ReadAsStringAsync())[^1];
        var track = Assert.Single(final["sections"]!["songs"]!.AsArray())!["track"]!;
        Assert.Equal(V2Ids.TrackSource("ext-deezer-song-fresh"), track["id"]!.GetValue<string>());
        Assert.Equal("Stored song", track["title"]!.GetValue<string>());
        Assert.Equal(200000, track["durationMs"]!.GetValue<long>());
        gateway.Verify(item => item.GetStoredSongAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        gateway.Verify(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ClientApiV2_SearchStreamsTypedRankedResourcesAndNativeTracks()
    {
        var result = new SearchResult
        {
            Songs = [V2BatchSong("known")],
            Albums = [new() { Id = "ext-deezer-album-7", Title = "Stored album", Artist = "Stored artist", ArtistId = "ext-deezer-artist-9", Year = 2020 }],
            Artists = [new() { Id = "ext-deezer-artist-9", Name = "Stored artist" }]
        };
        var gateway = SearchV2Gateway(result, [new() { Id = "ext-deezer-playlist-8", Name = "Stored playlist", CuratorName = "Curator", Provider = "deezer", TrackCount = 4 }]);
        using var factory = SearchV2Factory(gateway, request =>
        {
            var query = QueryHelpers.ParseQuery(request.RequestUri!.Query);
            if (!query.ContainsKey("SearchTerm")) return Json(200, V2LibrarySong);
            return Json(200, """
                {"Items":[{"Id":"native1","Name":"Library song","Type":"Audio","Artists":["Library artist"],"Album":"Library album","RunTimeTicks":2000000000},
                {"Id":"album1","Name":"Library album","Type":"MusicAlbum","AlbumArtists":[{"Id":"artist1","Name":"Library artist"}],"ProductionYear":2021},
                {"Id":"artist1","Name":"Library artist","Type":"MusicArtist"},
                {"Id":"private-listener","Name":"Library playlist","Type":"Playlist","ChildCount":3}]}
                """);
        });
        using var icons = new ProviderIconFiles();
        using var client = CreateClientApiClient(factory);
        icons.Use(factory.Services);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        var recording = await SeedClientDetailsAsync(factory, "listener", "known", personal: false);
        using var response = await client.GetAsync("/allstarr/v2/search?q=stored&limit=10&unused=ignored");
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, text);
        Assert.Equal("application/x-ndjson", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("no", response.Headers.GetValues("X-Accel-Buffering").Single());
        var snapshots = SearchV2Lines(text);
        Assert.Equal(Enumerable.Range(1, snapshots.Length), snapshots.Select(snapshot => snapshot["revision"]!.GetValue<int>()));
        var final = snapshots[^1];
        Assert.True(final["done"]!.GetValue<bool>());
        Assert.Empty(final["late"]!.AsArray());
        var sections = final["sections"]!;
        Assert.Equal(4, sections.AsObject().Count);
        var song = sections["songs"]!.AsArray().Single(hit => hit!["track"]!["isExternal"]!.GetValue<bool>())!;
        Assert.Equal("track", song["kind"]!.GetValue<string>());
        Assert.Equal(V2Ids.Track(recording), song["id"]!.GetValue<string>());
        Assert.Equal(song["id"]!.GetValue<string>(), song["track"]!["id"]!.GetValue<string>());
        Assert.Equal(200000, song["track"]!["durationMs"]!.GetValue<long>());
        Assert.NotNull(song["track"]!["keep"]);
        Assert.NotNull(song["track"]!["lyrics"]);
        Assert.Contains(sections["songs"]!.AsArray(), hit => hit!["track"]!["libraryItemId"]?.GetValue<string>() == "native1");
        Assert.Contains(sections["albums"]!.AsArray(), hit => hit!["album"]!["id"]!.GetValue<string>() == V2Ids.Album("ext-deezer-album-7"));
        Assert.Contains(sections["albums"]!.AsArray(), hit => hit!["album"]!["year"]?.GetValue<int>() == 2021);
        Assert.Contains(sections["artists"]!.AsArray(), hit => hit!["artist"]!["id"]!.GetValue<string>() == V2Ids.Artist("ext-deezer-artist-9"));
        var playlist = sections["playlists"]!.AsArray().Single(hit => hit!["playlist"]!["isExternal"]!.GetValue<bool>())!["playlist"]!;
        Assert.Equal("Stored playlist", playlist["name"]!.GetValue<string>());
        Assert.Equal("Curator", playlist["curator"]!.GetValue<string>());
        Assert.Equal(4, playlist["trackCount"]!.GetValue<int>());
        Assert.Contains(sections.AsObject().SelectMany(section => section.Value!.AsArray()), hit => hit!["id"]!.GetValue<string>() == final["top"]!["id"]!.GetValue<string>());
        Assert.True(final["items"]!.AsArray().Count <= 10);
        AssertSearchV2CamelCase(final);
        var excluded = await ReadV2Batch(client, "/allstarr/v2/search?q=stored");
        Assert.Equal(404, excluded["status"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("/allstarr/v2/search")]
    [InlineData("/allstarr/v2/search?q=%20")]
    [InlineData("/allstarr/v2/search?q=stored&limit=0")]
    [InlineData("/allstarr/v2/search?q=stored&limit=201")]
    [InlineData("/allstarr/v2/search?q=stored&limit=bad")]
    [InlineData("/allstarr/v2/search?q=stored&types=Audio")]
    [InlineData("/allstarr/v2/search?q=stored&types=")]
    [InlineData("/allstarr/v2/search?q=stored&types=songs,")]
    [InlineData("/allstarr/v2/search?q=stored&types=songs,songs")]
    [InlineData("/allstarr/v2/search?q=stored&q=other")]
    [InlineData("/allstarr/v2/search/lyrics")]
    [InlineData("/allstarr/v2/search/lyrics?q=a")]
    [InlineData("/allstarr/v2/search/lyrics?q=stored&limit=0")]
    [InlineData("/allstarr/v2/search/lyrics?q=stored&limit=51")]
    [InlineData("/allstarr/v2/search/lyrics?q=stored&limit=bad")]
    [InlineData("/allstarr/v2/search/lyrics?q=stored&q=other")]
    public async Task ClientApiV2_SearchRejectsInvalidParameters(string path)
    {
        using var factory = SearchV2Factory(SearchV2Gateway());
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        using var response = await client.GetAsync(path);
        await AssertClientApiError(response, 400, "invalid_request");
        if (path.StartsWith("/allstarr/v2/search/lyrics", StringComparison.Ordinal))
            Assert.Equal(400, (await ReadV2Batch(client, path))["status"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("/allstarr/v2/search?q=stored", "none", 401)]
    [InlineData("/allstarr/v2/search?q=stored", "query", 401)]
    [InlineData("/allstarr/v2/search?q=stored", "invalid", 401)]
    [InlineData("/allstarr/v2/search?q=stored", "unbound", 403)]
    [InlineData("/allstarr/v2/search/lyrics?q=stored", "none", 401)]
    [InlineData("/allstarr/v2/search/lyrics?q=stored", "query", 401)]
    [InlineData("/allstarr/v2/search/lyrics?q=stored", "invalid", 401)]
    [InlineData("/allstarr/v2/search/lyrics?q=stored", "unbound", 403)]
    public async Task ClientApiV2_SearchRequiresHeaderAuthenticationAndABoundListener(string path, string authentication, int status)
    {
        using var factory = ClientDetailsFactory(_ => Json(400, "{}"));
        using var client = CreateClientApiClient(factory);
        if (authentication == "query") path += "&api_key=listener-token";
        if (authentication == "invalid") client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer invalid token");
        if (authentication == "unbound") client.DefaultRequestHeaders.Authorization = new("Bearer", "unbound-key");
        using var response = await client.GetAsync(path);
        await AssertClientApiError(response, status, status == 401 ? "unauthorized" : "forbidden");
    }

    [Fact]
    public async Task ClientApiV2_SearchPlaylistsStayWithinEachListenersLibrary()
    {
        using var factory = SearchV2Factory(SearchV2Gateway());
        using var client = CreateClientApiClient(factory);
        foreach (var listener in new[] { "listener", "other" })
        {
            client.DefaultRequestHeaders.Authorization = new("Bearer", listener + "-token");
            using var response = await client.GetAsync("/allstarr/v2/search?q=private&types=playlists");
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.OK, text);
            var final = SearchV2Lines(text)[^1];
            var section = Assert.Single(final["sections"]!.AsObject());
            Assert.Equal("playlists", section.Key);
            Assert.Equal(V2Ids.Playlist("private-" + listener), Assert.Single(section.Value!.AsArray())!["playlist"]!["id"]!.GetValue<string>());
            Assert.DoesNotContain(V2Ids.Playlist("private-" + (listener == "listener" ? "other" : "listener")), text);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClientApiV2_SearchPreservesArtistSourcesAndTypesLinkedAndNamesakeIds(bool linked)
    {
        var links = new Mock<ILibraryArtistLinks>();
        links.Setup(item => item.ReadSavedAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(linked ? new Dictionary<string, IReadOnlyList<LibraryArtistLink>> { ["Same name"] = [new("deezer", "same")] } : []);
        var gateway = SearchV2Gateway(new()
        {
            Artists = [new() { Id = "ext-deezer-artist-same", Name = "Same name", ExternalProvider = "deezer", ExternalId = "same" }]
        });
        using var factory = SearchV2Factory(gateway, _ => Json(200, """{"Items":[{"Id":"library-artist","Name":"Same name","Type":"MusicArtist"}]}"""), services =>
        {
            services.RemoveAll<ILibraryArtistLinks>();
            services.AddSingleton(links.Object);
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        using var response = await client.GetAsync("/allstarr/v2/search?q=same%20name&types=artists");
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, text);
        var artists = SearchV2Lines(text)[^1]["sections"]!["artists"]!.AsArray();
        var library = artists.Single(hit => hit!["id"]!.GetValue<string>() == V2Ids.Artist("library-artist"))!;
        if (linked)
        {
            Assert.Single(artists);
            Assert.Equal("allstarr", library["source"]!.GetValue<string>());
            Assert.Equal("Allstarr", library["sourceName"]!.GetValue<string>());
            var parts = library["artist"]!["alsoOn"]!.AsArray();
            Assert.Equal(new[] { V2Ids.Artist("library-artist"), V2Ids.Artist("ext-deezer-artist-same") }, parts.Select(part => part!["id"]!.GetValue<string>()));
            Assert.Equal(new[] { "jellyfin", "deezer" }, parts.Select(part => part!["source"]!.GetValue<string>()));
            Assert.All(parts, part => Assert.False(string.IsNullOrWhiteSpace(part!["sourceName"]!.GetValue<string>())));
        }
        else
        {
            Assert.Equal(2, artists.Count);
            var service = artists.Single(hit => hit!["id"]!.GetValue<string>() == V2Ids.Artist("ext-deezer-artist-same"))!;
            Assert.Equal("deezer", service["source"]!.GetValue<string>());
            Assert.Equal("jellyfin", library["source"]!.GetValue<string>());
            Assert.Equal(V2Ids.Artist("library-artist"), Assert.Single(service["artist"]!["namesakes"]!.AsArray())!["id"]!.GetValue<string>());
            Assert.Empty(service["artist"]!["alsoOn"]!.AsArray());
        }
        AssertSearchV2CamelCase(SearchV2Lines(text)[^1]);
    }

    [Fact]
    public async Task ClientApiV2_LyricsSearchReturnsNativeTracksHighlightsConditionalReadsAndBatchResults()
    {
        using var factory = SearchV2Factory(SearchV2Gateway());
        using var icons = new ProviderIconFiles();
        using var client = CreateClientApiClient(factory);
        icons.Use(factory.Services);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        var recording = await SeedClientDetailsAsync(factory, "listener", "known", personal: false);
        await SeedClientDetailsAsync(factory, "listener", "next", personal: false);
        var foreign = await SeedClientDetailsAsync(factory, "other", "foreign", personal: false);
        await StoreSearchV2Lyrics(factory, "listener", "known", "😀 Silver moon rises");
        await StoreSearchV2Lyrics(factory, "listener", "next", "Silver moon later");
        await StoreSearchV2Lyrics(factory, "other", "foreign", "Silver moon privately");
        const string path = "/allstarr/v2/search/lyrics?q=silver%20moon&limit=1";
        using var response = await client.GetAsync(path);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, text);
        var body = JsonNode.Parse(text)!;
        var hit = Assert.Single(body["results"]!.AsArray())!;
        Assert.Equal(V2Ids.Track(recording), hit["track"]!["id"]!.GetValue<string>());
        Assert.Equal("Stored song", hit["track"]!["title"]!.GetValue<string>());
        Assert.Equal("😀 Silver moon rises", hit["line"]!["text"]!.GetValue<string>());
        Assert.Equal(1000, hit["line"]!["startMs"]!.GetValue<long>());
        Assert.Equal(2000, hit["line"]!["endMs"]!.GetValue<long>());
        Assert.True(hit["line"]!["synced"]!.GetValue<bool>());
        Assert.Equal(3, hit["highlights"]![0]!["start"]!.GetValue<int>());
        Assert.Equal("kept", hit["source"]!["id"]!.GetValue<string>());
        Assert.True(body["truncated"]!.GetValue<bool>());
        AssertSearchV2CamelCase(body);
        var batch = await ReadV2Batch(client, path);
        Assert.Equal(200, batch["status"]!.GetValue<int>());
        Assert.True(JsonNode.DeepEquals(body, batch["body"]));
        Assert.Equal(response.Headers.ETag!.ToString(), batch["etag"]!.GetValue<string>());
        await AssertV2NotModified(client, path, response.Headers.ETag.ToString());
        Assert.Equal(304, (await ReadV2Batch(client, path, response.Headers.ETag.ToString()))["status"]!.GetValue<int>());
        client.DefaultRequestHeaders.Authorization = new("Bearer", "other-token");
        using var other = await client.GetAsync(path);
        var own = JsonNode.Parse(await other.Content.ReadAsStringAsync())!;
        Assert.Equal(V2Ids.Track(foreign), Assert.Single(own["results"]!.AsArray())!["track"]!["id"]!.GetValue<string>());
        Assert.DoesNotContain(V2Ids.Track(recording), own.ToJsonString());
        Assert.False(own["truncated"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ClientApiV2_SearchFirstSnapshotMatchesV1SpeedWithDelayedProviderAndDetailReads()
    {
        var gateway = SearchV2Gateway(new() { Songs = [V2BatchSong("known")] }, delayMs: 1100);
        gateway.Setup(item => item.GetStoredSongAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(async (ProtocolExecutionContext execution, string _, string id) =>
            {
                await Task.Delay(1500, execution.CancellationToken);
                return V2BatchSong(id);
            });
        gateway.Setup(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(async (ProtocolExecutionContext execution, string _, string id) =>
            {
                await Task.Delay(1500, execution.CancellationToken);
                return V2BatchSong(id);
            });
        using var factory = SearchV2Factory(gateway, _ => Json(200, """{"Items":[]}"""));
        using var icons = new ProviderIconFiles();
        using var client = CreateClientApiClient(factory);
        icons.Use(factory.Services);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        await SeedClientDetailsAsync(factory, "listener", "known", personal: false);
        var legacy = await First("/allstarr/v1/search?q=stored&limit=20&shape=ranked", false);
        var native = await First("/allstarr/v2/search?q=stored&limit=20", true);
        var measurement = $"Search first snapshot: v1={legacy:F1}ms; v2={native:F1}ms; delta={native - legacy:F1}ms";
        _lyricsSearchOutput.WriteLine(measurement);
        Console.WriteLine(measurement);
        Assert.True(native < 900, $"First v2 snapshot took {native:F1}ms");
        Assert.True(native <= legacy + 200, $"First snapshots: v1={legacy:F1}ms; v2={native:F1}ms");
        gateway.Verify(item => item.GetStoredSongAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        gateway.Verify(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);

        async Task<double> First(string path, bool v2)
        {
            var watch = Stopwatch.StartNew();
            using var response = await client.GetAsync(path, HttpCompletionOption.ResponseHeadersRead);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
            var first = JsonNode.Parse((await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2)))!)!;
            var elapsed = watch.Elapsed.TotalMilliseconds;
            Assert.False(first["done"]!.GetValue<bool>());
            var song = Assert.Single(first["sections"]!["songs"]!.AsArray())!;
            Assert.NotNull(song[v2 ? "track" : "Id"]);
            var final = SearchV2Lines(await reader.ReadToEndAsync())[^1];
            Assert.True(final["done"]!.GetValue<bool>());
            return elapsed;
        }
    }

    private static Mock<IProtocolProviderGateway> SearchV2Gateway(SearchResult? result = null, IReadOnlyList<ExternalPlaylist>? playlists = null, int delayMs = 0)
    {
        var gateway = V2BatchGateway();
        gateway.Setup(item => item.GetProviderOrder(It.IsAny<ProviderCapabilityKind>())).Returns(["deezer", "qobuz"]);
        gateway.Setup(item => item.GetStreamingProviderAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync("deezer");
        gateway.Setup(item => item.StreamSearchAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<Func<string, SearchResult, IReadOnlyList<ExternalPlaylist>, CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns(async (ProtocolExecutionContext _, string _, int _, bool songs, bool albums, bool artists, bool lists,
                Func<string, SearchResult, IReadOnlyList<ExternalPlaylist>, CancellationToken, Task> write, CancellationToken token) =>
            {
                await write("deezer", new()
                {
                    Songs = songs ? result?.Songs ?? [] : [],
                    Albums = albums ? result?.Albums ?? [] : [],
                    Artists = artists ? result?.Artists ?? [] : []
                }, lists ? playlists ?? [] : [], token);
                if (delayMs > 0) await Task.Delay(delayMs, token);
                await write("qobuz", new(), [], token);
                return (IReadOnlyList<string>)[];
            });
        return gateway;
    }

    private static ProtocolFactory SearchV2Factory(Mock<IProtocolProviderGateway> gateway,
        Func<HttpRequestMessage, HttpResponseMessage>? library = null, Action<IServiceCollection>? configure = null) => ClientDetailsFactory(request =>
    {
        if (request.RequestUri!.AbsolutePath == "/Users/Me")
            return Json(200, request.Headers.GetValues("Authorization").Single().Contains("other-token", StringComparison.Ordinal)
                ? """{"Id":"other"}""" : """{"Id":"listener"}""");
        if (request.RequestUri.AbsolutePath is "/Users/listener/Items" or "/Users/other/Items")
        {
            if (library != null) return library(request);
            var listener = request.RequestUri.AbsolutePath.Contains("/other/", StringComparison.Ordinal) ? "other" : "listener";
            return Json(200, JsonSerializer.Serialize(new { Items = new[] { new { Id = "private-" + listener, Name = "Private playlist", Type = "Playlist" } } }));
        }
        return Json(200, """{"Items":[]}""");
    }, services =>
    {
        services.RemoveAll<IProtocolProviderGateway>();
        services.AddSingleton(gateway.Object);
        var index = new Mock<ILibraryIndexService>();
        index.Setup(item => item.FindKnownRecordingTracksAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<IReadOnlyList<LibraryRecordingLookup>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string>());
        services.RemoveAll<ILibraryIndexService>();
        services.AddSingleton(index.Object);
        configure?.Invoke(services);
    });

    private static async Task StoreSearchV2Lyrics(ProtocolFactory factory, string listener, string id, string text)
    {
        var owner = (await factory.Services.GetRequiredService<BackendIdentityResolver>().ResolveAsync(new("Jellyfin", listener)))!.UserId;
        var execution = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "primary", listener,
            new AllstarrPrincipal(owner, "jellyfin", "primary", listener, listener, false), "search-fixture", DateTimeOffset.UtcNow.AddMinutes(1), default);
        await factory.Services.GetRequiredService<LyricsSearchIndex>().StoreAsync(execution, V2BatchSong(id), null,
            new(new("kept", "Kept lyrics"), "line", [new(1000, 2000, text, "lead", []), new(2000, 3000, "Second verse", "lead", [])]));
    }

    private static JsonNode[] SearchV2Lines(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!).ToArray();

    private static void AssertSearchV2CamelCase(JsonNode node)
    {
        if (node is JsonObject map)
            foreach (var pair in map)
            {
                Assert.True(pair.Key.Length > 0 && !char.IsUpper(pair.Key[0]), pair.Key);
                if (pair.Value != null) AssertSearchV2CamelCase(pair.Value);
            }
        if (node is JsonArray values)
            foreach (var value in values.OfType<JsonNode>()) AssertSearchV2CamelCase(value);
    }
}
