using allstarr.Core.Enrichment;

namespace allstarr.Tests.Storage;

public sealed class PromotionCoverTests
{
    [Fact]
    public void PngCover_ProducesJpegSidecarBytes()
    {
        using var bitmap = new SkiaSharp.SKBitmap(2, 2);
        bitmap.Erase(SkiaSharp.SKColors.Red);
        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        var jpeg = PromotionCover.Jpeg(png.ToArray());
        Assert.NotNull(jpeg);
        Assert.True(jpeg.AsSpan().StartsWith(new byte[] { 255, 216, 255 }));
    }

    [Theory]
    // A service's 96-pixel mark, as apps show it for a song without art, is not a cover; real album art is.
    [InlineData(96, false)]
    [InlineData(299, false)]
    [InlineData(300, true)]
    [InlineData(1200, true)]
    public void OnlyAPictureLargeEnoughToBeAlbumArtIsACover(int side, bool cover)
    {
        using var bitmap = new SkiaSharp.SKBitmap(side, side);
        bitmap.Erase(SkiaSharp.SKColors.Red);
        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        Assert.Equal(cover, PromotionCover.IsCover(png.ToArray()));
        Assert.False(PromotionCover.IsCover(null));
        Assert.False(PromotionCover.IsCover([1, 2, 3, 4]));
    }

    [Fact]
    public void InvalidCover_IsRejected()
    {
        Assert.Null(PromotionCover.Jpeg([1, 2, 3, 4]));
    }

    [Fact]
    public void EmptyCover_RemovesCopiedEmbeddedArtwork()
    {
        var tag = new TagLib.Ogg.XiphComment { Pictures = [new TagLib.Picture(new TagLib.ByteVector(new byte[] { 1, 2, 3 }))] };
        TagLibManagedTagFileMutator.ApplyTags(tag, new Dictionary<string, string> { ["cover"] = "" });
        Assert.Empty(tag.Pictures);
    }
}
