using System.Net;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Models.Search;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Fact]
    public async Task ClientApi_SimilarAlbumsPreferTheLibraryAndOpenServiceAlbumsThroughAllstarr()
    {
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(service => service.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "seed-album"))
            .ReturnsAsync(new Album { Id = "ext-deezer-album-seed-album", Title = "Seed album", Artist = "Seed Artist", ArtistId = "ext-deezer-artist-seed" });
        gateway.Setup(service => service.GetPlayableProviderOrderAsync(It.IsAny<ProviderActorContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["deezer"]);
        gateway.Setup(service => service.GetSimilarArtistsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "seed", It.IsAny<int>()))
            .ReturnsAsync([
                new Artist { Name = "Library Friend", ExternalProvider = "deezer", ExternalId = "friend" },
                new Artist { Name = "Service Friend", ExternalProvider = "deezer", ExternalId = "other" },
                new Artist { Name = "Seed Artist", ExternalProvider = "deezer", ExternalId = "seed" }]);
        gateway.Setup(service => service.GetArtistAlbumsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "other"))
            .ReturnsAsync([
                new Album { Id = "ext-deezer-album-other-single", Title = "A single", Artist = "Service Friend", ReleaseType = "single", ExternalProvider = "deezer", ExternalId = "other-single" },
                new Album { Id = "ext-deezer-album-other-album", Title = "Other album", Artist = "Service Friend", ArtistId = "ext-deezer-artist-other", Year = 2020, SongCount = 10, ExternalProvider = "deezer", ExternalId = "other-album" }]);
        gateway.Setup(service => service.GetArtistAlbumsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "seed"))
            .ReturnsAsync([new Album { Id = "ext-deezer-album-seed-album", Title = "Seed album", Artist = "Seed Artist", ExternalProvider = "deezer", ExternalId = "seed-album" }]);
        using var factory = ClientDetailsFactory(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            var query = Uri.UnescapeDataString(request.RequestUri.Query);
            if (path == "/Users/Me") return Json(200, """{"Id":"listener"}""");
            if (path == "/Artists/AlbumArtists" && query.Contains("searchTerm=Library Friend", StringComparison.Ordinal))
                return Json(200, """{"Items":[{"Id":"lib-artist","Name":"Library Friend"}]}""");
            if (path == "/Users/listener/Items" && query.Contains("albumArtistIds=lib-artist", StringComparison.Ordinal))
                return Json(200, """{"Items":[{"Id":"lib-album","Type":"MusicAlbum","Name":"Owned album","AlbumArtist":"Library Friend","ProductionYear":2019,"ChildCount":9,"ImageTags":{"Primary":"owned-art"}}]}""");
            return Json(200, """{"Items":[],"TotalRecordCount":0}""");
        }, services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");

        using var response = await client.GetAsync("/allstarr/v1/items/ext-deezer-album-seed-album/similar?limit=12");
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, text);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var body = JsonDocument.Parse(text);
        Assert.Equal("deezer", body.RootElement.GetProperty("basedOn").GetString());
        var albums = body.RootElement.GetProperty("albums").EnumerateArray().ToArray();
        Assert.Equal(["lib-album", "ext-deezer-album-other-album"], albums.Select(album => album.GetProperty("id").GetString()));
        Assert.Equal("library", albums[0].GetProperty("source").GetString());
        Assert.False(albums[0].GetProperty("isExternal").GetBoolean());
        Assert.Equal(2019, albums[0].GetProperty("year").GetInt32());
        Assert.Equal(9, albums[0].GetProperty("songCount").GetInt32());
        Assert.Equal("deezer", albums[1].GetProperty("source").GetString());
        Assert.True(albums[1].GetProperty("isExternal").GetBoolean());
        Assert.Equal("Other album", albums[1].GetProperty("title").GetString());
        Assert.Equal("ext-deezer-artist-other", albums[1].GetProperty("artistId").GetString());
        Assert.Equal("deezer", albums[1].GetProperty("provider").GetProperty("id").GetString());
        Assert.StartsWith("/Items/ext-deezer-album-other-album/Images/Primary", albums[1].GetProperty("coverUrl").GetString());
        gateway.Verify(service => service.GetArtistAlbumsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "friend"), Times.Never);

        using var song = await client.GetAsync("/allstarr/v1/items/ext-deezer-song-mix-song/similar");
        Assert.Equal(HttpStatusCode.BadRequest, song.StatusCode);
    }

    [Fact]
    public async Task ClientApi_SimilarAlbumsForALibraryAlbumFindTheArtistOnDeezer()
    {
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(service => service.GetPlayableProviderOrderAsync(It.IsAny<ProviderActorContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["apple-musickit"]);
        gateway.Setup(service => service.SearchAsync(It.IsAny<ProtocolExecutionContext>(), "Seed Artist Seed album", 0, 5, 0, "deezer"))
            .ReturnsAsync(new SearchResult { Albums = [new Album { Title = "Seed album", Artist = "Seed Artist", ArtistId = "ext-deezer-artist-seed", ExternalProvider = "deezer" }] });
        gateway.Setup(service => service.SearchAsync(It.IsAny<ProtocolExecutionContext>(), "Seed Artist", 0, 0, 10, "deezer"))
            .ReturnsAsync(new SearchResult
            {
                Artists = [new Artist { Name = "Seed Artist", ExternalProvider = "deezer", ExternalId = "namesake" },
                new Artist { Name = "Seed Artist", ExternalProvider = "deezer", ExternalId = "seed" }]
            });
        gateway.Setup(service => service.GetSimilarArtistsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "seed", It.IsAny<int>()))
            .ReturnsAsync([new Artist { Name = "Service Friend", ExternalProvider = "deezer", ExternalId = "other" }]);
        gateway.Setup(service => service.GetArtistAlbumsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "other"))
            .ReturnsAsync([new Album { Id = "ext-deezer-album-other-album", Title = "Other album", Artist = "Service Friend", ExternalProvider = "deezer", ExternalId = "other-album" }]);
        using var factory = ClientDetailsFactory(request => request.RequestUri!.AbsolutePath switch
        {
            "/Users/Me" => Json(200, """{"Id":"listener"}"""),
            "/Users/listener/Items/library-seed" => Json(200, """{"Id":"library-seed","Type":"MusicAlbum","Name":"Seed album","AlbumArtist":"Seed Artist"}"""),
            _ => Json(200, """{"Items":[],"TotalRecordCount":0}""")
        }, services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");

        using var response = await client.GetAsync("/allstarr/v1/items/library-seed/similar");
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, text);
        using var body = JsonDocument.Parse(text);
        Assert.Equal("deezer", body.RootElement.GetProperty("basedOn").GetString());
        Assert.Equal("ext-deezer-album-other-album", Assert.Single(body.RootElement.GetProperty("albums").EnumerateArray()).GetProperty("id").GetString());
        gateway.Verify(service => service.GetSimilarArtistsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "namesake", It.IsAny<int>()), Times.Never);
    }
}
