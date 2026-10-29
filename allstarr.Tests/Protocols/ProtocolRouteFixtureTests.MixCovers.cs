using System.Net;
using System.Text.Json;
using allstarr.Core.Music;
using allstarr.Core.Playlists;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using SkiaSharp;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Theory]
    [InlineData("Jellyfin")]
    [InlineData("Subsonic")]
    public async Task PersonalMixCoverRoutesReturnPngAndRevalidateEachGeneration(string backend)
    {
        var model = CoverPlaylist() with { MixCover = new("mix-1", 1, "daily-1", Name: "Daily Mix", Count: 4) };
        var readable = true;
        var playlists = new Mock<IPlaylistVirtualizationService>(MockBehavior.Strict);
        playlists.Setup(service => service.ReadAsync(It.IsAny<ProtocolExecutionContext>(), model.ProtocolId,
            It.IsAny<CancellationToken>())).ReturnsAsync(() => readable ? model : null);
        using var factory = new ProtocolFactory(backend, request => request.RequestUri!.AbsolutePath switch
        {
            "/Users/Me" => Json(200, """{"Id":"user-1"}"""),
            "/rest/ping.view" => Json(200, """{"subsonic-response":{"status":"ok","version":"1.16.1"}}"""),
            _ => throw new InvalidOperationException("Unexpected cover relay")
        }, services =>
        {
            if (backend == "Jellyfin") AddArtworkPrincipal(services, "user-1");
            services.RemoveAll<IPlaylistVirtualizationService>();
            services.AddSingleton(playlists.Object);
        });
        using var client = factory.CreateClient();
        var auth = backend == "Jellyfin" ? "api_key=fixture-key" : "u=fixture&p=fixture&v=1.16.1&c=Tempo&f=json";
        var detailPath = backend == "Jellyfin" ? $"/Items/{model.ProtocolId}?{auth}" :
            $"/rest/getPlaylist?id={model.ProtocolId}&{auth}";
        using var detail = await client.GetAsync(detailPath);
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        using var metadata = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
        var coverId = backend == "Jellyfin" ? model.ProtocolId : metadata.RootElement
            .GetProperty("subsonic-response").GetProperty("playlist").GetProperty("coverArt").GetString()!;
        if (backend == "Jellyfin")
            Assert.False(string.IsNullOrEmpty(metadata.RootElement.GetProperty("ImageTags").GetProperty("Primary").GetString()));
        var imagePath = backend == "Jellyfin" ? $"/Items/{coverId}/Images/Primary?{auth}" :
            $"/rest/getCoverArt.view?id={coverId}&{auth}";
        using var first = await client.GetAsync(imagePath);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("image/png", first.Content.Headers.ContentType?.MediaType);
        Assert.NotNull(first.Headers.ETag);
        Assert.True(first.Headers.CacheControl?.Private);
        Assert.True(first.Headers.CacheControl?.MustRevalidate);
        Assert.Equal(TimeSpan.Zero, first.Headers.CacheControl?.MaxAge);
        var bytes = await first.Content.ReadAsByteArrayAsync();
        using var png = SKBitmap.Decode(bytes);
        Assert.Equal(600, png.Width);
        Assert.Equal(600, png.Height);
        using var conditional = new HttpRequestMessage(HttpMethod.Get, imagePath);
        conditional.Headers.IfNoneMatch.Add(first.Headers.ETag!);
        using var unchanged = await client.SendAsync(conditional);
        Assert.Equal(HttpStatusCode.NotModified, unchanged.StatusCode);
        Assert.Empty(await unchanged.Content.ReadAsByteArrayAsync());

        model = model with { MixCover = model.MixCover! with { Generation = 2 } };
        using var stale = new HttpRequestMessage(HttpMethod.Get, imagePath);
        stale.Headers.IfNoneMatch.Add(first.Headers.ETag!);
        using var refreshed = await client.SendAsync(stale);
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        Assert.NotEqual(first.Headers.ETag, refreshed.Headers.ETag);
        if (backend == "Jellyfin")
        {
            using var other = await client.GetAsync($"/Items/{model.ProtocolId}/Images/Backdrop?{auth}");
            Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
        }
        else
        {
            using var plain = await client.GetAsync($"/rest/getCoverArt?id={model.ProtocolId}&{auth}");
            Assert.Equal(refreshed.Headers.ETag, plain.Headers.ETag);
            using var updatedDetail = await client.GetAsync(detailPath);
            using var updatedMetadata = JsonDocument.Parse(await updatedDetail.Content.ReadAsStringAsync());
            Assert.NotEqual(coverId, updatedMetadata.RootElement.GetProperty("subsonic-response")
                .GetProperty("playlist").GetProperty("coverArt").GetString());
        }
        readable = false;
        using var denied = new HttpRequestMessage(HttpMethod.Get, imagePath);
        denied.Headers.IfNoneMatch.Add(refreshed.Headers.ETag!);
        using var inaccessible = await client.SendAsync(denied);
        Assert.Equal(HttpStatusCode.NotFound, inaccessible.StatusCode);
    }

    [Theory]
    [InlineData("Jellyfin", false)]
    [InlineData("Jellyfin", true)]
    [InlineData("Subsonic", false)]
    [InlineData("Subsonic", true)]
    public async Task OrdinaryPlaylistCoversKeepTheirExistingRoute(string backend, bool virtualPlaylist)
    {
        var model = CoverPlaylist() with { ArtworkReferenceKey = "existing-art", TargetPlaylistId = "native-playlist" };
        var id = virtualPlaylist ? model.ProtocolId : "native-playlist";
        var playlists = new Mock<IPlaylistVirtualizationService>(MockBehavior.Strict);
        playlists.Setup(service => service.ReadAsync(It.IsAny<ProtocolExecutionContext>(), model.ProtocolId,
            It.IsAny<CancellationToken>())).ReturnsAsync(model);
        byte[] expected = [1, 2, 3, 4];
        var relays = 0;
        using var factory = new ProtocolFactory(backend, request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/Users/Me") return Json(200, """{"Id":"user-1"}""");
            if (path == "/rest/ping.view") return Json(200, """{"subsonic-response":{"status":"ok","version":"1.16.1"}}""");
            Assert.Equal(backend == "Jellyfin" ? "/Items/native-playlist/Images/Primary" : "/rest/getCoverArt", path);
            if (backend == "Subsonic") Assert.Contains($"id={id}", request.RequestUri.Query);
            relays++;
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(expected) { Headers = { ContentType = new("image/jpeg") } } };
        }, services =>
        {
            if (backend == "Jellyfin") AddArtworkPrincipal(services, "user-1");
            services.RemoveAll<IPlaylistVirtualizationService>();
            services.AddSingleton(playlists.Object);
        });
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(backend == "Jellyfin" ? $"/Items/{id}/Images/Primary?api_key=fixture-key" :
            $"/rest/getCoverArt?id={id}&u=fixture&p=fixture&v=1.16.1&c=Tempo");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(1, relays);
    }

    private static VirtualPlaylistReadModel CoverPlaylist()
    {
        var linkId = Guid.Parse("88fc0616-e45e-44ac-81c3-6a884e86e15d");
        return new(PlaylistVirtualizationService.CreateProtocolId(linkId), linkId, Guid.NewGuid(), "Daily Mix", null,
            null, "personal", "mix-1", "1", PlaylistLinkMode.Virtual, []);
    }
}
