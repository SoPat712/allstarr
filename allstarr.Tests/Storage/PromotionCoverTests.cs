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
