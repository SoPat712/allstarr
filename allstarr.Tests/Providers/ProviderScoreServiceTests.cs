using allstarr.Services.Common;

namespace allstarr.Tests;

public sealed class ProviderScoreServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private static (string, string, DateTimeOffset, bool, long?, string?) Row(string provider, int daysAgo, bool passed, long? latency = 500, string capability = "metadata", string? reason = null) =>
        (provider, capability, Now.AddDays(-daysAgo), passed, latency, reason);

    [Fact]
    public void AReliableFastProviderScoresAtTheTop()
    {
        var score = Assert.Single(ProviderScoreService.ForTest(Enumerable.Range(0, 10).Select(day => Row("apple", day, true)), Now));
        Assert.Equal(100, score.Score);
        Assert.Equal(100d, score.UptimePercent);
    }

    [Fact]
    public void RecentFailuresCostMoreThanOldOnes()
    {
        var recent = Assert.Single(ProviderScoreService.ForTest(
            Enumerable.Range(0, 5).Select(day => Row("a", day, false)).Concat(Enumerable.Range(20, 5).Select(day => Row("a", day, true))), Now));
        var old = Assert.Single(ProviderScoreService.ForTest(
            Enumerable.Range(0, 5).Select(day => Row("a", day, true)).Concat(Enumerable.Range(20, 5).Select(day => Row("a", day, false))), Now));
        Assert.True(old.Score > recent.Score);
        Assert.Equal(old.UptimePercent, recent.UptimePercent);
    }

    [Fact]
    public void SlowProvidersScoreBelowFastOnesWithTheSameUptime()
    {
        var fast = Assert.Single(ProviderScoreService.ForTest(Enumerable.Range(0, 6).Select(day => Row("a", day, true, 400)), Now));
        var slow = Assert.Single(ProviderScoreService.ForTest(Enumerable.Range(0, 6).Select(day => Row("a", day, true, 9000)), Now));
        Assert.Equal(100, fast.Score);
        Assert.Equal(80, slow.Score);
    }

    [Fact]
    public void PartsExplainAReliableLosslessSourceThatStartsSlowly()
    {
        var rows = Enumerable.Range(0, 10).Select(day => Row("apple", day, true, 7146, "streaming"));
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
        var rows = Enumerable.Range(0, 10).Select(day => Row("deezer", day, true, 500, "streaming"));
        var samples = Enumerable.Range(0, 5).Select(_ => new ProviderScoreService.AudioSample("deezer", "mp3", null, null, 128_000));
        var score = Assert.Single(ProviderScoreService.ForTest(rows, Now, samples));
        Assert.Equal(60, score.Score);
        Assert.Equal(60, score.Parts!.CappedAt);
        Assert.True(score.Parts.Earned > 60);
    }

    [Fact]
    public void SourcesThatPlayNoAudioHaveNoQualityPartAndEachFeatureHasItsOwnParts()
    {
        var rows = Enumerable.Range(0, 10).Select(day => Row("lyrics", day, true, 4250, "lyrics"));
        var score = Assert.Single(ProviderScoreService.ForTest(rows, Now));
        Assert.Equal((80, 80, 10, 20, 0, 0), (score.Parts!.Reliability, score.Parts.ReliabilityOutOf, score.Parts.Speed, score.Parts.SpeedOutOf, score.Parts.Quality, score.Parts.QualityOutOf));
        Assert.Equal(score.Parts, Assert.Single(score.Capabilities).Parts);
    }

    [Fact]
    public void EachFeatureIsJudgedOnlyOnItsOwnChecks()
    {
        var rows = Enumerable.Range(0, 10).SelectMany(day => new[]
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
        var rows = Enumerable.Range(0, 10).Select(day => Row("source", day, true, milliseconds, "download"));
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
        var rows = Enumerable.Range(0, 10).SelectMany(day => new[]
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
        var rows = Enumerable.Range(0, 10).SelectMany(day => new[]
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

    [Fact]
    public void TooFewChecksGiveNoScoreInsteadOfAGuess()
    {
        var score = Assert.Single(ProviderScoreService.ForTest(Enumerable.Range(0, ProviderScoreService.MinimumChecks - 1).Select(day => Row("a", day, true)), Now));
        Assert.Null(score.Score);
        Assert.Equal(4, score.Checks);
    }

    [Fact]
    public void CapabilitiesAreScoredSeparatelyAndKeepTheLastFailure()
    {
        var rows = Enumerable.Range(0, 6).Select(day => Row("a", day, true, capability: "metadata"))
            .Concat(Enumerable.Range(0, 6).Select(day => Row("a", day, false, capability: "download", reason: day == 0 ? "timeout" : "unreachable")));
        var score = Assert.Single(ProviderScoreService.ForTest(rows, Now));
        Assert.Equal(100, score.Capabilities.Single(item => item.Capability == "metadata").Score);
        var download = score.Capabilities.Single(item => item.Capability == "download");
        Assert.Equal(6, download.Failures);
        Assert.Equal("timeout", download.LastFailureCode);
    }

    private static IEnumerable<(string, string, DateTimeOffset, bool, long?, string?)> Reliable(string provider) =>
        Enumerable.Range(0, 10).Select(day => Row(provider, day, true));

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
        var rows = Enumerable.Range(0, 6).Select(day => Row("apple", day, true, 100))
            .Concat(Enumerable.Range(0, 6).Select(day => Row("apple", day, true, 8000, capability: "streaming")));
        var score = Assert.Single(ProviderScoreService.ForTest(rows, Now, Enumerable.Repeat(Audio("apple", "flac", 16, 44100), 6)));
        Assert.Equal(80, score.Score);
    }
}
