using System.Net;
using System.Text.Json;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Models.Search;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Theory]
    [InlineData("Jellyfin")]
    [InlineData("Subsonic")]
    public async Task SearchPagination_KeepsReturnedRowsAndIncludesLateAnswersOnLaterPages(string backend)
    {
        var later = false;
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        Song Song(string id, string title) => new()
        {
            Id = "ext-deezer-song-" + id,
            ExternalId = id,
            ExternalProvider = "deezer",
            Title = title,
            Artist = "Artist"
        };
        var first = Song("first", "Track first");
        var second = Song("second", "Track second");
        var late = Song("late", "Track");
        gateway.Setup(item => item.SearchAsync(
                It.IsAny<ProtocolExecutionContext>(), "Track",
                It.IsAny<int>(), 0, 0, null))
            .Returns(() => Task.FromResult(new SearchResult { Songs = later ? [late, first, second, late] : [first, second] }));
        using var factory = new ProtocolFactory(backend, request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/Users/Me") return Json(200, """{"Id":"user-1"}""");
            if (path.Contains("ping", StringComparison.Ordinal))
                return Json(200, """{"subsonic-response":{"status":"ok","version":"1.16.1"}}""");
            return backend == "Jellyfin"
                ? Json(200, """{"Items":[],"TotalRecordCount":0}""")
                : Json(200, """{"subsonic-response":{"status":"ok","version":"1.16.1","searchResult3":{}}}""");
        }, services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
        }, new Dictionary<string, string?>
        {
            [backend + ":EnableExternalPlaylists"] = "false"
        });
        using var client = factory.CreateClient();
        foreach (var (offset, expected) in new[] { (0, first.Id), (1, late.Id), (1, late.Id), (2, second.Id) })
        {
            var path = backend == "Jellyfin"
                ? $"/Items?SearchTerm=Track&IncludeItemTypes=Audio&StartIndex={offset}&Limit=1&api_key=fixture"
                : $"/rest/search3.view?u=fixture&p=secret&v=1.16.1&c=fixture&f=json&query=Track&songCount=1&songOffset={offset}&albumCount=0&artistCount=0";
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var rows = backend == "Jellyfin"
                ? payload.RootElement.GetProperty("Items")
                : payload.RootElement.GetProperty("subsonic-response").GetProperty("searchResult3").GetProperty("song");
            Assert.Equal(expected, Assert.Single(rows.EnumerateArray()).GetProperty(backend == "Jellyfin" ? "Id" : "id").GetString());
            later = true;
        }
    }

    [Fact]
    public async Task SubsonicSearch_FirstPageRetainsNativeAndProviderRowsBeyondTheRequestedCount()
    {
        var later = false;
        var nativeOffsets = new List<string>();
        var nativeCounts = new List<string>();
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.SearchAsync(It.IsAny<ProtocolExecutionContext>(), "Track", It.IsAny<int>(), 0, 0, null))
            .Returns(() => Task.FromResult(new SearchResult
            {
                Songs = later
                    ? [new Song { Id = "late", Title = "Track", ExternalProvider = "deezer" },
                        new Song { Id = "provider", Title = "Track provider", ExternalProvider = "deezer" }]
                    : [new Song { Id = "provider", Title = "Track provider", ExternalProvider = "deezer" },
                        new Song { Id = "outside-window", Title = "Track outside", ExternalProvider = "deezer" }]
            }));
        using var factory = new ProtocolFactory("Subsonic", request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("ping", StringComparison.Ordinal))
                return Json(200, """{"subsonic-response":{"status":"ok","version":"1.16.1"}}""");
            var offset = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query)["songOffset"].ToString();
            nativeOffsets.Add(offset);
            nativeCounts.Add(Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query)["songCount"].ToString());
            return Json(200, offset == "0"
                ? """{"subsonic-response":{"status":"ok","version":"1.16.1","searchResult3":{"song":[{"id":"native","title":"Track native","artist":"Artist","isDir":false}]}}}"""
                : """{"subsonic-response":{"status":"ok","version":"1.16.1","searchResult3":{}}}""");
        }, services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
        }, new Dictionary<string, string?> { ["Subsonic:EnableExternalPlaylists"] = "false" });
        using var client = factory.CreateClient();
        var first = await Page(0);
        Assert.Equal(2, first.Length);
        Assert.Contains("native", first);
        Assert.Contains("provider", first);
        Assert.DoesNotContain("outside-window", first);
        later = true;
        Assert.Equal(["late"], await Page(1));
        Assert.Equal(["late"], await Page(1));
        Assert.Equal(["0", "0", "0"], nativeOffsets);
        Assert.Equal(["1", "2", "2"], nativeCounts);

        async Task<string[]> Page(int offset)
        {
            using var response = await client.GetAsync($"/rest/search3.view?u=fixture&p=secret&v=1.16.1&c=fixture&f=json&query=Track&songCount=1&songOffset={offset}&albumCount=0&artistCount=0");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return payload.RootElement.GetProperty("subsonic-response").GetProperty("searchResult3").GetProperty("song")
                .EnumerateArray().Select(item => item.GetProperty("id").GetString()!).ToArray();
        }
    }

    [Fact]
    public async Task SubsonicSearch_PaginationKeepsNativeFolderScopesSeparate()
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.SearchAsync(It.IsAny<ProtocolExecutionContext>(), "Track", It.IsAny<int>(), 0, 0, null))
            .ReturnsAsync(new SearchResult());
        using var factory = new ProtocolFactory("Subsonic", request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("ping", StringComparison.Ordinal))
                return Json(200, """{"subsonic-response":{"status":"ok","version":"1.16.1"}}""");
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query);
            var folder = query["musicFolderId"].ToString();
            var offset = int.Parse(query["songOffset"].ToString());
            var count = int.Parse(query["songCount"].ToString());
            return Json(200, JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["subsonic-response"] = new
                {
                    status = "ok",
                    version = "1.16.1",
                    searchResult3 = new
                    {
                        song = Enumerable.Range(0, 3).Skip(offset).Take(count)
                            .Select(index => new { id = folder + index, title = "Track " + index, artist = "Artist", isDir = false })
                    }
                }
            }));
        }, services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
        }, new Dictionary<string, string?> { ["Subsonic:EnableExternalPlaylists"] = "false" });
        using var client = factory.CreateClient();
        Assert.Equal(["first0"], await Page("first", 0));
        Assert.Equal(["second0"], await Page("second", 0));
        Assert.Equal(["first1"], await Page("first", 1));

        async Task<string[]> Page(string folder, int offset)
        {
            using var response = await client.GetAsync($"/rest/search3.view?u=fixture&p=secret&v=1.16.1&c=fixture&f=json&query=Track&songCount=1&songOffset={offset}&musicFolderId={folder}&albumCount=0&artistCount=0");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return payload.RootElement.GetProperty("subsonic-response").GetProperty("searchResult3").GetProperty("song")
                .EnumerateArray().Select(item => item.GetProperty("id").GetString()!).ToArray();
        }
    }

    [Fact]
    public async Task SubsonicSearch_ContinuationFetchesNativePrefixesSoLateRowsDoNotDropTheUnreturnedTail()
    {
        var later = false;
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.SearchAsync(It.IsAny<ProtocolExecutionContext>(), "Track", It.IsAny<int>(), 0, 0, null))
            .Returns(() => Task.FromResult(new SearchResult
            {
                Songs = later
                    ? [new Song { Id = "late", Title = "Track", ExternalProvider = "deezer" },
                        new Song { Id = "provider", Title = "Track provider", ExternalProvider = "deezer" }]
                    : [new Song { Id = "provider", Title = "Track provider", ExternalProvider = "deezer" }]
            }));
        using var factory = new ProtocolFactory("Subsonic", request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("ping", StringComparison.Ordinal))
                return Json(200, """{"subsonic-response":{"status":"ok","version":"1.16.1"}}""");
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query);
            var offset = int.Parse(query["songOffset"].ToString());
            var count = int.Parse(query["songCount"].ToString());
            return Json(200, JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["subsonic-response"] = new
                {
                    status = "ok",
                    version = "1.16.1",
                    searchResult3 = new
                    {
                        song = Enumerable.Range(0, 6).Skip(offset).Take(count)
                            .Select(index => new { id = "native-" + index, title = "Track native " + index, artist = "Artist", isDir = false })
                    }
                }
            }));
        }, services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
        }, new Dictionary<string, string?> { ["Subsonic:EnableExternalPlaylists"] = "false" });
        using var client = factory.CreateClient();
        var first = await Page(0);
        Assert.Equal(3, first.Length);
        Assert.Contains("provider", first);
        later = true;
        var second = await Page(2);
        var third = await Page(4);
        Assert.Contains("late", second);
        Assert.Contains("native-2", second);
        Assert.Contains("native-3", third);
        Assert.Equal(7, first.Concat(second).Concat(third).Distinct(StringComparer.Ordinal).Count());

        async Task<string[]> Page(int offset)
        {
            using var response = await client.GetAsync($"/rest/search3.view?u=fixture&p=secret&v=1.16.1&c=fixture&f=json&query=Track&songCount=2&songOffset={offset}&albumCount=0&artistCount=0");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return payload.RootElement.GetProperty("subsonic-response").GetProperty("searchResult3").GetProperty("song")
                .EnumerateArray().Select(item => item.GetProperty("id").GetString()!).ToArray();
        }
    }
}
