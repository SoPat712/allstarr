using allstarr.Core.Music;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using SkiaSharp;

namespace allstarr.Tests;

public sealed class MixCoverImageServiceTests
{
    [Fact]
    public async Task SongsFromOnlyTwoAlbumsDoNotRepeatTheirPicturesToFillTheGrid()
    {
        var mix = new PersonalMix(Guid.NewGuid(), "daily-1", "Daily Mix", "", 1, null, new(true, "Ready"), true,
            Enumerable.Range(0, 4).Select(index => new PersonalMixItem(Guid.Empty, 1,
                new() { Id = "song-" + index, Album = "Album " + index / 2, Artist = "Artist", IsLocal = true }, "library", "", 1)).ToArray());
        var service = new MixCoverImageService(MixCoverRenderer.Render, (reference, _) =>
        {
            return Task.FromResult<byte[]?>(Picture(reference is "library:song-0" or "library:song-1" ? 40 : 80));
        });
        var context = new DefaultHttpContext();
        var result = Assert.IsType<FileContentResult>(await service.CreateResponseAsync(MixCoverInput.From(mix), context));
        Assert.Equal(MixCoverRenderer.RenderCollage([Picture(40)]), result.FileContents);
        Assert.Equal("private, max-age=0, must-revalidate", context.Response.Headers.CacheControl);
    }

