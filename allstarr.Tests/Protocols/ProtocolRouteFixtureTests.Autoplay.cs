using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using allstarr.Core.Capabilities;
using allstarr.Core.ClientApiV2;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Fact]
    public async Task ClientApiV2_AutoplayBatchesMatchSerialPagesAndCursorsWithMissingAndDuplicateIds()
    {
        string[] ids = ["seed", "missing1", "first", "first", "copy-first", "second", "missing2", "third", "second", "copy-second", "fourth", "missing3", "fifth", "sixth", "seventh"];
        using var factory = AutoplayBatchFactory(ids, []);
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        var seen = new HashSet<string>(StringComparer.Ordinal) { Hash("id:" + V2Ids.TrackSource("seed")), Hash("musicbrainzRecording:SEED") };
        var offset = 0;
        string? cursor = null;
        do
        {
            var expected = new List<string>();
            while (offset < ids.Length && expected.Count < 2)
            {
                var serial = JsonNode.Parse(await client.GetStringAsync("/allstarr/v2/tracks?ids=" + ids[offset]))!;
                foreach (var track in serial["tracks"]!.AsArray())
                {
                    var keys = new[] { Hash("id:" + track!["id"]!.GetValue<string>()), Hash("musicbrainzRecording:" + track["externalIds"]!["musicbrainzRecording"]!.GetValue<string>().Replace("-", "", StringComparison.Ordinal).Trim().ToUpperInvariant()) };
                    if (keys.Any(seen.Contains)) continue;
                    seen.UnionWith(keys);
                    expected.Add(track["title"]!.GetValue<string>());
                }
                offset++;
            }
            var page = JsonNode.Parse(await client.GetStringAsync("/allstarr/v2/tracks/seed/autoplay?limit=2" + (cursor == null ? "" : "&cursor=" + cursor)))!;
            Assert.Equal(expected, AutoplayTitles(page));
            cursor = page["next"]?.GetValue<string>();
            if (offset < ids.Length)
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(new V2AutoplayReader.Cursor(V2Ids.TrackSource("seed"), 0, offset, seen.Order(StringComparer.Ordinal).ToArray()));
                Assert.Equal(WebEncoders.Base64UrlEncode(bytes) + "." + WebEncoders.Base64UrlEncode(SHA256.HashData(bytes)[..16]), cursor);
            }
            else Assert.Null(cursor);
        } while (cursor != null);

        static string Hash(string value) => WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.UTF8.GetBytes(value))[..16]);
    }

    [Fact]
    public async Task ClientApiV2_AutoplayTwentyTracksUseFewTrackReads()
    {
        var reads = new List<string[]>();
        using var factory = AutoplayBatchFactory(Enumerable.Range(1, 40).Select(index => "song" + index).ToArray(), reads);
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        var page = JsonNode.Parse(await client.GetStringAsync("/allstarr/v2/tracks/seed/autoplay?limit=20"))!;
        Assert.Equal(Enumerable.Range(1, 20).Select(index => "song" + index), AutoplayTitles(page));
        Assert.InRange(reads.Count, 2, 3); // Includes the seed read; each track read shapes library rows once.
        Assert.Contains(reads, ids => ids.Length is >= 20 and <= 25);
    }

    [Fact]
    public async Task ClientApiV2_AutoplayCancellationDuringChunkKeepsCursorAndMarksPartial()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var cancel = true;
        using var factory = AutoplayBatchFactory(new[] { "seed" }.Concat(Enumerable.Range(1, 30).Select(index => "song" + index)).ToArray(), [], ids =>
        {
            if (!cancel || !ids.Contains("song26")) return;
            cancel = false;
            clock.Advance(TimeSpan.FromSeconds(5));
            throw new OperationCanceledException();
        }, new RecommendationClock(clock));
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        var view = JsonNode.Parse(await client.GetStringAsync("/allstarr/v2/tracks/seed?views=autoplay"))!;
        Assert.Equal("partial", view["viewStatus"]!["autoplay"]!.GetValue<string>());
        var page = view["views"]!["autoplay"]!;
        Assert.Equal(Enumerable.Range(1, 24).Select(index => "song" + index), AutoplayTitles(page));
        var cursor = page["next"]!.GetValue<string>();
        Assert.True(V2AutoplayReader.TryCursor("seed", cursor, out var position));
        Assert.Equal(25, position.Offset);
        var next = JsonNode.Parse(await client.GetStringAsync("/allstarr/v2/tracks/seed/autoplay?cursor=" + cursor))!;
        Assert.Equal(Enumerable.Range(25, 6).Select(index => "song" + index), AutoplayTitles(next));
        Assert.Null(next["next"]);
    }

    private static ProtocolFactory AutoplayBatchFactory(string[] candidates, List<string[]> reads, Action<string[]>? beforeRead = null, TimeProvider? clock = null)
    {
        var songs = candidates.Append("seed").Distinct().Where(id => !id.StartsWith("missing", StringComparison.Ordinal))
            .ToDictionary(id => id, id => new { Id = id, Type = "Audio", Name = id, ProviderIds = new { MusicBrainzTrack = id.Replace("copy-", "", StringComparison.Ordinal) } });
        return ClientDetailsFactory(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/Users/Me") return Json(200, """{"Id":"listener"}""");
            var query = QueryHelpers.ParseQuery(request.RequestUri.Query);
            if (query.TryGetValue("ids", out var requested))
            {
                var ids = requested.ToString().Split(',');
                if (query.ContainsKey("fields")) { reads.Add(ids); beforeRead?.Invoke(ids); }
                return Json(200, JsonSerializer.Serialize(new { Items = ids.Reverse().Where(songs.ContainsKey).Select(id => songs[id]).ToArray() }));
            }
            var selected = request.RequestUri.AbsolutePath.EndsWith("/InstantMix", StringComparison.Ordinal) ? candidates : [];
            return Json(200, JsonSerializer.Serialize(new { Items = selected.Select(id => new { Id = id }).ToArray() }));
        }, services =>
        {
            if (clock != null) services.AddSingleton(clock);
        });
    }

    [Fact]
    public async Task ClientApiV2_AutoplayRecognizesLibraryGuidAliasesAcrossPages()
    {
        var seed = Guid.NewGuid();
        var ready = Guid.NewGuid();
        var last = Guid.NewGuid();
        var songs = new[] { seed, ready, last }.Select(id => new { Id = id.ToString("N"), Type = "Audio", Name = id == seed ? "Seed" : id == ready ? "Ready" : "Last" }).ToArray();
        using var factory = ClientDetailsFactory(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/Users/Me") return Json(200, """{"Id":"listener"}""");
            var query = QueryHelpers.ParseQuery(request.RequestUri.Query);
            if (query.TryGetValue("ids", out var value))
            {
                var ids = value.ToString().Split(',').Select(Guid.Parse).ToArray();
                return Json(200, JsonSerializer.Serialize(new { Items = songs.Where(song => ids.Contains(Guid.Parse(song.Id))).ToArray() }));
            }
            var items = new[] { songs[0], songs[1], songs[1] with { Id = ready.ToString("D") }, songs[2] };
            return Json(200, JsonSerializer.Serialize(new { Items = items }));
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        var first = JsonNode.Parse(await client.GetStringAsync($"/allstarr/v2/tracks/{seed:D}/autoplay?limit=1"))!;
        Assert.Equal(["Ready"], AutoplayTitles(first));
        var second = JsonNode.Parse(await client.GetStringAsync($"/allstarr/v2/tracks/{seed:N}/autoplay?limit=1&cursor=" + first["next"]!.GetValue<string>()))!;
        Assert.Equal(["Last"], AutoplayTitles(second));
    }

    [Fact]
    public async Task ClientApiV2_AutoplayDefaultsTo25AndAllows50Tracks()
    {
        var songs = Enumerable.Range(0, 60).Select(index => new { Id = "song" + index, Type = "Audio", Name = "Song " + index }).ToArray();
        using var factory = ClientDetailsFactory(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/Users/Me") return Json(200, """{"Id":"listener"}""");
            var query = QueryHelpers.ParseQuery(request.RequestUri.Query);
            var ids = query.TryGetValue("ids", out var requested) ? requested.ToString().Split(',') : [];
            return Json(200, JsonSerializer.Serialize(new { Items = query.ContainsKey("ids") ? songs.Where(song => ids.Contains(song.Id)).ToArray() : songs }));
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        var page = JsonNode.Parse(await client.GetStringAsync("/allstarr/v2/tracks/song0/autoplay"))!;
        Assert.Equal(25, page["tracks"]!.AsArray().Count);
        var view = JsonNode.Parse(await client.GetStringAsync("/allstarr/v2/tracks/song0?views=autoplay"))!;
        Assert.Equal(25, view["views"]!["autoplay"]!["tracks"]!.AsArray().Count);
        var maximum = JsonNode.Parse(await client.GetStringAsync("/allstarr/v2/tracks/song0/autoplay?limit=50"))!;
        Assert.Equal(50, maximum["tracks"]!.AsArray().Count);
    }

    [Fact]
    public async Task ClientApiV2_AutoplayUsesInstantMixThenFillsFromSimilarArtistAndAlbumAcrossPages()
    {
        var calls = new List<string>();
        using var factory = AutoplayLibraryFactory(calls);
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        var first = JsonNode.Parse(await client.GetStringAsync("/allstarr/v2/tracks/seed/autoplay?limit=2"))!;
        Assert.Equal(["mix", "similar"], AutoplayTitles(first));
        Assert.Equal(["instant", "similar"], calls);
        var cursor = first["next"]!.GetValue<string>();
        var second = JsonNode.Parse(await client.GetStringAsync("/allstarr/v2/tracks/seed/autoplay?limit=2&cursor=" + cursor))!;
        Assert.Equal(["artist", "album"], AutoplayTitles(second));
        Assert.Equal(4, first["tracks"]!.AsArray().Concat(second["tracks"]!.AsArray()).Select(track => track!["id"]!.GetValue<string>()).Distinct().Count());
        Assert.DoesNotContain("seed", AutoplayTitles(first).Concat(AutoplayTitles(second)));
        Assert.Null(second["next"]);

        using var wrongSeed = await client.GetAsync("/allstarr/v2/tracks/other/autoplay?cursor=" + cursor);
        await AssertClientApiError(wrongSeed, 400, "invalid_request");
        var changed = cursor[..^1] + (cursor[^1] == 'a' ? 'b' : 'a');
        using var tampered = await client.GetAsync("/allstarr/v2/tracks/seed/autoplay?cursor=" + changed);
        await AssertClientApiError(tampered, 400, "invalid_request");
    }

    [Theory]
    [InlineData("?cursor=bad")]
    [InlineData("?cursor=")]
    [InlineData("?limit=0")]
    [InlineData("?limit=51")]
    [InlineData("?limit=two")]
    public async Task ClientApiV2_AutoplayRejectsInvalidPaging(string query)
    {
        using var factory = ClientDetailsFactory(_ => Json(200, """{"Id":"listener"}"""));
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        using var response = await client.GetAsync("/allstarr/v2/tracks/seed/autoplay" + query);
        await AssertClientApiError(response, 400, "invalid_request");
    }

    [Fact]
    public async Task ClientApiV2_AutoplayViewAndContinuationWorkInsideBatch()
    {
        using var factory = AutoplayLibraryFactory([]);
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        using var response = await client.PostAsJsonAsync("/allstarr/v2/batch", new
        {
            requests = new[]
            {
                new { id = "view", path = "/allstarr/v2/tracks/seed?views=autoplay" },
                new { id = "page", path = "/allstarr/v2/tracks/seed/autoplay?limit=1" },
                new { id = "bad", path = "/allstarr/v2/tracks/seed/autoplay?cursor=bad" }
            }
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var results = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["responses"]!;
        Assert.Equal(200, results["view"]!["status"]!.GetValue<int>());
        Assert.Equal("ok", results["view"]!["body"]!["viewStatus"]!["autoplay"]!.GetValue<string>());
        Assert.Equal(["mix", "similar", "artist", "album"], AutoplayTitles(results["view"]!["body"]!["views"]!["autoplay"]!));
        Assert.Equal(["mix"], AutoplayTitles(results["page"]!["body"]!));
        var cursor = results["page"]!["body"]!["next"]!.GetValue<string>();
        using var next = await client.PostAsJsonAsync("/allstarr/v2/batch", new
        {
            requests = new[] { new { id = "next", path = "/allstarr/v2/tracks/seed/autoplay?limit=1&cursor=" + cursor } }
        });
        var continued = JsonNode.Parse(await next.Content.ReadAsStringAsync())!["responses"]!["next"]!;
        Assert.Equal(200, continued["status"]!.GetValue<int>());
        Assert.Equal(["similar"], AutoplayTitles(continued["body"]!));
        Assert.Equal(400, results["bad"]!["status"]!.GetValue<int>());
    }

    [Fact]
    public async Task ClientApiV2_AutoplayHonoursCleanOnlyOnEverySource()
    {
        using var factory = AutoplayLibraryFactory([], cleanOnly: true);
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        var page = JsonNode.Parse(await client.GetStringAsync("/allstarr/v2/tracks/seed/autoplay"))!;
        Assert.Equal(["mix", "similar", "artist", "album"], AutoplayTitles(page));
        Assert.All(page["tracks"]!.AsArray(), track => Assert.False(track!["explicit"]!.GetValue<bool>()));
    }

    [Fact]
    public async Task ClientApiV2_AutoplaySlowSourceKeepsGatheredTracksAndCanContinue()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var slow = true;
        using var factory = AutoplayLibraryFactory([], beforeSimilar: () =>
        {
            if (!slow) return;
            slow = false;
            clock.Advance(TimeSpan.FromSeconds(5));
        }, clock: new RecommendationClock(clock));
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        var view = JsonNode.Parse(await client.GetStringAsync("/allstarr/v2/tracks/seed?views=autoplay"))!;
        Assert.Equal("partial", view["viewStatus"]!["autoplay"]!.GetValue<string>());
        var page = view["views"]!["autoplay"]!;
        Assert.Equal(["mix"], AutoplayTitles(page));
        var next = JsonNode.Parse(await client.GetStringAsync("/allstarr/v2/tracks/seed/autoplay?cursor=" + page["next"]!.GetValue<string>()))!;
        Assert.Equal(["similar", "artist", "album"], AutoplayTitles(next));
        Assert.Null(next["next"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClientApiV2_AutoplayServiceSeedUsesRadioAndExcludesTheSeedRecording(bool recordingId)
    {
        var seed = AutoplaySong("seed", "USABC1200001");
        var same = AutoplaySong("copy", seed.Isrc!);
        var ready = AutoplaySong("ready", "USABC1200002");
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(value => value.GetPlayableProviderOrderAsync(It.IsAny<ProviderActorContext>(), It.IsAny<CancellationToken>())).ReturnsAsync(["deezer"]);
        gateway.Setup(value => value.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "seed")).ReturnsAsync(seed);
        gateway.Setup(value => value.GetStoredSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", It.IsAny<string>()))
            .ReturnsAsync((ProtocolExecutionContext _, string _, string id) => new[] { seed, same, ready }.Single(song => song.ExternalId == id));
        gateway.Setup(value => value.GetStoredSongAsync(It.IsAny<ProtocolExecutionContext>(), "allstarr", It.IsAny<string>())).ReturnsAsync(seed);
        gateway.Setup(value => value.GetSimilarSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "seed", It.IsAny<int>())).ReturnsAsync([seed, same, ready]);
        gateway.Setup(value => value.CanPlaySongAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<Song>())).ReturnsAsync(true);
        gateway.Setup(value => value.GetArtistTracksAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "artist")).ReturnsAsync([]);
        using var factory = ClientDetailsFactory(request => request.RequestUri!.AbsolutePath == "/Users/Me"
            ? Json(200, """{"Id":"listener"}""") : throw new InvalidOperationException("Unexpected library request"), services => services.AddSingleton(gateway.Object));
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        var seedRecording = Guid.Empty;
        foreach (var song in new[] { seed, same, ready })
        {
            var recording = await SeedClientDetailsAsync(factory, "listener", song.ExternalId!, personal: false);
            if (song == seed) seedRecording = recording;
            await using var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync();
            var snapshot = await db.ExternalMetadataSnapshots.AsNoTracking().SingleAsync(item => item.ResourceKind == "track" && item.ExternalIdHash == CanonicalCatalogKeys.Hash(song.ExternalId!));
            snapshot.Id = Guid.NewGuid();
            snapshot.SnapshotVersion++;
            snapshot.PayloadJson = JsonSerializer.Serialize(song);
            snapshot.PayloadSha256 = CanonicalCatalogKeys.Hash(snapshot.PayloadJson);
            db.ExternalMetadataSnapshots.Add(snapshot);
            await db.SaveChangesAsync();
        }
        var id = recordingId ? V2Ids.Track(seedRecording) : seed.Id;
        var page = JsonNode.Parse(await client.GetStringAsync($"/allstarr/v2/tracks/{id}/autoplay?limit=1"))!;
        Assert.Equal(["ready"], AutoplayTitles(page));
        gateway.Verify(value => value.CanPlaySongAsync(It.IsAny<ProtocolExecutionContext>(), It.Is<Song>(song => song.Id == ready.Id)), Times.AtLeastOnce);
        var alias = recordingId ? seed.Id : V2Ids.Track(seedRecording);
        var continued = JsonNode.Parse(await client.GetStringAsync($"/allstarr/v2/tracks/{alias}/autoplay?cursor=" + page["next"]!.GetValue<string>()))!;
        Assert.Empty(AutoplayTitles(continued));
        Assert.Null(continued["next"]);
    }

    private static Song AutoplaySong(string id, string isrc) => new()
    {
        Id = "ext-deezer-song-" + id,
        ExternalId = id,
        ExternalProvider = "deezer",
        Title = id,
        Artist = "Artist",
        ArtistId = "ext-deezer-artist-artist",
        Isrc = isrc,
        ExplicitContentLyrics = 0
    };

    private static string[] AutoplayTitles(JsonNode page) => page["tracks"]!.AsArray().Select(track => track!["title"]!.GetValue<string>()).ToArray();

    private static ProtocolFactory AutoplayLibraryFactory(List<string> calls, bool cleanOnly = false, Action? beforeSimilar = null, TimeProvider? clock = null)
    {
        object Track(string id, string? recording = null, string rating = "Clean") => new
        {
            Id = id,
            Type = "Audio",
            Name = id,
            ArtistItems = new[] { new { Id = "artist1", Name = "Artist" } },
            AlbumId = "album1",
            Album = "Album",
            OfficialRating = rating,
            ProviderIds = new { MusicBrainzTrack = recording ?? id }
        };
        var items = new[] { Track("seed"), Track("mix", "shared"), Track("copy", "shared"), Track("similar"), Track("artist"), Track("album"), Track("explicit", rating: "Explicit"), Track("unknown", rating: "Unknown") };
        var byId = items.ToDictionary(item => JsonSerializer.SerializeToNode(item)!["Id"]!.GetValue<string>());
        return ClientDetailsFactory(request =>
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath == "/Users/Me") return Json(200, """{"Id":"listener"}""");
            var query = QueryHelpers.ParseQuery(uri.Query);
            if (query.TryGetValue("ids", out var ids))
                return Json(200, JsonSerializer.Serialize(new { Items = ids.ToString().Split(',').Where(byId.ContainsKey).Select(id => byId[id]).ToArray() }));
            string[] selected;
            if (uri.AbsolutePath.EndsWith("/InstantMix", StringComparison.Ordinal)) { calls.Add("instant"); selected = ["seed", "mix", "mix", "explicit", "unknown"]; }
            else if (uri.AbsolutePath.EndsWith("/Similar", StringComparison.Ordinal)) { calls.Add("similar"); beforeSimilar?.Invoke(); selected = ["seed", "copy", "similar", "explicit", "unknown"]; }
            else if (query.ContainsKey("artistIds")) { calls.Add("artist"); selected = ["seed", "mix", "copy", "artist", "explicit", "unknown"]; }
            else if (query.ContainsKey("parentId")) { calls.Add("album"); selected = ["seed", "artist", "album", "explicit", "unknown"]; }
            else throw new InvalidOperationException("Unexpected library request: " + uri.PathAndQuery);
            return Json(200, JsonSerializer.Serialize(new { Items = selected.Where(id => cleanOnly || id is not ("explicit" or "unknown")).Select(id => byId[id]).ToArray() }));
        }, services =>
        {
            if (cleanOnly) ClientCleanOnlyServices(services);
            if (clock != null) services.AddSingleton(clock);
        });
    }
}
