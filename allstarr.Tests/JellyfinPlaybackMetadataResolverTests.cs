using System.Net;
using System.Text;
using allstarr.Models.Settings;
using allstarr.Services.Common;
using allstarr.Services.Jellyfin;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace allstarr.Tests;

public sealed class JellyfinPlaybackMetadataResolverTests
{
    [Fact]
    public async Task ResolveAsync_UsesServerCredentialAndCachesTheItemFixture()
    {
        var requestCount = 0;
        var resolver = CreateResolver(request =>
        {
            requestCount++;
            Assert.Equal("server-api-key", request.Headers.GetValues("X-Emby-Token").Single());
            Assert.Equal("user-1", ParseQuery(request.RequestUri!).GetValueOrDefault("userId"));
            return Json("""
                {
                  "Name": "Fixture title",
                  "AlbumArtist": "Fixture artist",
                  "Album": "Fixture album",
                  "ImageTags": { "Primary": "etag-1" }
                }
                """);
        });

        var first = await resolver.ResolveAsync("item-1", CancellationToken.None);
        var second = await resolver.ResolveAsync("item-1", CancellationToken.None);

        Assert.NotNull(first);
        Assert.Equal("Fixture title", first.Title);
        Assert.Equal("Fixture artist", first.Artist);
        Assert.Equal("Fixture album", first.Album);
        Assert.Equal("/api/admin/downloads/artwork/item-1", first.CoverArtUrl);
        Assert.NotNull(second);
        Assert.Equal(first.Title, second.Title);
        Assert.Equal(1, requestCount);
    }

    [Fact]
    public async Task ResolveAsync_UsesAlbumArtworkWhenAudioItemHasNoPrimaryImage()
    {
        var resolver = CreateResolver(_ => Json("""
            {
              "Name": "Album track",
              "AlbumArtist": "Artist",
              "AlbumId": "album-42",
              "AlbumPrimaryImageTag": "album-etag"
            }
            """));

        var metadata = await resolver.ResolveAsync("track-1", CancellationToken.None);

        Assert.NotNull(metadata);
        Assert.Equal("/api/admin/downloads/artwork/album-42", metadata.CoverArtUrl);
    }

    [Fact]
    public async Task ResolveArtworkAsync_ReturnsOnlyBoundedImageContent()
    {
        var resolver = CreateResolver(request =>
        {
            Assert.Equal("server-api-key", request.Headers.GetValues("X-Emby-Token").Single());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3, 4])
                {
                    Headers = { ContentType = new("image/png") }
                }
            };
        });

        var artwork = await resolver.ResolveArtworkAsync("item-1", CancellationToken.None);

        Assert.NotNull(artwork);
        Assert.Equal("image/png", artwork.ContentType);
        Assert.Equal([1, 2, 3, 4], artwork.Content);
    }

    [Fact]
    public async Task ResolveAsync_CachesMissesSeparatelyFromMetadata()
    {
        var requestCount = 0;
        var resolver = CreateResolver(_ =>
        {
            requestCount++;
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        Assert.Null(await resolver.ResolveAsync("missing", CancellationToken.None));
        Assert.Null(await resolver.ResolveAsync("missing", CancellationToken.None));
        Assert.Equal(1, requestCount);
    }

    private static JellyfinPlaybackMetadataResolver CreateResolver(
        Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var client = new HttpClient(new StubHttpMessageHandler(responder));
        var cache = new TestMemoryApplicationCache();
        return new JellyfinPlaybackMetadataResolver(
            new StubHttpClientFactory(client),
            Options.Create(new JellyfinSettings
            {
                Url = "http://jellyfin.test",
                ApiKey = "server-api-key",
                UserId = "user-1"
            }),
            cache,
            NullLogger<JellyfinPlaybackMetadataResolver>.Instance);
    }

    private static HttpResponseMessage Json(string content) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json")
        };

    private static Dictionary<string, string> ParseQuery(Uri uri)
    {
        return uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(
                part => Uri.UnescapeDataString(part[0]),
                part => part.Length > 1 ? Uri.UnescapeDataString(part[1]) : string.Empty);
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(responder(request));
    }
}
