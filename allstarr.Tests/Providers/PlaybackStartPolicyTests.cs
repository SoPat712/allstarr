using allstarr.Core.Capabilities;
using allstarr.Core.Music;
using allstarr.Core.Providers.Qobuz;

namespace allstarr.Tests;

public sealed class PlaybackStartPolicyTests
{
    private static readonly ProviderMediaFormat Cd = new("audio/flac", "flac", "flac", sampleRate: 44100, bitDepth: 16);
    private static readonly ProviderMediaFormat High = new("audio/flac", "flac", "flac", sampleRate: 96000, bitDepth: 24);
    private static readonly ProviderMediaFormat Mp3 = new("audio/mpeg", "mp3", "mp3", bitrate: 320000);

    [Theory]
    [InlineData("FLAC_24_HIGH", 192000)]
    [InlineData("FLAC_24_LOW", 96000)]
    public void ConfiguredLosslessTiersRetainTheirDeclaredSampleRate(string configured, int rate)
    {
        using var http = new HttpClient();
        var source = new QobuzStreamingCapabilityAdapter(http, null!, null!, configured);
        Assert.Equal(rate, source.PreferredFormat(ProviderAudioQuality.Any)!.SampleRate);
        Assert.Equal(48000, ProviderDeclaredAudio.Parse("alac-24-48")!.SampleRate);
    }

    [Theory]
    [InlineData("MP3_128", "mp3", 128000, null)]
    [InlineData("AAC 256 kbps", "aac", 256000, null)]
    [InlineData("FLAC lossless", "flac", null, 16)]
    [InlineData("alac-24-96", "alac", null, 24)]
    public void DeclaredFormatsSupplyUnprobedRankingFacts(string declaration, string codec, int? bitrate, int? depth)
    {
        var media = ProviderDeclaredAudio.Parse(declaration);
        Assert.Equal(codec, media!.Codec);
        Assert.Equal(bitrate, media.Bitrate);
        Assert.Equal(depth, media.BitDepth);
    }

    [Fact]
    public void DeclaredKindsDistinguishOpaqueLosslessOptionsAndExcludeSpatialAudio()
    {
        Assert.Equal("flac", ProviderDeclaredAudio.Parse("best", "lossless")!.Codec);
        Assert.Null(ProviderDeclaredAudio.Parse("lossless spatial", "spatial"));
        Assert.Equal("aac", ProviderDeclaredAudio.Parse("AAC 256; lossless unavailable", "lossy")!.Codec);
    }

    [Theory]
    [InlineData("Balanced", 0)]
    [InlineData("BestQuality", 0)]
    [InlineData("FastStart", 0)]
    public void ABetterFormatAtTheCachedSourceRemainsEligible(string mode, int expected) =>
        Assert.Equal(expected, PlaybackStartPolicy.Order(mode,
        [
            new(0, new("audio/mpeg", "mp3", "mp3", 128000), TimeSpan.FromSeconds(2), IsCached: true, AvailableMedia: High),
            new(1, Cd, TimeSpan.FromSeconds(5))
        ])[0]);

    [Theory]
    [InlineData("Balanced")]
    [InlineData("BestQuality")]
    [InlineData("FastStart")]
    public void LimitedLossySourceStaysBehindABetterDownloadSource(string mode) =>
        Assert.Equal(1, PlaybackStartPolicy.Order(mode,
        [
            new(0, new("audio/mpeg", "mp3", "mp3", 128000, 44100), TimeSpan.FromMilliseconds(1)),
            new(1, new("audio/mp4", "m4a", "aac", 256000, 44100), TimeSpan.FromSeconds(5))
        ])[0]);

    [Fact]
    public void LimitedLossySourceRemainsTheLastFallback() =>
        Assert.Equal([0, 2, 1], PlaybackStartPolicy.Order("Balanced",
        [
            new(0, Cd, TimeSpan.FromMilliseconds(10)),
            new(1, new("audio/mpeg", "mp3", "mp3", 128000), TimeSpan.FromMilliseconds(1)),
            new(2, Mp3, TimeSpan.FromSeconds(1))
        ]));

    [Theory]
    [InlineData("Balanced", false)]
    [InlineData("BestQuality", false)]
    [InlineData("FastStart", true)]
    public void LowerCachedQualityCannotWinWhileTheBetterSourceIsStillStarting(string mode, bool expected)
    {
        var low = new ProviderMediaFormat("audio/mpeg", "mp3", "mp3", 128000);
        Assert.Equal(expected, PlaybackStartPolicy.ShouldHedge(mode, Cd, new(1, low, null, IsCached: true)));
        Assert.False(PlaybackStartPolicy.ShouldHedge(mode, Cd, new(1, low, null)));
        Assert.True(PlaybackStartPolicy.ShouldHedge(mode, Cd, new(1, Cd, null, IsCached: true)));
    }

    [Theory]
    [InlineData("Balanced", 1)]
    [InlineData("BestQuality", 1)]
    [InlineData("FastStart", 0)]
    public void LowBitrateCacheDoesNotDisplaceABetterLossySource(string mode, int expected) =>
        Assert.Equal(expected, PlaybackStartPolicy.Order(mode,
        [
            new(0, new("audio/mpeg", "mp3", "mp3", 128000, 44100), null, IsCached: true),
            new(1, new("audio/mp4", "m4a", "aac", 256000, 44100), null)
        ])[0]);

