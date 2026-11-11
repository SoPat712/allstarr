using System.Net;
using allstarr.Core.Capabilities;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Models.Settings;
using allstarr.Services.Common;
using allstarr.Services.Jellyfin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Moq;
using SkiaSharp;

namespace allstarr.Tests;

public sealed class MixCoverArtLoaderTests
{
    [Fact]
    public async Task LocalAlbumItemsLoadAndLocalCopiesDoNotSendServiceAlbumIdsToTheLibrary()
    {
        var paths = new List<string>();
        using var client = new HttpClient(new CoverHandler(request =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            var color = request.RequestUri.AbsolutePath switch
            {
                "/Items/third/Images/Primary" => SKColors.Green,
                "/Items/fourth/Images/Primary" => SKColors.Red,
                "/Items/fifth/Images/Primary" => SKColors.Yellow,
                _ => SKColors.Blue
            };
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Picture(color)) };
        }));
        var clients = new Mock<IHttpClientFactory>();
        clients.Setup(item => item.CreateClient(It.IsAny<string>())).Returns(client);
        var cache = new DisabledApplicationCache();
        using var jellyfin = new JellyfinProxyService(clients.Object, Options.Create(new JellyfinSettings { Url = "http://library.test" }),
            new HttpContextAccessor(), NullLogger<JellyfinProxyService>.Instance, cache,
            new MediaAssetResolver(cache, NullLogger<MediaAssetResolver>.Instance), new ConfigurationBuilder().Build());
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetPublicArtworkUriAsync("deezer", ProviderResourceKind.Track, "missing",
            It.IsAny<CancellationToken>(), false, false)).ReturnsAsync((Uri?)null);
        var loader = new MixCoverArtLoader(jellyfin, clients.Object, gateway.Object, NullLogger<MixCoverArtLoader>.Instance);
        var song = new Song { Id = "local-talk", AlbumId = "3027f96ecc3c97320fdc6ce9e60e1fba", IsLocal = true };
        Assert.NotNull(await loader.LoadAsync(MixCoverArt.Reference(song)!, default));
        Assert.Equal("/Items/3027f96ecc3c97320fdc6ce9e60e1fba/Images/Primary", Assert.Single(paths));
        song.AlbumId = "ext-deezer-album-42";
        Assert.Equal("library:local-talk", MixCoverArt.Reference(song));
        Assert.NotNull(await loader.LoadAsync(MixCoverArt.Reference(song)!, default));
        Assert.Equal("/Items/local-talk/Images/Primary", paths[1]);
        song.AlbumId = "3027f96ecc3c97320fdc6ce9e60e1fba";
        var mix = new PersonalMix(Guid.NewGuid(), "daily-1", "Hip-Hop Blend", "", 1, null, new(true, "Ready"), true,
            new[] { song, new Song { Id = "ext-deezer-song-missing", ExternalProvider = "deezer" },
                new Song { Id = "third", IsLocal = true }, new Song { Id = "fourth", IsLocal = true }, new Song { Id = "fifth", IsLocal = true } }
                .Select(item => new PersonalMixItem(Guid.Empty, 1, item, "library", "", 1)).ToArray());
        var context = new DefaultHttpContext();
        var result = Assert.IsType<FileContentResult>(await new MixCoverImageService(loader).CreateResponseAsync(MixCoverInput.From(mix), context));
        Assert.Equal(MixCoverRenderer.RenderCollage(new[] { SKColors.Blue, SKColors.Green, SKColors.Red, SKColors.Yellow }.Select(Picture).ToArray()), result.FileContents);
        Assert.NotEmpty(context.Response.Headers.ETag.ToString());
    }

    [Fact]
    public async Task MissingServiceArtworkIsSkippedAndReportedOncePerMixWithoutCredentials()
    {
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetPublicArtworkUriAsync("deezer", ProviderResourceKind.Track, "missing",
            It.IsAny<CancellationToken>(), false, false)).ReturnsAsync((Uri?)null);
        var messages = new List<string>();
        var logger = new Mock<Microsoft.Extensions.Logging.ILogger<MixCoverArtLoader>>();
        logger.Setup(item => item.Log(Microsoft.Extensions.Logging.LogLevel.Information, It.IsAny<Microsoft.Extensions.Logging.EventId>(),
            It.IsAny<It.IsAnyType>(), It.IsAny<Exception?>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Callback(new InvocationAction(call => messages.Add(call.Arguments[2].ToString()!)));
        using var client = new HttpClient(new CoverHandler(_ => throw new HttpRequestException("https://user:secret@covers.example.test/?token=secret")));
        var clients = new Mock<IHttpClientFactory>();
        clients.Setup(item => item.CreateClient(It.IsAny<string>())).Returns(client);
        var service = new MixCoverImageService(new MixCoverArtLoader(null!, clients.Object, gateway.Object, logger.Object));
        var missing = new Song { Id = "ext-deezer-song-missing", ExternalProvider = "deezer" };
        var input = new MixCoverInput("daily-1", 1, "daily-1", Count: 5,
            Art: [MixCoverArt.Reference(missing)!, "https://covers.example.test/picture?token=secret"]);
        await service.CreateResponseAsync(input, new DefaultHttpContext());
        await service.CreateResponseAsync(input, new DefaultHttpContext());
        Assert.Equal(2, messages.Count);
        Assert.Contains(messages, message => message.Contains("service:ext-deezer-song-missing") && message.Contains("no artwork"));
        Assert.DoesNotContain(messages, message => message.Contains("secret") || message.Contains("https://"));
        await service.CreateResponseAsync(input with { Id = "daily-2" }, new DefaultHttpContext());
        Assert.Equal(4, messages.Count);
    }

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
