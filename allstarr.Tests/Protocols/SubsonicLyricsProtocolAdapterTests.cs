using allstarr.Core.Protocols.Subsonic;
using allstarr.Models.Domain;
using allstarr.Models.Lyrics;

namespace allstarr.Tests;

public sealed class SubsonicLyricsProtocolAdapterTests
{
    private static readonly Song Song = new()
    {
        Artist = "Fixture Artist",
        Title = "Fixture Title"
    };

    [Fact]
    public void StructuredMapper_ParsesRepeatedTimestampsFractionsAndStableOrder()
    {
        var lyrics = new LyricsInfo
        {
            SyncedLyrics = "[00:02.5]second\n[00:01.25][00:03.125]shared"
        };

        var result = SubsonicStructuredLyricsMapper.Map(Song, lyrics);

        Assert.NotNull(result);
        Assert.True(result.Synced);
        Assert.Collection(
            result.Lines,
            line => Assert.Equal((1_250L, "shared"), (line.StartMilliseconds, line.Text)),
            line => Assert.Equal((2_500L, "second"), (line.StartMilliseconds, line.Text)),
            line => Assert.Equal((3_125L, "shared"), (line.StartMilliseconds, line.Text)));
    }

    [Fact]
    public void StructuredMapper_UsesPlainLyricsWithoutInventingTimestamps()
    {
        var lyrics = new LyricsInfo { PlainLyrics = "first\r\nsecond" };

        var result = SubsonicStructuredLyricsMapper.Map(Song, lyrics);

        Assert.NotNull(result);
        Assert.False(result.Synced);
        Assert.All(result.Lines, line => Assert.Equal(0, line.StartMilliseconds));
        Assert.Equal(["first", "second"], result.Lines.Select(line => line.Text));
    }

    [Fact]
    public void StructuredMapper_FallsBackToPlainWhenSyncedDocumentHasNoTimedLines()
    {
        var lyrics = new LyricsInfo
        {
            SyncedLyrics = "[ar:Fixture Artist]",
            PlainLyrics = "plain fallback"
        };

        var result = SubsonicStructuredLyricsMapper.Map(Song, lyrics);

        Assert.NotNull(result);
        Assert.False(result.Synced);
        Assert.Equal("plain fallback", Assert.Single(result.Lines).Text);
    }
}
