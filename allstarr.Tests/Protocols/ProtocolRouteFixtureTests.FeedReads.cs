using System.Diagnostics;
using System.Net;
using System.Text.Json;
using allstarr.Core.Identity;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Fact]
    public async Task ClientApi_MixItemsSingleFlightConcurrentOpensAndReuseStoredMetadata()
    {
        var reads = 0;
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetStoredSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", It.IsAny<string>()))
            .Returns(async (ProtocolExecutionContext _, string _, string id) =>
            {
                Interlocked.Increment(ref reads);
                await Task.Delay(800);
                return new Song { Id = "ext-deezer-song-" + id, Artist = "Artist", Title = "Song", AlbumId = "ext-deezer-album-one", ExternalProvider = "deezer", ExternalId = id };
            });
        using var factory = ClientMixFactory(gateway);
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var mix = await SeedClientMixAsync(factory, serviceSong: ClientMixSong());
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync())
        {
            var row = await db.ExternalMetadataSnapshots.AsNoTracking().SingleAsync(item => item.ResourceKind == "personal-mixes");
            var payload = JsonSerializer.Deserialize<PersonalMixDocument>(row.PayloadJson)!;
            payload.Mixes["custom"] = mix with
            {
                Songs = Enumerable.Range(0, 100).Select(index => new PersonalMixItem(mix.Id, 2,
                new Song
                {
                    Id = "ext-deezer-song-" + index % 4,
                    ExternalProvider = "deezer",
                    ExternalId = (index % 4).ToString(),
                    Title = "Song " + index,
                    Artist = "Artist",
                    Album = "Album",
                    Duration = 180
                }, "deezer", "Picked for you", 1)).ToArray()
            };
            row.Id = Guid.NewGuid(); row.SnapshotVersion++;
            row.PayloadJson = JsonSerializer.Serialize(payload);
            db.ExternalMetadataSnapshots.Add(row);
            await db.SaveChangesAsync();
        }
        var watch = Stopwatch.StartNew();
        var burst = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => client.GetStringAsync($"/allstarr/v1/mixes/{mix.Id}/items")));
        var cold = watch.Elapsed.TotalMilliseconds;
        Assert.All(burst, body => Assert.Equal(burst[0], body));
        Assert.Equal(4, reads);
        using var result = JsonDocument.Parse(burst[0]);
        Assert.Equal(100, result.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(Enumerable.Range(0, 100).Select(index => "Song " + index),
            result.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("title").GetString()));
        watch.Restart();
        Assert.Equal(burst[0], await client.GetStringAsync($"/allstarr/v1/mixes/{mix.Id}/items"));
        Assert.Equal(4, reads);
        _lyricsSearchOutput.WriteLine($"Mix items 32 concurrent opens / 100 ordered rows: first burst {cold:F1} ms; warm {watch.Elapsed.TotalMilliseconds:F1} ms; stored metadata reads=4 total, 0 warm; response bytes equal.");
    }

    [Fact]
    public async Task ClientApi_MixesReuseOwnerCardsAndInvalidateAfterOwnChanges()
    {
        var nativeReads = 0;
        ProtocolExecutionContext? observed = null;
        using var factory = ClientMixFactory(new(), configure: services => services.Configure<MvcOptions>(options =>
            options.Filters.Add(new ClientApiObservationFilter(context => observed = context))), observe: request =>
        {
            if (request.RequestUri!.AbsolutePath != "/Users/listener/Items") return;
            Interlocked.Increment(ref nativeReads);
            Thread.Sleep(150);
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var mix = await SeedClientMixAsync(factory);
        var owner = (await factory.Services.GetRequiredService<BackendIdentityResolver>().ResolveAsync(new("Jellyfin", "listener")))!.UserId;
        var contexts = factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>();
        await using (var db = await contexts.CreateDbContextAsync())
        {
            var row = await db.ExternalMetadataSnapshots.AsNoTracking().SingleAsync(item => item.ResourceKind == "personal-mixes");
            var payload = JsonSerializer.Deserialize<PersonalMixDocument>(row.PayloadJson)!;
            for (var index = 0; index < 24; index++)
            {
                var id = Guid.NewGuid();
                payload.Mixes["custom:" + id] = mix with
                {
                    Id = id,
                    Name = "Saved " + index,
                    Songs = Enumerable.Range(0, 50).Select(position => mix.Songs[position % 2] with { MixId = id }).ToArray()
                };
            }
            row.Id = Guid.NewGuid(); row.SnapshotVersion++;
            row.PayloadJson = JsonSerializer.Serialize(payload, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
            db.ExternalMetadataSnapshots.Add(row);
            await db.SaveChangesAsync();
        }
        var watch = Stopwatch.StartNew();
        var first = await client.GetStringAsync("/allstarr/v1/mixes?timeZoneId=UTC");
        var cold = watch.Elapsed.TotalMilliseconds;
        Assert.Equal(1, nativeReads);
        watch.Restart();
        var warm = await client.GetStringAsync("/allstarr/v1/mixes?timeZoneId=UTC");
        var elapsed = watch.Elapsed.TotalMilliseconds;
        Assert.Equal(first, warm);
        Assert.Equal(1, nativeReads);
        await factory.Services.GetRequiredService<PersonalMixService>().SetInAppsAsync(owner, mix.Id, false, default);
        var changed = await client.GetStringAsync("/allstarr/v1/mixes?timeZoneId=UTC");
        Assert.Equal(2, nativeReads);
        await using (var db = await contexts.CreateDbContextAsync())
        {
            (await db.LibraryTracks.SingleAsync()).Revision++;
            await db.SaveChangesAsync();
        }
        Assert.Equal(changed, await client.GetStringAsync("/allstarr/v1/mixes?timeZoneId=UTC"));
        Assert.InRange(nativeReads, 2, 3);
        await using var readDb = await contexts.CreateDbContextAsync();
        var key = readDb.FeedReadKey(observed!, "mixes:UTC", new(true, ["library"]));
        await factory.Services.GetRequiredService<allstarr.Core.Caching.ApplicationCacheRequestCoalescer>()
            .RunAsync(key, () => Task.FromResult<object?>(null), default);
        _lyricsSearchOutput.WriteLine($"Mix list, 25 saved mixes with 50-song fixtures: cold {cold:F1} ms; warm {elapsed:F1} ms; native detail reads 1 -> 0; response bytes equal.");
    }

    [Theory]
    [InlineData("", 24)]
    [InlineData("&SearchTerm=Added%20virtual", 4)]
    [InlineData("&NameStartsWith=A", 4)]
    [InlineData("&MediaTypes=Video", 0)]
    public async Task JellyfinPlaylistCountDoesNotEnumerateNativePlaylists(string filter, int expected)
    {
        var requests = new List<string>();
        using var factory = PlaylistBrowseFactory(requests);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/Items?UserId=user-1&Limit=0&Recursive=true&IncludeItemTypes=Playlist&EnableTotalRecordCount=true&api_key=fixture-key" + filter);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expected, body.RootElement.GetProperty("TotalRecordCount").GetInt32());
        Assert.Empty(body.RootElement.GetProperty("Items").EnumerateArray());
        Assert.Single(requests, request => request.Contains("IncludeItemTypes=Playlist", StringComparison.Ordinal));
        Assert.Contains(requests, request => Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri("http://fixture" + request).Query)
            .GetValueOrDefault("Limit").ToString() == "0");
    }

    [Fact]
    public async Task JellyfinPlaylistBrowseReusesOwnerSummariesAndCountUsesOnlyTheNativeCount()
    {
        var requests = new List<string>();
        var summaries = 0;
        using var factory = PlaylistBrowseFactory(requests, virtualCount: 36, nativeDelay: 150, summaryDelay: 150,
            summariesRead: () => Interlocked.Increment(ref summaries));
        using var client = factory.CreateClient();
        const string query = "/Items?UserId=user-1&Recursive=true&IncludeItemTypes=Playlist&EnableTotalRecordCount=true&api_key=fixture-key";
        var watch = Stopwatch.StartNew();
        var fullMerge = await client.GetStringAsync(query + "&Limit=200&SortBy=SortName");
        var baseline = watch.Elapsed.TotalMilliseconds;
        var expected = System.Text.Json.Nodes.JsonNode.Parse(fullMerge)!.AsObject();
        expected["Items"] = new System.Text.Json.Nodes.JsonArray();
        var before = expected.ToJsonString();
        Assert.Equal(1, summaries);
        using var full = await client.GetAsync(query + "&Limit=200");
        Assert.Equal(1, summaries);
        using var count = await client.GetAsync(query + "&Limit=0");
        Assert.Equal(2, summaries);
        requests.Clear();
        watch.Restart();
        var after = await client.GetStringAsync(query + "&Limit=0");
        var warm = watch.Elapsed.TotalMilliseconds;
        Assert.Equal(before, after);
        Assert.Equal(2, summaries);
        Assert.Single(requests, request => request.Contains("IncludeItemTypes=Playlist", StringComparison.Ordinal));
        _lyricsSearchOutput.WriteLine($"Playlist count with 36 served summaries and 150 ms native pages: full-list merge {baseline:F1} ms -> count-only warm {warm:F1} ms; native pages 3 -> 1; summary reads 1 -> 0; response content/order equal.");
        await factory.Services.GetRequiredService<allstarr.Core.Caching.IApplicationCache>().DeleteByPatternAsync("feed:read:v1:*");
        watch.Restart();
        var uncachedList = await client.GetStringAsync(query + "&Limit=200");
        var listBefore = watch.Elapsed.TotalMilliseconds;
        Assert.Equal(3, summaries);
        requests.Clear();
        watch.Restart();
        var warmList = await client.GetStringAsync(query + "&Limit=200");
        _lyricsSearchOutput.WriteLine($"Playlist list: uncached {listBefore:F1} ms -> warm {watch.Elapsed.TotalMilliseconds:F1} ms; native pages={requests.Count(request => request.Contains("IncludeItemTypes=Playlist", StringComparison.Ordinal))}; summary reads 1 -> 0; response bytes equal.");
        Assert.Equal(uncachedList, warmList);
        Assert.Equal(3, summaries);
    }

    [Fact]
    public async Task JellyfinLatestDoesNotReadOrMergeServedPlaylists()
    {
        var summaries = 0;
        var requests = new List<string>();
        using var factory = PlaylistBrowseFactory(requests, virtualCount: 36, summariesRead: () => summaries++);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/Items/Latest?UserId=user-1&api_key=fixture-key");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, summaries);
        Assert.Single(requests, request => request.StartsWith("/Items/Latest?", StringComparison.Ordinal));
    }

    [Fact]
    public async Task JellyfinPlaylistBrowseCachesAnAbsentNativeFolder()
    {
        var requests = new List<string>();
        using var factory = PlaylistBrowseFactory(requests, nativeCount: 0);
        using var client = factory.CreateClient();
        const string path = "/Items?UserId=user-1&IncludeItemTypes=Playlist&api_key=fixture-key";
        var first = await client.GetStringAsync(path);
        requests.Clear();
        Assert.Equal(first, await client.GetStringAsync(path));
        Assert.DoesNotContain(requests, request => QueryHelpers.ParseQuery(new Uri("http://fixture" + request).Query)
            .GetValueOrDefault("Limit").ToString() == "1");
    }

    [Fact]
    public async Task JellyfinVirtualPlaylistPreservesAnEmptyNativeParentAcrossCacheReads()
    {
        using var factory = PlaylistBrowseFactory(nativeFolderId: "");
        using var client = factory.CreateClient();
        const string path = "/Items/allstarr-vpl-0198a537719c7ea89e5a17e1f2f963f0?api_key=fixture-key";
        var first = await client.GetStringAsync(path);
        using var body = JsonDocument.Parse(first);
        Assert.Equal("", body.RootElement.GetProperty("ParentId").GetString());
        Assert.Equal(first, await client.GetStringAsync(path));
    }

    [Fact]
    public async Task JellyfinPlaylistCountKeepsTheExistingNativeBrowseCap()
    {
        using var factory = PlaylistBrowseFactory(nativeCount: 10001);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/Items?UserId=user-1&IncludeItemTypes=Playlist&Limit=0&api_key=fixture-key");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(10004, body.RootElement.GetProperty("TotalRecordCount").GetInt32());
        Assert.Empty(body.RootElement.GetProperty("Items").EnumerateArray());
    }
}
