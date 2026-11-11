using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Models.Search;
using allstarr.Models.Subsonic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    private const string NativeCharliAlbum = """{"Id":"native-charli","Type":"MusicAlbum","Name":"Charli","AlbumArtists":[{"Id":"native-artist","Name":"Charli xcx"}],"ChildCount":15,"UnknownFutureField":{"Keep":true}}""";

    [Theory]
    [InlineData("Charli xcx")]
    [InlineData("Charli-xcx")]
    public async Task Search_NormalizedNativeAlbumNamesPreferTheLibraryCopyWhileAnUnlinkedArtistStays(string nativeArtist)
    {
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(service => service.SearchAsync(It.IsAny<ProtocolExecutionContext>(), "charli", 0, 20, 20, null))
            .ReturnsAsync(new SearchResult
            {
                Albums =
                [
                    new Album { Id = "ext-deezer-album-charli", Title = "Charli", Artist = "Charli XCX", SongCount = 15 },
                    new Album { Id = "ext-deezer-album-deluxe", Title = "Charli", Artist = "Charli XCX", Edition = "Deluxe", SongCount = 18 }
                ],
                Artists = [new Artist { Id = "ext-deezer-artist-charli", Name = "Charli XCX" }]
            });
        gateway.Setup(service => service.SearchAsync(It.IsAny<ProtocolExecutionContext>(), "charli", 0, 0, 200, null))
            .ReturnsAsync(new SearchResult { Artists = [new Artist { Id = "ext-deezer-artist-charli", Name = "Charli XCX" }] });
        var nativeArtistJson = JsonSerializer.Serialize(new { Id = "native-artist", Type = "MusicArtist", Name = nativeArtist });
        using var factory = new ProtocolFactory("Jellyfin", request => request.RequestUri!.AbsolutePath switch
        {
            "/Users/Me" => Json(200, """{"Id":"user-1"}"""),
            "/Artists" => Json(200, "{\"Items\":[" + nativeArtistJson + "],\"TotalRecordCount\":1}"),
            _ => Json(200, "{\"Items\":[" + NativeCharliAlbum + "," + nativeArtistJson + "],\"TotalRecordCount\":2}")
        }, services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
        }, new Dictionary<string, string?> { ["Jellyfin:EnableExternalPlaylists"] = "false" });
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/Items?api_key=fixture&SearchTerm=charli&IncludeItemTypes=MusicAlbum,MusicArtist&Limit=20");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var rows = body.RootElement.GetProperty("Items").EnumerateArray().ToArray();
        // The same-named service artist has no matched songs behind it, so it stays beside the library artist.
        Assert.Equal(new[] { "native-artist", "ext-deezer-artist-charli", "native-charli", "ext-deezer-album-deluxe" },
            rows.Select(row => row.GetProperty("Id").GetString()));
        Assert.Equal(NativeCharliAlbum, rows[2].GetRawText());
        using var artistsResponse = await client.GetAsync("/Artists?api_key=fixture&SearchTerm=charli&Limit=20");
        Assert.Equal(HttpStatusCode.OK, artistsResponse.StatusCode);
        using var artistsBody = JsonDocument.Parse(await artistsResponse.Content.ReadAsStringAsync());
        Assert.Equal(["ext-deezer-artist-charli", "native-artist"], artistsBody.RootElement.GetProperty("Items").EnumerateArray()
            .Select(row => row.GetProperty("Id").GetString()).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task SearchStream_KeepsSameNamedServiceReleasesBesideTheLibraryAndLabelsTheirSource()
    {
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(service => service.StreamSearchAsync(It.IsAny<ProtocolExecutionContext>(), "charli", 20,
                false, true, true, false,
                It.IsAny<Func<string, SearchResult, IReadOnlyList<ExternalPlaylist>, CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns(async (ProtocolExecutionContext context, string query, int limit, bool songs, bool albums, bool artists, bool playlists,
                Func<string, SearchResult, IReadOnlyList<ExternalPlaylist>, CancellationToken, Task> write, CancellationToken token) =>
            {
                await write("deezer", new SearchResult
                {
                    Albums =
                    [
                        new Album { Id = "ext-deezer-album-charli", Title = "Charli", Artist = "Charli XCX" },
                        new Album { Id = "ext-deezer-album-fresh", Title = "Fresh", Artist = "Charli XCX" }
                    ],
                    Artists = [new Artist { Id = "ext-deezer-artist-charli", Name = "Charli XCX" }]
                }, [], token);
                await write("qobuz", new SearchResult
                {
                    Albums = [new Album { Id = "ext-qobuz-album-fresh", Title = "Fresh", Artist = "charli xcx" }]
                }, [], token);
                return (IReadOnlyList<string>)[];
            });
        using var factory = ClientApiFactory(request => request.RequestUri!.AbsolutePath switch
        {
            "/Users/Me" => Json(200, """{"Id":"listener"}"""),
            "/System/Info/Public" => Json(200, """{"Id":"native-server"}"""),
            "/Users/listener/Items" => Json(200, "{\"Items\":[" + NativeCharliAlbum + "," +
                """{"Id":"native-artist","Type":"MusicArtist","Name":"Charli-xcx"}]}"""),
            _ => Json(404, "{}")
        }, services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        using var response = await client.GetAsync("/allstarr/v1/search?q=charli&types=MusicAlbum,MusicArtist");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var lines = (await response.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonNode.Parse(line)!).ToArray();
        Assert.Equal(3, lines.Length);
        // The library's Charli album and artist no longer hide Deezer's; a second service's repeat of Fresh still does.
        var deezer = lines[0]["items"]!.AsArray();
        Assert.Equal(["ext-deezer-album-charli", "ext-deezer-album-fresh", "ext-deezer-artist-charli"],
            deezer.Select(item => item!["Id"]!.GetValue<string>()).Order(StringComparer.Ordinal).ToArray());
        Assert.All(deezer, item => Assert.Equal("deezer", item!["source"]!.GetValue<string>()));
        Assert.All(deezer, item => Assert.False(string.IsNullOrWhiteSpace(item!["sourceName"]!.GetValue<string>())));
        Assert.Empty(lines[1]["items"]!.AsArray());
        Assert.True(lines[2]["done"]!.GetValue<bool>());
    }
}
