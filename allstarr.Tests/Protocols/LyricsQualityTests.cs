using allstarr.Core.Capabilities;
using allstarr.Core.Music;
using allstarr.Models.Domain;
using allstarr.Models.Lyrics;

namespace allstarr.Tests;

public sealed class LyricsQualityTests
{
    [Fact]
    public void MaskedWordsLoseInProportionToAffectedLines()
    {
        var song = new Song();
        var clean = LyricsQuality.Evaluate(new LyricsInfo { PlainLyrics = "fuck\nshit" }, song)!;
        var half = LyricsQuality.Evaluate(new LyricsInfo { PlainLyrics = "f**k\nshit" }, song)!;
        var all = LyricsQuality.Evaluate(new LyricsInfo { PlainLyrics = "f**k\ns***" }, song)!;
        Assert.Equal(0.1, clean.Score - half.Score, 6);
        Assert.Equal(0.2, clean.Score - all.Score, 6);
        Assert.Contains("censored_words", half.Reasons);
        Assert.DoesNotContain("censored_words", LyricsQuality.Evaluate(new LyricsInfo { PlainLyrics = "* Hello *" }, song)!.Reasons);
    }

    [Theory]
    [InlineData("Instrumental")]
    [InlineData("(Instrumental)")]
    [InlineData("♪♫…")]
    [InlineData("[Chorus]\n[Verse 2]")]
    public void InstrumentalBodiesAlwaysLoseToRealPlainLyrics(string text)
    {
        var song = new Song { Duration = 120 };
        var empty = LyricsQuality.Evaluate(new LyricsInfo { SyncedLyrics = "[01:50]<01:50>" + text.Replace("\n", "\n[01:51]<01:51>") }, song)!;
        var real = LyricsQuality.Evaluate(new LyricsInfo { PlainLyrics = "f**k" }, song)!;
        Assert.True(empty.Score < real.Score);
        Assert.Contains("instrumental", empty.Reasons);
    }

    [Fact]
    public void DroppedMarkersCannotSupplyWordTimingOrDurationCoverage()
    {
        var score = LyricsQuality.Evaluate(new LyricsInfo
        {
            SyncedLyrics = "[00:01]Real words\n[01:50]<01:50>[Verse <01:51>2]"
        }, new Song { Duration = 120 })!;
        Assert.Contains("synced_lines", score.Reasons);
        Assert.Contains("duration_mismatch", score.Reasons);
    }

    [Fact]
    public void InstrumentalFlagWithoutTextHasAnInstrumentalReason()
    {
        var score = LyricsQuality.Evaluate(new LyricsInfo { Instrumental = true }, new Song())!;
        Assert.Equal(0, score.Score);
        Assert.Contains("instrumental", score.Reasons);
        Assert.Contains("instrumental", LyricsQuality.Evaluate(new LyricsInfo { SyncedLyrics = "(Instrumental)" }, new Song())!.Reasons);
    }

    [Fact]
    public void EqualScoresPreferMoreNonEmptyLines()
    {
        var shortCopy = LyricsQuality.Evaluate(new LyricsInfo { PlainLyrics = "Hello" }, new Song())!;
        var longCopy = LyricsQuality.Evaluate(new LyricsInfo { PlainLyrics = "[Verse 2]\nHello\n\nWorld" }, new Song())!;
        Assert.Equal(shortCopy.Score, longCopy.Score);
        Assert.Equal(2, longCopy.LineCount);
        Assert.True(LyricsQuality.Better(longCopy, shortCopy));
        Assert.Contains("non_empty_lines", longCopy.Reasons);
    }

