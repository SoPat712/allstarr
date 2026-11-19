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
    public void ReleaseTagsAreWrittenUnderTheNamesTaggersUse()
    {
        var tag = new TagLib.Ogg.XiphComment();
        TagLibManagedTagFileMutator.ApplyTags(tag, new Dictionary<string, string>
        {
            ["musicbrainz_albumartistid"] = "6dee6b6f-aa72-424c-9c8c-0d31ba5ff673",
            ["musicbrainz_releasetrackid"] = "23c40af4-035d-47ee-9f79-9452c8228a99",
            ["artists"] = "Doja Cat;Tyga",
            ["artistsort"] = "Doja Cat & Tyga",
            ["albumartistsort"] = "Doja Cat",
            ["barcode"] = "886447753231",
            ["label"] = "Kemosabe",
            ["catalognumber"] = "G010004101434I",
            ["media"] = "Digital Media",
            ["releasecountry"] = "XW",
            ["releasestatus"] = "official",
            ["releasetype"] = "single",
            ["originalyear"] = "2019"
        });
        Assert.Equal(["Doja Cat", "Tyga"], tag.GetField("ARTISTS"));
        Assert.Equal("6dee6b6f-aa72-424c-9c8c-0d31ba5ff673", tag.GetFirstField("MUSICBRAINZ_ALBUMARTISTID"));
        Assert.Equal("23c40af4-035d-47ee-9f79-9452c8228a99", tag.GetFirstField("MUSICBRAINZ_RELEASETRACKID"));
        Assert.Equal("Doja Cat & Tyga", tag.GetFirstField("ARTISTSORT"));
        Assert.Equal("Doja Cat", tag.GetFirstField("ALBUMARTISTSORT"));
        foreach (var (field, value) in new[] { ("BARCODE", "886447753231"), ("LABEL", "Kemosabe"), ("CATALOGNUMBER", "G010004101434I"),
                     ("MEDIA", "Digital Media"), ("RELEASECOUNTRY", "XW"), ("RELEASESTATUS", "official"), ("RELEASETYPE", "single"), ("ORIGINALYEAR", "2019") })
            Assert.Equal(value, tag.GetFirstField(field));
    }

    [Fact]
    public void EmptyCover_RemovesCopiedEmbeddedArtwork()
    {
        var tag = new TagLib.Ogg.XiphComment { Pictures = [new TagLib.Picture(new TagLib.ByteVector(new byte[] { 1, 2, 3 }))] };
        TagLibManagedTagFileMutator.ApplyTags(tag, new Dictionary<string, string> { ["cover"] = "" });
        Assert.Empty(tag.Pictures);
    }
}
