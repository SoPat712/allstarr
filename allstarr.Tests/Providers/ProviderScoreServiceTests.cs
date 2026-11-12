using allstarr.Core.Providers;

namespace allstarr.Tests;

public sealed class ProviderScoreServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private static (string, string, DateTimeOffset, bool, long?, string?) Row(string provider, int daysAgo, bool passed, long? latency = 500, string capability = "metadata", string? reason = null) =>
        (provider, capability, Now.AddDays(-daysAgo), passed, latency, reason);

    [Fact]
    public void AReliableFastProviderScoresAtTheTop()
    {
        var score = Assert.Single(ProviderScoreService.ForTest(Enumerable.Range(0, 25).Select(day => Row("apple", day, true)), Now));
        Assert.Equal(100, score.Score);
        Assert.Equal(100d, score.UptimePercent);
    }

    [Fact]
    public void RecentFailuresCostMoreThanOldOnes()
    {
        var recent = Assert.Single(ProviderScoreService.ForTest(
            Enumerable.Range(0, 10).Select(day => Row("a", day, false)).Concat(Enumerable.Range(20, 10).Select(day => Row("a", day, true))), Now));
        var old = Assert.Single(ProviderScoreService.ForTest(
            Enumerable.Range(0, 10).Select(day => Row("a", day, true)).Concat(Enumerable.Range(20, 10).Select(day => Row("a", day, false))), Now));
        Assert.True(old.Score > recent.Score);
        Assert.Equal(old.UptimePercent, recent.UptimePercent);
    }

    [Fact]
    public void SlowProvidersScoreBelowFastOnesWithTheSameUptime()
    {
        var fast = Assert.Single(ProviderScoreService.ForTest(Enumerable.Range(0, 25).Select(day => Row("a", day, true, 400)), Now));
        var slow = Assert.Single(ProviderScoreService.ForTest(Enumerable.Range(0, 25).Select(day => Row("a", day, true, 9000)), Now));
        Assert.Equal(100, fast.Score);
        Assert.Equal(80, slow.Score);
    }

    [Fact]
    public void PartsExplainAReliableLosslessSourceThatStartsSlowly()
    {
        var rows = Enumerable.Range(0, 25).Select(day => Row("apple", day, true, 7146, "streaming"));
        var samples = Enumerable.Range(0, 5).Select(_ => new ProviderScoreService.AudioSample("apple", "alac", 24, 44100, null));
        var score = Assert.Single(ProviderScoreService.ForTest(rows, Now, samples));
        var parts = Assert.IsType<ProviderScoreParts>(score.Parts);
        Assert.Equal(82, score.Score);
        Assert.Equal((50, 50, 2, 20, 30, 30), (parts.Reliability, parts.ReliabilityOutOf, parts.Speed, parts.SpeedOutOf, parts.Quality, parts.QualityOutOf));
        Assert.Equal(score.Score, parts.Reliability + parts.Speed + parts.Quality);
        Assert.Equal(7146, parts.TypicalMilliseconds);
        Assert.Null(parts.CappedAt);
    }

    [Fact]
    public void PartsShowWhenALossySourceIsHeldAtItsCeiling()
    {
        var rows = Enumerable.Range(0, 25).Select(day => Row("deezer", day, true, 500, "streaming"));
        var samples = Enumerable.Range(0, 5).Select(_ => new ProviderScoreService.AudioSample("deezer", "mp3", null, null, 128_000));
        var score = Assert.Single(ProviderScoreService.ForTest(rows, Now, samples));
        Assert.Equal(60, score.Score);
        Assert.Equal(60, score.Parts!.CappedAt);
        Assert.True(score.Parts.Earned > 60);
    }

    [Fact]
    public void ReliableFastLowBitrateAudioIsLimitedOnlyByDeliveredQuality()
    {
        var rows = Enumerable.Range(0, 25).Select(day => Row("deezer", day, true, 2500, "streaming"));
        var samples = Enumerable.Range(0, 5).Select(_ => new ProviderScoreService.AudioSample("deezer", "mp3", null, 44100, 128_000));
        var score = Assert.Single(ProviderScoreService.ForTest(rows, Now, samples, plays: Plays("deezer", 84, 0, 120)));
        Assert.Equal("MP3 128 kbps", score.Quality!.Typical);
        Assert.Equal(100d, score.UptimePercent);
        Assert.Equal(60, score.Score);
        Assert.Equal(score.Parts!.ReliabilityOutOf, score.Parts.Reliability);
        Assert.Equal(score.Parts.SpeedOutOf, score.Parts.Speed);
        Assert.Equal(120, score.Parts.TypicalMilliseconds);
        Assert.True(score.Parts.TimedOnRealPlays);
        Assert.Equal(8, score.Parts.Quality);
        Assert.Equal(60, score.Parts.CappedAt);
        var dataSaver = Assert.Single(ProviderScoreService.ForTest(rows, Now, samples, target: "DataSaver", plays: Plays("deezer", 84, 0, 120)));
        Assert.Equal(100, dataSaver.Score);
        Assert.Null(dataSaver.Parts!.CappedAt);
    }

    [Fact]
    public void SourcesThatPlayNoAudioHaveNoQualityPartAndEachFeatureHasItsOwnParts()
    {
        var rows = Enumerable.Range(0, 25).Select(day => Row("lyrics", day, true, 4250, "lyrics"));
        var score = Assert.Single(ProviderScoreService.ForTest(rows, Now));
        Assert.Equal((80, 80, 10, 20, 0, 0), (score.Parts!.Reliability, score.Parts.ReliabilityOutOf, score.Parts.Speed, score.Parts.SpeedOutOf, score.Parts.Quality, score.Parts.QualityOutOf));
        Assert.Equal(score.Parts, Assert.Single(score.Capabilities).Parts);
    }

    [Fact]
    public void EachFeatureIsJudgedOnlyOnItsOwnChecks()
    {
        var rows = Enumerable.Range(0, 25).SelectMany(day => new[]
        {
            Row("apple", day, true, 184, "metadata"),
            Row("apple", day, true, 200, "playlist"),
            Row("apple", day, true, 7146, "streaming"),
            Row("apple", day, true, 7103, "download"),
        });
        var samples = Enumerable.Range(0, 5).Select(_ => new ProviderScoreService.AudioSample("apple", "alac", 24, 44100, null));
        var score = Assert.Single(ProviderScoreService.ForTest(rows, Now, samples));
        ProviderCapabilityScore Feature(string name) => score.Capabilities.Single(item => item.Capability == name);
        Assert.Equal(100, Feature("metadata").Score);
        Assert.Equal(100, Feature("playlist").Score);
        Assert.Equal((20, 0), (Feature("metadata").Parts!.Speed, Feature("metadata").Parts!.QualityOutOf));
        Assert.Equal(82, Feature("streaming").Score);
        Assert.Equal(30, Feature("streaming").Parts!.Quality);
        Assert.Equal(100, Feature("download").Score);
        Assert.Equal(score.Score, Feature("streaming").Score);
    }

    [Theory]
    [InlineData(300)]
    [InlineData(60_000)]
    public void HowLongADownloadTakesNeverChangesItsScore(long milliseconds)
    {
        var rows = Enumerable.Range(0, 25).Select(day => Row("source", day, true, milliseconds, "download"));
        var samples = Enumerable.Range(0, 5).Select(_ => new ProviderScoreService.AudioSample("source", "flac", 16, 44100, null));
        var download = Assert.Single(Assert.Single(ProviderScoreService.ForTest(rows, Now, samples)).Capabilities);
        var parts = download.Parts!;
        Assert.Equal(100, download.Score);
        Assert.Equal((60, 60, 0, 0, 40, 40), (parts.Reliability, parts.ReliabilityOutOf, parts.Speed, parts.SpeedOutOf, parts.Quality, parts.QualityOutOf));
        Assert.Equal(milliseconds, parts.TypicalMilliseconds);
    }

    [Fact]
    public void ABrokenPlaylistFeatureDoesNotLowerHowASourcePlays()
    {
        var rows = Enumerable.Range(0, 25).SelectMany(day => new[]
        {
            Row("source", day, false, 300, "playlist", "unreachable"),
            Row("source", day, true, 500, "streaming"),
        });
        var samples = Enumerable.Range(0, 5).Select(_ => new ProviderScoreService.AudioSample("source", "flac", 16, 44100, null));
        var score = Assert.Single(ProviderScoreService.ForTest(rows, Now, samples));
        Assert.Equal(100, score.Score);
        Assert.Equal(100, score.Capabilities.Single(item => item.Capability == "streaming").Score);
        Assert.Equal(20, score.Capabilities.Single(item => item.Capability == "playlist").Score);
    }

    [Fact]
    public void LossyAudioCeilingOnlyAppliesToPlayingAndDownloading()
    {
        var rows = Enumerable.Range(0, 25).SelectMany(day => new[]
        {
            Row("deezer", day, true, 177, "metadata"),
            Row("deezer", day, true, 267, "download"),
            Row("deezer", day, true, 779, "streaming"),
        });
        var samples = Enumerable.Range(0, 5).Select(_ => new ProviderScoreService.AudioSample("deezer", "mp3", null, null, 128_000));
        var score = Assert.Single(ProviderScoreService.ForTest(rows, Now, samples));
        Assert.Equal(100, score.Capabilities.Single(item => item.Capability == "metadata").Score);
        Assert.Equal(60, score.Capabilities.Single(item => item.Capability == "download").Score);
        Assert.Equal(60, score.Capabilities.Single(item => item.Capability == "streaming").Score);
    }

    private static IEnumerable<(string, string, DateTimeOffset, bool, long?, string?)> Streaming(string provider, long latency = 500) =>
        Enumerable.Range(0, 25).Select(day => Row(provider, day, true, latency, "streaming"));

    private static IEnumerable<ProviderScoreService.AudioSample> Delivers(string provider, string codec, int? depth = null, int? bitrate = null) =>
        Enumerable.Range(0, 5).Select(_ => new ProviderScoreService.AudioSample(provider, codec, depth, 44100, bitrate));

    [Theory]
    [InlineData("DataSaver", 100, 100, 100)]
    [InlineData("High", 100, 100, 65)]
    [InlineData("CdLossless", 100, 100, 60)]
    [InlineData("HiResLossless", 100, 75, 40)]
    [InlineData("BestAvailable", 100, 75, 40)]
    public void ScoresFollowWhatEachListenerAskedFor(string target, int hiRes, int cd, int lowLossy)
    {
        int Score(string codec, int? depth, int? bitrate) => Assert.Single(ProviderScoreService.ForTest(Streaming("s"), Now, Delivers("s", codec, depth, bitrate), target)).Score!.Value;
        Assert.Equal(hiRes, Score("alac", 24, null));
        Assert.Equal(cd, Score("flac", 16, null));
        Assert.Equal(lowLossy, Score("mp3", null, 128_000));
    }

    private static IEnumerable<ProviderScoreService.SongCheck> Songs(string provider, string capability, int found, int missing) =>
        Enumerable.Range(0, found + missing).Select(index => new ProviderScoreService.SongCheck(provider, capability, $"song-{index}", index < found, Now));

    [Fact]
    public void ASourceMissingHalfTheSongsLosesThatShareOnlyWhereItMatters()
    {
        var rows = Streaming("s").Concat(Enumerable.Range(0, 25).Select(day => Row("s", day, true, 300, "metadata")));
        var score = Assert.Single(ProviderScoreService.ForTest(rows, Now, Delivers("s", "flac", 16), songs: Songs("s", "streaming", 10, 10)));
        var streaming = score.Capabilities.Single(item => item.Capability == "streaming");
        Assert.Equal(90, streaming.Score);
        Assert.Equal((10, 20, 10, 20), (streaming.Parts!.Coverage, streaming.Parts.CoverageOutOf, streaming.Parts.SongsFound, streaming.Parts.SongsTried));
        Assert.Equal(100, score.Capabilities.Single(item => item.Capability == "metadata").Score);
    }

    [Fact]
    public void TooFewSongsDoNotCountYetAndTheLatestAnswerForASongStands()
    {
        var few = Assert.Single(ProviderScoreService.ForTest(Streaming("s"), Now, Delivers("s", "flac", 16), songs: Songs("s", "streaming", 0, 19)));
        Assert.Equal(100, few.Score);
        Assert.Equal((0, 19), (few.Parts!.CoverageOutOf, few.Parts.SongsTried));
        var added = Songs("s", "streaming", 0, 20).Concat(Songs("s", "streaming", 20, 0).Select(song => song with { At = Now.AddHours(1) }));
        var later = Assert.Single(ProviderScoreService.ForTest(Streaming("s"), Now, Delivers("s", "flac", 16), songs: added));
        Assert.Equal(100, later.Score);
        Assert.Equal(20, later.Parts!.SongsFound);
    }

    [Fact]
    public void LyricsSourcesAreJudgedOnWhetherTheyHaveTheLyrics()
    {
        var rows = Enumerable.Range(0, 25).Select(day => Row("l", day, true, 200, "lyrics"));
        var score = Assert.Single(ProviderScoreService.ForTest(rows, Now, songs: Songs("l", "lyrics", 5, 15)));
        var lyrics = Assert.Single(score.Capabilities);
        Assert.Equal(81, lyrics.Score);
        Assert.Equal(6, lyrics.Parts!.Coverage);
    }

    private static IEnumerable<ProviderScoreService.RealPlay> Plays(string provider, int completed, int failed, long start = 3000, bool cached = false) =>
        Enumerable.Range(0, completed).Select(_ => new ProviderScoreService.RealPlay(provider, true, cached, start))
            .Concat(Enumerable.Range(0, failed).Select(_ => new ProviderScoreService.RealPlay(provider, false, cached, start)));

    [Fact]
    public void RealPlayFailuresCountForHalfOnceThereAreEnough()
    {
        var few = Assert.Single(ProviderScoreService.ForTest(Streaming("s"), Now, Delivers("s", "flac", 16), plays: Plays("s", 10, 19, 500)));
        Assert.Equal(100, few.Score);
        Assert.Equal(0, few.Parts!.RealPlays);
        var many = Assert.Single(ProviderScoreService.ForTest(Streaming("s"), Now, Delivers("s", "flac", 16), plays: Plays("s", 20, 20, 500)));
        Assert.Equal(88, many.Score);
        Assert.Equal((40, 20), (many.Parts!.RealPlays, many.Parts.RealPlayFailures));
    }

    [Fact]
    public void RealSongStartsReplaceTestTimesButCachedPlaysNeverDo()
    {
        var real = Assert.Single(ProviderScoreService.ForTest(Streaming("s", 800), Now, Delivers("s", "mp3", null, 320_000), plays: Plays("s", 10, 0, 3100)));
        Assert.Equal(3100, real.Parts!.TypicalMilliseconds);
        Assert.True(real.Parts.TimedOnRealPlays);
        var cached = Assert.Single(ProviderScoreService.ForTest(Streaming("s", 800), Now, Delivers("s", "mp3", null, 320_000), plays: Plays("s", 40, 0, 50, cached: true)));
        Assert.Equal(800, cached.Parts!.TypicalMilliseconds);
        Assert.False(cached.Parts.TimedOnRealPlays);
    }

    [Fact]
    public void TooFewChecksGiveNoScoreInsteadOfAGuess()
    {
        var score = Assert.Single(ProviderScoreService.ForTest(Enumerable.Range(0, ProviderScoreService.MinimumChecks - 1).Select(day => Row("a", day, true)), Now));
        Assert.Null(score.Score);
        Assert.Equal(ProviderScoreService.MinimumChecks - 1, score.Checks);
    }

    [Fact]
    public void CapabilitiesAreScoredSeparatelyAndKeepTheLastFailure()
    {
        var rows = Enumerable.Range(0, 25).Select(day => Row("a", day, true, capability: "metadata"))
            .Concat(Enumerable.Range(0, 25).Select(day => Row("a", day, false, capability: "download", reason: day == 0 ? "timeout" : "unreachable")));
        var score = Assert.Single(ProviderScoreService.ForTest(rows, Now));
        Assert.Equal(100, score.Capabilities.Single(item => item.Capability == "metadata").Score);
        var download = score.Capabilities.Single(item => item.Capability == "download");
        Assert.Equal(25, download.Failures);
        Assert.Equal("timeout", download.LastFailureCode);
    }

    private static IEnumerable<(string, string, DateTimeOffset, bool, long?, string?)> Reliable(string provider) =>
        Enumerable.Range(0, 25).Select(day => Row(provider, day, true));

    private static ProviderScoreService.AudioSample Audio(string provider, string codec, int? depth = null, int? rate = null, int? bitrate = null) =>
        new(provider, codec, depth, rate, bitrate);

    [Fact]
    public void AReliable128KbpsSourceCannotScoreAboveItsCeiling()
    {
        var score = Assert.Single(ProviderScoreService.ForTest(Reliable("deezer"), Now,
            Enumerable.Repeat(Audio("deezer", "mp3", bitrate: 128_000), 5).Concat(Enumerable.Repeat(Audio("deezer", "mp3", rate: 44100), 5))));
        Assert.Equal(60, score.Score);
        Assert.Equal("MP3 128 kbps", score.Quality!.Typical);
        Assert.False(score.Quality.Lossless);
    }

    [Fact]
    public void LosslessSourcesKeepTheirScoreAndNameTheirFormat()
    {
        var score = Assert.Single(ProviderScoreService.ForTest(Reliable("apple"), Now,
            Enumerable.Repeat(Audio("apple", "flac", 16, 44100), 6).Append(Audio("apple", "flac", 24, 96000))));
        Assert.Equal(100, score.Score);
        Assert.Equal("Lossless 16-bit/44.1 kHz", score.Quality!.Typical);
        Assert.Equal("Lossless 24-bit/96 kHz", score.Quality.Best);
    }

    [Fact]
    public void PlaceholderBitratesDoNotCountAsRealQuality()
    {
        var score = Assert.Single(ProviderScoreService.ForTest(Reliable("yt"), Now, Enumerable.Repeat(Audio("yt", "aac", bitrate: 1337), 6)));
        Assert.Equal("AAC", score.Quality!.Typical);
        Assert.Equal(75, score.Score);
    }

    [Fact]
    public void MetadataOnlySourcesAreNotPenalizedForHavingNoAudio()
    {
        var score = Assert.Single(ProviderScoreService.ForTest(Reliable("genie"), Now));
        Assert.Null(score.Quality);
        Assert.Equal(100, score.Score);
    }

    [Fact]
    public void AudioSourcesAreTimedOnHowFastSongsStart()
    {
        var rows = Enumerable.Range(0, 25).Select(day => Row("apple", day, true, 100))
            .Concat(Enumerable.Range(0, 25).Select(day => Row("apple", day, true, 8000, capability: "streaming")));
        var score = Assert.Single(ProviderScoreService.ForTest(rows, Now, Enumerable.Repeat(Audio("apple", "flac", 16, 44100), 6)));
        Assert.Equal(80, score.Score);
    }
}
