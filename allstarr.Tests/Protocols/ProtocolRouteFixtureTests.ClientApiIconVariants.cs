using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using SkiaSharp;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Theory]
    [InlineData("variant=invalid")]
    [InlineData("variant=Color")]
    [InlineData("format=jpg")]
    [InlineData("format=")]
    [InlineData("size=15")]
    [InlineData("size=257")]
    [InlineData("size=abc")]
    public async Task ClientApi_ProviderIconParametersRejectInvalidValues(string query)
    {
        using var factory = ClientApiFactory(_ => Json(200, """{"Id":"listener"}"""));
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        using var response = await client.GetAsync("/allstarr/v1/providers/deezer/icon?" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(400, json.RootElement.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task ClientApi_ProviderIconVariantsResizeAndRevalidateWithoutRasterizingSvg()
    {
        using var fixtures = new ProviderIconFiles();
        using var factory = ClientApiFactory(_ => Json(200, """{"Id":"listener"}"""));
        using var client = CreateClientApiClient(factory);
        fixtures.Use(factory.Services);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        foreach (var (query, size) in new[] { ("?variant=glyph", 96), ("?variant=glyph&format=png&size=16", 16), ("?format=png&size=256", 256) })
        {
            var path = "/allstarr/v1/providers/deezer/icon" + query;
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
            using var bitmap = SKBitmap.Decode(await response.Content.ReadAsByteArrayAsync());
            Assert.Equal(size, bitmap.Width);
            Assert.Equal(size, bitmap.Height);
            Assert.Equal(0, bitmap.GetPixel(0, 0).Alpha);
            if (query.Contains("glyph", StringComparison.Ordinal))
                Assert.All(bitmap.Pixels, pixel => Assert.Equal(new SKColor(0, 0, 0, pixel.Alpha), pixel));
            using var conditional = new HttpRequestMessage(HttpMethod.Get, path);
            conditional.Headers.IfNoneMatch.Add(response.Headers.ETag!);
            using var unchanged = await client.SendAsync(conditional);
            Assert.Equal(HttpStatusCode.NotModified, unchanged.StatusCode);
            Assert.Empty(await unchanged.Content.ReadAsByteArrayAsync());
        }
        using var own = await client.GetAsync("/allstarr/v1/providers/allstarr/icon");
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        using var svg = await client.GetAsync("/allstarr/v1/providers/deezer/icon");
        Assert.Equal("image/svg+xml", svg.Content.Headers.ContentType?.MediaType);
        using var missing = await client.GetAsync("/allstarr/v1/providers/spotify/icon?format=png");
        await AssertClientApiError(missing, 404, "not_found");
        using var glyphMissing = await client.GetAsync("/allstarr/v1/providers/spotify/icon?variant=glyph");
        await AssertClientApiError(glyphMissing, 404, "not_found");
    }

    [Fact]
    public async Task ClientApi_RegisteredProvidersHaveKnownBrandSlugs()
    {
        using var factory = ClientApiFactory(_ => throw new InvalidOperationException("Unexpected upstream request"));
        var brands = "applemusic spotify deezer youtubemusic tidal soundcloud amazonmusic qobuz jiosaavn melon genie bugs lastfm listenbrainz musicbrainz genius musixmatch jellyfin lrclib lrcred lyricsplus unison paxsenix spicylyrics monochrome artistgrid koito multiscrobbler allstarr".Split(' ');
        foreach (var provider in factory.Services.GetRequiredService<allstarr.Core.Capabilities.IProviderRegistry>().Providers)
            Assert.Contains(allstarr.Services.Common.ProviderArtworkResolver.Brand(provider.Id), brands);
    }

    private sealed class ProviderIconFiles : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "allstarr-client-icon-fixtures", Guid.NewGuid().ToString("N"));
        private readonly PhysicalFileProvider _files;

        internal ProviderIconFiles()
        {
            foreach (var brand in new[] { "deezer", "spotify", "jellyfin", "allstarr" })
            {
                var path = Path.Combine(_root, "images/providers/brands", brand);
                Directory.CreateDirectory(path);
                File.WriteAllText(Path.Combine(path, "color.svg"), """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16"><path fill="#000" d="M4 4h8v8H4z"/></svg>""");
                if (brand == "spotify") continue;
                using var bitmap = new SKBitmap(512, 512);
                bitmap.Erase(SKColors.Transparent);
                using (var canvas = new SKCanvas(bitmap))
                using (var paint = new SKPaint { Color = SKColors.Black }) canvas.DrawRect(128, 128, 256, 256, paint);
                using var image = SKImage.FromBitmap(bitmap);
                using var png = image.Encode(SKEncodedImageFormat.Png, 100);
                File.WriteAllBytes(Path.Combine(path, "color.png"), png.ToArray());
                File.WriteAllBytes(Path.Combine(path, "glyph.png"), png.ToArray());
            }
            _files = new(_root);
        }

        internal void Use(IServiceProvider services, bool glyph = true)
        {
            if (!glyph)
                foreach (var brand in new[] { "deezer", "jellyfin" })
                    File.Delete(Path.Combine(_root, "images/providers/brands", brand, "glyph.png"));
            var environment = services.GetRequiredService<IWebHostEnvironment>();
            environment.WebRootFileProvider = _files;
            environment.ContentRootFileProvider = _files;
        }

        public void Dispose()
        {
            _files.Dispose();
            Directory.Delete(_root, true);
        }
    }
}
