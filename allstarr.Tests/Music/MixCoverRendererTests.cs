using allstarr.Core.Music;
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
        Assert.NotEqual(bitmap.GetPixel(20, 20), bitmap.GetPixel(300, 300));
    }

    [Theory]
    [InlineData("Neo SOUL", "R&B")]
    [InlineData("trap", "Hip-hop")]
    [InlineData("k-pop", "Pop")]
    [InlineData("alternative metal", "Indie rock")]
    [InlineData("deep house", "Electronic")]
    [InlineData("blues", "Jazz")]
    [InlineData("country", "Folk")]
    [InlineData("hardcore", "Metal")]
    [InlineData("reggaeton", "Latin")]
    [InlineData("ambient", "Classical")]
    public void Genre_matching_uses_ordered_substrings(string tag, string expected) =>
        Assert.Equal(expected, MixCoverRenderer.PaletteName(tag));

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
    public void Unmatched_and_empty_tags_use_the_seeded_palette()
    {
        Assert.Equal("Hip-hop", MixCoverRenderer.PaletteName(""));
        Assert.Equal(MixCoverRenderer.PaletteName("Unlisted"), MixCoverRenderer.PaletteName("unlisted"));
        Assert.Equal(2166136261u, MixCoverRenderer.Hash(""));
        Assert.Equal(3826002220u, MixCoverRenderer.Hash("a"));
        Assert.Equal(unchecked((2166136261u ^ 0x1f3b5u) * 16777619u), MixCoverRenderer.Hash("\U0001f3b5"));
        Assert.Equal(unchecked((2166136261u ^ 0xd800u) * 16777619u), MixCoverRenderer.Hash("\ud800"));
        var random = new MixCoverRenderer.Random(1);
        Assert.Equal(0.6270739405881613, random.Next());
        Assert.Equal(0.002735721180215478, random.Next());
    }
}
