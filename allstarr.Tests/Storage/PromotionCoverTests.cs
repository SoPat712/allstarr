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
            ["releasetype"] = "album;compilation",
            ["originalyear"] = "2019",
            ["script"] = "Latn",
            ["track"] = "4",
            ["totaltracks"] = "12",
            ["disc"] = "1",
            ["totaldiscs"] = "2"
        });
        Assert.Equal(["Doja Cat", "Tyga"], tag.GetField("ARTISTS"));
        Assert.Equal("6dee6b6f-aa72-424c-9c8c-0d31ba5ff673", tag.GetFirstField("MUSICBRAINZ_ALBUMARTISTID"));
        Assert.Equal("23c40af4-035d-47ee-9f79-9452c8228a99", tag.GetFirstField("MUSICBRAINZ_RELEASETRACKID"));
        Assert.Equal("Doja Cat & Tyga", tag.GetFirstField("ARTISTSORT"));
        Assert.Equal("Doja Cat", tag.GetFirstField("ALBUMARTISTSORT"));
        foreach (var (field, value) in new[] { ("BARCODE", "886447753231"), ("LABEL", "Kemosabe"), ("CATALOGNUMBER", "G010004101434I"),
                     ("MEDIA", "Digital Media"), ("RELEASECOUNTRY", "XW"), ("RELEASESTATUS", "official"), ("ORIGINALYEAR", "2019"), ("SCRIPT", "Latn"),
                     // The track number is written unpadded and each total under both of its names.
                     ("TRACKNUMBER", "4"), ("TRACKTOTAL", "12"), ("TOTALTRACKS", "12"), ("DISCNUMBER", "1"), ("DISCTOTAL", "2"), ("TOTALDISCS", "2") })
            Assert.Equal(value, tag.GetFirstField(field));
        Assert.Equal(["album", "compilation"], tag.GetField("RELEASETYPE"));
        Assert.Equal(4u, tag.Track);
        Assert.Equal(12u, tag.TrackCount);
    }

    [Theory]
    // A service's full-size cover is brought down to the chosen side for the copy inside the file, its shape kept.
    [InlineData(4000, 4000, 1000, 1000, 1000)]
    [InlineData(3000, 1500, 1000, 1000, 500)]
    [InlineData(4000, 4000, 500, 500, 500)]
    // A cover already that small is embedded as it came, and so is every cover when the listener asks for full size.
    [InlineData(1000, 1000, 1000, 1000, 1000)]
    [InlineData(500, 500, 1000, 500, 500)]
    [InlineData(4000, 4000, 0, 4000, 4000)]
    public void TheCoverInsideAFileIsScaledDownToTheChosenSide(int width, int height, int side, int embeddedWidth, int embeddedHeight)
    {
        using var bitmap = new SkiaSharp.SKBitmap(width, height);
        bitmap.Erase(SkiaSharp.SKColors.Teal);
        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var jpeg = image.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 90);
        var cover = jpeg.ToArray();

        var embedded = PromotionCover.Embedded(cover, side);

        using var codec = SkiaSharp.SKCodec.Create(SkiaSharp.SKData.CreateCopy(embedded));
        Assert.Equal((embeddedWidth, embeddedHeight), (codec.Info.Width, codec.Info.Height));
        Assert.True(PromotionCover.IsCover(embedded));
        Assert.Equal(side == 0 || width <= side, ReferenceEquals(cover, embedded));
    }

    [MediaToolsTheory]
    // Every tag an add writes is read back before the file is placed, so each must survive each kind of file.
    [InlineData("flac", "flac")]
    [InlineData("m4a", "aac")]
    [InlineData("m4a", "alac")]
    [InlineData("wav", "pcm_s24le")]
    public async Task ReleaseTagsAreReadBackFromARealFile(string extension, string codec)
    {
        var directory = Path.Combine(Path.GetTempPath(), "allstarr-release-tags", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "song." + extension);
            await ExtensionDownloadDecryptionTests.RunAsync("ffmpeg",
                ["-v", "error", "-nostdin", "-f", "lavfi", "-i", "sine=frequency=440:duration=1", "-threads", "1", "-c:a", codec, "-y", path]);
            var tags = new Dictionary<string, string>
            {
                ["title"] = "Working Bitch",
                ["artist"] = "Ashnikko",
                ["album"] = "Hi, It\u2019s Me",
                ["albumArtist"] = "Ashnikko",
                ["track"] = "4",
                ["totaltracks"] = "5",
                ["disc"] = "1",
                ["totaldiscs"] = "1",
                ["year"] = "2019",
                ["date"] = "2019-07-12",
                ["originaldate"] = "2019-07-12",
                ["isrc"] = "GBAYE1900594",
                ["musicbrainz_recordingid"] = "6d7b21c8-922c-4aa4-aff5-36b1505107b8",
                ["musicbrainz_releaseid"] = "b266145e-3601-481e-89cf-f0cf0afc2c83",
                ["musicbrainz_releasegroupid"] = "dc0eec68-ee74-4c2f-bb4c-dd6e7dd39a15",
                ["musicbrainz_artistid"] = "6dee6b6f-aa72-424c-9c8c-0d31ba5ff673",
                ["musicbrainz_albumartistid"] = "6dee6b6f-aa72-424c-9c8c-0d31ba5ff673",
                ["musicbrainz_releasetrackid"] = "ba028898-252b-4c58-be7d-6dac896ae5a3",
                ["artists"] = "Ashnikko;Yung Baby Tate",
                ["artistsort"] = "Ashnikko",
                ["albumartistsort"] = "Ashnikko",
                ["originalyear"] = "2019",
                ["releasecountry"] = "XW",
                ["releasestatus"] = "official",
                ["releasetype"] = "ep",
                ["media"] = "Digital Media",
                ["script"] = "Latn",
                ["barcode"] = "190295416812",
                ["label"] = "Parlophone",
                ["catalognumber"] = "DP019"
            };
            Assert.All(TagLibManagedTagFileMutator.ReleaseTags, key => Assert.Contains(key, tags.Keys));
            // Lyrics go in twice: by line where everything reads them, and by word in a tag of their own.
            const string words = "[00:00.100]<00:00.100>I'm <00:00.300>a <00:00.400>work<00:00.500>ing <00:00.600>bitch<00:00.900>";
            tags["lyrics"] = "[00:00.10]I'm a working bitch";
            tags["wordlyrics"] = words;
            var mutator = new TagLibManagedTagFileMutator();

            mutator.Apply(path, tags, default);

            Assert.True(mutator.Matches(path, tags));
            Assert.Equal(words, TagLibManagedTagFileMutator.ReadWordLyrics(path));
            using (var lyrics = TagLib.File.Create(path)) Assert.Equal("[00:00.10]I'm a working bitch", lyrics.Tag.Lyrics);
            // An add without word times takes the tag away again.
            var without = new Dictionary<string, string> { ["wordlyrics"] = "" };
            mutator.Apply(path, without, default);
            Assert.True(mutator.Matches(path, without));
            Assert.Null(TagLibManagedTagFileMutator.ReadWordLyrics(path));
            using var file = TagLib.File.Create(path);
            Assert.Equal("6dee6b6f-aa72-424c-9c8c-0d31ba5ff673", file.Tag.MusicBrainzReleaseArtistId);
            Assert.Equal("XW", file.Tag.MusicBrainzReleaseCountry);
            if (extension != "flac") return;
            var xiph = (TagLib.Ogg.XiphComment)file.GetTag(TagLib.TagTypes.Xiph);
            Assert.Equal(["Ashnikko", "Yung Baby Tate"], xiph.GetField("ARTISTS"));
            Assert.Equal("4", xiph.GetFirstField("TRACKNUMBER"));
            Assert.Equal("5", xiph.GetFirstField("TOTALTRACKS"));
            Assert.Equal("ba028898-252b-4c58-be7d-6dac896ae5a3", xiph.GetFirstField("MUSICBRAINZ_RELEASETRACKID"));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void EmptyCover_RemovesCopiedEmbeddedArtwork()
    {
        var tag = new TagLib.Ogg.XiphComment { Pictures = [new TagLib.Picture(new TagLib.ByteVector(new byte[] { 1, 2, 3 }))] };
        TagLibManagedTagFileMutator.ApplyTags(tag, new Dictionary<string, string> { ["cover"] = "" });
        Assert.Empty(tag.Pictures);
    }
}
