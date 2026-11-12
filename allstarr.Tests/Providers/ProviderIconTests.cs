using allstarr.Core.Artwork;
using allstarr.Core.Capabilities;
using allstarr.Core.Extensions;
using allstarr.Core.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Moq;
using SkiaSharp;
using System.Text.Json;

namespace allstarr.Tests;

public sealed class ProviderIconTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "allstarr-icon-fixtures", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("apple-musickit", "applemusic")]
    [InlineData("apple-download", "applemusic")]
    [InlineData("spotiflac-apple-music", "applemusic")]
    [InlineData("spotify", "spotify")]
    [InlineData("spotiflac-spotify-web", "spotify")]
    [InlineData("deezer", "deezer")]
    [InlineData("spotiflac-deezer", "deezer")]
    [InlineData("spotiflac-ytmusic-spotiflac", "youtubemusic")]
    [InlineData("youtube", "youtubemusic")]
    [InlineData("ytmusic", "youtubemusic")]
    [InlineData("spotiflac-tidal-web", "tidal")]
    [InlineData("tidal", "tidal")]
    [InlineData("spotiflac-soundcloud", "soundcloud")]
    [InlineData("spotiflac-amazon", "amazonmusic")]
    [InlineData("amazon", "amazonmusic")]
    [InlineData("spotiflac-qobuz-web", "qobuz")]
    [InlineData("qobuz", "qobuz")]
    [InlineData("spotiflac-jiosaavn", "jiosaavn")]
    [InlineData("spotiflac-melon-music", "melon")]
    [InlineData("spotiflac-genie-music", "genie")]
    [InlineData("spotiflac-bugs-music", "bugs")]
    [InlineData("multi-scrobbler", "multiscrobbler")]
    [InlineData("jellyfin", "jellyfin")]
    [InlineData("navidrome", "navidrome")]
    [InlineData("subsonic", "subsonic")]
    [InlineData("opensubsonic", "subsonic")]
    [InlineData("local", "jellyfin")]
    [InlineData("native", "jellyfin")]
    [InlineData("lastfm", "lastfm")]
    [InlineData("listenbrainz", "listenbrainz")]
    [InlineData("musicbrainz", "musicbrainz")]
    [InlineData("genius", "genius")]
    [InlineData("musixmatch", "musixmatch")]
    [InlineData("lrclib", "lrclib")]
    [InlineData("lrcred", "lrcred")]
    [InlineData("lyricsplus", "lyricsplus")]
    [InlineData("unison", "unison")]
    [InlineData("paxsenix", "paxsenix")]
    [InlineData("spicylyrics", "spicylyrics")]
    [InlineData("monochrome", "monochrome")]
    [InlineData("artistgrid", "artistgrid")]
    [InlineData("koito", "koito")]
    [InlineData("allstarr", "allstarr")]
    [InlineData("musicbrainz-enrichment", "musicbrainz")]
    [InlineData("spotiflac-new-service", "new-service")]
    public void ProviderIdsMapToTheirBrand(string id, string brand) =>
        Assert.Equal(brand, ProviderArtworkResolver.Brand(id));

    [Fact]
    public void BrandFilesWinThenPackageThenLegacyAndGlyphNeverUsesColour()
    {
        var package = new ExtensionPackageRecord
        {
            PackagePath = _root,
            ManifestJson = """{"id":"fixture","version":"1.0.0","sdkVersion":"1","entryPoint":"index.js","icon":"assets/icon.png","glyphIcon":"assets/glyph.svg","capabilities":[{"kind":"Metadata","hooks":["getTrack"],"accountScopes":[],"accountRequired":false}],"permissions":[]}"""
        };
        Write("assets/icon.png");
        Write("assets/glyph.svg");
        Write("images/providers/deezer.svg");
        Write("images/providers/brands/deezer/color.svg");
        Write("images/providers/brands/deezer/color.png");
        Write("images/providers/brands/deezer/glyph.png");
        using var files = new PhysicalFileProvider(_root);
        var environment = new Mock<IWebHostEnvironment>();
        environment.SetupGet(value => value.WebRootFileProvider).Returns(files);
        environment.SetupGet(value => value.ContentRootFileProvider).Returns(files);
        var descriptor = new ProviderDescriptor("spotiflac-deezer", "Fixture", "Fixture", ProviderOrigin.Extension,
            "1", "1", [new(ProviderCapabilityKind.Metadata, ProviderCapabilitySupportState.ConfiguredOnly,
                ProviderAccountRequirement.None, "1")], new ProviderPermissionDescriptor(), entryPoint: "index.js");
        var artwork = ProviderArtworkResolver.ResolveArtwork(environment.Object, descriptor, package);
        Assert.EndsWith("brands/deezer/color.svg", artwork.Color.Select(null)!.File.PhysicalPath);
        Assert.EndsWith("brands/deezer/color.png", artwork.Color.Select("png")!.File.PhysicalPath);
        Assert.EndsWith("brands/deezer/glyph.png", artwork.Glyph.Select(null)!.File.PhysicalPath);
        File.Delete(Path.Combine(_root, "images/providers/brands/deezer/color.svg"));
        artwork = ProviderArtworkResolver.ResolveArtwork(environment.Object, descriptor, package);
        Assert.EndsWith("brands/deezer/color.png", artwork.Color.Select(null)!.File.PhysicalPath);
        Directory.Delete(Path.Combine(_root, "images/providers/brands"), true);
        artwork = ProviderArtworkResolver.ResolveArtwork(environment.Object, descriptor, package);
        Assert.EndsWith("assets/icon.png", artwork.Color.Select(null)!.File.PhysicalPath);
        Assert.EndsWith("providers/deezer.svg", artwork.Color.Select("svg")!.File.PhysicalPath);
        Assert.EndsWith("assets/glyph.svg", artwork.Glyph.Select(null)!.File.PhysicalPath);
        Assert.Null(artwork.Glyph.Select("png"));
        artwork = ProviderArtworkResolver.ResolveArtwork(environment.Object, descriptor, null);
        Assert.EndsWith("providers/deezer.svg", artwork.Color.Select(null)!.File.PhysicalPath);
        Assert.Null(artwork.Color.Select("png"));
        Assert.Null(artwork.Glyph.Select(null));
        Assert.Null(artwork.Provider("deezer", "Deezer").GlyphUrl);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(96)]
    [InlineData(256)]
    public void PngRenderingPreservesTransparencyAndBlackGlyphs(int size)
    {
        using var bitmap = new SKBitmap(512, 512);
        bitmap.Erase(SKColors.Transparent);
        using (var canvas = new SKCanvas(bitmap))
        using (var paint = new SKPaint { Color = SKColors.Red })
            canvas.DrawRect(128, 128, 256, 256, paint);
        using var image = SKImage.FromBitmap(bitmap);
        using var source = image.Encode(SKEncodedImageFormat.Png, 100);
        var result = ProviderIconRenderer.Render(source.ToArray(), size, true);
        Assert.NotNull(result);
        using var output = SKBitmap.Decode(result);
        Assert.Equal(size, output.Width);
        Assert.Equal(size, output.Height);
        Assert.Equal(0, output.GetPixel(0, 0).Alpha);
        Assert.Contains(output.Pixels, pixel => pixel.Alpha > 0);
        Assert.All(output.Pixels, pixel => Assert.Equal(new SKColor(0, 0, 0, pixel.Alpha), pixel));
        using var color = SKBitmap.Decode(ProviderIconRenderer.Render(source.ToArray(), size, false));
        Assert.Equal(SKColors.Red, color.GetPixel(size / 2, size / 2));
    }

    [Theory]
    [InlineData("../glyph.png")]
    [InlineData("/glyph.png")]
    [InlineData("assets/../../glyph.png")]
    [InlineData("assets\\..\\glyph.svg")]
    [InlineData("glyph.jpg")]
    public void ManifestGlyphRejectsUnsafeOrUnsupportedPaths(string path)
    {
        var json = JsonSerializer.Serialize(new
        {
            id = "fixture",
            version = "1.0.0",
            sdkVersion = "1",
            entryPoint = "index.js",
            glyphIcon = path,
            capabilities = new[] { new { kind = "Metadata", hooks = new[] { "getTrack" } } },
            permissions = Array.Empty<object>()
        });
        Assert.Throws<ExtensionSdkValidationException>(() => ExtensionSdkV1.ParseManifest(json));
    }

    [Fact]
    public void CompatibleManifestsRetainTheirOptionalGlyph()
    {
        var normalized = SpotiFlacExtensionCompatibility.NormalizeManifest(
            """{"name":"fixture","type":["lyrics_provider"],"glyphIcon":"assets/glyph.svg"}""", "function getLyrics() {}");
        Assert.Equal("assets/glyph.svg", ExtensionSdkV1.ParseManifest(normalized).GlyphIcon);
    }

    private void Write(string path)
    {
        var full = Path.Combine(_root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "fixture");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
