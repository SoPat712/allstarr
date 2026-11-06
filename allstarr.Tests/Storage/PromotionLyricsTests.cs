using allstarr.Core.Enrichment;
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
}
