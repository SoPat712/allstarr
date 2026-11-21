using allstarr.Core.Music;
using allstarr.Core.Providers.Lyrics;
using allstarr.Models.Lyrics;

namespace allstarr.Tests;

public sealed class LyricsCreditsTests
{
    private const string CheckQrc = """
        [510,520]Check(510,200) - (710,100)FLO(810,220)
        [1030,3390]Lyrics by：Ryan Ashley/Uzoechi Emenike/Curtis Robertson Jr.(1030,3390)
        [4420,3410]Composed by：Ryan Ashley/Uzoechi Emenike(4420,3410)
        [7830,3000]Produced by：Styalz Fuego/Rudy Sandapa(7830,3000)
        [11000,1200]Check (11000,400)me(11400,800)
        [13000,1000]Out(13000,1000)
        """;

    [Fact]
    public void QqTitleAndCreditsAreDroppedAndWordTimesKept()
    {
        var info = LyricsVersions.Map(ChineseWordLyricsParser.ParseQrc(CheckQrc, "qqlyrics").RequireValue(), "qqlyrics", 180);
        var rich = RichLyricsMapper.Map(info)!;
        Assert.Equal(new[] { "Check me", "Out" }, rich.Lines.Select(line => line.Text));
        Assert.Equal(11000L, rich.Lines[0].StartMs);
        Assert.Equal(new[] { "Check ", "me" }, rich.Lines[0].Words.Select(word => word.Text));
        Assert.Equal(new[] { 11000L, 11400L }, rich.Lines[0].Words.Select(word => word.StartMs));
        Assert.Equal(new long?[] { 11400, 12200 }, rich.Lines[0].Words.Select(word => word.EndMs));
        Assert.StartsWith("[00:11.000]<00:11.000>Check <00:11.400>me", LyricsTiming.SidecarContent(info));
        Assert.StartsWith("[00:11.000]", LyricsCleanup.Clean(info).SyncedLyrics);
    }

    [Fact]
    public void SyncedAndPlainTextLoseTheSameHeader()
    {
        const string synced = "[ti:Check]\n[00:00.51]Check - FLO\n[00:01.03]作词：Ryan Ashley\n[00:02.00]编曲 : Rudy Sandapa\n[00:11.00]Check me";
        Assert.Equal("[ti:Check]\n[00:11.00]Check me", LyricsTiming.SidecarContent(new LyricsInfo { SyncedLyrics = synced }));
        Assert.Equal("[00:11.000]Check me", LyricsCleanup.Clean(new LyricsInfo { SyncedLyrics = synced }).SyncedLyrics);
        Assert.Equal("Check me\nOut", LyricsCleanup.Clean(new LyricsInfo
        { PlainLyrics = "Check - FLO\nLyrics by：Ryan Ashley\nOP：Label\n\nCheck me\nOut" }).PlainLyrics);
    }

    [Fact]
    public void AMatchingTitleLineGoesWithoutCredits()
    {
        Assert.Equal("Check me", LyricsCleanup.Clean(new LyricsInfo { TrackName = "Check", PlainLyrics = "Check - FLO\nCheck me" }).PlainLyrics);
    }

    [Theory]
    [InlineData("Note to self: don't call\nI know you won't")]
    [InlineData("Left - right, we go\nSecond line")]
    [InlineData("First line\nProduced by: Someone\nLast line")]
    [InlineData("Lyrics by: Someone\nComposed by: Someone Else")]
    public void SungLinesAndCreditOnlyLyricsStay(string plain)
    {
        Assert.Equal(plain, LyricsCleanup.Clean(new LyricsInfo { TrackName = "Song", PlainLyrics = plain }).PlainLyrics);
        Assert.Same(plain, LyricsTiming.SidecarContent(new LyricsInfo { TrackName = "Song", PlainLyrics = plain }));
    }

    [Fact]
    public void TrailingCreditsAreDropped()
    {
        Assert.Equal("Line one\nLine two", LyricsCleanup.Clean(new LyricsInfo
        { PlainLyrics = "Line one\nLine two\n\n作曲：Someone\nMixed by: Someone Else\nMastered by：Another" }).PlainLyrics);
    }
}
