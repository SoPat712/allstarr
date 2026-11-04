using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using allstarr.Core.Capabilities;
using allstarr.Core.ClientApi;
using allstarr.Core.ClientEvents;
using allstarr.Core.Identity;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClientApi_LyricsVersionsHaveStableIdsMetadataAndIndependentRichPreviews(bool native)
    {
        var recording = Guid.NewGuid();
        var versions = ClientLyricsVersions();
        using var factory = ClientLyricsFactory(recording, new() { ["lrclib"] = versions[0] with { Versions = versions } });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var (owner, _) = await SeedClientLyricsAsync(factory, recording);
        var path = $"/allstarr/v1/items/{(native ? "native" : "ext-deezer-song-lyrics")}/lyrics";
        var sources = await ReadClientLyricsJsonAsync(client, path + "/sources");
        var entries = sources["sources"]!.AsArray().Where(entry => entry!["providerId"]!.GetValue<string>() == "lrclib").ToArray();
        Assert.Equal(new[] { "lrclib:101", "lrclib:102", "lrclib:103" }, entries.Select(entry => entry!["sourceId"]!.GetValue<string>()));
        Assert.All(entries, entry =>
        {
            Assert.Equal("lrclib", entry!["providerId"]!.GetValue<string>());
            Assert.Equal("LRCLIB", entry["providerName"]!.GetValue<string>());
            Assert.Equal("Artist", entry["artist"]!.GetValue<string>());
        });
        Assert.Equal("Fixture (Live)", entries[2]!["title"]!.GetValue<string>());
        Assert.Equal(271000, entries[2]!["durationMs"]!.GetValue<long>());
        Assert.Null(entries[2]!["album"]);
        using var subscription = factory.Services.GetRequiredService<ClientEventHub>().Subscribe(owner, null)!;
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync();
        var audits = await db.AuditEvents.CountAsync();
        var execution = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "primary", "listener",
            new AllstarrPrincipal(owner, "jellyfin", "primary", "listener", "Listener", false), "preview-state", DateTimeOffset.UtcNow.AddMinutes(1), default);
        var lyricIndex = factory.Services.GetRequiredService<LyricsSearchIndex>();
        var indexedBefore = (await lyricIndex.SnapshotAsync(execution)).Select(document => (document.Id, document.UpdatedAt)).Order().ToArray();
        for (var index = 0; index < versions.Length; index++)
        {
            using var response = await client.GetAsync(path + "?sourceId=lrclib:" + (101 + index));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            var rich = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
            Assert.Equal("lrclib:" + (101 + index), rich["source"]!["id"]!.GetValue<string>());
            Assert.Equal("word", rich["format"]!.GetValue<string>());
            Assert.Equal(ClientLyricsVersionText(index), rich["lines"]![0]!["text"]!.GetValue<string>());
            Assert.Contains(ClientLyricsVersionText(index), entries[index]!["preview"]!.GetValue<string>());
            var background = rich["lines"]![1]!;
            Assert.Equal("background", background["part"]!.GetValue<string>());
            Assert.Equal("v2", background["voice"]!.GetValue<string>());
            Assert.Equal("Second singer", background["singer"]!.GetValue<string>());
            Assert.Equal(1200, background["words"]![0]!["startMs"]!.GetValue<long>());
            Assert.Equal(1600, background["words"]![0]!["endMs"]!.GetValue<long>());
        }
        foreach (var sourceId in new[] { "lrclib:missing", "unknown", "" })
        {
            using var unknown = await client.GetAsync(path + "?sourceId=" + sourceId);
            await AssertClientApiError(unknown, 404, "not_found");
        }
        Assert.Equal(0, subscription.PendingCount);
        Assert.Equal(indexedBefore, (await lyricIndex.SnapshotAsync(execution)).Select(document => (document.Id, document.UpdatedAt)).Order().ToArray());
        Assert.Equal(audits, await db.AuditEvents.CountAsync());
        Assert.False(await db.RecordingLyricsChoices.AnyAsync(choice => choice.OwnerUserId == owner));
        var after = await ReadClientLyricsJsonAsync(client, path + "/sources");
        Assert.True(JsonNode.DeepEquals(sources["revision"], after["revision"]));
        Assert.Equal(entries.Select(entry => entry!["sourceId"]!.GetValue<string>()),
            after["sources"]!.AsArray().Where(entry => entry!["providerId"]!.GetValue<string>() == "lrclib").Select(entry => entry!["sourceId"]!.GetValue<string>()));
        Assert.Equal("Studio constellation", (await ReadClientLyricsJsonAsync(client, path))["lines"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task ClientApi_LyricsVersionPinDrivesPlaybackSearchAndProviderFallback()
    {
        var recording = Guid.NewGuid();
        var versions = ClientLyricsVersions();
        var results = new Dictionary<string, ProviderLyricsResult?>
        {
            ["lrclib"] = versions[0] with { Versions = versions },
            ["lyricsplus"] = ClientLyricsResult("plain")
        };
        using var factory = ClientLyricsFactory(recording, results);
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var (owner, other) = await SeedClientLyricsAsync(factory, recording);
        const string path = "/allstarr/v1/items/ext-deezer-song-lyrics/lyrics";
        var sources = await ReadClientLyricsJsonAsync(client, path + "/sources");
        using var foreign = factory.Services.GetRequiredService<ClientEventHub>().Subscribe(other, null)!;
        using var pin = await client.PutAsJsonAsync(path + "/source", new { sourceId = "lrclib:103", expectedRevision = sources["revision"] });
        Assert.Equal(HttpStatusCode.OK, pin.StatusCode);
        var personal = JsonNode.Parse(await pin.Content.ReadAsStringAsync())!;
        Assert.Equal("personal", personal["chosen"]!["layer"]!.GetValue<string>());
        Assert.Equal("lrclib:103", personal["chosen"]!["sourceId"]!.GetValue<string>());
        Assert.Equal("Studio constellation", (await ReadClientLyricsJsonAsync(client, path + "?sourceId=lrclib:101"))["lines"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("Concert fireworks", (await ReadClientLyricsJsonAsync(client, path))["lines"]![0]!["text"]!.GetValue<string>());
        var jellyfin = await ReadClientLyricsJsonAsync(client, "/Audio/ext-deezer-song-lyrics/Lyrics");
        Assert.Equal("Concert fireworks", jellyfin["Lyrics"]![0]!["Text"]!.GetValue<string>());
        Assert.Single((await ReadClientLyricsJsonAsync(client, "/allstarr/v1/search/lyrics?q=concert%20fireworks"))["results"]!.AsArray());
        Assert.Empty((await ReadClientLyricsJsonAsync(client, "/allstarr/v1/search/lyrics?q=studio%20constellation"))["results"]!.AsArray());
        using var stale = await client.PutAsJsonAsync(path + "/source", new { sourceId = "lrclib:102", expectedRevision = sources["revision"] });
        await AssertClientApiError(stale, 409, "conflict");
        results["lrclib"] = versions[0] with { Versions = versions.Take(2).ToArray() };
        Assert.Equal("Studio constellation", (await ReadClientLyricsJsonAsync(client, path))["lines"]![0]!["text"]!.GetValue<string>());
        Assert.Single((await ReadClientLyricsJsonAsync(client, "/allstarr/v1/search/lyrics?q=studio%20constellation"))["results"]!.AsArray());
        Assert.Empty((await ReadClientLyricsJsonAsync(client, "/allstarr/v1/search/lyrics?q=concert%20fireworks"))["results"]!.AsArray());
        using var disappeared = await client.GetAsync(path + "?sourceId=lrclib:103");
        await AssertClientApiError(disappeared, 404, "not_found");
        results["lrclib"] = null;
        Assert.Equal("lyricsplus", (await ReadClientLyricsJsonAsync(client, path))["source"]!["id"]!.GetValue<string>());
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync();
        Assert.Equal("lrclib:103", (await db.RecordingLyricsChoices.SingleAsync(choice => choice.OwnerUserId == owner)).SourceId);
        Assert.Equal("lyricsplus", (await db.RecordingLyricsChoices.SingleAsync(choice => choice.OwnerUserId == other)).SourceId);
        Assert.Equal(0, foreign.PendingCount);
    }

    [Fact]
    public async Task ClientApi_LyricsLegacyProviderPinSelectsBestVersion()
    {
        var recording = Guid.NewGuid();
        var versions = ClientLyricsVersions();
        using var factory = ClientLyricsFactory(recording, new() { ["lrclib"] = versions[0] with { Versions = versions } });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        await SeedClientLyricsAsync(factory, recording);
        const string path = "/allstarr/v1/items/ext-deezer-song-lyrics/lyrics";
        var sources = await ReadClientLyricsJsonAsync(client, path + "/sources");
        using var pin = await client.PutAsJsonAsync(path + "/source", new { sourceId = "lrclib", expectedRevision = sources["revision"] });
        Assert.Equal(HttpStatusCode.OK, pin.StatusCode);
        var selected = JsonNode.Parse(await pin.Content.ReadAsStringAsync())!;
        Assert.Equal("personal", selected["chosen"]!["layer"]!.GetValue<string>());
        Assert.Equal("Studio constellation", (await ReadClientLyricsJsonAsync(client, path))["lines"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("Studio constellation", (await ReadClientLyricsJsonAsync(client, path + "?sourceId=lrclib"))["lines"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task ClientApi_LyricsVersionsRemainReviewableWhenTheBestRecordHasNoLyrics()
    {
        var recording = Guid.NewGuid();
        using var factory = ClientLyricsFactory(recording, new()
        {
            ["lrclib"] = new ProviderLyricsResult(ProviderLyricsAvailabilityState.Available, "lrclib") { Versions = ClientLyricsVersions() }
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        await SeedClientLyricsAsync(factory, recording);
        const string path = "/allstarr/v1/items/ext-deezer-song-lyrics/lyrics";
        var sources = await ReadClientLyricsJsonAsync(client, path + "/sources");
        Assert.Equal(3, sources["sources"]!.AsArray().Count(source => source!["providerId"]!.GetValue<string>() == "lrclib"));
        Assert.Null(sources["chosen"]);
        Assert.Equal("Concert fireworks", (await ReadClientLyricsJsonAsync(client, path + "?sourceId=lrclib:103"))["lines"]![0]!["text"]!.GetValue<string>());
        using var automatic = await client.GetAsync(path);
        await AssertClientApiError(automatic, 404, "not_found");
    }

    private static string ClientLyricsVersionText(int index) => new[] { "Studio constellation", "Acoustic lantern", "Concert fireworks" }[index];

    private static ProviderLyricsResult[] ClientLyricsVersions() => Enumerable.Range(0, 3).Select(index =>
        (ClientLyricsResult("word") with
        {
            Metadata = new((101 + index).ToString(), index == 2 ? "Fixture (Live)" : "Fixture", "Artist", index == 2 ? null : "Album", index == 2 ? 271000 : 240000),
        })).Select((result, index) => new ProviderLyricsResult(result.Availability, "lrclib", result.Format,
            "[00:01.000]<00:01.000>" + ClientLyricsVersionText(index) + "<00:01.800>\n[00:01.200]<00:01.200>Echo<00:01.600>", result.Revision,
        [
            new(ClientLyricsVersionText(index), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(800), [new(ClientLyricsVersionText(index), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(800))]),
            new("Echo", TimeSpan.FromMilliseconds(1200), TimeSpan.FromMilliseconds(400), [new("Echo", TimeSpan.FromMilliseconds(1200), TimeSpan.FromMilliseconds(400))], true, "v2", "Second singer")
        ])
        { Metadata = result.Metadata }).ToArray();
}
