using System.Net;
using System.Text.Json;
using allstarr.Core.Playlists;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Theory]
    [InlineData("native-mix-song")]
    [InlineData("ext-deezer-song-mix-song")]
    [InlineData("ext-deezer-album-mix-album")]
    public async Task ClientApi_AppearancesListTheMixesHoldingAnItemsSongs(string itemId)
    {
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(service => service.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "mix-album"))
            .ReturnsAsync(new Album { Id = "ext-deezer-album-mix-album", Title = "Service album", Songs = [ClientMixSong()] });
        gateway.Setup(service => service.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "mix-song"))
            .ReturnsAsync(ClientMixSong());
        using var factory = ClientMixFactory(gateway);
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var mix = await SeedClientMixAsync(factory);

        using var response = await client.GetAsync($"/allstarr/v1/items/{itemId}/appearances");
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, text);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var body = JsonDocument.Parse(text);
        Assert.Equal(itemId, body.RootElement.GetProperty("itemId").GetString());
        Assert.Equal(1, body.RootElement.GetProperty("songCount").GetInt32());
        var appearance = Assert.Single(body.RootElement.GetProperty("appearances").EnumerateArray());
        Assert.Equal("mix", appearance.GetProperty("kind").GetString());
        Assert.Equal(mix.Id.ToString(), appearance.GetProperty("id").GetString());
        Assert.Equal("Saved favourites", appearance.GetProperty("title").GetString());
        Assert.Equal($"/allstarr/v1/mixes/{mix.Id}/cover", appearance.GetProperty("coverUrl").GetString());
        Assert.Equal(1, appearance.GetProperty("matchedSongCount").GetInt32());
        Assert.Equal(PlaylistVirtualizationService.CreateProtocolId(mix.Id), appearance.GetProperty("playlistId").GetString());
    }

    [Fact]
    public async Task ClientApi_AppearancesAreOnlyForAlbumsAndSongs()
    {
        using var factory = ClientMixFactory(new());
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        await SeedClientMixAsync(factory);

        using var playlist = await client.GetAsync("/allstarr/v1/items/ext-deezer-playlist-mix/appearances");
        Assert.Equal(HttpStatusCode.BadRequest, playlist.StatusCode);
        using var unknown = await client.GetAsync("/allstarr/v1/items/ext-deezer-album-missing/appearances");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task ClientApi_AppearancesOfAnUnknownExternalSongAreNotFound()
    {
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(service => service.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "never-heard"))
            .ReturnsAsync((Song?)null);
        using var factory = ClientMixFactory(gateway);
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");

        using var response = await client.GetAsync("/allstarr/v1/items/ext-deezer-song-never-heard/appearances");
        await AssertClientApiError(response, 404, "not_found");
    }

    [Fact]
    public async Task ClientApi_AppearancesCountAProjectedLibraryCopyOfTheRequestedSong()
    {
        var gateway = new Mock<IProtocolProviderGateway>();
        using var factory = ClientMixFactory(gateway);
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var recording = await SeedClientDetailsAsync(factory, "listener", "mix-song", personal: false);
        var mix = await SeedClientMixAsync(factory, serviceSong: ClientMixSong());
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync())
        {
            var local = await db.LibraryTracks.SingleAsync(track => track.BackendItemId == "native-mix-song");
            local.CanonicalRecordingId = recording;
            await db.SaveChangesAsync();
        }

        using var items = await client.GetAsync($"/allstarr/v1/mixes/{mix.Id}/items");
        Assert.Equal(HttpStatusCode.OK, items.StatusCode);
        using var listed = JsonDocument.Parse(await items.Content.ReadAsStringAsync());
        var song = Assert.Single(listed.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("native-mix-song", song.GetProperty("itemId").GetString());
        Assert.False(song.GetProperty("isExternal").GetBoolean());

        using var response = await client.GetAsync("/allstarr/v1/items/ext-deezer-song-mix-song/appearances");
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, text);
        using var body = JsonDocument.Parse(text);
        Assert.Equal(1, body.RootElement.GetProperty("songCount").GetInt32());
        var appearance = Assert.Single(body.RootElement.GetProperty("appearances").EnumerateArray());
        Assert.Equal(mix.Id.ToString(), appearance.GetProperty("id").GetString());
        Assert.Equal(1, appearance.GetProperty("matchedSongCount").GetInt32());
    }
}
