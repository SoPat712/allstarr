using allstarr.Core.Capabilities;
using allstarr.Services.Common;
using Microsoft.AspNetCore.Http;

namespace allstarr.Tests;

public sealed class AudioQualityPolicyTests
{
    [Theory]
    [InlineData("DataSaver", "aac-96", "MP3_128", "MP3_320")]
    [InlineData("High", "aac-320", "MP3_320", "MP3_320")]
    [InlineData("CdLossless", "alac-16-44", "FLAC", "FLAC_16")]
    [InlineData("HiResLossless", "alac-24-96", "FLAC", "FLAC_24_LOW")]
    [InlineData("BestAvailable", "alac-24-192", "FLAC", "FLAC_24_HIGH")]
    public void ProviderCeilings_MapEverySharedStep(
        string step, string apple, string deezer, string qobuz)
    {
        var actual = AudioQualityPolicy.ProviderCeilings(step);
        Assert.Equal((apple, deezer, qobuz), actual);
    }

    [Theory]
    [InlineData("128", ProviderAudioQuality.DataSaver)]
    [InlineData("191", ProviderAudioQuality.DataSaver)]
    [InlineData("192", ProviderAudioQuality.Lossy)]
    [InlineData("320", ProviderAudioQuality.Lossy)]
    [InlineData("0", ProviderAudioQuality.Any)]
    [InlineData(null, ProviderAudioQuality.Any)]
    public void SubsonicBandwidthCap_CanOnlyLowerPlaybackQuality(
        string? maxBitRate, ProviderAudioQuality expected) =>
        Assert.Equal(expected, StreamQualityHelper.FromSubsonicMaxBitRate(maxBitRate));

    [Theory]
    [InlineData("AudioCodec", "aac")]
    [InlineData("AudioCodec", "flac,aac")]
    [InlineData("TranscodingContainer", "m4a")]
    public void JellyfinLossyRequest_OutranksBroadDeviceBandwidth(
        string key,
        string value)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = QueryString.Create(
            new KeyValuePair<string, string?>[]
            {
                new("MaxStreamingBitrate", "140000000"),
                new(key, value)
            });

        Assert.Equal(StreamQuality.High,
            StreamQualityHelper.ParseFromQueryString(context.Request.Query));
    }
}
