using System.Net;
using System.Text.Json;
using allstarr.Core.Playlists;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Theory]
    [InlineData("/Users/user-1/Items")]
    [InlineData("/Items?UserId=user-1")]
    [InlineData("/Playlists?UserId=user-1")]
    public async Task JellyfinPlaylistBrowse_NoLimitMergesTwentyNativeAndFourVirtualRows(string path)
    {
        using var factory = PlaylistBrowseFactory();
        using var client = factory.CreateClient();
        var separator = path.Contains('?') ? "&" : "?";
        using var response = await client.GetAsync(path + separator + "IncludeItemTypes=Playlist&Recursive=true&Fields=DateCreated,Etag,ParentId,Path,SortName,ProviderIds,ChildCount,Overview&api_key=fixture-key");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var rows = body.RootElement.GetProperty("Items").EnumerateArray().ToArray();
        Assert.Equal(24, rows.Length);
        Assert.Equal(24, body.RootElement.GetProperty("TotalRecordCount").GetInt32());
        Assert.Equal(rows.Select(row => row.GetProperty("SortName").GetString()).Order(StringComparer.OrdinalIgnoreCase),
            rows.Select(row => row.GetProperty("SortName").GetString()));
        Assert.Equal("Added virtual 0", rows[0].GetProperty("Name").GetString());
        Assert.Equal("playlists-folder", rows[0].GetProperty("ParentId").GetString());
        using var nativeShape = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Jellyfin", "native-playlist-summary.json")));
        var keys = nativeShape.RootElement.EnumerateObject().Select(property => property.Name).Order().ToArray();
        Assert.All(rows, row => Assert.Equal(keys, row.EnumerateObject().Select(property => property.Name).Order()));
    }

    [Theory]
    [InlineData("SortName", "Ascending")]
    [InlineData("SortName", "Descending")]
    [InlineData("DateCreated", "Descending")]
    [InlineData("DateLastContentAdded", "Descending")]
    [InlineData("Random", "Ascending")]
    [InlineData("Unrecognized", "Ascending")]
    public async Task JellyfinPlaylistBrowse_PagesContainEveryPlaylistExactlyOnce(string sort, string order)
    {
        using var factory = PlaylistBrowseFactory();
        using var client = factory.CreateClient();
        var ids = new List<string>();
        for (var start = 0; start < 28; start += 7)
        {
            using var response = await client.GetAsync($"/Users/user-1/Items?IncludeItemTypes=Playlist&StartIndex={start}&Limit=7&SortBy={sort}&SortOrder={order}&api_key=fixture-key");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(24, body.RootElement.GetProperty("TotalRecordCount").GetInt32());
            ids.AddRange(body.RootElement.GetProperty("Items").EnumerateArray().Select(row => row.GetProperty("Id").GetString()!));
        }
        Assert.Equal(24, ids.Count);
        Assert.Equal(24, ids.Distinct().Count());
        Assert.Equal(4, ids.Count(id => id.StartsWith(PlaylistVirtualizationService.IdPrefix, StringComparison.Ordinal)));
        if (sort == "Unrecognized")
            Assert.Equal(Enumerable.Range(0, 20).Select(index => $"native-{index:00}"), ids.Where(id => id.StartsWith("native-", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("SearchTerm=Added%20virtual", 4)]
    [InlineData("NameStartsWith=A", 4)]
    [InlineData("Filters=IsFavorite", 0)]
    [InlineData("MediaTypes=Audio", 24)]
    [InlineData("MediaTypes=Video", 0)]
    [InlineData("ParentId=playlists-folder", 24)]
    [InlineData("ParentId=other-folder", 0)]
    [InlineData("IncludeItemTypes=MusicAlbum,Playlist", 24)]
    [InlineData("Ids=allstarr-vpl-0198a537719c7ea89e5a17e1f2f963f0", 1)]
    public async Task JellyfinPlaylistBrowse_FiltersVirtualRowsWithNativeRows(string filter, int expected)
    {
        using var factory = PlaylistBrowseFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/Users/user-1/Items?IncludeItemTypes=Playlist&{filter}&api_key=fixture-key");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expected, body.RootElement.GetProperty("TotalRecordCount").GetInt32());
        Assert.Equal(expected, body.RootElement.GetProperty("Items").GetArrayLength());
    }

    [Theory]
    [InlineData("/Items/{0}")]
    [InlineData("/Users/user-1/Items/{0}")]
    [InlineData("/Playlists/{0}")]
    [InlineData("/Playlists/{0}/Items")]
    [InlineData("/Playlists/{0}/Users")]
    public async Task JellyfinVirtualPlaylist_ReadRoutesRemainAvailable(string route)
    {
        using var factory = PlaylistBrowseFactory();
        using var client = factory.CreateClient();
        var path = string.Format(route, "allstarr-vpl-0198a537719c7ea89e5a17e1f2f963f0");
        using var response = await client.GetAsync(path + "?api_key=fixture-key");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (route.Contains("Items/{0}", StringComparison.Ordinal))
        {
            Assert.Equal("playlists-folder", body.RootElement.GetProperty("ParentId").GetString());
            foreach (var key in new[] { "DateCreated", "Etag", "ImageBlurHashes", "Path", "SortName", "ProviderIds", "UserData" })
                Assert.True(body.RootElement.TryGetProperty(key, out _));
        }
    }

    [Fact]
    public async Task JellyfinVirtualPlaylist_IdLookupDoesNotRequireAnItemType()
    {
        using var factory = PlaylistBrowseFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/Items?UserId=user-1&Ids=allstarr-vpl-0198a537719c7ea89e5a17e1f2f963f0&api_key=fixture-key");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Single(body.RootElement.GetProperty("Items").EnumerateArray());
        Assert.Equal(1, body.RootElement.GetProperty("TotalRecordCount").GetInt32());
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(1, 21)]
    public async Task JellyfinSearch_NoLimitKeepsTheExistingProviderWindow(int start, int expected)
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.SearchAsync(It.IsAny<ProtocolExecutionContext>(), "fixture", expected, 0, 0, null))
            .ReturnsAsync(new allstarr.Models.Search.SearchResult());
        using var factory = new ProtocolFactory("Jellyfin", request => request.RequestUri!.AbsolutePath == "/Users/Me"
            ? Json(200, """{"Id":"user-1"}""") : Json(200, """{"Items":[],"TotalRecordCount":0}"""), services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
        }, new Dictionary<string, string?> { ["Jellyfin:EnableExternalPlaylists"] = "false" });
        using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/Items?SearchTerm=fixture&IncludeItemTypes=Audio&StartIndex={start}&api_key=fixture-key");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        gateway.VerifyAll();
    }

    [Theory]
    [InlineData("SortName", "Ascending")]
    [InlineData("DateCreated,SortName", "Descending,Ascending")]
    public async Task JellyfinPlaylistBrowse_FinerPlayerUsesCachedFolderAndPagesMergedRows(string sort, string order)
    {
        var requests = new List<string>();
        using var factory = PlaylistBrowseFactory(requests);
        using var client = factory.CreateClient();
        using var warm = await client.GetAsync("/Items?IncludeItemTypes=Playlist&api_key=fixture-key");
        requests.Clear();
        var query = $"ParentId=playlists-folder&MediaTypes=Audio&Fields=DateCreated,SortName&ImageTypeLimit=1&SortBy={sort}&SortOrder={order}&userId=user-1&api_key=fixture-key";
        using var completeResponse = await client.GetAsync("/Items?" + query);
        using var complete = JsonDocument.Parse(await completeResponse.Content.ReadAsStringAsync());
        Assert.Equal(24, complete.RootElement.GetProperty("TotalRecordCount").GetInt32());
        var rows = complete.RootElement.GetProperty("Items").EnumerateArray().ToArray();
        Assert.Equal(4, rows.Count(row => row.GetProperty("Id").GetString()!.StartsWith("allstarr-vpl-", StringComparison.Ordinal)));
        // All fixture dates tie within native and virtual groups; the secondary name key remains ascending.
        Assert.Equal(rows.Where(row => row.GetProperty("Id").GetString()!.StartsWith("allstarr-vpl-", StringComparison.Ordinal))
            .Select(row => row.GetProperty("SortName").GetString()).Order(StringComparer.OrdinalIgnoreCase),
            rows.Where(row => row.GetProperty("Id").GetString()!.StartsWith("allstarr-vpl-", StringComparison.Ordinal)).Select(row => row.GetProperty("SortName").GetString()));
        if (sort.StartsWith("DateCreated", StringComparison.Ordinal))
            Assert.Equal("native-00", rows[0].GetProperty("Id").GetString());
        else Assert.StartsWith("allstarr-vpl-", rows[0].GetProperty("Id").GetString());
        using var pageResponse = await client.GetAsync("/Items?" + query + "&StartIndex=2&Limit=5");
        using var page = JsonDocument.Parse(await pageResponse.Content.ReadAsStringAsync());
        Assert.Equal(rows.Skip(2).Take(5).Select(row => row.GetProperty("Id").GetString()),
            page.RootElement.GetProperty("Items").EnumerateArray().Select(row => row.GetProperty("Id").GetString()));
        Assert.DoesNotContain(requests, request => QueryHelpers.ParseQuery(new Uri("http://fixture" + request).Query).GetValueOrDefault("Limit").ToString() == "1");
        Assert.All(requests.Where(request => request.Contains("IncludeItemTypes=Playlist", StringComparison.Ordinal)),
            request => Assert.False(QueryHelpers.ParseQuery(new Uri("http://fixture" + request).Query).ContainsKey("ParentId")));
    }

    [Theory]
    [InlineData("SortName&SortOrder=Ascending&Limit=100")]
    [InlineData("DateModified&SortOrder=Descending&Recursive=true&StartIndex=0&Limit=100")]
    public async Task JellyfinPlaylistBrowse_MusiverMusicParentMeansAllPlaylists(string sort)
    {
        var requests = new List<string>();
        using var factory = PlaylistBrowseFactory(requests);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/Users/user-1/Items?ParentId=music-library&IncludeItemTypes=Playlist&MediaTypes=Audio&SortBy={sort}&Fields=ChildCount,Overview&api_key=fixture-key");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var rows = body.RootElement.GetProperty("Items").EnumerateArray().ToArray();
        Assert.Equal(24, rows.Length);
        Assert.Contains(rows, row => row.GetProperty("Id").GetString() == "native-00");
        Assert.Contains(rows, row => row.GetProperty("Id").GetString() == "native-01");
        Assert.Equal(4, rows.Count(row => row.GetProperty("Id").GetString()!.StartsWith("allstarr-vpl-", StringComparison.Ordinal)));
        Assert.DoesNotContain(requests, request => QueryHelpers.ParseQuery(new Uri("http://fixture" + request).Query).ContainsKey("ParentId"));
        using var repeat = await client.GetAsync($"/Users/user-1/Items?ParentId=music-library&IncludeItemTypes=Playlist&SortBy={sort}&api_key=fixture-key");
        Assert.Single(requests, request => request.StartsWith("/Users/user-1/Views", StringComparison.Ordinal));
    }

    [Fact]
    public async Task JellyfinPlaylistBrowse_OrdinaryParentDoesNotProbePlaylistFolder()
    {
        var requests = new List<string>();
        using var factory = PlaylistBrowseFactory(requests);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/Items?ParentId=other-folder&MediaTypes=Audio&api_key=fixture-key");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(requests, request => request.StartsWith("/Items?", StringComparison.Ordinal));
        Assert.DoesNotContain(requests, request => request.Contains("Fields=ParentId", StringComparison.Ordinal));
        Assert.DoesNotContain(requests, request => request.Contains("/Views", StringComparison.Ordinal) || request.StartsWith("/UserViews", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("&Fields=ItemCounts")]
    [InlineData("&Fields=RecursiveItemCount")]
    [InlineData("&Fields=ChildCount&Fields=Genres&Fields=DateCreated&Fields=ParentId&Fields=Overview")]
    public async Task JellyfinPlaylistBrowse_ClientFieldsHaveNativeKeySetsAndRealCounts(string fields)
    {
        using var factory = PlaylistBrowseFactory();
        using var client = factory.CreateClient();
        foreach (var path in new[] { "/users/user-1/items", "/Items?UserId=user-1", "/Playlists?UserId=user-1" })
        {
            using var response = await client.GetAsync(path + (path.Contains('?') ? "&" : "?") +
                "IncludeItemTypes=Playlist&MediaTypes=Audio&SortBy=SortName&StartIndex=0&Limit=100" + fields + "&api_key=fixture-key");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var rows = body.RootElement.GetProperty("Items").EnumerateArray().ToArray();
            var native = rows.First(row => row.GetProperty("Id").GetString() == "native-00");
            var virtualRow = rows.First(row => row.GetProperty("Id").GetString()!.StartsWith("allstarr-vpl-", StringComparison.Ordinal));
            Assert.Equal(native.EnumerateObject().Select(property => property.Name).Order(), virtualRow.EnumerateObject().Select(property => property.Name).Order());
            Assert.Equal(2, virtualRow.GetProperty("ChildCount").GetInt32());
            Assert.Equal(60000 * TimeSpan.TicksPerMillisecond, virtualRow.GetProperty("RunTimeTicks").GetInt64());
            Assert.Equal(JsonValueKind.Object, virtualRow.GetProperty("ImageBlurHashes").ValueKind);
            Assert.True(virtualRow.TryGetProperty("Overview", out _));
            Assert.Equal(fields.Contains("RecursiveItemCount", StringComparison.Ordinal), virtualRow.TryGetProperty("RecursiveItemCount", out var count));
            if (count.ValueKind != JsonValueKind.Undefined) Assert.Equal(2, count.GetInt32());
        }
        using var nativeResponse = await client.GetAsync("/Users/user-1/Items/00112233445566778899aabbccddeeff?api_key=fixture-key" + fields);
        using var virtualResponse = await client.GetAsync("/users/user-1/items/allstarr-vpl-0198a537719c7ea89e5a17e1f2f963f0?api_key=fixture-key" + fields);
        Assert.Equal(HttpStatusCode.OK, nativeResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, virtualResponse.StatusCode);
        using var nativeItem = JsonDocument.Parse(await nativeResponse.Content.ReadAsStringAsync());
        using var virtualItem = JsonDocument.Parse(await virtualResponse.Content.ReadAsStringAsync());
        Assert.Equal(nativeItem.RootElement.EnumerateObject().Select(property => property.Name).Order(),
            virtualItem.RootElement.EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal(2, virtualItem.RootElement.GetProperty("ChildCount").GetInt32());
        Assert.Equal(60000 * TimeSpan.TicksPerMillisecond, virtualItem.RootElement.GetProperty("RunTimeTicks").GetInt64());
    }

    [Theory]
    [InlineData("Audio,Unknown")]
    [InlineData("Audio%2C%20Unknown")]
    public async Task JellyfinPlaylistBrowse_RequestedCoverRatioMatchesNativeKeys(string mediaTypes)
    {
        using var factory = PlaylistBrowseFactory();
        using var client = factory.CreateClient();
        using var warm = await client.GetAsync("/Users/user-1/Views?api_key=fixture-key");
        using var response = await client.GetAsync($"/Items?Fields=PrimaryImageAspectRatio,SortName,Path,ChildCount,MediaSourceCount,PrimaryImageAspectRatio&ImageTypeLimit=1&Limit=100&MediaTypes={mediaTypes}&ParentId=playlists-folder&SortBy=SortName&SortOrder=Ascending&StartIndex=0&userId=user-1&api_key=fixture-key");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var rows = body.RootElement.GetProperty("Items").EnumerateArray().ToArray();
        Assert.Equal(24, rows.Length);
        var keys = rows.First(row => row.GetProperty("Id").GetString() == "native-00").EnumerateObject().Select(property => property.Name).Order().ToArray();
        Assert.All(rows, row => Assert.Equal(keys, row.EnumerateObject().Select(property => property.Name).Order()));
        Assert.All(rows.Where(row => row.GetProperty("Id").GetString()!.StartsWith("allstarr-vpl-", StringComparison.Ordinal)),
            row => Assert.Equal(1d, row.GetProperty("PrimaryImageAspectRatio").GetDouble()));
    }

    [Theory]
    [InlineData("music-library", 1, 1)]
    [InlineData("playlists-folder", 1, 0)]
    [InlineData("other-folder", 0, 1)]
    public async Task JellyfinVirtualPlaylist_IdsLookupAcceptsMusicLibraryWithColdViews(string parent, int expected, int views)
    {
        var requests = new List<string>();
        using var factory = PlaylistBrowseFactory(requests);
        using var client = factory.CreateClient();
        const string id = "allstarr-vpl-0198a537719c7ea89e5a17e1f2f963f0";
        using var response = await client.GetAsync($"/Users/user-1/Items?MediaTypes=Audio&ParentId={parent}&Recursive=true&SortBy=SortName&SortOrder=Ascending&StartIndex=0&IncludeItemTypes=Playlist&Fields=PrimaryImageAspectRatio,BasicSyncInfo,DateCreated,DateLastMediaAdded&Ids={id}&api_key=fixture-key");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expected, body.RootElement.GetProperty("TotalRecordCount").GetInt32());
        Assert.Equal(expected, body.RootElement.GetProperty("Items").GetArrayLength());
        if (expected == 1)
        {
            var row = body.RootElement.GetProperty("Items")[0];
            Assert.Equal(id, row.GetProperty("Id").GetString());
        }
        Assert.Equal(views, requests.Count(request => request.StartsWith("/Users/user-1/Views", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task JellyfinPlaylist_IdsLookupPreservesNativeParentQueryWithoutViews()
    {
        var requests = new List<string>();
        using var factory = PlaylistBrowseFactory(requests);
        using var client = factory.CreateClient();
        const string id = "00112233445566778899aabbccddeeff";
        using var response = await client.GetAsync($"/Users/user-1/Items?MediaTypes=Audio&ParentId=music-library&Recursive=true&SortBy=SortName&SortOrder=Ascending&StartIndex=0&IncludeItemTypes=Playlist&Fields=PrimaryImageAspectRatio,BasicSyncInfo,DateCreated,DateLastMediaAdded&Ids={id}&api_key=fixture-key");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(id, Assert.Single(body.RootElement.GetProperty("Items").EnumerateArray()).GetProperty("Id").GetString());
        Assert.Equal(1, body.RootElement.GetProperty("TotalRecordCount").GetInt32());
        var native = Assert.Single(requests, request => request.StartsWith("/Users/user-1/Items?", StringComparison.Ordinal));
        var query = QueryHelpers.ParseQuery(new Uri("http://fixture" + native).Query);
        Assert.Equal("music-library", query["ParentId"].ToString());
        Assert.Equal(id, query["Ids"].ToString());
        Assert.DoesNotContain(requests, request => request.Contains("/Views", StringComparison.Ordinal));
    }

    private static ProtocolFactory PlaylistBrowseFactory(List<string>? requests = null)
    {
        var playlists = Enumerable.Range(0, 4).Select(index =>
        {
            var id = Guid.Parse($"0198a537-719c-7ea8-9e5a-17e1f2f963f{index}");
            return new VirtualPlaylistReadModel(PlaylistVirtualizationService.CreateProtocolId(id), id, id,
                $"Added virtual {index}", null, null, "listenbrainz", "createdfor:weekly-jams", "1", PlaylistLinkMode.Virtual, [])
            { SummaryTrackCount = 2, SummaryDurationMilliseconds = 60000, MixCover = new(id.ToString(), 1, "external", Name: "Added virtual") };
        }).ToArray();
        var service = new Mock<IPlaylistVirtualizationService>();
        service.Setup(item => item.ReadAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProtocolExecutionContext _, string id, CancellationToken _) => playlists.SingleOrDefault(item => item.ProtocolId == id));
        service.Setup(item => item.ListAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<CancellationToken>())).ReturnsAsync(playlists);
        return new ProtocolFactory("Jellyfin", request =>
        {
            requests?.Add(request.RequestUri!.PathAndQuery);
            if (request.RequestUri!.AbsolutePath == "/Users/Me") return Json(200, """{"Id":"user-1"}""");
            if (request.RequestUri.AbsolutePath is "/Users/user-1/Views" or "/UserViews")
                return Json(200, """{"Items":[{"Id":"music-library","CollectionType":"music","Name":"Music"},{"Id":"playlists-folder","CollectionType":"playlists","Type":"PlaylistsFolder","Name":"Playlists"}],"TotalRecordCount":2}""");
            var query = QueryHelpers.ParseQuery(request.RequestUri.Query);
            if (query.GetValueOrDefault("SortBy") == "Unrecognized") return Json(400, """{"Message":"Unknown sort"}""");
            var fixture = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Jellyfin", "native-playlist-summary.json"));
            var native = Enumerable.Range(0, 20).Select(index =>
            {
                var row = JsonSerializer.Deserialize<Dictionary<string, object?>>(fixture)!;
                if (query.GetValueOrDefault("Fields").ToString().Contains("PrimaryImageAspectRatio", StringComparison.Ordinal)) row["PrimaryImageAspectRatio"] = 1d;
                row["Id"] = $"native-{index:00}";
                row["Name"] = $"Native {index:00}";
                row["SortName"] = $"native {index:0000000000}";
                row["ParentId"] = "playlists-folder";
                row["Path"] = $"/var/lib/jellyfin/data/playlists/Native {index:00}";
                if (query.GetValueOrDefault("Fields").ToString().Contains("Genres", StringComparison.Ordinal))
                {
                    row["Genres"] = Array.Empty<string>();
                    row["GenreItems"] = Array.Empty<object>();
                }
                if (query.GetValueOrDefault("Fields").ToString().Contains("RecursiveItemCount", StringComparison.Ordinal)) row["RecursiveItemCount"] = 30;
                row["UserData"] = new { PlaybackPositionTicks = 0, PlayCount = 0, IsFavorite = false, Played = false, Key = $"native-{index:00}", ItemId = $"native-{index:00}" };
                return row;
            }).ToArray();
            if (request.RequestUri.AbsolutePath.EndsWith("/Items/00112233445566778899aabbccddeeff", StringComparison.OrdinalIgnoreCase))
            {
                native[0]["Id"] = "00112233445566778899aabbccddeeff";
                native[0]["PrimaryImageAspectRatio"] = 1d;
                native[0]["Genres"] = Array.Empty<string>();
                native[0]["GenreItems"] = Array.Empty<object>();
                return Json(200, JsonSerializer.Serialize(native[0]));
            }
            if (query.GetValueOrDefault("ids") == "00112233445566778899aabbccddeeff")
            {
                native[0]["Id"] = "00112233445566778899aabbccddeeff";
                return Json(200, JsonSerializer.Serialize(new { Items = new[] { native[0] }, TotalRecordCount = 1 }));
            }
            IEnumerable<Dictionary<string, object?>> filtered = native;
            if (query.GetValueOrDefault("SearchTerm").ToString().Length > 0) filtered = filtered.Where(row => row["Name"]!.ToString()!.Contains(query.GetValueOrDefault("SearchTerm").ToString(), StringComparison.OrdinalIgnoreCase));
            if (query.GetValueOrDefault("NameStartsWith").ToString().Length > 0) filtered = filtered.Where(row => row["Name"]!.ToString()!.StartsWith(query.GetValueOrDefault("NameStartsWith").ToString(), StringComparison.OrdinalIgnoreCase));
            if (query.GetValueOrDefault("Filters") == "IsFavorite" || query.GetValueOrDefault("MediaTypes") == "Video" || query.GetValueOrDefault("ParentId") == "other-folder" || query.GetValueOrDefault("Ids").ToString().Length > 0) filtered = [];
            if (query.GetValueOrDefault("ParentId") == "music-library") filtered = filtered.Skip(2);
            var all = filtered.ToArray();
            var start = int.TryParse(query.GetValueOrDefault("StartIndex"), out var offset) ? offset : 0;
            var limit = int.TryParse(query.GetValueOrDefault("Limit"), out var size) ? size : int.MaxValue;
            // The fixture deliberately caps upstream pages to exercise complete native reads.
            return Json(200, JsonSerializer.Serialize(new { Items = all.Skip(start).Take(Math.Min(limit, limit == 500 ? 8 : 20)), TotalRecordCount = all.Length, StartIndex = start }));
        }, services =>
        {
            services.RemoveAll<IPlaylistVirtualizationService>();
            services.AddSingleton(service.Object);
        }, new Dictionary<string, string?> { ["Jellyfin:LibraryId"] = null });
    }
}
