using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using allstarr.Core.Matching;
using allstarr.Core.Playlists;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Fact]
    public async Task VirtualPlaylistHydratesNativeAndExternalRowsTogetherWithoutChangingOrder()
    {
        var externalStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nativeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "overlap"))
            .Returns(async () =>
            {
                externalStarted.TrySetResult();
                await release.Task;
                return new Song { Id = "ext-deezer-song-overlap", Title = "External", Artist = "Artist", ExternalProvider = "deezer", ExternalId = "overlap" };
            });
        var link = Guid.NewGuid();
        var id = PlaylistVirtualizationService.CreateProtocolId(link);
        var tracks = new[]
        {
            new VirtualPlaylistTrack(0, "native-mix-song", "Library song", "Artist", null, null, null, null, TrackMatchState.Accepted),
            new VirtualPlaylistTrack(1, "ext-deezer-song-overlap", "External", "Artist", null, null, null, null, TrackMatchState.Accepted,
                RouteKind: TrackRouteKind.External, RouteProviderId: "deezer", RouteExternalId: "overlap")
        };
        var service = new Mock<IPlaylistVirtualizationService>();
        service.Setup(item => item.ReadAsync(It.IsAny<ProtocolExecutionContext>(), id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VirtualPlaylistReadModel(id, link, Guid.NewGuid(), "Playlist", null, null, "deezer", "one", "1", PlaylistLinkMode.Virtual, tracks));
        using var factory = ClientMixFactory(gateway, configure: services =>
        {
            services.RemoveAll<IPlaylistVirtualizationService>();
            services.AddSingleton(service.Object);
        }, observe: request =>
        {
            if (request.RequestUri!.AbsolutePath != "/Items") return;
            nativeStarted.TrySetResult();
            Assert.True(externalStarted.Task.IsCompleted, "External hydration must start before waiting for native rows.");
            release.TrySetResult();
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        try
        {
            using var response = await client.GetAsync($"/Playlists/{id}/Items?Limit=50").WaitAsync(TimeSpan.FromSeconds(4));
            response.EnsureSuccessStatusCode();
            Assert.True(nativeStarted.Task.IsCompleted);
            var first = await response.Content.ReadAsStringAsync();
            using var body = JsonDocument.Parse(first);
            Assert.Equal(["native-mix-song", "ext-deezer-song-overlap"], body.RootElement.GetProperty("Items").EnumerateArray().Select(item => item.GetProperty("Id").GetString()));
            Assert.Equal(first, await client.GetStringAsync($"/Playlists/{id}/Items?Limit=50"));
            gateway.Verify(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "overlap"), Times.Once);
        }
        finally { release.TrySetResult(); }
    }
    [Fact]
    public async Task VirtualPlaylistScreenKeepsItemsFieldsAndImageBytesAfterIndexBookkeeping()
    {
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "mix-song"))
            .ReturnsAsync(ClientMixSong());
        gateway.Setup(item => item.GetStoredSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "mix-song"))
            .ReturnsAsync(ClientMixSong());
        gateway.Setup(item => item.GetPublicArtworkUriAsync("deezer", allstarr.Core.Capabilities.ProviderResourceKind.Track,
                "mix-song", It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(new Uri("https://images.example.test/mix.png"));
        using var factory = ClientMixFactory(gateway, configure: UseTimingImageCache, nativeResponse: request => request.RequestUri!.Host == "images.example.test"
            ? new(HttpStatusCode.OK) { Content = new ByteArrayContent(MixPicture()) { Headers = { ContentType = new("image/png") } } }
            : null);
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var mix = await SeedClientMixAsync(factory);
        var id = PlaylistVirtualizationService.CreateProtocolId(mix.Id);
        var paths = new[] { $"/Playlists/{id}/Items?Limit=200&Fields=MediaSources,DateCreated", $"/Items/{id}", $"/Items/{id}/Images/Primary?fillWidth=64&format=png" };
        var expected = new List<byte[]>();
        foreach (var path in paths)
        {
            using var response = await client.GetAsync(path);
            response.EnsureSuccessStatusCode();
            expected.Add(await response.Content.ReadAsByteArrayAsync());
        }
        var providerReads = gateway.Invocations.Count;
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync())
        {
            var track = await db.LibraryTracks.SingleAsync();
            track.IndexedAt = DateTimeOffset.UtcNow;
            track.UpdatedAt = track.IndexedAt;
            track.Revision++;
            db.ExternalMetadataSnapshots.Add(FeedReadsTests.Snapshot(track.OwnerUserId, "library-artist-index"));
            await db.SaveChangesAsync();
        }
        for (var index = 0; index < paths.Length; index++)
        {
            using var response = await client.GetAsync(paths[index]);
            response.EnsureSuccessStatusCode();
            Assert.Equal(expected[index], await response.Content.ReadAsByteArrayAsync());
            _lyricsSearchOutput.WriteLine($"Playlist screen {index} warm: " + Assert.Single(response.Headers.GetValues("Server-Timing")));
        }
        Assert.Equal(providerReads, gateway.Invocations.Count);
    }

}
