using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using allstarr.Core.Playlists;
using allstarr.Core.Matching;
using allstarr.Core.Music;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using allstarr.Services.Common;
using allstarr.Services.Jellyfin;
using allstarr.Core.Protocols;
using allstarr.Models.Search;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Theory]
    [InlineData(0, 40, true, true, false, null)]
    [InlineData(0, 40, true, false, false, null)]
    [InlineData(0, 40, true, false, false, "Descending")]
    [InlineData(0, 40, true, false, true, null)]
    [InlineData(7, 5, true, true, false, null)]
    [InlineData(0, 20, false, true, false, null)]
    public async Task SearchNativeWindow_MixedPlaylistSearchPreservesTheFullLibraryPage(int start, int limit, bool media, bool totalEnabled, bool countDespiteDisabled, string? sortOrder)
    {
        var calls = new List<(string Path, Dictionary<string, string> Query)>();
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        var clientFields = media ? "ChildCount,CumulativeRunTimeTicks,MediaSources" : "DateCreated,Etag,ParentId,Path,SortName,ProviderIds,ChildCount,Overview";
        var rows = Enumerable.Range(0, 1600).Select(index =>
        {
            var row = SearchWindowRow(index, media);
            if (index % 4 == 3)
            {
                row["Type"] = "Playlist";
                row["ParentId"] = "playlists-folder";
            }
            return row;
        }).ToArray();
        HttpResponseMessage Respond(HttpRequestMessage request)
        {
            var path = request.RequestUri!.AbsolutePath;
            var query = QueryHelpers.ParseQuery(request.RequestUri.Query)
                .ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.OrdinalIgnoreCase);
            calls.Add((path, query));
            if (path == "/Users/Me") return Json(200, """{"Id":"user-1"}""");
            Assert.Equal("/Users/user-1/Items", path);
            var offset = int.Parse(query["StartIndex"]);
            var count = int.Parse(query["Limit"]);
            var selected = rows.OrderBy(row => row["SortName"]!.GetValue<string>(), StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row["Id"]!.GetValue<string>(), StringComparer.Ordinal).AsEnumerable();
            if (query.ContainsKey("SortBy") && query.GetValueOrDefault("SortOrder") == "Descending") selected = selected.Reverse();
            if (query.TryGetValue("Ids", out var ids)) selected = selected.Where(row => ids.Split(',').Contains(row["Id"]!.ToString()));
            var includeMedia = query["Fields"].Split(',').Contains("MediaSources");
            return Json(200, JsonSerializer.Serialize(new
            {
                Items = selected.Skip(offset).Take(count).Select(row =>
                {
                    var copy = (JsonObject)row.DeepClone();
                    if (!includeMedia) copy.Remove("MediaSources");
                    return copy;
                }),
                TotalRecordCount = query.GetValueOrDefault("EnableTotalRecordCount") == "false" && !countDespiteDisabled ? 0 : rows.Length,
                StartIndex = offset
            }));
        }
        var scheduled = 0;
        async Task<HttpResponseMessage> Send(HttpRequestMessage request)
        {
            var reply = Respond(request);
            var delay = 0;
            if (media && start == 0 && limit == 40)
            {
                if (request.RequestUri!.AbsolutePath == "/Users/Me") delay = 150;
                else
                {
                    using var payload = JsonDocument.Parse(await reply.Content.ReadAsStringAsync());
                    var query = QueryHelpers.ParseQuery(request.RequestUri.Query);
                    delay = query["Fields"].ToString().Contains("MediaSources", StringComparison.Ordinal)
                        ? 20 + 7 * payload.RootElement.GetProperty("Items").GetArrayLength() : 20;
                }
                scheduled += delay;
                await Task.Delay(delay);
            }
            return reply;
        }
        var previousRows = new List<JsonNode>();
        var previousWatch = System.Diagnostics.Stopwatch.StartNew();
        using (var verification = new HttpRequestMessage(HttpMethod.Get, "http://fixture/Users/Me"))
        using (await Send(verification)) { }
        for (var offset = 0; offset < rows.Length; offset += 500)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"http://fixture/Users/user-1/Items?SearchTerm=a&StartIndex={offset}&Limit=500&IncludeItemTypes=Audio,MusicAlbum,MusicArtist,Playlist&Fields=" +
                "ChildCount,CumulativeRunTimeTicks,DateCreated,Etag,ParentId,Path,SortName,ProviderIds,ChildCount,Overview" + (media ? ",MediaSources" : "") +
                "&EnableTotalRecordCount=" + totalEnabled.ToString().ToLowerInvariant() + (sortOrder == null ? "" : "&SortOrder=" + sortOrder));
            using var previousResponse = await Send(request);
            var payload = JsonNode.Parse(await previousResponse.Content.ReadAsStringAsync())!;
            previousRows.AddRange(payload["Items"]!.AsArray().Select(row => row!.DeepClone()));
            if (offset + payload["Items"]!.AsArray().Count >= payload["TotalRecordCount"]!.GetValue<int>()) break;
        }
        previousWatch.Stop();
        Assert.Equal(totalEnabled || countDespiteDisabled ? 5 : 2, calls.Count);
        var previousCalls = calls.Count;
        var previousScheduled = scheduled;
        calls.Clear();
        scheduled = 0;
        using var factory = new ProtocolFactory("Jellyfin", Respond, services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
            services.RemoveAll<IHttpClientFactory>();
            services.AddSingleton<IHttpClientFactory>(new StubHttpClientFactory(new SearchWindowHttpHandler(Send)));
        });
        using var client = factory.CreateClient();
        var currentWatch = System.Diagnostics.Stopwatch.StartNew();
        var requestPath = "/Items?userId=user-1&api_key=fixture&searchTerm=a&recursive=true" +
            $"&startIndex={start}&limit={limit}&" + string.Join('&', clientFields.Split(',').Select(field => "fields=" + field)) +
            "&includeItemTypes=Audio&includeItemTypes=MusicAlbum&includeItemTypes=MusicArtist&includeItemTypes=Playlist" +
            "&enableUserData=true&enableImageTypes=Primary&enableTotalRecordCount=" + totalEnabled.ToString().ToLowerInvariant() + (sortOrder == null ? "" : "&SortOrder=" + sortOrder);
        using var response = await client.GetAsync(requestPath);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        currentWatch.Stop();
        if (media && start == 0 && limit == 40)
        {
            Assert.Equal(totalEnabled || countDespiteDisabled ? 11430 : 3670, previousScheduled);
            Assert.Equal(470, scheduled);
            _lyricsSearchOutput.WriteLine($"Native search replay: before {previousWatch.Elapsed.TotalMilliseconds:F1} ms / {previousCalls} calls / {previousRows.Count} media rows; after {currentWatch.Elapsed.TotalMilliseconds:F1} ms / 3 calls / 40 media rows (includes 150 ms verification). Native delay: {previousScheduled - 150} -> 320 ms; same-page direct delay 300 ms.");
        }
        var expected = new JsonObject
        {
            ["Items"] = JsonSerializer.SerializeToNode(previousRows.OrderBy(row => row["SortName"]!.GetValue<string>(), Comparer<string>.Create((left, right) =>
                (sortOrder == "Descending" ? -1 : 1) * StringComparer.OrdinalIgnoreCase.Compare(left, right)))
                .ThenBy(row => row["Id"]!.GetValue<string>(), StringComparer.Ordinal).Skip(start).Take(limit).Select(row =>
                {
                    var copy = row.DeepClone().AsObject();
                    if (media && copy["Type"]!.ToString() == "Playlist")
                    {
                        copy.Remove("SortName");
                        copy.Remove("ParentId");
                    }
                    return copy;
                })),
            ["TotalRecordCount"] = previousRows.Count,
            ["StartIndex"] = start
        };
        Assert.Equal(expected.ToJsonString(), body);
        Assert.Equal(media ? 3 : 2, calls.Count);
        Assert.Equal("/Users/Me", calls[0].Path);
        var native = calls[1].Query;
        Assert.Equal((totalEnabled ? start + limit + 1 : 500).ToString(), native["Limit"]);
        Assert.Equal(totalEnabled.ToString().ToLowerInvariant(), native["EnableTotalRecordCount"]);
        Assert.Equal("0", native["StartIndex"]);
        Assert.Equal("Audio,MusicAlbum,MusicArtist,Playlist", native["IncludeItemTypes"]);
        Assert.Equal("a", native["SearchTerm"]);
        if (totalEnabled) Assert.Equal("SortName", native["SortBy"]);
        else Assert.False(native.ContainsKey("SortBy"));
        Assert.DoesNotContain("MediaSources", native["Fields"]);
        Assert.Equal(media ? "ChildCount,CumulativeRunTimeTicks,SortName,ParentId" : clientFields, native["Fields"]);
        if (media)
        {
            var detail = calls[2].Query;
            Assert.Equal(limit.ToString(), detail["Limit"]);
            Assert.Equal(limit, detail["Ids"].Split(',').Length);
            Assert.Equal(media, detail["Fields"].Contains("MediaSources", StringComparison.Ordinal));
            Assert.False(detail.ContainsKey("SearchTerm"));
        }
        gateway.VerifyNoOtherCalls();
        if (media && start == 0 && limit == 40 && sortOrder == null)
        {
            calls.Clear();
            scheduled = 0;
            var warmWatch = System.Diagnostics.Stopwatch.StartNew();
            using var warm = await client.GetAsync(requestPath);
            Assert.Equal(body, await warm.Content.ReadAsStringAsync());
            warmWatch.Stop();
            Assert.Equal(2, calls.Count);
            Assert.Equal(320, scheduled);
            calls.Clear();
            scheduled = 0;
            using var directRequest = new HttpRequestMessage(HttpMethod.Get,
                "http://fixture/Users/user-1/Items?SearchTerm=a&StartIndex=0&Limit=40&Fields=MediaSources,ChildCount,CumulativeRunTimeTicks");
            var directWatch = System.Diagnostics.Stopwatch.StartNew();
            using var direct = await Send(directRequest);
            await direct.Content.ReadAsStringAsync();
            directWatch.Stop();
            Assert.Single(calls);
            Assert.Equal(300, scheduled);
            _lyricsSearchOutput.WriteLine($"Warm native replay (totals={totalEnabled}, reported={countDespiteDisabled}): route {warmWatch.Elapsed.TotalMilliseconds:F1} ms / 2 calls; direct {directWatch.Elapsed.TotalMilliseconds:F1} ms / 1 call.");
        }
    }

    [Theory]
    [InlineData("Audio", 0, 1, 20)]
    [InlineData("MusicAlbum", 4, 5, 20)]
    [InlineData("Audio,MusicAlbum,MusicArtist", 0, 40, 40)]
    [InlineData("MusicArtist", 0, 5, int.MaxValue)]
    public async Task SearchNativeWindow_MergedSearchKeepsItsExistingRankedPage(string types, int start, int limit, int fetch)
    {
        var queries = new List<Dictionary<string, string>>();
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(service => service.SearchAsync(It.IsAny<ProtocolExecutionContext>(), "a",
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), null))
            .ReturnsAsync(new SearchResult());
        var kinds = types.Split(',');
        var rows = Enumerable.Range(0, 80).Select(index =>
        {
            var row = SearchWindowRow(index, true);
            row["Type"] = kinds[index % kinds.Length];
            return row;
        }).ToArray();
        using var factory = new ProtocolFactory("Jellyfin", request =>
        {
            if (request.RequestUri!.AbsolutePath == "/Users/Me") return Json(200, """{"Id":"user-1"}""");
            Assert.Equal("/Items", request.RequestUri.AbsolutePath);
            var query = QueryHelpers.ParseQuery(request.RequestUri.Query)
                .ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.OrdinalIgnoreCase);
            queries.Add(query);
            return Json(200, JsonSerializer.Serialize(new { Items = rows.Take(int.Parse(query["Limit"])), TotalRecordCount = rows.Length }));
        }, services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
        }, new Dictionary<string, string?> { ["Jellyfin:EnableExternalPlaylists"] = "false" });
        using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/Items?api_key=fixture&SearchTerm=a&IncludeItemTypes={types}&StartIndex={start}&Limit={limit}&Fields=MediaSources,ChildCount");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var query = Assert.Single(queries);
        Assert.Equal(fetch.ToString(), query["Limit"]);
        Assert.Equal("0", query["StartIndex"]);
        Assert.Equal(types, query["IncludeItemTypes"]);
        Assert.Equal("MediaSources,ChildCount", query["Fields"]);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var candidates = rows.Take(fetch).ToArray();
        // All fixture names have equal relevance; merged kinds keep their existing artist/album/song order.
        var ordered = candidates.OrderBy(row => row["Type"]!.GetValue<string>() switch { "MusicArtist" => 0, "MusicAlbum" => 1, _ => 2 });
        var expected = ordered.Skip(start).Take(limit).ToArray();
        Assert.Equal(expected.Select(row => row.ToJsonString()),
            body.RootElement.GetProperty("Items").EnumerateArray().Select(row => JsonNode.Parse(row.GetRawText())!.ToJsonString()));
        Assert.Equal(candidates.Length, body.RootElement.GetProperty("TotalRecordCount").GetInt32());
    }

    [Fact]
    public async Task SearchNativeWindow_ReadsNativeRowsAndServedSummariesTogether()
    {
        var nativeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var summariesStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var viewsStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        JellyfinProxyService? summaryProxy = null;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var id = Guid.Parse("0198a537-719c-7ea8-9e5a-17e1f2f963f0");
        var served = new VirtualPlaylistReadModel(PlaylistVirtualizationService.CreateProtocolId(id), id, id,
            "a 000 served", null, null, "listenbrainz", "weekly", "1", PlaylistLinkMode.Virtual, [])
        { SummaryTrackCount = 2, SummaryDurationMilliseconds = 60000 };
        var summaries = new Mock<IPlaylistVirtualizationService>();
        summaries.Setup(service => service.ListAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                summariesStarted.TrySetResult();
                var (views, status) = await summaryProxy!.GetJsonAsync("UserViews");
                views?.Dispose();
                Assert.Equal(200, status);
                return new[] { served, served with { ProtocolId = "allstarr-vpl-unmatched", Name = "Other" } };
            });
        var requests = new List<string>();
        using var factory = new ProtocolFactory("Jellyfin", _ => throw new InvalidOperationException(), services =>
        {
            services.RemoveAll<IPlaylistVirtualizationService>();
            services.AddSingleton(summaries.Object);
            services.RemoveAll<IHttpClientFactory>();
            services.AddSingleton<IHttpClientFactory>(new StubHttpClientFactory(new SearchWindowHttpHandler(async request =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (path == "/Users/Me") return Json(200, """{"Id":"user-1"}""");
                if (path == "/Users/user-1/Views") return Json(200,
                    """{"Items":[{"Id":"playlists-folder","Type":"PlaylistsFolder","CollectionType":"playlists"}]}""");
                requests.Add(request.RequestUri.PathAndQuery);
                if (path == "/UserViews")
                {
                    viewsStarted.TrySetResult();
                    await release.Task;
                    await Task.Delay(150);
                    return Json(200, """{"Items":[]}""");
                }
                nativeStarted.TrySetResult();
                await release.Task;
                await Task.Delay(150);
                var query = QueryHelpers.ParseQuery(request.RequestUri.Query);
                var rows = new[] { SearchWindowRow(0, false), SearchWindowRow(1, false) };
                var selected = query.TryGetValue("Ids", out var ids)
                    ? rows.Where(row => ids.ToString().Split(',').Contains(row["Id"]!.ToString())) : rows;
                return Json(200, JsonSerializer.Serialize(new { Items = selected, TotalRecordCount = 2, StartIndex = 0 }));
            })));
        });
        using var client = factory.CreateClient();
        summaryProxy = factory.Services.GetRequiredService<JellyfinProxyService>();
        using var warm = await client.GetAsync("/Users/user-1/Views?api_key=fixture");
        var pending = client.GetAsync("/Items?api_key=fixture&SearchTerm=a&IncludeItemTypes=Audio,MusicAlbum,MusicArtist,Playlist&Limit=2&Fields=DateCreated,Etag,ParentId,Path,SortName,ProviderIds,ChildCount,Overview");
        try { await Task.WhenAll(nativeStarted.Task, summariesStarted.Task, viewsStarted.Task).WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { release.TrySetResult(); }
        using var response = await pending;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(served.ProtocolId, body.RootElement.GetProperty("Items")[0].GetProperty("Id").GetString());
        Assert.Equal(3, body.RootElement.GetProperty("TotalRecordCount").GetInt32());
        Assert.DoesNotContain(body.RootElement.GetProperty("Items").EnumerateArray(), row => row.GetProperty("Id").GetString() == "allstarr-vpl-unmatched");
        Assert.Equal(2, requests.Count);
        Assert.Single(requests, path => path.StartsWith("/UserViews", StringComparison.Ordinal));
        Assert.Single(requests, path => path.StartsWith("/Items?", StringComparison.Ordinal));
        summaries.Verify(service => service.ListAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SearchNativeWindow_TiedBoundaryKeepsTheExistingIdOrderAndRemembersEachAlbumOnce()
    {
        var writes = new List<string>();
        var cache = new Mock<IApplicationCache>();
        cache.Setup(value => value.SetStringAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan?>()))
            .Callback<string, string, TimeSpan?>((key, _, _) => writes.Add(key)).ReturnsAsync(true);
        var calls = new List<Dictionary<string, string>>();
        var rows = Enumerable.Range(0, 600).Select(index =>
        {
            var row = SearchWindowRow(index, true);
            row["Name"] = "a";
            row["SortName"] = "a";
            row["Type"] = "MusicAlbum";
            return row;
        }).ToArray();
        using var factory = new ProtocolFactory("Jellyfin", request =>
        {
            if (request.RequestUri!.AbsolutePath == "/Users/Me") return Json(200, """{"Id":"user-1"}""");
            var query = QueryHelpers.ParseQuery(request.RequestUri.Query)
                .ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.OrdinalIgnoreCase);
            calls.Add(query);
            var selected = rows.Reverse();
            if (query.TryGetValue("Ids", out var ids)) selected = selected.Where(row => ids.Split(',').Contains(row["Id"]!.ToString()));
            return Json(200, JsonSerializer.Serialize(new
            {
                Items = selected.Take(int.Parse(query["Limit"])).Select(row =>
                {
                    var copy = (JsonObject)row.DeepClone();
                    if (!query["Fields"].Contains("MediaSources", StringComparison.Ordinal)) copy.Remove("MediaSources");
                    return copy;
                }),
                TotalRecordCount = rows.Length,
                StartIndex = 0
            }));
        }, services =>
        {
            services.RemoveAll<IApplicationCache>();
            services.AddSingleton(cache.Object);
        });
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/Items?api_key=fixture&SearchTerm=a&IncludeItemTypes=MusicAlbum,Playlist&Limit=1&Fields=MediaSources&EnableTotalRecordCount=true");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(rows[0].ToJsonString(), JsonNode.Parse(body.RootElement.GetProperty("Items")[0].GetRawText())!.ToJsonString());
        Assert.Equal(new[] { "2", "10000", "1" }, calls.Select(call => call["Limit"]));
        Assert.Equal(600, writes.Count(key => key.StartsWith("jellyfin:catalog-type:", StringComparison.Ordinal)));
        Assert.Equal(writes.Count, writes.Distinct().Count());
    }

    [Fact]
    public async Task SearchNativeWindow_OwnedCopiesUseOneAudioIdBatchAfterTheNativePrefix()
    {
        var gateway = new Mock<IProtocolProviderGateway>();
        var songs = Enumerable.Range(0, 2).Select(index => new Song
        {
            Id = $"ext-deezer-song-{index}",
            ExternalId = index.ToString(),
            ExternalProvider = "deezer",
            Title = $"a {index}"
        }).ToArray();
        gateway.Setup(service => service.SearchAsync(It.IsAny<ProtocolExecutionContext>(), "a", 20, 0, 0, null))
            .ReturnsAsync(new SearchResult { Songs = songs.ToList() });
        var library = new Mock<ILibraryIndexService>();
        library.Setup(service => service.FindKnownRecordingTracksAsync(It.IsAny<ProtocolExecutionContext>(),
                It.IsAny<IReadOnlyList<LibraryRecordingLookup>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(songs.Select((song, index) => (song.Id, Native: $"native-{index}"))
                .ToDictionary(pair => pair.Id, pair => pair.Native));
        var calls = new List<(string Path, Dictionary<string, string> Query)>();
        using var factory = new ProtocolFactory("Jellyfin", request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            var query = QueryHelpers.ParseQuery(request.RequestUri.Query)
                .ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.OrdinalIgnoreCase);
            calls.Add((path, query));
            if (path == "/Users/Me") return Json(200, """{"Id":"user-1"}""");
            if (path == "/Items") return Json(200, """{"Items":[],"TotalRecordCount":0}""");
            Assert.Equal("/Users/user-1/Items", path);
            return Json(200, JsonSerializer.Serialize(new
            {
                Items = Enumerable.Range(0, 2).Select(index => new { Id = $"native-{index}", Type = "Audio", Name = $"a {index}" }),
                TotalRecordCount = 2
            }));
        }, services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
            services.RemoveAll<MusicSearchService>();
            services.AddSingleton(new MusicSearchService(gateway.Object, library.Object));
        });
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/Items?api_key=fixture&SearchTerm=a&IncludeItemTypes=Audio&Limit=2&Fields=MediaSources");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, calls.Count);
        Assert.Equal("20", calls[1].Query["Limit"]);
        Assert.Equal("MediaSources", calls[1].Query["Fields"]);
        var batch = calls[2];
        Assert.Equal("/Users/user-1/Items", batch.Path);
        Assert.Equal("2", batch.Query["Limit"]);
        Assert.Equal("Audio", batch.Query["IncludeItemTypes"]);
        Assert.Equal(new[] { "native-0", "native-1" }, batch.Query["Ids"].Split(',').Order());
        Assert.False(batch.Query.ContainsKey("Fields"));
        library.Verify(service => service.FindKnownRecordingTracksAsync(It.IsAny<ProtocolExecutionContext>(),
            It.IsAny<IReadOnlyList<LibraryRecordingLookup>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SearchNativeWindow_FavoritesKeepTheClientFilteredQueryWithoutCatalogReads()
    {
        var calls = new List<(string Path, Dictionary<string, string> Query)>();
        using var factory = new ProtocolFactory("Jellyfin", request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            var query = QueryHelpers.ParseQuery(request.RequestUri.Query)
                .ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.OrdinalIgnoreCase);
            calls.Add((path, query));
            return path == "/Users/Me" ? Json(200, """{"Id":"user-1"}""") : Json(200, """{"Items":[],"TotalRecordCount":0}""");
        });
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/Items?api_key=fixture&SearchTerm=a&IsFavorite=true&IncludeItemTypes=Audio,MusicAlbum,MusicArtist,Playlist&Limit=40&Fields=MediaSources");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, calls.Count);
        Assert.Equal("/Items", calls[1].Path);
        Assert.Equal("a", calls[1].Query["SearchTerm"]);
        Assert.Equal("40", calls[1].Query["Limit"]);
        Assert.Equal("MediaSources", calls[1].Query["Fields"]);
        Assert.Equal("Audio,MusicAlbum,MusicArtist,Playlist", calls[1].Query["IncludeItemTypes"]);
    }

    [Fact]
    public async Task SearchNativeWindow_CappedLibraryKeepsTheSameNativeSetBeforeSorting()
    {
        var calls = new List<Dictionary<string, string>>();
        var rows = Enumerable.Range(0, 10001).Select(index => new
        {
            Id = $"native-{index:D5}",
            Type = "MusicArtist",
            Name = $"a {10000 - index:D5}",
            SortName = $"a {10000 - index:D5}"
        }).ToArray();
        using var factory = new ProtocolFactory("Jellyfin", request =>
        {
            if (request.RequestUri!.AbsolutePath == "/Users/Me") return Json(200, """{"Id":"user-1"}""");
            var query = QueryHelpers.ParseQuery(request.RequestUri.Query)
                .ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.OrdinalIgnoreCase);
            calls.Add(query);
            var selected = rows.AsEnumerable();
            if (query.ContainsKey("SortBy")) selected = selected.OrderBy(row => row.SortName, StringComparer.OrdinalIgnoreCase);
            if (query.TryGetValue("Ids", out var ids)) selected = selected.Where(row => ids.Split(',').Contains(row.Id));
            return Json(200, JsonSerializer.Serialize(new { Items = selected.Take(int.Parse(query["Limit"])), TotalRecordCount = rows.Length }));
        });
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/Items?api_key=fixture&SearchTerm=a&IncludeItemTypes=MusicArtist,Playlist&Limit=1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("native-09999", Assert.Single(body.RootElement.GetProperty("Items").EnumerateArray()).GetProperty("Id").GetString());
        Assert.Equal(10000, body.RootElement.GetProperty("TotalRecordCount").GetInt32());
        Assert.Equal(new[] { "2", "10000", "1" }, calls.Select(call => call["Limit"]));
        Assert.False(calls[1].ContainsKey("SortBy"));
    }

    private sealed class SearchWindowHttpHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }

    private static JsonObject SearchWindowRow(int index, bool media)
    {
        var row = new JsonObject
        {
            ["Id"] = $"native-{index:D4}",
            ["Type"] = index % 3 == 0 ? "MusicArtist" : index % 3 == 1 ? "MusicAlbum" : "Audio",
            ["Name"] = $"a track {1599 - index:D4}",
            ["SortName"] = $"a track {1599 - index:D4}",
            ["Artists"] = new JsonArray("Artist"),
            ["AlbumArtist"] = "Artist",
            ["Album"] = "Collection",
            ["RunTimeTicks"] = 1800000000L,
            ["ChildCount"] = 1,
            ["UnknownFutureField"] = new JsonObject { ["Keep"] = index }
        };
        if (media) row["MediaSources"] = new JsonArray(new JsonObject { ["Id"] = $"source-{index}", ["Bitrate"] = 900000 });
        return row;
    }
}
