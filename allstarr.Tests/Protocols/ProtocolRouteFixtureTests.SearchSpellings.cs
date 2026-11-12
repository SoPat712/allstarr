using System.Net;
using System.Text.Json;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Models.Search;
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
}
