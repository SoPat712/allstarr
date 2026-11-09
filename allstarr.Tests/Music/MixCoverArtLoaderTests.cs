using System.Net;
using allstarr.Core.Capabilities;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SkiaSharp;

namespace allstarr.Tests;

public sealed class MixCoverArtLoaderTests
{
    [Fact]
    public async Task ServiceSongsWithoutSavedUrlsUsePublicItemArtworkForTheCollage()
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        var shades = new[] { SKColors.Red, SKColors.Green, SKColors.Blue, SKColors.Yellow };
        var songs = Enumerable.Range(0, 4).Select(index => new Song
        {
            Id = $"ext-deezer-song-{index}",
            AlbumId = $"ext-deezer-album-{index}",
            ExternalProvider = "deezer",
            ExternalId = index.ToString()
        }).ToArray();
        foreach (var song in songs)
            gateway.Setup(item => item.GetPublicArtworkUriAsync("deezer", ProviderResourceKind.Track, song.ExternalId!,
                It.IsAny<CancellationToken>(), false, false)).ReturnsAsync(new Uri($"https://covers.example.test/{song.ExternalId}.jpg"));
        using var client = new HttpClient(new CoverHandler(request => new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Picture(shades[int.Parse(request.RequestUri!.AbsolutePath[1..^4])]))
            { Headers = { ContentType = new("image/jpeg") } }
        }));
        var clients = new Mock<IHttpClientFactory>();
        clients.Setup(item => item.CreateClient(It.IsAny<string>())).Returns(client);
        var loader = new MixCoverArtLoader(null!, clients.Object, gateway.Object, NullLogger<MixCoverArtLoader>.Instance);
        var input = new MixCoverInput("weekly", 1, "discover-weekly", Count: songs.Length,
            Art: MixCoverArt.Pick(songs).Select(song => MixCoverArt.Reference(song)!).ToArray());
        Assert.Equal(songs.Select(song => "service:" + song.Id), input.Art);
        songs[0].CoverArtUrl = "https://covers.example.test/0.jpg";
        Assert.NotEqual(MixCoverImageService.Version(input), MixCoverImageService.Version(input with
        { Art = MixCoverArt.Pick(songs).Select(song => MixCoverArt.Reference(song)!).ToArray() }));
        var context = new DefaultHttpContext();
        var result = Assert.IsType<FileContentResult>(await new MixCoverImageService(loader)
            .CreateResponseAsync(input, context, MixCoverImageService.Version(input)));
        Assert.Equal("image/jpeg", result.ContentType);
        Assert.Equal("private, max-age=31536000, immutable", context.Response.Headers.CacheControl);
        using var collage = SKBitmap.Decode(result.FileContents);
        Assert.Equal((600, 600), (collage.Width, collage.Height));
        Assert.True(collage.GetPixel(150, 150).Red > 240);
        Assert.True(collage.GetPixel(450, 150).Green > 100);
        Assert.True(collage.GetPixel(150, 450).Blue > 240);
        Assert.True(collage.GetPixel(450, 450).Red > 240);
        gateway.VerifyAll();
        gateway.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task FailingServiceArtworkUsesAShortLivedGradientAndRetries()
    {
        var available = false;
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.GetPublicArtworkUriAsync("deezer", ProviderResourceKind.Track, "42",
            It.IsAny<CancellationToken>(), false, false)).Returns(() => available
                ? Task.FromResult<Uri?>(new("https://covers.example.test/42.jpg"))
                : Task.FromException<Uri?>(new HttpRequestException("Cover unavailable")));
        using var client = new HttpClient(new CoverHandler(_ => new(HttpStatusCode.OK)
        { Content = new ByteArrayContent(Picture(SKColors.Blue)) { Headers = { ContentType = new("image/jpeg") } } }));
        var clients = new Mock<IHttpClientFactory>();
        clients.Setup(item => item.CreateClient(It.IsAny<string>())).Returns(client);
        var service = new MixCoverImageService(new MixCoverArtLoader(null!, clients.Object, gateway.Object, NullLogger<MixCoverArtLoader>.Instance));
        var input = new MixCoverInput("weekly", 1, "discover-weekly", Count: 17, Art: ["service:ext-deezer-song-42"]);
        var context = new DefaultHttpContext();
        context.Request.Headers.IfNoneMatch = $"\"{MixCoverImageService.GetTag(input)}\"";
        var fallback = Assert.IsType<FileContentResult>(await service.CreateResponseAsync(input, context, MixCoverImageService.Version(input)));
        Assert.Equal("image/png", fallback.ContentType);
        Assert.Equal("private, max-age=60", context.Response.Headers.CacheControl);
        Assert.False(context.Response.Headers.ContainsKey("ETag"));
        available = true;
        var recovered = Assert.IsType<FileContentResult>(await service.CreateResponseAsync(input, new DefaultHttpContext(), MixCoverImageService.Version(input)));
        Assert.Equal("image/jpeg", recovered.ContentType);
        Assert.NotEqual(fallback.FileContents, recovered.FileContents);
        gateway.Verify(item => item.GetPublicArtworkUriAsync("deezer", ProviderResourceKind.Track, "42", It.IsAny<CancellationToken>(), false, false), Times.Exactly(2));
    }

    private static byte[] Picture(SKColor color)
    {
        using var bitmap = new SKBitmap(30, 30);
        bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(SKEncodedImageFormat.Jpeg, 100).ToArray();
    }

    private sealed class CoverHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(respond(request));
    }
}