    [Fact]
    public async Task FailedAlbumPictureCanUseAnotherSongWithoutReloadingSuccessfulAlbums()
    {
        var mix = new PersonalMix(Guid.NewGuid(), "daily-1", "Daily Mix", "", 1, null, new(true, "Ready"), true,
            Enumerable.Range(1, 6).Select(index => new PersonalMixItem(Guid.Empty, 1,
                new() { Id = "song-" + index, Album = "Album " + (index == 5 ? 1 : index == 6 ? 2 : index), Artist = "Artist", IsLocal = true }, "library", "", 1)).ToArray());
        var service = new MixCoverImageService(MixCoverRenderer.Render, (reference, _) =>
        {
            Assert.NotEqual("library:song-5", reference);
            return Task.FromResult(reference == "library:song-2" ? null : Picture(int.Parse(reference[^1..]) * 40));
        });
        var context = new DefaultHttpContext();
        var result = Assert.IsType<FileContentResult>(await service.CreateResponseAsync(MixCoverInput.From(mix), context));
        Assert.Equal(MixCoverRenderer.RenderCollage(new[] { 1, 3, 4, 6 }.Select(index => Picture(index * 40)).ToArray()), result.FileContents);
        Assert.NotEmpty(context.Response.Headers.ETag.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedSecondPictureUsesTheFifthSongAndKeepsTheOriginalOrder(bool undecodable)
    {
        var loaded = new List<string>();
        var pictures = Enumerable.Range(1, 5).ToDictionary(index => "library:album-" + index, index => Picture(index * 40));
        var mix = new PersonalMix(Guid.NewGuid(), "daily-1", "Daily Mix", "", 1, null, new(true, "Ready"), true,
            Enumerable.Range(1, 5).Select(index => new PersonalMixItem(Guid.Empty, 1,
                new() { Id = "song-" + index, AlbumId = "album-" + index, IsLocal = true }, "library", "", 1)).ToArray());
        var input = MixCoverInput.From(mix);
        var service = new MixCoverImageService(MixCoverRenderer.Render, (reference, _) =>
        {
            loaded.Add(reference);
            return Task.FromResult(reference == "library:album-2" ? undecodable ? new byte[] { 1, 2, 3 } : null : pictures[reference]);
        });
        var context = new DefaultHttpContext();
        var result = Assert.IsType<FileContentResult>(await service.CreateResponseAsync(input, context));
        Assert.Equal(MixCoverRenderer.RenderCollage(new[] { 1, 3, 4, 5 }.Select(index => pictures["library:album-" + index]).ToArray()), result.FileContents);
        Assert.Equal(5, loaded.Count);
        Assert.NotEmpty(context.Response.Headers.ETag.ToString());
        var intact = new MixCoverImageService(MixCoverRenderer.Render, (reference, _) => Task.FromResult<byte[]?>(pictures[reference]));
        var original = Assert.IsType<FileContentResult>(await intact.CreateResponseAsync(input, new DefaultHttpContext()));
        Assert.Equal(MixCoverRenderer.RenderCollage(Enumerable.Range(1, 4).Select(index => pictures["library:album-" + index]).ToArray()), original.FileContents);
    }

    [Fact]
    public async Task OnlyTwoLoadableDistinctPicturesUseTheFirstWithShortCaching()
    {
        var first = Picture(40);
        var attempts = new List<string>();
        var service = new MixCoverImageService(MixCoverRenderer.Render, (reference, _) =>
        {
            attempts.Add(reference);
            return Task.FromResult(reference switch { "first" => first, "second" => Picture(80), _ => null });
        });
        var input = new MixCoverInput("mix", 1, "daily-1", Count: 5, Art: ["first", "missing", "second", "first"],
            FallbackArt: new Dictionary<string, string> { ["second"] = "second", ["also-missing"] = "also-missing" });
        var context = new DefaultHttpContext();
        var result = Assert.IsType<FileContentResult>(await service.CreateResponseAsync(input, context, MixCoverImageService.Version(input)));
        Assert.Equal(MixCoverRenderer.RenderCollage([first]), result.FileContents);
        Assert.Equal(new[] { "first", "missing", "second", "also-missing" }, attempts);
        Assert.Equal("private, max-age=60", context.Response.Headers.CacheControl);
        Assert.False(context.Response.Headers.ContainsKey("ETag"));
    }

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ServiceCoverWinsWithoutRenderingAndUsesImageValidators(bool synced)
    {
        var bytes = Picture(120);
        var owner = Guid.NewGuid();
        var input = MixCoverInput.From(new(Guid.NewGuid(), "external", "Discover Weekly", "", 1, null, new(true, "Ready"), synced,
            [new(Guid.NewGuid(), 1, new() { Id = "native-song", IsLocal = true }, "library", "", 1)])
        { Source = "spotify", SourcePlaylistId = "weekly", ArtworkUrl = "/api/admin/playlist-sources/source/playlists/weekly/artwork?revision=cover" });
        var service = new MixCoverImageService(_ => throw new InvalidOperationException("Unexpected render"),
            serviceArt: (selected, listener, _) =>
            {
                Assert.Equal(owner, listener);
                Assert.Equal(input, selected);
                return Task.FromResult<allstarr.Core.Artwork.ResolvedMediaAsset?>(new(bytes, "image/png", "hash", null, null, true));
            });
        var context = new DefaultHttpContext();
        var result = Assert.IsType<FileContentResult>(await service.CreateResponseAsync(input, context, owner: owner));
        Assert.Equal(bytes, result.FileContents);
        Assert.Equal("image/png", result.ContentType);
        Assert.Equal("private, max-age=300", context.Response.Headers.CacheControl);
        var conditional = new DefaultHttpContext();
        conditional.Request.Headers.IfNoneMatch = context.Response.Headers.ETag;
        Assert.Equal(304, Assert.IsType<StatusCodeResult>(await service.CreateResponseAsync(input, conditional, owner: owner)).StatusCode);
        var unavailable = new DefaultHttpContext();
        Assert.IsType<NotFoundResult>(await service.CreateResponseAsync(input, unavailable));
        Assert.Equal("private, no-store", unavailable.Response.Headers.CacheControl);
    }

    [Theory]
    [InlineData("lastfm")]
    [InlineData("listenbrainz")]
    public async Task ServiceWithoutCoverUsesSongsThenAnEmptyGradient(string provider)
    {
        var renders = 0;
        var service = new MixCoverImageService(_ => { renders++; return [9]; }, (_, _) => Task.FromResult<byte[]?>(Picture(80)));
        var mix = new PersonalMix(Guid.NewGuid(), "external", "Your songs", "", 1, null, new(true, "Ready"), true,
            Enumerable.Range(0, 4).Select(index => new PersonalMixItem(Guid.NewGuid(), 1,
                new() { Id = "song-" + index, AlbumId = "album-" + index, IsLocal = true }, provider, "", 1)).ToArray())
        { Source = provider };
        var input = MixCoverInput.From(mix);
        Assert.Null(input.ServiceArtwork);
        Assert.Equal(4, input.Art!.Count);
        var collage = Assert.IsType<FileContentResult>(await service.CreateResponseAsync(input, new DefaultHttpContext()));
        Assert.Equal("image/jpeg", collage.ContentType);
        Assert.Equal(0, renders);
        var empty = Assert.IsType<FileContentResult>(await service.CreateResponseAsync(MixCoverInput.From(mix with { Songs = [] }), new DefaultHttpContext()));
        Assert.Equal("image/png", empty.ContentType);
        Assert.Equal(1, renders);
    }

    private static byte[] Picture(int shade)
    {
        using var bitmap = new SKBitmap(40, 30);
        bitmap.Erase(new SKColor((byte)shade, 80, 160));
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }
}
