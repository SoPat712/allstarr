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
    // Jellyfin apps send a list either comma-separated or as the same parameter repeated; both ask services for every type.
    [Theory]
    [InlineData("IncludeItemTypes=Audio,MusicAlbum,MusicArtist")]
    [InlineData("IncludeItemTypes=Audio&IncludeItemTypes=MusicAlbum&IncludeItemTypes=MusicArtist")]
    [InlineData("includeItemTypes=Audio&includeItemTypes=MusicAlbum&includeItemTypes=MusicArtist&ParentId=music-library")]
    public async Task Search_RepeatedItemTypesStillFindServiceAlbumsAndArtists(string types)
    {
        var limits = new List<(int Songs, int Albums, int Artists)>();
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(service => service.SearchAsync(It.IsAny<ProtocolExecutionContext>(), "Thats what they all say",
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>()))
            .Callback<ProtocolExecutionContext, string, int, int, int, string?>((_, _, songs, albums, artists, _) => limits.Add((songs, albums, artists)))
            .ReturnsAsync(new SearchResult
            {
                Albums = [new Album { Id = "ext-deezer-album-harlow", ExternalProvider = "deezer", ExternalId = "harlow", Title = "Thats What They All Say", Artist = "Jack Harlow", SongCount = 15 }]
            });
        using var factory = new ProtocolFactory("Jellyfin", request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/Users/Me") return Json(200, """{"Id":"verified-user"}""");
            return Json(200, """{"Items":[],"TotalRecordCount":0}""");
        }, services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
        });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/Items?api_key=fixture&SearchTerm=Thats%20what%20they%20all%20say&Recursive=true&Limit=40&{types}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var (songs, albums, artists) = Assert.Single(limits);
        Assert.True(songs > 0 && albums > 0 && artists > 0, $"songs {songs}, albums {albums}, artists {artists}");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Contains(document.RootElement.GetProperty("Items").EnumerateArray(),
            item => item.GetProperty("Id").GetString() == "ext-deezer-album-harlow");
    }
}
