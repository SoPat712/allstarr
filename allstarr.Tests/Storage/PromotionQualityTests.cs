using allstarr.Core.Enrichment;
using allstarr.Models.Domain;

namespace allstarr.Tests.Storage;

public sealed class PromotionQualityTests
{
    [Theory]
    [InlineData("flac", 16, 44100, false, true)]
    [InlineData("flac", 24, 192000, false, true)]
    [InlineData("flac", 16, 32000, false, false)]
    [InlineData("mp3", 0, 44100, false, false)]
    [InlineData("mp3", 0, 44100, true, false)]
    [InlineData(null, 0, 0, false, false)]
    public void Floor_RequiresMeasuredLosslessEvidence(string? codec, int bits, int rate, bool allowLossy, bool expected)
    {
        Assert.Equal(expected, PromotionQuality.Accepts(new() { AllowLossy = allowLossy }, codec, bits, rate));
    }

    [Fact]
    public void Lossy_RequiresBothPermissionAndLossyFloor()
    {
        var profile = new PromotionProfile { AllowLossy = true, QualityFloor = new("lossy", 16, 44100) };
        Assert.True(PromotionQuality.Accepts(profile, "aac", null, 48000));
        Assert.True(PromotionQuality.Accepts(profile with { QualityFloor = new("any") }, "aac", null, 48000));
        Assert.False(PromotionQuality.Accepts(profile with { AllowLossy = false }, "aac", null, 48000));
        Assert.False(PromotionQuality.Accepts(profile, "aac", null, 32000));
    }

    [Fact]
    public void BestSource_PrefersLosslessThenResolutionWithinCeiling()
    {
        var profile = new PromotionProfile { AllowLossy = true, QualityFloor = new("lossy") };
        var sources = new[]
        {
            new Song { ExternalId = "lossy", Codec = "aac", SampleRate = 48000 },
            new Song { ExternalId = "cd", Codec = "flac", BitDepth = 16, SampleRate = 44100 },
            new Song { ExternalId = "high", Codec = "flac", BitDepth = 24, SampleRate = 192000 }
        };
        Assert.Equal(new[] { "high", "cd", "lossy" }, PromotionQuality.Order(profile, sources).Select(item => item.ExternalId));
    }

    [Fact]
    public void BestSource_TriesUnknownResolutionUntilTheCeilingIsMet()
    {
        var profile = new PromotionProfile();
        Assert.True(PromotionQuality.CanImprove(profile, new Song(), "flac", 16, 44100, null));
        Assert.False(PromotionQuality.CanImprove(profile, new Song(), "flac", 24, 96000, null));
        Assert.False(PromotionQuality.CanImprove(profile, new Song { Codec = "aac", SampleRate = 48000 }, "flac", 16, 44100, null));
    }
}
