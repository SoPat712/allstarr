using System.Net;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
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
    public async Task ActiveExtensionPackageIconWinsOverBundledServiceMark(string backend)
    {
        const string provider = "spotiflac-tidal-web";
        var root = Path.Combine(Path.GetTempPath(), "allstarr-cover-package-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var icon = new SKBitmap(4, 2);
            icon.Erase(SKColors.Blue);
            using var image = SKImage.FromBitmap(icon);
            using var png = image.Encode(SKEncodedImageFormat.Png, 100);
            await File.WriteAllBytesAsync(Path.Combine(root, "icon.png"), png.ToArray());
            var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
            gateway.Setup(item => item.GetArtistAsync(It.IsAny<ProtocolExecutionContext>(), provider, "resource"))
                .ReturnsAsync(new Artist { Id = $"ext-{provider}-artist-resource", ExternalProvider = provider, ExternalId = "resource" });
            using var factory = new ProtocolFactory(backend, request => request.RequestUri!.AbsolutePath == "/Users/Me"
                ? Json(200, """{"Id":"user-1"}""")
                : Json(200, """{"subsonic-response":{"status":"ok","version":"1.16.1"}}"""), services =>
            {
                if (backend == "Jellyfin") AddArtworkPrincipal(services, "user-1");
                services.RemoveAll<IProtocolProviderGateway>();
                services.AddSingleton(gateway.Object);
            });
            using var client = factory.CreateClient();
            await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync())
            {
                db.ExtensionPackages.Add(new()
                {
                    Id = Guid.NewGuid(),
                    ExtensionId = provider,
                    DisplayName = "Tidal",
                    Version = "1.0.0",
                    SdkVersion = "1",
                    Sha256 = new string('a', 64),
                    ContentSha256 = new string('b', 64),
                    PackagePath = root,
                    State = ExtensionPackageState.Active,
                    StagedAt = DateTimeOffset.UtcNow,
                    ManifestJson = """{"id":"spotiflac-tidal-web","version":"1.0.0","sdkVersion":"1","entryPoint":"index.js","icon":"icon.png","capabilities":[{"kind":"Metadata","hooks":["getTrack"],"accountScopes":[],"accountRequired":false}],"permissions":[]}"""
                });
                await db.SaveChangesAsync();
            }
            var id = $"ext-{provider}-artist-resource";
            using var response = await client.GetAsync(backend == "Jellyfin" ? $"/Items/{id}/Images/Primary?api_key=fixture-key" :
                $"/rest/getCoverArt?id={id}&u=fixture&p=fixture&v=1.16.1&c=Tempo");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var result = SKBitmap.Decode(await response.Content.ReadAsByteArrayAsync());
            Assert.Equal(600, result.Width);
            Assert.Equal(SKColors.Blue, result.GetPixel(0, 0));
            Assert.Equal(SKColors.Blue, result.GetPixel(300, 300));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    public static IEnumerable<object[]> ProviderCoverCases()
    {
        foreach (var backend in new[] { "Jellyfin", "Subsonic" })
            foreach (var provider in new[] { "deezer", "spotiflac-tidal-web", "spotiflac-no-mark" })
                foreach (var kind in new[] { "song", "album", "artist" })
                    foreach (var real in new[] { false, true })
                        yield return [backend, provider, kind, real];
    }

    [Theory]
    [MemberData(nameof(ProviderCoverCases))]
    public async Task ExternalCoversUseProviderRasterOnlyWhenRealArtworkIsAbsent(string backend, string provider, string kind, bool real)
    {
        var imageRequests = 0;
        using var bitmap = new SKBitmap(8, 8);
        bitmap.Erase(SKColors.Red);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        var realBytes = encoded.ToArray();
        var cover = real ? "https://images.example.test/cover.png" : null;
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), provider, "resource"))
            .ReturnsAsync(new Song { Id = $"ext-{provider}-song-resource", ExternalProvider = provider, ExternalId = "resource", CoverArtUrl = cover });
        gateway.Setup(item => item.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), provider, "resource"))
            .ReturnsAsync(new Album { Id = $"ext-{provider}-album-resource", ExternalProvider = provider, ExternalId = "resource", CoverArtUrl = cover });
        gateway.Setup(item => item.GetArtistAsync(It.IsAny<ProtocolExecutionContext>(), provider, "resource"))
            .ReturnsAsync(new Artist { Id = $"ext-{provider}-artist-resource", ExternalProvider = provider, ExternalId = "resource", ImageUrl = cover });
        using var factory = new ProtocolFactory(backend, request =>
        {
            if (request.RequestUri!.AbsolutePath == "/Users/Me") return Json(200, """{"Id":"user-1"}""");
            if (request.RequestUri.AbsolutePath == "/cover.png")
            {
                imageRequests++;
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(realBytes) { Headers = { ContentType = new("image/png") } } };
            }
            return Json(200, """{"subsonic-response":{"status":"ok","version":"1.16.1"}}""");
        }, services =>
        {
            if (backend == "Jellyfin") AddArtworkPrincipal(services, "user-1");
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
        });
        using var client = factory.CreateClient();
        var id = $"ext-{provider}-{kind}-resource";
        var url = backend == "Jellyfin" ? $"/Items/{id}/Images/Primary?api_key=fixture-key" :
            $"/rest/getCoverArt?id={id}&u=fixture&p=fixture&v=1.16.1&c=Tempo";
        using var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        using var result = SKBitmap.Decode(bytes);
        Assert.NotNull(result);
        Assert.Equal(real ? 8 : 600, result.Width);
        Assert.Equal(real ? 8 : 600, result.Height);
        Assert.Equal(real ? 1 : 0, imageRequests);
        if (real) Assert.Equal(realBytes, bytes);
        Assert.NotNull(response.Headers.ETag);
        using var conditional = new HttpRequestMessage(HttpMethod.Get, url);
        conditional.Headers.IfNoneMatch.Add(response.Headers.ETag!);
        using var repeated = await client.SendAsync(conditional);
        Assert.Equal(HttpStatusCode.NotModified, repeated.StatusCode);
        Assert.Empty(await repeated.Content.ReadAsByteArrayAsync());
        if (!real)
        {
            var thumbnailUrl = url + (backend == "Jellyfin" ? "&maxWidth=96&maxHeight=96" : "&size=96");
            using var thumbnail = await client.GetAsync(thumbnailUrl);
            Assert.Equal(HttpStatusCode.OK, thumbnail.StatusCode);
            Assert.Equal("image/png", thumbnail.Content.Headers.ContentType?.MediaType);
            using var small = SKBitmap.Decode(await thumbnail.Content.ReadAsByteArrayAsync());
            Assert.Equal(96, small.Width);
            Assert.Equal(96, small.Height);
        }
    }
}
