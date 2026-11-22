using System.Net;
using System.Text.Json;
using allstarr.Core.Artwork;
using allstarr.Core.Caching;
using allstarr.Core.Matching;
using allstarr.Core.Playlists;
using allstarr.Core.Protocols;
using allstarr.Core.Protocols.Jellyfin;
using allstarr.Core.Storage;
using allstarr.Models.Settings;
using allstarr.Services.Jellyfin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace allstarr.Tests;

public sealed partial class LibraryIndexServiceTests
{
    [Fact]
    public async Task APlaylistSongTheMediaServerNoLongerReturnsIsAskedAboutAndPlaysFromItsServiceCopy()
    {
        var viewer = Context(_userA, "principal-a");
        await _service.UpsertAsync(viewer, Input());
        var linkId = Guid.NewGuid();
        var protocolId = PlaylistVirtualizationService.CreateProtocolId(linkId);
        var playlists = new Mock<IPlaylistVirtualizationService>(MockBehavior.Strict);
        // The playlist reads as the index has it: the library copy while it counts as in the library, the service copy after.
        playlists.Setup(item => item.ReadAsync(viewer, protocolId, It.IsAny<CancellationToken>())).Returns(async () =>
        {
            await using var db = await _factory.CreateDbContextAsync();
            var local = await LibraryTrackAccess.Query(db, viewer, new BackendLibraryAccess(true, ["music"])).AnyAsync();
            return new VirtualPlaylistReadModel(protocolId, linkId, linkId, "Synced", null, null, "spotify", "source", "1",
                PlaylistLinkMode.Virtual, [local
                    ? new VirtualPlaylistTrack(0, "local-item", "Song", "Artist", "Album", null, 240_000, null, TrackMatchState.Accepted,
                        "spotify", "source-song", TrackRouteKind.Local)
                    : new VirtualPlaylistTrack(0, "ext-deezer-song-deleted", "Song", "Artist", "Album", null, 240_000, null,
                        TrackMatchState.Accepted, "spotify", "source-song", TrackRouteKind.External, "deezer", "deleted")]);
        });
        using var backend = new EmptyItemsBackend();
        using var client = new HttpClient(backend);
        var clients = new Mock<IHttpClientFactory>();
        clients.Setup(item => item.CreateClient(It.IsAny<string>())).Returns(client);
        using var cache = new MemoryApplicationCache(_clock);
        var proxy = new JellyfinProxyService(clients.Object, Options.Create(new JellyfinSettings { Url = "http://backend.test" }),
            new HttpContextAccessor(), NullLogger<JellyfinProxyService>.Instance, cache,
            new MediaAssetResolver(cache, NullLogger<MediaAssetResolver>.Instance), new ConfigurationBuilder().Build());
        var server = new GoneScanner("local-item");
        var adapter = new JellyfinVirtualPlaylistProtocolAdapter(playlists.Object, Mock.Of<IJellyfinPlaylistMutationResolver>(),
            proxyService: proxy, responseBuilder: new JellyfinResponseBuilder(), goneTracks: GoneTracks(server));

        var served = await ReadOnlyItemAsync(adapter, viewer, protocolId);

        Assert.Equal("ext-deezer-song-deleted", served.GetProperty("Id").GetString());
        Assert.False(served.TryGetProperty("PlayAccess", out var access) && access.GetString() == "None");
        Assert.Equal(["local-item"], Assert.Single(server.Asked));
        // Once it is known gone it is not asked about again.
        Assert.Equal("ext-deezer-song-deleted", (await ReadOnlyItemAsync(adapter, viewer, protocolId)).GetProperty("Id").GetString());
        Assert.Single(server.Asked);
    }

    [Fact]
    public async Task APlaylistSongTheMediaServerStillHasButDoesNotReturnStaysAsItWas()
    {
        var viewer = Context(_userA, "principal-a");
        await _service.UpsertAsync(viewer, Input());
        var linkId = Guid.NewGuid();
        var protocolId = PlaylistVirtualizationService.CreateProtocolId(linkId);
        var playlists = new Mock<IPlaylistVirtualizationService>(MockBehavior.Strict);
        playlists.Setup(item => item.ReadAsync(viewer, protocolId, It.IsAny<CancellationToken>())).ReturnsAsync(
            new VirtualPlaylistReadModel(protocolId, linkId, linkId, "Synced", null, null, "spotify", "source", "1",
                PlaylistLinkMode.Virtual, [new VirtualPlaylistTrack(0, "local-item", "Song", "Artist", "Album", null, 240_000, null,
                    TrackMatchState.Accepted, "spotify", "source-song", TrackRouteKind.Local)]));
        using var backend = new EmptyItemsBackend();
        using var client = new HttpClient(backend);
        var clients = new Mock<IHttpClientFactory>();
        clients.Setup(item => item.CreateClient(It.IsAny<string>())).Returns(client);
        using var cache = new MemoryApplicationCache(_clock);
        var proxy = new JellyfinProxyService(clients.Object, Options.Create(new JellyfinSettings { Url = "http://backend.test" }),
            new HttpContextAccessor(), NullLogger<JellyfinProxyService>.Instance, cache,
            new MediaAssetResolver(cache, NullLogger<MediaAssetResolver>.Instance), new ConfigurationBuilder().Build());
        var adapter = new JellyfinVirtualPlaylistProtocolAdapter(playlists.Object, Mock.Of<IJellyfinPlaylistMutationResolver>(),
            proxyService: proxy, responseBuilder: new JellyfinResponseBuilder(), goneTracks: GoneTracks(new GoneScanner()));

        var served = await ReadOnlyItemAsync(adapter, viewer, protocolId);

        Assert.Equal(PlaylistVirtualizationService.UnresolvedItemPrefix + "local-item", served.GetProperty("Id").GetString());
        playlists.Verify(item => item.ReadAsync(viewer, protocolId, It.IsAny<CancellationToken>()), Times.Once);
    }

    private static async Task<JsonElement> ReadOnlyItemAsync(JellyfinVirtualPlaylistProtocolAdapter adapter,
        ProtocolExecutionContext viewer, string protocolId)
    {
        var result = Assert.IsType<JsonResult>(await adapter.ReadItemsAsync(viewer, protocolId, null, new QueryCollection(), default));
        using var page = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
        return Assert.Single(page.RootElement.GetProperty("Items").EnumerateArray().ToArray()).Clone();
    }

    // The media server answers every item read without the deleted song.
    private sealed class EmptyItemsBackend : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.RequestUri!.AbsolutePath == "/Items"
                    ? """{"Items":[],"TotalRecordCount":0}"""
                    : """{"Id":"fixture-server"}""")
            });
    }
}