    [Theory]
    [InlineData(null, "Words", "plain_text", 0.3)]
    [InlineData("[01:50]Words", null, "synced_lines", 0.7)]
    [InlineData("[01:50]<01:50>Two <01:51>words", null, "synced_words", 1.0)]
    [InlineData("[01:50]<01:50>Two words", null, "synced_lines", 0.7)]
    [InlineData("[01:50]<01:50>Two <01:50>words", null, "synced_lines", 0.7)]
    public void ScoreUsesRealTimingGranularity(string? synced, string? plain, string reason, double expected)
    {
        var score = LyricsQuality.Evaluate(new LyricsInfo { SyncedLyrics = synced, PlainLyrics = plain, Duration = 120 },
            new Song { Duration = 120 });
        Assert.NotNull(score);
        Assert.Equal(expected, score.Score, 6);
        Assert.Contains(reason, score.Reasons);
    }

    [Theory]
    [InlineData("Word", "synced_words")]
    [InlineData("Whole line", "synced_lines")]
    public void ExplicitTimingForOneWordCountsButWholeLineTimingDoesNot(string text, string reason)
    {
        var lyrics = new LyricsInfo
        {
            SyncedLyrics = "[01:50]" + text,
            TimedLines = [new(text, TimeSpan.FromSeconds(110), TimeSpan.FromSeconds(1),
                [new(text, TimeSpan.FromSeconds(110), TimeSpan.FromSeconds(1))])]
        };
        Assert.Contains(reason, LyricsQuality.Evaluate(lyrics, new Song { Duration = 120 })!.Reasons);
    }

    [Theory]
    [InlineData(10, "duration_mismatch")]
    [InlineData(70, "duration_mismatch")]
    [InlineData(110, "duration_fits")]
    [InlineData(120, "duration_fits")]
    [InlineData(125, "duration_mismatch")]
    public void DurationUsesLastBodyTimestampWithoutTheInferredSongEnd(int lastSecond, string reason)
    {
        var lyrics = new LyricsInfo { SyncedLyrics = $"[{lastSecond / 60:00}:{lastSecond % 60:00}]Last words", Duration = 120 };
        var score = LyricsQuality.Evaluate(lyrics, new Song { Duration = 120 });
        Assert.Contains(reason, score!.Reasons);
        Assert.InRange(score.Score, 0, 1);
    }

    [Fact]
    public void RecordingDurationOverridesItemAndProviderDurations()
    {
        var lyrics = new LyricsInfo { SyncedLyrics = "[01:50]Last line", Duration = 400 };
        Assert.Contains("duration_fits", LyricsQuality.Evaluate(lyrics, new Song { Duration = 10 }, 120000)!.Reasons);
        Assert.Contains("duration_mismatch", LyricsQuality.Evaluate(lyrics, new Song { Duration = 10 })!.Reasons);
        Assert.DoesNotContain("duration_mismatch", LyricsQuality.Evaluate(lyrics, new Song())!.Reasons);
    }

    [Fact]
    public void ExplicitWordEndsAndVocalPartsCountWithoutUsingAnInferredEnd()
    {
        var lyrics = new LyricsInfo
        {
            Duration = 120,
            SyncedLyrics = "[00:01]Words",
            TimedLines = [new("Words", TimeSpan.FromSeconds(1), vocalParts:
            [new("Two words", TimeSpan.FromSeconds(1), segments:
                [new("Two ", TimeSpan.FromSeconds(1)), new("words", TimeSpan.FromSeconds(100), TimeSpan.FromSeconds(30))])])]
        };
        var score = LyricsQuality.Evaluate(lyrics, new Song { Duration = 120 });
        Assert.Contains("synced_words", score!.Reasons);
        Assert.Contains("duration_mismatch", score.Reasons);
    }

    [Fact]
    public void UnavailableBodiesHaveNoScoreAndEmptyBodiesAreInstrumental()
    {
        Assert.Null(LyricsQuality.Evaluate(null, new Song()));
        Assert.Null(LyricsQuality.Evaluate(new LyricsInfo(), new Song()));
        Assert.Contains("instrumental", LyricsQuality.Evaluate(new LyricsInfo { SyncedLyrics = "[00:01]" }, new Song())!.Reasons);
    }
}
