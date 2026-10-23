using allstarr.Core.Capabilities;
using allstarr.Core.Music;

namespace allstarr.Tests;

public sealed class PlaybackStartPolicyTests
{
    private static readonly ProviderMediaFormat Cd = new("audio/flac", "flac", "flac", sampleRate: 44100, bitDepth: 16);
    private static readonly ProviderMediaFormat High = new("audio/flac", "flac", "flac", sampleRate: 96000, bitDepth: 24);
    private static readonly ProviderMediaFormat Mp3 = new("audio/mpeg", "mp3", "mp3", bitrate: 320000);

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
}