    [Theory]
    [InlineData("Balanced")]
    [InlineData("BestQuality")]
    public void CachedLossySampleRateDoesNotOutrankBitrate(string mode) =>
        Assert.Equal(1, PlaybackStartPolicy.Order(mode,
        [
            new(0, new("audio/mpeg", "mp3", "mp3", 128000, 48000, 16), null, IsCached: true),
            new(1, new("audio/mp4", "m4a", "aac", 256000, 44100), null)
        ])[0]);

    [Theory]
    [InlineData("FastStart")]
    [InlineData("Balanced")]
    [InlineData("BestQuality")]
    public void EqualLossyCacheStartsWithoutWaiting(string mode) =>
        Assert.Equal(0, PlaybackStartPolicy.Order(mode,
        [new(0, Mp3, null, IsCached: true), new(1, Mp3, TimeSpan.FromMilliseconds(10))])[0]);

    [Theory]
    [InlineData("FastStart", 7, 2)]
    [InlineData("Balanced", 7, 2)]
    [InlineData("Balanced", 3, 1)]
    [InlineData("BestQuality", 7, 1)]
    public void ChoosesQualityOnlyWithinTheSelectedStartBudget(string mode, int highSeconds, int expected)
    {
        var ordered = PlaybackStartPolicy.Order(mode,
        [
            new(0, Mp3, TimeSpan.FromMilliseconds(100)),
            new(1, High, TimeSpan.FromSeconds(highSeconds)),
            new(2, Cd, TimeSpan.FromMilliseconds(400))
        ]);
        Assert.Equal(expected, ordered[0]);
        Assert.Equal(3, ordered.Distinct().Count());
    }

    [Theory]
    [InlineData("FastStart")]
    [InlineData("Balanced")]
    [InlineData("BestQuality")]
    public void ReplayUsesAnAlreadyCachedHigherQualityRecording(string mode)
    {
        Assert.Equal(1, PlaybackStartPolicy.Order(mode,
        [
            new(0, Cd, TimeSpan.FromMilliseconds(400)),
            new(1, High, TimeSpan.FromSeconds(12), IsCached: true)
        ])[0]);
    }

    [Fact]
    public void MissingMeasurementsPreserveSourcePriorityWithoutInventingLosslessFacts()
    {
        Assert.Equal([0, 1], PlaybackStartPolicy.Order("Balanced", [new(0, null, null), new(1, null, null)]));
        Assert.Equal([1, 0], PlaybackStartPolicy.Order("FastStart",
            [new(0, Mp3, TimeSpan.FromMilliseconds(10)), new(1, Cd, null)]));
        Assert.Equal(TimeSpan.FromSeconds(2), PlaybackStartPolicy.HedgeDelay("Balanced"));
        Assert.Equal(TimeSpan.FromSeconds(2), PlaybackStartPolicy.HedgeDelay("FastStart"));
        Assert.Equal(TimeSpan.FromSeconds(6), PlaybackStartPolicy.HedgeDelay("BestQuality"));
    }

    [Theory]
    [InlineData("Balanced", 10)]
    [InlineData("BestQuality", 10)]
    [InlineData("FastStart", 2)]
    public void KnownLossySourcesCannotRaceASlowerLosslessStart(string mode, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), PlaybackStartPolicy.HedgeDelay(mode, ProviderAudioQuality.Lossless, Mp3));

    [Fact]
    public void LosslessFallbacksComeBeforeKnownLossySources() =>
        Assert.Equal([2, 1, 0], PlaybackStartPolicy.Order("Balanced",
        [
            new(0, Mp3, TimeSpan.FromMilliseconds(100)),
            new(1, Cd, TimeSpan.FromSeconds(5)),
            new(2, Cd, TimeSpan.FromSeconds(4))
        ]));

    [Theory]
    [InlineData(ProviderAudioQuality.Any, "CdLossless", ProviderAudioQuality.Lossless)]
    [InlineData(ProviderAudioQuality.HighResolution, "CdLossless", ProviderAudioQuality.Lossless)]
    [InlineData(ProviderAudioQuality.Lossy, "CdLossless", ProviderAudioQuality.Lossy)]
    [InlineData(ProviderAudioQuality.Any, "BestAvailable", ProviderAudioQuality.Any)]
    [InlineData(ProviderAudioQuality.HighResolution, "BestAvailable", ProviderAudioQuality.HighResolution)]
    public void TheConfiguredQualityIsACeiling(ProviderAudioQuality requested, string step, ProviderAudioQuality expected) =>
        Assert.Equal(expected, AudioQualityPolicy.Limit(requested, step));

    [Theory]
    [InlineData("CdLossless", null, "CdLossless")]
    [InlineData("CdLossless", "High", "High")]
    [InlineData("CdLossless", "BestAvailable", "CdLossless")]
    public void PersonalLimitsOnlyLowerTheHouseholdLimit(string household, string? personal, string expected) =>
        Assert.Equal(expected, AudioQualityPolicy.Lower(household, personal));
}
