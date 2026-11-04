using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Matching;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Fact]
    public async Task ClientApi_LyricsSourcesReturnPendingStatesWithoutStartingAnotherLookup()
    {
        var recording = Guid.NewGuid();
        var resolver = new Mock<IProtocolLyricsResolver>(MockBehavior.Strict);
        resolver.Setup(item => item.FindAllAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<Song>(), It.IsAny<string>(), It.IsAny<ProtocolLyricsSources>()))
            .ReturnsAsync(new[]
            {
                new ProtocolLyricsCandidate("lyricsplus", null) { State = "pending" },
                new ProtocolLyricsCandidate("lrclib", new allstarr.Models.Lyrics.LyricsInfo { Source = "lrclib", SyncedLyrics = "[00:01]Known words" }),
                new ProtocolLyricsCandidate("genius", null)
            });
        using var factory = ClientLyricsFactory(recording, new(), configure: services =>
        {
            services.RemoveAll<IProtocolLyricsResolver>();
            services.AddSingleton(resolver.Object);
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        await SeedClientLyricsAsync(factory, recording);
        var result = await ReadClientLyricsJsonAsync(client, "/allstarr/v1/items/ext-deezer-song-lyrics/lyrics/sources");
        var sources = result["sources"]!.AsArray();
        Assert.Equal(new[] { "pending", "ready", "unavailable" }, sources.Select(source => source!["state"]!.GetValue<string>()));
        Assert.Null(sources[0]!["format"]);
        Assert.Equal("lrclib", result["chosen"]!["sourceId"]!.GetValue<string>());
        resolver.Verify(item => item.FindAllAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<Song>(), It.IsAny<string>(), It.IsAny<ProtocolLyricsSources>()), Times.Once);
        resolver.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("word", false)]
    [InlineData("word", true)]
    [InlineData("line", false)]
    [InlineData("plain", false)]
    public async Task ClientApi_LyricsPreservesTimingAndVocalParts(string format, bool native)
    {
        var recording = Guid.NewGuid();
        using var factory = ClientLyricsFactory(recording, new() { ["lyricsplus"] = ClientLyricsResult(format) });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        await SeedClientLyricsAsync(factory, recording);
        using var response = await client.GetAsync($"/allstarr/v1/items/{(native ? "native" : "ext-deezer-song-lyrics")}/lyrics");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        Assert.Equal("lyricsplus", root.GetProperty("source").GetProperty("id").GetString());
        Assert.Equal("LyricsPlus", root.GetProperty("source").GetProperty("name").GetString());
        Assert.Equal(format, root.GetProperty("format").GetString());
        var lines = root.GetProperty("lines");
        if (format == "plain")
        {
            Assert.All(lines.EnumerateArray(), line =>
            {
                Assert.Equal(JsonValueKind.Null, line.GetProperty("startMs").ValueKind);
                Assert.Equal(JsonValueKind.Null, line.GetProperty("endMs").ValueKind);
                Assert.Empty(line.GetProperty("words").EnumerateArray());
            });
        }
        else if (format == "line")
        {
            Assert.Equal(1000, lines[0].GetProperty("startMs").GetInt64());
            Assert.Equal(2000, lines[0].GetProperty("endMs").GetInt64());
            Assert.All(lines.EnumerateArray(), line => Assert.Empty(line.GetProperty("words").EnumerateArray()));
        }
        else
        {
            Assert.Equal(1000, lines[0].GetProperty("startMs").GetInt64());
            Assert.Equal(1800, lines[0].GetProperty("endMs").GetInt64());
            var word = Assert.Single(lines[0].GetProperty("words").EnumerateArray());
            Assert.Equal("Lead", word.GetProperty("text").GetString());
            Assert.Equal(1000, word.GetProperty("startMs").GetInt64());
            Assert.Equal(1800, word.GetProperty("endMs").GetInt64());
            var background = Assert.Single(lines.EnumerateArray(), line => line.GetProperty("part").GetString() == "background");
            Assert.Equal("v2", background.GetProperty("voice").GetString());
            Assert.Equal("Second singer", background.GetProperty("singer").GetString());
            var backingWord = Assert.Single(background.GetProperty("words").EnumerateArray());
            Assert.Equal("Echo", backingWord.GetProperty("text").GetString());
            Assert.Equal(1200, backingWord.GetProperty("startMs").GetInt64());
            Assert.Equal(1600, backingWord.GetProperty("endMs").GetInt64());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClientApi_LyricsSourcesPinPersonallyAndMatchJellyfin(bool native)
    {
        var recording = Guid.NewGuid();
        using var factory = ClientLyricsFactory(recording, new()
        {
            ["unison"] = ClientLyricsResult("plain"),
            ["lrclib"] = ClientLyricsResult("line"),
            ["lyricsplus"] = ClientLyricsResult("word")
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var (owner, other) = await SeedClientLyricsAsync(factory, recording);
        var path = $"/allstarr/v1/items/{(native ? "native" : "ext-deezer-song-lyrics")}/lyrics";
        var automatic = await ReadClientLyricsJsonAsync(client, path + "/sources");
        Assert.Equal("automatic", automatic["chosen"]!["layer"]!.GetValue<string>());
        Assert.Equal("lyricsplus", automatic["chosen"]!["sourceId"]!.GetValue<string>());
        var available = automatic["sources"]!.AsArray().Where(source => source!["format"] != null).ToArray();
        Assert.Equal(new[] { "plain", "line", "word" }, available.Select(source => source!["format"]!.GetValue<string>()));
        Assert.All(available, source => Assert.InRange(source!["preview"]!.GetValue<string>().Split('\n').Length, 1, 4));
        Assert.DoesNotContain(other.ToString(), automatic.ToJsonString());
        Assert.Null(automatic["revision"]!["personal"]);

        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync())
        {
            db.RecordingLyricsChoices.Add(new() { Id = Guid.NewGuid(), CanonicalRecordingId = recording, SourceId = "unison", Revision = 1 });
            await db.SaveChangesAsync();
        }
        var household = await ReadClientLyricsJsonAsync(client, path + "/sources");
        Assert.Equal("household", household["chosen"]!["layer"]!.GetValue<string>());
        using var pinned = await client.PutAsJsonAsync(path + "/source", new { sourceId = "lrclib", expectedRevision = household["revision"], scope = "household", ownerUserId = other });
        Assert.Equal(HttpStatusCode.OK, pinned.StatusCode);
        var personal = JsonNode.Parse(await pinned.Content.ReadAsStringAsync())!;
        Assert.Equal("personal", personal["chosen"]!["layer"]!.GetValue<string>());
        Assert.Equal("lrclib", personal["chosen"]!["sourceId"]!.GetValue<string>());
        var sources = await ReadClientLyricsJsonAsync(client, path + "/sources");
        Assert.Equal("personal", sources["chosen"]!["layer"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(personal["revision"], sources["revision"]));
        var rich = await ReadClientLyricsJsonAsync(client, path);
        Assert.Equal("lrclib", rich["source"]!["id"]!.GetValue<string>());
        var jellyfin = await ReadClientLyricsJsonAsync(client, $"/Audio/{(native ? "native" : "ext-deezer-song-lyrics")}/Lyrics");
        Assert.Equal(rich["lines"]![0]!["text"]!.GetValue<string>(), jellyfin["Lyrics"]![0]!["Text"]!.GetValue<string>());
        Assert.Equal(rich["lines"]![0]!["startMs"]!.GetValue<long>() * 10000, jellyfin["Lyrics"]![0]!["Start"]!.GetValue<long>());

        using var staleSet = await client.PutAsJsonAsync(path + "/source", new { sourceId = "lyricsplus", expectedRevision = household["revision"] });
        await AssertClientApiError(staleSet, 409, "conflict");
        using var staleClear = await client.PostAsJsonAsync(path + "/source/clear", new { expectedRevision = household["revision"] });
        await AssertClientApiError(staleClear, 409, "conflict");
        using var cleared = await client.PostAsJsonAsync(path + "/source/clear", new { expectedRevision = personal["revision"], scope = "household", ownerUserId = other });
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        var after = JsonNode.Parse(await cleared.Content.ReadAsStringAsync())!;
        Assert.Null(after["revision"]!["personal"]);
        Assert.Equal("household", after["chosen"]!["layer"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(household["revision"]!["household"], after["revision"]!["household"]));
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync())
        {
            var active = await db.RecordingLyricsChoices.Where(choice => choice.RevokedAt == null).ToArrayAsync();
            Assert.DoesNotContain(active, choice => choice.OwnerUserId == owner);
            Assert.Equal("lyricsplus", Assert.Single(active, choice => choice.OwnerUserId == other).SourceId);
            Assert.Equal(1, Assert.Single(active, choice => choice.OwnerUserId == other).Revision);
            Assert.Equal("unison", Assert.Single(active, choice => choice.OwnerUserId == null).SourceId);
        }
    }

    [Theory]
    [InlineData("hidden")]
    [InlineData("unknown")]
    [InlineData("wrong-id")]
    [InlineData("folder")]
    [InlineData("ext-deezer-song-missing")]
    public async Task ClientApi_LyricsUnavailableItemsNeverExposeOrChangeChoices(string itemId)
    {
        var recording = Guid.NewGuid();
        using var factory = ClientLyricsFactory(recording, new() { ["lyricsplus"] = ClientLyricsResult("word") });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        await SeedClientLyricsAsync(factory, recording);
        var path = $"/allstarr/v1/items/{itemId}/lyrics";
        using var rich = await client.GetAsync(path);
        await AssertClientApiError(rich, 404, "not_found");
        using var sources = await client.GetAsync(path + "/sources");
        await AssertClientApiError(sources, 404, "not_found");
        using var preview = await client.GetAsync(path + "?sourceId=lrclib:101");
        await AssertClientApiError(preview, 404, "not_found");
        using var pin = await client.PutAsJsonAsync(path + "/source", new { sourceId = "lyricsplus", expectedRevision = new { } });
        await AssertClientApiError(pin, 404, "not_found");
        using var clear = await client.PostAsJsonAsync(path + "/source/clear", new { expectedRevision = new { } });
        await AssertClientApiError(clear, 404, "not_found");
    }

    [Fact]
    public async Task ClientApi_LyricsSourcesResolveStoredExternalIdentity()
    {
        var recording = Guid.NewGuid();
        using var factory = ClientLyricsFactory(recording, new() { ["lyricsplus"] = ClientLyricsResult("word") }, externalIdentityFallback: true);
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        await SeedClientLyricsAsync(factory, recording);
        const string path = "/allstarr/v1/items/ext-deezer-song-lyrics/lyrics";
        var sources = await ReadClientLyricsJsonAsync(client, path + "/sources");
        Assert.Equal("automatic", sources["chosen"]!["layer"]!.GetValue<string>());
        using var pin = await client.PutAsJsonAsync(path + "/source", new { sourceId = "lyricsplus", expectedRevision = sources["revision"] });
        Assert.Equal(HttpStatusCode.OK, pin.StatusCode);
        var personal = JsonNode.Parse(await pin.Content.ReadAsStringAsync())!;
        Assert.Equal("personal", personal["chosen"]!["layer"]!.GetValue<string>());
        var rich = await ReadClientLyricsJsonAsync(client, path);
        Assert.Equal("lyricsplus", rich["source"]!["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task ClientApi_LyricsClearRestoresAutomaticAndUnavailableSourcesConflict()
    {
        var recording = Guid.NewGuid();
        using var factory = ClientLyricsFactory(recording, new()
        {
            ["unison"] = ClientLyricsResult("plain"),
            ["lyricsplus"] = ClientLyricsResult("word"),
            ["lrclib"] = null
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        await SeedClientLyricsAsync(factory, recording);
        const string path = "/allstarr/v1/items/ext-deezer-song-lyrics/lyrics";
        foreach (var sourceId in new[] { "lrclib", "missing" })
        {
            using var unavailable = await client.PutAsJsonAsync(path + "/source", new { sourceId, expectedRevision = new { } });
            await AssertClientApiError(unavailable, 409, "conflict");
        }
        using var pinned = await client.PutAsJsonAsync(path + "/source", new { sourceId = "unison", expectedRevision = new { } });
        Assert.Equal(HttpStatusCode.OK, pinned.StatusCode);
        var personal = JsonNode.Parse(await pinned.Content.ReadAsStringAsync())!;
        Assert.Equal("personal", personal["chosen"]!["layer"]!.GetValue<string>());
        Assert.Equal("unison", personal["chosen"]!["sourceId"]!.GetValue<string>());
        using var clear = await client.PostAsJsonAsync(path + "/source/clear", new { expectedRevision = personal["revision"] });
        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
        var automatic = JsonNode.Parse(await clear.Content.ReadAsStringAsync())!;
        Assert.Equal("automatic", automatic["chosen"]!["layer"]!.GetValue<string>());
        Assert.Equal("lyricsplus", automatic["chosen"]!["sourceId"]!.GetValue<string>());
        Assert.Null(automatic["revision"]!["personal"]);
        Assert.Null(automatic["revision"]!["household"]);
    }

    [Fact]
    public async Task ClientApi_LyricsWithoutContentReturnsNotFound()
    {
        var recording = Guid.NewGuid();
        using var factory = ClientLyricsFactory(recording, new() { ["lyricsplus"] = null });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        await SeedClientLyricsAsync(factory, recording);
        using var response = await client.GetAsync("/allstarr/v1/items/ext-deezer-song-lyrics/lyrics");
        await AssertClientApiError(response, 404, "not_found");
        var sources = await ReadClientLyricsJsonAsync(client, "/allstarr/v1/items/ext-deezer-song-lyrics/lyrics/sources");
        Assert.Null(sources["chosen"]);
        Assert.Null(sources["sources"]![0]!["format"]);
    }

    [Fact]
    public async Task ClientApi_LyricsReadsNativeBackendWithoutCreatingARecording()
    {
        const string backend = """{"Metadata":{"IsSynced":true,"Length":100000000},"Lyrics":[{"Start":10000000,"Text":"Library first"},{"Start":20000000,"Text":"Library second"}]}""";
        using var factory = ClientLyricsFactory(Guid.NewGuid(), new(), indexed: false, backendLyrics: backend);
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var rich = await ReadClientLyricsJsonAsync(client, "/allstarr/v1/items/native/lyrics");
        Assert.Equal("backend", rich["source"]!["id"]!.GetValue<string>());
        Assert.Equal("line", rich["format"]!.GetValue<string>());
        Assert.Equal(1000, rich["lines"]![0]!["startMs"]!.GetValue<long>());
        Assert.Equal(2000, rich["lines"]![0]!["endMs"]!.GetValue<long>());
        Assert.Equal("Library first", rich["lines"]![0]!["text"]!.GetValue<string>());
        var jellyfin = await ReadClientLyricsJsonAsync(client, "/Audio/native/Lyrics");
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(backend), jellyfin));
        var sources = await ReadClientLyricsJsonAsync(client, "/allstarr/v1/items/native/lyrics/sources");
        var source = Assert.Single(sources["sources"]!.AsArray(), candidate => candidate!["sourceId"]!.GetValue<string>() == "backend")!;
        Assert.Equal("ready", source["state"]!.GetValue<string>());
        Assert.Equal("line", source["format"]!.GetValue<string>());
        Assert.Equal("Library first\nLibrary second", source["preview"]!.GetValue<string>());
        Assert.Equal("backend", sources["chosen"]!["sourceId"]!.GetValue<string>());
        Assert.Equal("automatic", sources["chosen"]!["layer"]!.GetValue<string>());
        Assert.Null(sources["revision"]!["personal"]);
        Assert.Null(sources["revision"]!["household"]);
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync();
        Assert.Empty(await db.CanonicalRecordings.ToArrayAsync());
    }

    [Fact]
    public async Task ClientApi_LyricsOffSwitchBlocksReadsAndWritesBeforeAuthentication()
    {
        using var factory = ClientApiFactory(_ => throw new InvalidOperationException("Unexpected upstream request"));
        using var client = CreateClientApiClient(factory, enabled: false);
        const string path = "/allstarr/v1/items/native/lyrics";
        using var rich = await client.GetAsync(path);
        await AssertClientApiError(rich, 404, "not_found");
        using var sources = await client.GetAsync(path + "/sources");
        await AssertClientApiError(sources, 404, "not_found");
        using var preview = await client.GetAsync(path + "?sourceId=lrclib:101");
        await AssertClientApiError(preview, 404, "not_found");
        using var pin = await client.PutAsJsonAsync(path + "/source", new { sourceId = "lrclib", expectedRevision = new { } });
        await AssertClientApiError(pin, 404, "not_found");
        using var clear = await client.PostAsJsonAsync(path + "/source/clear", new { expectedRevision = new { } });
        await AssertClientApiError(clear, 404, "not_found");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"sourceId\":\"lrclib\"}")]
    [InlineData("{\"sourceId\":\"lrclib\",\"expectedRevision\":null}")]
    [InlineData("{\"sourceId\":\"lrclib\",\"expectedRevision\":{\"personal\":{\"id\":\"11111111-1111-4111-8111-111111111111\",\"revision\":\"1\"}}}")]
    [InlineData("{\"sourceId\":\"lrclib\",\"expectedRevision\":{\"household\":{\"id\":\"11111111-1111-4111-8111-111111111111\",\"revision\":\"1\"}}}")]
    public async Task ClientApi_LyricsWritesRequireRevisionAndStrictNumbers(string body)
    {
        using var factory = ClientApiFactory(_ => Json(200, """{"Id":"listener"}"""));
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        using var response = await client.PutAsync("/allstarr/v1/items/native/lyrics/source", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        await AssertClientApiError(response, 400, "invalid_request");
        using var clear = await client.PostAsync("/allstarr/v1/items/native/lyrics/source/clear", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        await AssertClientApiError(clear, 400, "invalid_request");
    }

    private static async Task<JsonNode> ReadClientLyricsJsonAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        return JsonNode.Parse(body)!;
    }

    private static ProviderLyricsResult ClientLyricsResult(string format) => new(ProviderLyricsAvailabilityState.Available, "fixture",
        format == "plain" ? ProviderLyricsFormat.PlainText : format == "line" ? ProviderLyricsFormat.LineTimed : ProviderLyricsFormat.WordTimed,
        format == "plain" ? "Plain first\nPlain second\nThird\nFourth\nFifth" : format == "line" ? "[00:01.00]Line first\n[00:02.00]Line second" : "[00:01.00]Lead\n[00:01.20]Echo",
        timedLines: format != "word" ? null :
        [
            new("Lead", TimeSpan.FromMilliseconds(1000), TimeSpan.FromMilliseconds(800), [new("Lead", TimeSpan.FromMilliseconds(1000), TimeSpan.FromMilliseconds(800))]),
            new("Echo", TimeSpan.FromMilliseconds(1200), TimeSpan.FromMilliseconds(400), [new("Echo", TimeSpan.FromMilliseconds(1200), TimeSpan.FromMilliseconds(400))], true, "v2", "Second singer")
        ]);

    private static ProtocolFactory ClientLyricsFactory(Guid recording, Dictionary<string, ProviderLyricsResult?> results,
        bool indexed = true, string? backendLyrics = null, bool externalIdentityFallback = false,
        Func<string?>? readBackendLyrics = null, Action<IServiceCollection>? configure = null)
    {
        Song Song() => new() { Id = "ext-deezer-song-lyrics", ExternalProvider = "deezer", ExternalId = "lyrics", CanonicalRecordingId = recording, Title = "Fixture", Artist = "Artist", Album = "Album", AlbumId = "ext-deezer-album-lyrics", Duration = 10 };
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(service => service.GetProviderOrder(ProviderCapabilityKind.Lyrics)).Returns(results.Keys.ToArray());
        gateway.Setup(service => service.GetPlayableProviderOrderAsync(It.IsAny<ProviderActorContext>(), It.IsAny<CancellationToken>())).ReturnsAsync(new[] { "deezer" });
        gateway.Setup(service => service.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", It.IsAny<string>()))
            .ReturnsAsync((ProtocolExecutionContext _, string _, string id) =>
            {
                if (id != "lyrics") return null;
                var song = Song();
                if (externalIdentityFallback)
                {
                    song.Id = "";
                    song.CanonicalRecordingId = null;
                }
                return song;
            });
        gateway.Setup(service => service.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "allstarr", recording.ToString("D"))).ReturnsAsync(Song);
        gateway.Setup(service => service.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "lyrics"))
            .ReturnsAsync(new Album { Id = "ext-deezer-album-lyrics", ExternalProvider = "deezer", ExternalId = "lyrics", Title = "Album", Artist = "Artist" });
        gateway.Setup(service => service.GetLyricsAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), It.IsAny<string>(),
                ProviderLyricsFormat.LineTimed, It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>()))
            .ReturnsAsync((ProtocolExecutionContext _, string source, string _, ProviderLyricsFormat? _, string? _, IReadOnlyList<string>? _, string? _, int? _, string? _) => results.GetValueOrDefault(source));
        var library = new Mock<ILibraryIndexService>(MockBehavior.Strict);
        library.Setup(service => service.FindByBackendItemAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProtocolExecutionContext _, string id, CancellationToken _) => indexed
                ? new IndexedLibraryTrack(Guid.NewGuid(), id, "/fixture", "Fixture", "Artist", "", null, 10000, null, null, null, null, recording, new Dictionary<string, string>(), default, default, 1) : null);
        library.Setup(service => service.ResolveKnownTrackAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        var policies = new Mock<IEffectiveProviderPolicyResolver>();
        policies.Setup(service => service.ResolveForUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid user, CancellationToken _) => new EffectiveProviderPolicySnapshot(
                ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty.Add(ProviderCapabilityKind.Lyrics, results.Keys.ToImmutableArray()),
                ImmutableHashSet<string>.Empty, AudioQualityPolicy.DefaultStep, 0.07)
            { UserId = user });
        return ClientApiFactory(request => request.RequestUri!.AbsolutePath switch
        {
            "/Users/Me" => Json(200, """{"Id":"listener","Policy":{"IsAdministrator":true}}"""),
            "/Items/native-album" => Json(200, """{"Id":"native-album","Type":"MusicAlbum","Name":"Album"}"""),
            "/Items/native" => Json(200, """{"Id":"native","Type":"Audio","Name":"Fixture","AlbumArtist":"Artist"}"""),
            "/Audio/hidden/Lyrics" => Json(200, "{"),
            "/Audio/native/Lyrics" => (readBackendLyrics?.Invoke() ?? backendLyrics) is { } body ? Json(200, body) : Json(404, "{}"),
            "/Items" => Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query)["ids"].ToString() switch
            {
                "native-album" => Json(200, """{"Items":[{"Id":"native-album","Type":"MusicAlbum","Name":"Album"}]}"""),
                "native" => Json(200, """{"Items":[{"Id":"native","Type":"Audio","Name":"Fixture","AlbumArtist":"Artist","AlbumId":"native-album","RunTimeTicks":100000000}]}"""),
                "wrong-id" => Json(200, """{"Items":[{"Id":"native","Type":"Audio"}]}"""),
                "folder" => Json(200, """{"Items":[{"Id":"folder","Type":"Folder"}]}"""),
                var ids when ids.Split(',').Contains("native", StringComparer.Ordinal) => Json(200, """{"Items":[{"Id":"native","Type":"Audio","Name":"Fixture","AlbumArtist":"Artist","AlbumId":"native-album","RunTimeTicks":100000000}]}"""),
                _ => Json(200, """{"Items":[]}""")
            },
            _ => Json(404, "{}")
        }, services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
            services.RemoveAll<ILibraryIndexService>();
            services.AddSingleton(library.Object);
            services.RemoveAll<IEffectiveProviderPolicyResolver>();
            services.AddSingleton(policies.Object);
            configure?.Invoke(services);
        });
    }

    private static async Task<(Guid Owner, Guid Other)> SeedClientLyricsAsync(ProtocolFactory factory, Guid recording)
    {
        var identities = factory.Services.GetRequiredService<BackendIdentityResolver>();
        var owner = (await identities.ResolveAsync(new BackendIdentityDescriptor("Jellyfin", "listener")))!.UserId;
        var other = (await identities.ResolveAsync(new BackendIdentityDescriptor("Jellyfin", "other")))!.UserId;
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync();
        db.CanonicalRecordings.Add(new() { Id = recording, CreatedByUserId = owner, PublicId = CanonicalRecordingRecord.DefaultPublicId(recording), Title = "Fixture", Revision = 1 });
        db.ProviderTrackIdentities.Add(new()
        {
            Id = Guid.NewGuid(),
            CanonicalRecordingId = recording,
            ProviderId = "deezer",
            ExternalId = "lyrics",
            ExternalIdHash = CanonicalCatalogKeys.Hash("lyrics"),
            ResourceKind = ProviderResourceKind.Track,
            Scope = ProviderIdentityScope.Catalog,
            Verification = ProviderIdentityVerification.Verified,
            VerificationMethod = "isrc",
            DecisionVersion = 1,
            Revision = 1
        });
        db.RecordingLyricsChoices.Add(new() { Id = Guid.NewGuid(), CanonicalRecordingId = recording, OwnerUserId = other, SourceId = "lyricsplus", Revision = 1 });
        await db.SaveChangesAsync();
        return (owner, other);
    }
}
