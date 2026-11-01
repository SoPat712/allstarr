using System.Net;
using allstarr.Core.Capabilities;
using allstarr.Controllers;
using allstarr.Models.Domain;
using allstarr.Services.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using allstarr.Core.Protocols;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Fact]
    public void ExternalImages_HaveNoSubstituteBytePath()
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance;
        Assert.Null(typeof(JellyfinController).GetField("PlaceholderImageBytes", flags));
        Assert.Null(typeof(JellyfinController).GetMethod("GetPlaceholderImage", flags));
    }

    [Theory]
    [InlineData("/Items/ext-deezer-song-42/Images/Primary")]
    [InlineData("/Items/ext-deezer-album-42/Images/Primary/0/tag/jpg/96/96/0/0")]
    [InlineData("/Items/ext-deezer-artist-42/Images/Primary?maxWidth=96")]
    [InlineData("/Items/ext-applemusic-playlist-42/Images/Primary")]
    public async Task ExternalImageFailure_IsEmptyNotFoundAndNeverCached(string path)
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(service => service.GetPublicArtworkUriAsync(It.IsAny<string>(), It.IsAny<ProviderResourceKind>(),
            "42", It.IsAny<CancellationToken>())).ReturnsAsync(new Uri("https://images.example.test/cover.jpg"));
        using var factory = new ProtocolFactory("Jellyfin", _ => new(HttpStatusCode.NotFound), services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
        });
        using var client = factory.CreateClient();
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Head })
        {
            using var response = await client.SendAsync(new(method, path));
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            Assert.False(response.Content.Headers.ContentType?.MediaType?.StartsWith("image/") == true);
            Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        }
    }

    [Fact]
    public async Task ExternalImageTimeout_NextRequestCanReturnRealArtwork()
    {
        var fail = true;
        byte[] bytes = [0xFF, 0xD8, 0xFF, 0xD9];
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(service => service.GetPublicArtworkUriAsync("deezer", ProviderResourceKind.Track,
            "42", It.IsAny<CancellationToken>())).ReturnsAsync(new Uri("https://images.example.test/cover.jpg"));
        using var factory = new ProtocolFactory("Jellyfin", _ => fail ? throw new TaskCanceledException("Artwork timeout") :
            new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) { Headers = { ContentType = new("image/jpeg") } } }, services =>
            {
                services.RemoveAll<IProtocolProviderGateway>();
                services.AddSingleton(gateway.Object);
            });
        using var client = factory.CreateClient();
        const string path = "/Items/ext-deezer-song-42/Images/Primary";
        using var failure = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, failure.StatusCode);
        Assert.True(failure.Headers.CacheControl?.NoStore);
        fail = false;
        using var success = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        Assert.Equal(bytes, await success.Content.ReadAsByteArrayAsync());
    }
    [Fact]
    public async Task ExternalArtworkTransportTimeout_IsRetriedWithoutAnError()
    {
        var attempts = 0;
        var errors = new List<string>();
        byte[] bytes = [0xFF, 0xD8, 0xFF, 0xD9];
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(service => service.GetPublicArtworkUriAsync("deezer", ProviderResourceKind.Track,
            "42", It.IsAny<CancellationToken>())).ReturnsAsync(new Uri("https://images.example.test/cover.jpg"));
        using var factory = new ProtocolFactory("Jellyfin", _ => ++attempts == 1 ? throw new TaskCanceledException("Transport timeout") :
            new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) { Headers = { ContentType = new("image/jpeg") } } }, services =>
            {
                services.RemoveAll<IProtocolProviderGateway>();
                services.AddSingleton(gateway.Object);
                services.AddSingleton<ILogger<JellyfinController>>(new PlaybackCapturingLogger(errors));
            });
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/Items/ext-deezer-song-42/Images/Primary");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(2, attempts);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task ExternalSongCoverFailure_UsesStoredAlbumWithoutProviderLookup()
    {
        byte[] bytes = [0xFF, 0xD8, 0xFF, 0xD9];
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(service => service.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "42"))
            .ReturnsAsync(new Song { Id = "ext-deezer-song-42", CoverArtUrl = "https://images.example.test/missing.jpg" });
        gateway.Setup(service => service.GetStoredSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "42"))
            .ReturnsAsync(new Song { Id = "ext-deezer-song-42", AlbumId = "ext-deezer-album-9" });
        gateway.Setup(service => service.GetStoredAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "9"))
            .ReturnsAsync(new Album { Id = "ext-deezer-album-9", CoverArtUrl = "https://images.example.test/album.jpg" });
        using var factory = new ProtocolFactory("Jellyfin", request => request.RequestUri!.AbsolutePath switch
        {
            "/Users/Me" => Json(200, """{"Id":"user-1"}"""),
            "/album.jpg" => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) { Headers = { ContentType = new("image/jpeg") } } },
            "/missing.jpg" => new(HttpStatusCode.NotFound),
            _ => throw new InvalidOperationException("Unexpected artwork request")
        }, services =>
        {
            AddArtworkPrincipal(services, "user-1");
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
        });
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/Items/ext-deezer-song-42/Images/Primary?api_key=fixture-key");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
        gateway.Verify(service => service.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        gateway.VerifyAll();
        gateway.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExternalImageClientDisconnect_DoesNotLogAnError()
    {
        var errors = new List<string>();
        IHttpContextAccessor? contexts = null;
        var resolver = new Mock<IMediaAssetResolver>(MockBehavior.Strict);
        resolver.Setup(service => service.ResolveAsync(It.IsAny<MediaAssetIdentity>(),
            It.IsAny<Func<CancellationToken, Task<MediaAssetSource?>>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback(() => contexts!.HttpContext!.RequestAborted = new CancellationToken(true))
            .ThrowsAsync(new OperationCanceledException());
        using var factory = new ProtocolFactory("Jellyfin", _ => throw new InvalidOperationException("No upstream request expected"), services =>
        {
            services.RemoveAll<IMediaAssetResolver>();
            services.AddSingleton(resolver.Object);
            services.AddSingleton<ILogger<JellyfinController>>(new PlaybackCapturingLogger(errors));
        });
        contexts = factory.Services.GetRequiredService<IHttpContextAccessor>();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/Items/ext-deezer-song-42/Images/Primary");
        Assert.Empty(errors);
    }

}
