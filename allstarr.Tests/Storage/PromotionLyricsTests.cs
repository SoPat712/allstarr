using allstarr.Core.Capabilities;
using allstarr.Core.Enrichment;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Models.Lyrics;

namespace allstarr.Tests.Storage;

public sealed class PromotionLyricsTests
{
    [Fact]
    public void Lyrics_ReassessSharedScoresAgainstMeasuredFileDuration()
    {
        var song = new Song { Title = "Song", Duration = 200 };
        var lyrics = new LyricsInfo { SyncedLyrics = "[00:01.00]First\n[01:20.00]Last" };
        Assert.Same(lyrics.SyncedLyrics, LibraryPromotionService.SelectPromotionLyrics([new("source", lyrics)], song, 100000));
        Assert.Null(LibraryPromotionService.SelectPromotionLyrics([new("source", lyrics)], song, 200000));
    }

    [Fact]
    public void Lyrics_RejectPlainOrUnverifiedTiming()
    {
        var song = new Song { Title = "Song" };
        Assert.Null(LibraryPromotionService.SelectPromotionLyrics([new("source", new LyricsInfo { PlainLyrics = "Words" })], song, 100000));
        Assert.Null(LibraryPromotionService.SelectPromotionLyrics([new("source", new LyricsInfo { SyncedLyrics = "[03:00]Wrong file" })], song, 100000));
    }

    [Fact]
    public void Lyrics_KeepOnlyTheHighestScoringFittingVersion()
    {
        var song = new Song { Title = "Song" };
        var candidates = new[]
        {
            new ProtocolLyricsCandidate("plain", new LyricsInfo { PlainLyrics = "Words" }),
            new ProtocolLyricsCandidate("synced", new LyricsInfo { SyncedLyrics = "[00:01]First\n[01:20]Last" })
        };
        Assert.Equal(candidates[1].Lyrics!.SyncedLyrics, LibraryPromotionService.SelectPromotionLyrics(candidates, song, 100000));
    }

    [Fact]
    public void Lyrics_RenderStructuredTimingAsLrc()
    {
        var lyrics = new LyricsInfo
        {
            SyncedLyrics = "structured timing",
            TimedLines = [new("First", TimeSpan.FromSeconds(1)), new("Last", TimeSpan.FromSeconds(80))]
        };
        var result = LibraryPromotionService.SelectPromotionLyrics([new("source", lyrics)], new Song { Title = "Song" }, 100000);
        Assert.Equal("[00:01.000]First\n[01:20.000]Last", result);
    }

    [Fact]
    public void Lyrics_RenderStructuredWordsAsEnhancedLrc()
    {
        var lyrics = new LyricsInfo
        {
            SyncedLyrics = "[00:01.000]café 世界",
            TimedLines = [new("café 世界", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(79),
                [new("café ", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)),
                 new("世界", TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(78))])]
        };
        var result = LibraryPromotionService.SelectPromotionLyrics([new("source", lyrics)], new Song { Title = "Song" }, 100000);
        Assert.Equal("[00:01.000]<00:01.000>café <00:02.000>世界<01:20.000>", result);
    }

    [Fact]
    public void Lyrics_InTheFilesOwnTagCarryLineTimesOnly()
    {
        // Word times are dropped with the spaces between words kept, and each line's time is in hundredths of a second,
        // rounded properly: a tag library that rounds 12.997 itself writes it back as 12.10.
        Assert.Equal("[00:09.52]I'm a working bitch\n[00:13.00]Ain't got no time\n[01:20.00]café 世界",
            LyricsTiming.TagContent("[00:09.515]<00:09.515>I'm <00:10.615>a <00:10.655>work<00:11.007>ing <00:11.335>bitch<00:12.129>\n" +
                "[00:12.997]<00:12.997>Ain't <00:13.455>got <00:13.741>no <00:13.997>time<00:14.329>\n[01:19.996]<01:19.996>café <01:20.500>世界<01:21.000>"));
        // Line-timed and untimed lyrics keep their words as they are.
        Assert.Equal("[00:00.10]First\n[00:00.80]Last", LyricsTiming.TagContent("[00:00.100]First\n[00:00.80]Last"));
        Assert.Equal("Words\nMore words", LyricsTiming.TagContent("Words\nMore words"));
    }

    [Theory]
    // Latin words run together are unreadable: the version ranks below one that reads properly.
    [InlineData("I'maworkingbitch", "Ain'tgotnotimefordick", "Youwishyoucouldsway", true)]
    [InlineData("I'm a working bitch", "Ain't got no time for dick", "You wish you could sway", false)]
    // A script written without spaces is as it should be.
    [InlineData("世界世界世界世界世界", "世界世界世界世界世界", "世界世界世界世界世界", false)]
    public void Lyrics_WithWordsRunTogetherRankBelowAVersionThatReads(string first, string second, string third, bool runTogether)
    {
        ProviderLyricsTimedLine Line(string text, int second) => new(text, TimeSpan.FromSeconds(second), TimeSpan.FromSeconds(4),
            Enumerable.Range(0, 4).Select(index => new ProviderLyricsTimedSegment(text.Substring(index * (text.Length / 4), index == 3 ? text.Length - 3 * (text.Length / 4) : text.Length / 4),
                TimeSpan.FromSeconds(second + index), TimeSpan.FromSeconds(1))).ToArray());
        var song = new Song { Title = "Song", Duration = 100 };
        var words = new LyricsInfo { SyncedLyrics = "structured timing", TimedLines = [Line(first, 10), Line(second, 40), Line(third, 80)] };
        var clean = new LyricsInfo { SyncedLyrics = "[00:10.00]I'm a working bitch\n[00:40.00]Ain't got no time\n[01:20.00]You wish" };

        var score = LyricsQuality.Evaluate(words, song, 100000)!;

        Assert.Equal(runTogether, score.Reasons.Contains("words_run_together"));
        Assert.Equal(runTogether, LyricsQuality.Evaluate(clean, song, 100000)!.Score > score.Score);
    }

    [Fact]
    public void Lyrics_RenderBackgroundPartsAsSeparateEnhancedLrcLines()
    {
        var lyrics = new LyricsInfo
        {
            SyncedLyrics = "[00:01.000]café oh yes 世界",
            TimedLines = [new("café oh yes 世界", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(79), vocalParts:
                [new("oh yes", TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(1),
                    [new("oh ", TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(0.5)),
                     new("yes", TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(0.5))], isBackground: true),
                 new("café 世界", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(79),
                    [new("café ", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)),
                     new("世界", TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(78))])])]
        };
        var result = LibraryPromotionService.SelectPromotionLyrics([new("source", lyrics)], new Song { Title = "Song" }, 100000);
        Assert.Equal("[00:01.000]<00:01.000>café <00:02.000>世界<01:20.000>\n[00:01.500]<00:01.500>oh <00:02.000>yes<00:02.500>", result);
    }
}
