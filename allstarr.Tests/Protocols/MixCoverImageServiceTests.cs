using allstarr.Core.Music;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using SkiaSharp;

namespace allstarr.Tests;

public sealed class MixCoverImageServiceTests
{
    [Fact]
    public async Task ConcurrentRequestsRenderOnceAndCacheEvictsWithinBothLimits()
    {
        var renders = 0;
        var service = new MixCoverImageService(_ => { Interlocked.Increment(ref renders); return [1, 2, 3]; }, maxEntries: 2, maxBytes: 6);
        var input = new MixCoverInput("mix-1", 1, "daily");
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(async () =>
            Assert.IsType<FileContentResult>(await service.CreateResponseAsync(input, new DefaultHttpContext())))));
        Assert.Equal(1, renders);
        await service.CreateResponseAsync(input with { Id = "mix-2" }, new DefaultHttpContext());
        await service.CreateResponseAsync(input with { Id = "mix-3" }, new DefaultHttpContext());
        await service.CreateResponseAsync(input, new DefaultHttpContext());
        Assert.Equal(4, renders);
        var byteLimited = new MixCoverImageService(_ => { renders++; return [1, 2, 3]; }, maxEntries: 8, maxBytes: 3);
        await byteLimited.CreateResponseAsync(input, new DefaultHttpContext());
        await byteLimited.CreateResponseAsync(input with { Id = "mix-2" }, new DefaultHttpContext());
        await byteLimited.CreateResponseAsync(input, new DefaultHttpContext());
        Assert.Equal(7, renders);
    }

    [Fact]
    public async Task ConditionalRequestSkipsRenderingAndTagIncludesAllInputs()
    {
        var service = new MixCoverImageService(_ => throw new InvalidOperationException("Unexpected render"));
        var input = new MixCoverInput("mix-1", 1, "daily", Art: ["library:album-1"]);
        var tag = MixCoverImageService.GetTag(input);
        Assert.EndsWith("-collage-v4", tag);
        var previousVersion = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(input)))[..16].ToLowerInvariant();
        Assert.NotEqual(previousVersion, MixCoverImageService.Version(input));
        var context = new DefaultHttpContext();
        context.Request.Headers.IfNoneMatch = $"\"{tag}\"";
        Assert.Equal(304, Assert.IsType<StatusCodeResult>(await service.CreateResponseAsync(input, context)).StatusCode);
        var changes = new[]
        {
            input with { Id = "mix-2" }, input with { Generation = 2 }, input with { Kind = "weekly" },
            input with { Tag = "tag" }, input with { Artists = "artist" }, input with { Seed = "seed" },
            input with { Source = "source" }, input with { SourcePlaylistId = "playlist" },
            input with { Name = "name" }, input with { Count = 1 }, input with { Art = ["library:album-2"] }, input with { Art = null }
        };
        Assert.All(changes, changed => Assert.NotEqual(tag, MixCoverImageService.GetTag(changed)));
    }

    [Fact]
    public async Task SongCoversMakeACollageThatIsKeptOnlyWhenEveryPictureLoaded()
    {
        var failing = new HashSet<string> { "library:4" };
        var loads = 0;
        var service = new MixCoverImageService(_ => [9], (reference, _) =>
        {
            Interlocked.Increment(ref loads);
            return Task.FromResult(failing.Contains(reference) ? null : Picture(reference.Length * 40 % 255));
        });
        var input = new MixCoverInput("mix-1", 1, "daily-1", Art: ["library:1", "library:2", "library:3", "library:4"]);

        var partialContext = new DefaultHttpContext();
        var partial = Assert.IsType<FileContentResult>(await service.CreateResponseAsync(input, partialContext, MixCoverImageService.Version(input)));
        Assert.Equal("image/jpeg", partial.ContentType);
        Assert.Equal("private, max-age=60", partialContext.Response.Headers.CacheControl.ToString());
        Assert.False(partialContext.Response.Headers.ContainsKey("ETag"));

        failing.Clear();
        var context = new DefaultHttpContext();
        var full = Assert.IsType<FileContentResult>(await service.CreateResponseAsync(input, context, MixCoverImageService.Version(input)));
        Assert.Equal("image/jpeg", full.ContentType);
        Assert.Equal("private, max-age=31536000, immutable", context.Response.Headers.CacheControl.ToString());
        using var collage = SKBitmap.Decode(full.FileContents);
        Assert.Equal((600, 600), (collage.Width, collage.Height));
        Assert.Equal(8, loads);
        await service.CreateResponseAsync(input, new DefaultHttpContext());
        Assert.Equal(8, loads);
    }

    [Fact]
    public async Task NonemptyMixWithoutArtworkNeverKeepsOrRevalidatesAGradient()
    {
        var input = new MixCoverInput("mix-1", 1, "discover-weekly", Count: 17, Art: []);
        var service = new MixCoverImageService(_ => [9]);
        var context = new DefaultHttpContext();
        context.Request.Headers.IfNoneMatch = $"\"{MixCoverImageService.GetTag(input)}\"";
        var fallback = Assert.IsType<FileContentResult>(await service.CreateResponseAsync(input, context, MixCoverImageService.Version(input)));
        Assert.Equal("image/png", fallback.ContentType);
        Assert.Equal("private, max-age=60", context.Response.Headers.CacheControl);
        Assert.False(context.Response.Headers.ContainsKey("ETag"));
        Assert.NotEqual(MixCoverImageService.Version(input), MixCoverImageService.Version(input with { Art = ["service:ext-deezer-song-42"] }));

        var empty = input with { Count = 0 };
        var emptyContext = new DefaultHttpContext();
        Assert.IsType<FileContentResult>(await service.CreateResponseAsync(empty, emptyContext, MixCoverImageService.Version(empty)));
        Assert.Equal("private, max-age=31536000, immutable", emptyContext.Response.Headers.CacheControl);
    }

    private static byte[] Picture(int shade)
    {
        using var bitmap = new SKBitmap(40, 30);
        bitmap.Erase(new SKColor((byte)shade, 80, 160));
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }
}
