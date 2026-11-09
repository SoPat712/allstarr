using allstarr.Core.Music;
using allstarr.Models.Domain;
using SkiaSharp;

namespace allstarr.Tests;

public sealed class MixCoverRendererTests
{
    public static TheoryData<string, string, string> Covers => new()
    {
        { "daily-1", "R&B", "" }, { "daily-2", "Indie rock", "" }, { "daily-3", "Electronic", "" },
        { "discover-weekly", "", "" }, { "release-radar", "", "" }, { "on-repeat", "", "" },
        { "rewind", "", "" }, { "chill", "", "" }, { "focus", "", "" }, { "workout", "", "" },
        { "party", "", "" }, { "sleep", "", "" }, { "custom", "R&B", "" },
        { "custom", "Indie rock", "" }, { "custom", "Latin", "" },
        { "external", "", "lastfm" }, { "external", "", "listenbrainz" }
    };

    [Theory]
    [MemberData(nameof(Covers))]
    public void Every_kind_is_a_deterministic_opaque_square(string kind, string tag, string source)
    {
        var input = new MixCoverInput("fixture-" + kind, 1, kind, tag, "SZA, Khalid, Daniel Caesar",
            "Kill Bill", source, source == "lastfm" ? "loved" : "createdfor:weekly-jams", "Weekly Jams", 11);
        var first = MixCoverRenderer.Render(input);
        Assert.Equal(first, MixCoverRenderer.Render(input));
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, first[..8]);
        using var bitmap = SKBitmap.Decode(first);
        Assert.Equal(600, bitmap.Width);
        Assert.Equal(600, bitmap.Height);
        Assert.Equal(255, bitmap.GetPixel(0, 0).Alpha);
        Assert.True(new[] { (0, 0), (599, 0), (0, 599), (599, 599) }.Select(point => bitmap.GetPixel(point.Item1, point.Item2)).Distinct().Count() > 1);
    }

    [Theory]
    [InlineData("lastfm", "loved", "similar")]
    [InlineData("listenbrainz", "createdfor:weekly-jams", "createdfor:weekly-exploration")]
    public void Mixes_from_one_service_get_different_covers(string source, string first, string second)
    {
        MixCoverInput Mix(string id) => new("fixture-" + id, 1, "external", "", "", "", source, id, "Mix", 10);
        Assert.NotEqual(MixCoverRenderer.Render(Mix(first)), MixCoverRenderer.Render(Mix(second)));
    }

    [Theory]
    [MemberData(nameof(Covers))]
    public void Covers_depend_only_on_the_mix_not_its_wording(string kind, string tag, string source)
    {
        var input = new MixCoverInput("fixture-" + kind, 1, kind, tag, "Artists", "Seed", source, "similar", "Name", 11);
        Assert.Equal(MixCoverRenderer.Render(input), MixCoverRenderer.Render(input with { Artists = "Other", Seed = "Other", Name = "Other", Count = 30 }));
    }

    [Fact]
    public void Mixes_of_one_kind_get_their_own_colours()
    {
        var first = MixCoverRenderer.Bold("daily-mix-one");
        Assert.NotEqual(first.Stops, MixCoverRenderer.Bold("daily-mix-two").Stops);
        Assert.Equal(first.Stops, MixCoverRenderer.Bold("daily-mix-one").Stops);
        Assert.NotEqual(MixCoverRenderer.Render(new("daily-mix-one", 1, "daily-1")), MixCoverRenderer.Render(new("daily-mix-two", 1, "daily-1")));
        // Across many mixes: hues all round the wheel, pale and deep covers, and the two stops of each well apart.
        var covers = Enumerable.Range(0, 60).Select(index => MixCoverRenderer.Bold($"mix-{index}")).ToArray();
        var colours = covers.Select(cover => cover.Stops.Select(stop => { SKColor.Parse(stop).ToHsv(out var h, out var s, out var v); return (h, s, v); }).ToArray()).ToArray();
        Assert.True(colours.Select(stops => (int)(stops[0].h / 60)).Distinct().Count() >= 5);
        Assert.Contains(colours, stops => stops[0].v >= 95 && stops[0].s <= 50);
        Assert.Contains(colours, stops => stops[1].v <= 40);
        Assert.All(colours, stops =>
        {
            var apart = Math.Abs(stops[0].h - stops[1].h) % 360;
            Assert.InRange(Math.Min(apart, 360 - apart), 35, 80);
        });
    }

    // The web UI draws the same gradient (webui/src/lib/covers/cover-for.test.ts checks these values too).
    [Theory]
    [InlineData("daily-mix-one", 289, "#9a91fa", "#3db1db")]
    [InlineData("mix-2", 86, "#9e2392", "#572a1a")]
    public void Gradients_match_the_web_ui(string id, int angle, string first, string second)
    {
        var (stops, actual) = MixCoverRenderer.Bold(id);
        Assert.Equal([first, second], stops);
        Assert.Equal(angle, actual);
    }

    [Theory]
    [InlineData(0, 100, 100, "#ff0000")]
    [InlineData(120, 100, 100, "#00ff00")]
    [InlineData(240, 100, 50, "#000080")]
    [InlineData(390, 0, 100, "#ffffff")]
    [InlineData(200, 78, 96, "#36b5f5")]
    public void Colours_match_the_web_ui(int hue, int saturation, int value, string expected) =>
        Assert.Equal(expected, MixCoverRenderer.Hsv(hue, saturation, value));

    [Fact]
    public void Collages_crop_each_picture_into_its_quarter()
    {
        byte[] Picture(SKColor colour, int width, int height)
        {
            using var bitmap = new SKBitmap(width, height);
            bitmap.Erase(colour);
            using var image = SKImage.FromBitmap(bitmap);
            return image.Encode(SKEncodedImageFormat.Png, 100).ToArray();
        }
        var colours = new[] { SKColors.Red, SKColors.Lime, SKColors.Blue, SKColors.Yellow };
        var collage = MixCoverRenderer.RenderCollage(colours.Select((colour, index) => Picture(colour, 100 + index * 50, 80)).ToArray());
        using var bitmap = SKBitmap.Decode(collage);
        Assert.Equal((600, 600), (bitmap.Width, bitmap.Height));
        foreach (var (colour, point) in colours.Zip(new[] { (150, 150), (450, 150), (150, 450), (450, 450) }))
        {
            var pixel = bitmap.GetPixel(point.Item1, point.Item2);
            Assert.InRange(Math.Abs(pixel.Red - colour.Red) + Math.Abs(pixel.Green - colour.Green) + Math.Abs(pixel.Blue - colour.Blue), 0, 30);
        }
        Assert.NotNull(MixCoverRenderer.RenderCollage([Picture(SKColors.Red, 10, 10)]));
        Assert.Null(MixCoverRenderer.RenderCollage([Picture(SKColors.Red, 10, 10), Picture(SKColors.Blue, 10, 10)]));
        Assert.Null(MixCoverRenderer.RenderCollage([[1, 2, 3]]));
    }

    [Fact]
    public void Cover_art_is_four_songs_from_different_albums_or_one()
    {
        Song Song(string id, string? album, bool local = true, string? cover = null) => new()
        {
            Id = id,
            Title = id,
            Artist = "Artist",
            Album = album ?? "",
            AlbumId = local ? album : null,
            IsLocal = local,
            ExternalProvider = local ? null : "deezer",
            CoverArtUrl = cover
        };
        var songs = new[]
        {
            Song("a", "album-1"), Song("b", "album-1"), Song("c", "album-2"),
            Song("d", null, local: false, cover: "https://cdn-images.dzcdn.net/images/cover/abc/500x500.jpg"),
            Song("e", null, local: false), Song("f", "album-3"), Song("g", "album-4")
        };
        Assert.Equal(["a", "c", "d", "f"], MixCoverArt.Pick(songs).Select(song => song.Id));
        Assert.Equal(["a"], MixCoverArt.Pick(songs.Take(3)).Select(song => song.Id));
        Assert.Equal("library:album-1", MixCoverArt.Reference(songs[0]));
        Assert.Null(MixCoverArt.Reference(songs[4]));
        Assert.Null(MixCoverArt.Reference(Song("h", null, local: false, cover: "http://127.0.0.1/cover.jpg")));
        var mix = new PersonalMix(Guid.NewGuid(), "daily-1", "Daily Mix 1", "Made for you", 2, null, new(true, "Ready"), true,
            songs.Select(song => new PersonalMixItem(Guid.Empty, 2, song, "taste", "", 1)).ToArray());
        Assert.Equal(["library:album-1", "library:album-2", "https://cdn-images.dzcdn.net/images/cover/abc/500x500.jpg", "library:album-3"],
            MixCoverInput.From(mix).Art!);
    }

    [Fact]
    public void Daily_cover_inputs_retain_the_cluster_and_top_artists()
    {
        var mix = new PersonalMix(Guid.NewGuid(), "daily-2", "Daily Mix 2 · Neo Soul · SZA, Khalid", "Made for you",
            4, null, new(true, "Ready"), true, []);
        var input = MixCoverInput.From(mix);
        Assert.Equal(mix.Id.ToString(), input.Id);
        Assert.Equal(4, input.Generation);
        Assert.Equal("Neo Soul", input.Tag);
        Assert.Equal("SZA, Khalid", input.Artists);
    }

    [Fact]
    public void Seeds_hash_whole_code_points()
    {
        Assert.Equal(2166136261u, MixCoverRenderer.Hash(""));
        Assert.Equal(3826002220u, MixCoverRenderer.Hash("a"));
        Assert.Equal(unchecked((2166136261u ^ 0x1f3b5u) * 16777619u), MixCoverRenderer.Hash("\U0001f3b5"));
        Assert.Equal(unchecked((2166136261u ^ 0xd800u) * 16777619u), MixCoverRenderer.Hash("\ud800"));
    }
}
