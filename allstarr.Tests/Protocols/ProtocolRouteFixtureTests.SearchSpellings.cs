using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using allstarr.Core.Matching;
using allstarr.Core.Music;
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
    public async Task JellyfinSearch_AsksTheMediaServerAgainWithTheLibrarysOwnSpelling()
    {
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.SearchAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(),
            It.IsAny<int>(), null)).ReturnsAsync(new SearchResult());
        gateway.Setup(item => item.SearchPlaylistsAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), It.IsAny<int>())).ReturnsAsync([]);
        var spellings = new Mock<ILibrarySpellings>();
        spellings.Setup(item => item.RespellAsync(It.IsAny<ProtocolExecutionContext>(), "cybersex", It.IsAny<CancellationToken>()))
            .ReturnsAsync("Cyber Sex");
        var searched = new List<string>();
        using var factory = new ProtocolFactory("Jellyfin", request =>
        {
            if (request.RequestUri!.AbsolutePath == "/Users/Me") return Json(200, """{"Id":"user-1"}""");
            var term = QueryHelpers.ParseQuery(request.RequestUri.Query).FirstOrDefault(pair =>
                pair.Key.Equals("SearchTerm", StringComparison.OrdinalIgnoreCase)).Value.ToString();
            lock (searched) searched.Add(term);
            // The media server only knows the spaced title, as Jellyfin's own search does.
            return Json(200, term == "Cyber Sex"
                ? """{"Items":[{"Id":"native-cybersex","Name":"Cyber Sex","Type":"Audio","Artists":["Doja Cat"],"Album":"Hot Pink","RunTimeTicks":1950000000}],"TotalRecordCount":1}"""
                : """{"Items":[],"TotalRecordCount":0}""");
        }, services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
            services.RemoveAll<ILibrarySpellings>();
            services.AddSingleton(spellings.Object);
        });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/Items?SearchTerm=cybersex&IncludeItemTypes=Audio&Recursive=true&Limit=20&api_key=fixture-key");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Contains(body.RootElement.GetProperty("Items").EnumerateArray(), item => item.GetProperty("Id").GetString() == "native-cybersex");
        Assert.Contains("cybersex", searched);
        Assert.Contains("Cyber Sex", searched);
    }

    [Fact]
    public async Task ClientApi_RankedSearchRanksTheLibrarysSpellingAboveSongsThatOnlyMatchTheTypedWord()
    {
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetProviderOrder(It.IsAny<allstarr.Core.Capabilities.ProviderCapabilityKind>())).Returns(["applemusic"]);
        gateway.Setup(item => item.StreamSearchAsync(It.IsAny<ProtocolExecutionContext>(), "cybersex", 20, true, true, true, true,
                It.IsAny<Func<string, SearchResult, IReadOnlyList<ExternalPlaylist>, CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns(async (ProtocolExecutionContext context, string query, int limit, bool songs, bool albums, bool artists, bool playlists,
                Func<string, SearchResult, IReadOnlyList<ExternalPlaylist>, CancellationToken, Task> write, CancellationToken token) =>
            {
                await write("applemusic", new SearchResult
                {
                    Songs = [new Song { Id = "ext-applemusic-song-other", ExternalProvider = "applemusic", ExternalId = "other",
                        Title = "Cybersex", Artist = "Someone Else", Album = "Night", Duration = 180 }]
                }, [], token);
                return (IReadOnlyList<string>)[];
            });
        var index = new Mock<ILibraryIndexService>();
        index.Setup(item => item.FindKnownRecordingTracksAsync(It.IsAny<ProtocolExecutionContext>(),
                It.IsAny<IReadOnlyList<LibraryRecordingLookup>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string>());
        var spellings = new Mock<ILibrarySpellings>();
        spellings.Setup(item => item.RespellAsync(It.IsAny<ProtocolExecutionContext>(), "cybersex", It.IsAny<CancellationToken>()))
            .ReturnsAsync("Cyber Sex");
        using var factory = ClientApiFactory(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/Users/Me") return Json(200, """{"Id":"listener"}""");
            if (request.RequestUri.AbsolutePath == "/System/Info/Public") return Json(200, """{"Id":"native-server"}""");
            if (request.RequestUri.AbsolutePath != "/Users/listener/Items") return Json(404, "{}");
            var term = QueryHelpers.ParseQuery(request.RequestUri.Query).FirstOrDefault(pair =>
                pair.Key.Equals("SearchTerm", StringComparison.OrdinalIgnoreCase)).Value.ToString();
            // The media server knows only the spaced title, as Jellyfin's own search does.
            return Json(200, term == "Cyber Sex"
                ? """{"Items":[{"Id":"native-cybersex","Name":"Cyber Sex","Type":"Audio","Artists":["Doja Cat"],"Album":"Hot Pink","RunTimeTicks":1950000000}],"TotalRecordCount":1}"""
                : """{"Items":[],"TotalRecordCount":0}""");
        }, services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
            services.RemoveAll<ILibraryIndexService>();
            services.AddSingleton(index.Object);
            services.RemoveAll<ILibrarySpellings>();
            services.AddSingleton(spellings.Object);
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");

        using var response = await client.GetAsync("/allstarr/v1/search?q=cybersex&limit=20&shape=ranked", HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        string[] ids = [];
        // Snapshots arrive as sources answer; the last one that lists both songs is the ranking a client shows.
        while (await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)) is { } line)
        {
            var snapshot = JsonNode.Parse(line)!;
            var rows = snapshot["items"]!.AsArray().Select(row => row!["Id"]!.GetValue<string>()).ToArray();
            if (rows.Contains("native-cybersex") && rows.Contains("ext-applemusic-song-other")) ids = rows;
            if (snapshot["done"]?.GetValue<bool>() == true) break;
        }
        Assert.Equal("native-cybersex", ids.FirstOrDefault());
    }
}
