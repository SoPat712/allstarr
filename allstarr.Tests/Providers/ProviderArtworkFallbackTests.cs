using allstarr.Models.Domain;
using allstarr.Services.Common;
using allstarr.Services.Jellyfin;
using SkiaSharp;

namespace allstarr.Tests;

public sealed class ProviderArtworkFallbackTests
{
    [Theory]
    [InlineData(600)]
    [InlineData(96)]
    public void PackageIconsKeepTheirBackgroundAndAspectRatio(int dimension)
    {
        using var icon = new SKBitmap(4, 2);
        icon.Erase(SKColors.Blue);
        icon.SetPixel(2, 1, SKColors.Red);
        using var image = SKImage.FromBitmap(icon);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        var result = ProviderArtworkFallback.Render(new(png.ToArray(), "image/png"), dimension);
        Assert.NotNull(result);
        Assert.Equal("image/png", result.ContentType);
        using var rendered = SKBitmap.Decode(result.Bytes);
        Assert.Equal(dimension, rendered.Width);
        Assert.Equal(dimension, rendered.Height);
        Assert.Equal(SKColors.Blue, rendered.GetPixel(0, 0));
    }

    [Fact]
    public void MonogramUsesTheExistingTileColorsAndAVisibleInitial()
    {
        var fontBytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "inter-cover.ttf"));
        using var rendered = SKBitmap.Decode(ProviderArtworkFallback.Monogram("No mark", 96, fontBytes).Bytes);
        Assert.Equal(SKColor.Parse("#dbe6ff"), rendered.GetPixel(0, 0));
        Assert.Contains(rendered.Pixels, pixel => pixel == SKColor.Parse("#001b3f"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FallbackTagsAreVersionedAndNativeTagsRemainUnchanged(bool local)
    {
        var builder = new JellyfinResponseBuilder();
        var song = builder.ConvertSongToJellyfinItem(new Song { Id = "song", ExternalProvider = "deezer", IsLocal = local });
        var album = builder.ConvertAlbumToJellyfinItem(new Album { Id = "album", ExternalProvider = "deezer", IsLocal = local });
        var artist = builder.ConvertArtistToJellyfinItem(new Artist { Id = "artist", ExternalProvider = "deezer", IsLocal = local });
        string Tag(Dictionary<string, object?> item) => Assert.IsType<Dictionary<string, string>>(item["ImageTags"])["Primary"];
        Assert.Equal(local ? "song" : "song-art-v3", Tag(song));
        Assert.Equal(local ? "album" : "album-art-v4", Tag(album));
        if (local) Assert.Equal("artist", Tag(artist));
        else Assert.Empty(Assert.IsType<Dictionary<string, string>>(artist["ImageTags"]));
        Assert.False(ProviderArtworkFallback.IsFallback("deezer", "https://images.example.test/cover.png", "song"));
    }
}
