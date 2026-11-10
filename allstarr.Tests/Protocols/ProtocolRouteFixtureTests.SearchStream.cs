using System.Net;
using System.Text.Json.Nodes;
using allstarr.Core.Matching;
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
    [Fact]
    public async Task ClientApi_SearchStreamAcceptsSingleLetterDeduplicatesAndTerminates()
    {
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.StreamSearchAsync(It.IsAny<ProtocolExecutionContext>(), "a", 20,
                true, true, true, true,
                It.IsAny<Func<string, SearchResult, IReadOnlyList<ExternalPlaylist>, CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns(async (ProtocolExecutionContext context, string query, int limit, bool songs, bool albums, bool artists, bool playlists,
                Func<string, SearchResult, IReadOnlyList<ExternalPlaylist>, CancellationToken, Task> write, CancellationToken token) =>
            {
                await write("deezer", new SearchResult
                {
                    Songs = [new Song { Id = "ext-deezer-song-one", ExternalProvider = "deezer", ExternalId = "one",
                        Title = "Visible", Artist = "Artist", Album = "Release", Duration = 200 }],
                    Albums = [new Album { Id = "ext-deezer-album-owned", Title = "Owned", Artist = "Artist" }],
                    Artists = [new Artist { Id = "ext-deezer-artist-owned", Name = "Owned artist" }]
                }, [], token);
                await write("qobuz", new SearchResult
                {
                    Songs = [new Song { Id = "ext-qobuz-song-two", ExternalProvider = "qobuz", ExternalId = "two",
                        Title = "Visible", Artist = "Artist", Album = "Release", Duration = 200 }]
                }, [], token);
                return (IReadOnlyList<string>)["slow"];
            });
        var index = new Mock<ILibraryIndexService>();
        index.Setup(item => item.FindKnownRecordingTracksAsync(It.IsAny<ProtocolExecutionContext>(),
                It.IsAny<IReadOnlyList<LibraryRecordingLookup>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string>());
        using var factory = ClientApiFactory(request => request.RequestUri!.AbsolutePath switch
        {
            "/Users/Me" => Json(200, """{"Id":"listener"}"""),
            "/System/Info/Public" => Json(200, """{"Id":"native-server"}"""),
            "/Users/listener/Items" => Json(200, """{"Items":[{"Id":"native-album","Type":"MusicAlbum","Name":"Owned","AlbumArtist":"Artist","Artists":["Artist"]},{"Id":"native-artist","Type":"MusicArtist","Name":"Owned artist"}]}"""),
            _ => Json(404, "{}")
        }, services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
            services.RemoveAll<ILibraryIndexService>();
            services.AddSingleton(index.Object);
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        using var response = await client.GetAsync("/allstarr/v1/search?q=a");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/x-ndjson", response.Content.Headers.ContentType!.MediaType);
        var lines = (await response.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonNode.Parse(line)!).ToArray();
        Assert.Equal(3, lines.Length);
        Assert.Equal("deezer", lines[0]["provider"]!.GetValue<string>());
        var item = Assert.Single(lines[0]["items"]!.AsArray())!;
        Assert.Equal("ext-deezer-song-one", item["Id"]!.GetValue<string>());
        Assert.Equal("Audio", item["Type"]!.GetValue<string>());
        Assert.Null(item["id"]);
        Assert.Empty(lines[1]["items"]!.AsArray());
        Assert.True(lines[2]["done"]!.GetValue<bool>());
        Assert.Equal("slow", Assert.Single(lines[2]["late"]!.AsArray())!.GetValue<string>());
    }
}
