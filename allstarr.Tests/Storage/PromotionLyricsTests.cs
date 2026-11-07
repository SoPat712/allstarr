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
