using System.Net;
using System.Text.Json;
using allstarr.Core.Matching;
using allstarr.Core.Playlists;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Fact]
    public async Task JellyfinNativePlaylist_ReadsPublishedExternalRowsWithoutLegacyConfiguration()
    {
        const string nativeId = "4383a46d8bcac3be2ef9385053ea18df";
        var linkId = Guid.CreateVersion7();
        var model = new VirtualPlaylistReadModel(
            PlaylistVirtualizationService.CreateProtocolId(linkId), linkId, Guid.CreateVersion7(),
            "Weekly", null, null, "spotify", "weekly", "revision-2", PlaylistLinkMode.Materialized,
            [
                new(0, "ext-deezer-song-42", "External", "Artist", "Album", null, 180_000, null,
                    TrackMatchState.Accepted, RouteKind: TrackRouteKind.External,
                    RouteProviderId: "deezer", RouteExternalId: "42"),
                new(1, "local-song", "Local", "Artist", "Album", null, 180_000, null,
                    TrackMatchState.Accepted),
                new(2, "allstarr-unresolved-missing", "Missing", "Artist", null, null, null, null,
                    TrackMatchState.Unresolved, RouteKind: TrackRouteKind.Unresolved)
            ], TargetPlaylistId: nativeId);
        var virtualization = new Mock<IPlaylistVirtualizationService>(MockBehavior.Strict);
        virtualization.Setup(service => service.ReadAsync(It.IsAny<ProtocolExecutionContext>(), nativeId,
                It.IsAny<CancellationToken>())).ReturnsAsync(model);
        virtualization.Setup(service => service.ListAsync(It.IsAny<ProtocolExecutionContext>(),
                It.IsAny<CancellationToken>())).ReturnsAsync([model]);
        var observed = new List<string>();
        using var factory = new ProtocolFactory("Jellyfin", request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            observed.Add(path);
            return path switch
            {
                "/Users/Me" => Json(200, """{"Id":"user-1","Name":"Owner"}"""),
                "/Items" => Json(200, """{"Items":[{"Id":"local-song","Name":"Local","Type":"Audio","MediaSources":[{"Id":"local-song"}]}],"TotalRecordCount":1}"""),
                "/Users/user-1/Items" => Json(200, $$"""{"Items":[{"Id":"{{nativeId}}","Name":"Weekly","Type":"Playlist","ChildCount":1}],"TotalRecordCount":1}"""),
                _ => Json(200, """{"Items":[{"Id":"stale-local","Name":"Old song","Type":"Audio"}],"TotalRecordCount":1}""")
            };
        }, services =>
        {
            services.RemoveAll<IPlaylistVirtualizationService>();
            services.AddSingleton(virtualization.Object);
        }, new Dictionary<string, string?> { ["SpotifyImport:Enabled"] = "false" });
        using var client = factory.CreateClient();

        foreach (var path in new[]
        {
            $"/Playlists/{nativeId}/Items?StartIndex=0&Limit=50",
            $"/Users/user-1/Items?ParentId={nativeId}&IncludeItemTypes=Audio"
        })
        {
            using var response = await client.GetAsync($"{path}&api_key=fixture-key");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var items = json.RootElement.GetProperty("Items").EnumerateArray().ToArray();
            Assert.Equal(3, json.RootElement.GetProperty("TotalRecordCount").GetInt32());
            Assert.Equal(["ext-deezer-song-42", "local-song", "allstarr-unresolved-missing"],
                items.Select(item => item.GetProperty("Id").GetString()));
            Assert.All(items, item => Assert.Equal(nativeId, item.GetProperty("ParentId").GetString()));
            Assert.Equal("None", items[2].GetProperty("PlayAccess").GetString());
        }
        using var detail = await client.GetAsync($"/Items/{nativeId}?api_key=fixture-key");
        using var detailJson = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
        Assert.Equal(3, detailJson.RootElement.GetProperty("ChildCount").GetInt32());
        using var browse = await client.GetAsync("/Users/user-1/Items?IncludeItemTypes=Playlist&api_key=fixture-key");
        using var browseJson = JsonDocument.Parse(await browse.Content.ReadAsStringAsync());
        var summary = Assert.Single(browseJson.RootElement.GetProperty("Items").EnumerateArray());
        Assert.Equal(nativeId, summary.GetProperty("Id").GetString());
        Assert.Equal(3, summary.GetProperty("ChildCount").GetInt32());
        Assert.Equal(1, browseJson.RootElement.GetProperty("TotalRecordCount").GetInt32());
        Assert.DoesNotContain($"/Playlists/{nativeId}/Items", observed);
    }
}
