using System.Net;
using System.Text.Json;
using allstarr.Core.Playlists;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
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
}
