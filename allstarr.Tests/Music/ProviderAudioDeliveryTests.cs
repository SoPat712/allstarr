using allstarr.Core.Capabilities;
using allstarr.Core.Music;

namespace allstarr.Tests;

public sealed class ProviderAudioDeliveryTests
{
    [Theory]
    [InlineData("bitrate")]
    [InlineData("sampleRate")]
    [InlineData("bitDepth")]
    [InlineData("channels")]
    [InlineData("exactSampleRate")]
    [InlineData("exactChannels")]
    [InlineData("profileChannels")]
    public void AudioLimits_RejectOnlyKnownViolations(string fact)
    {
        ProviderAudioDeliveryRequest request = fact switch
        {
            "bitrate" => new ProviderAudioDeliveryRequest { MaxBitrate = 128000 },
            "sampleRate" => new() { MaxSampleRate = 44100 },
            "bitDepth" => new() { MaxBitDepth = 16 },
            "channels" => new() { MaxChannels = 2 },
            "exactSampleRate" => new() { SampleRate = 44100 },
            "exactChannels" => new() { Channels = 2 },
            _ => new() { Profiles = [new(["flac"], [], ["http"], 2)] }
        };

        Assert.True(request.Evaluate(new("audio/flac", "flac", "flac")).Supported);
        Assert.True(request.Evaluate(new("audio/flac", "flac", "flac", 128000, 44100, 16, 2)).Supported);
        Assert.False(request.Evaluate(new("audio/flac", "flac", "flac", 256000, 96000, 24, 6)).Supported);
    }

    [Theory]
    [InlineData("AudioBitrate", 128000, 256000, true)]
    [InlineData("AudioSampleRate", 44100, 96000, true)]
    [InlineData("AudioBitDepth", 16, 24, true)]
    [InlineData("AudioChannels", 2, 6, true)]
    [InlineData("AudioChannels", 2, 6, false)]
    public void CodecConditions_RejectOnlyKnownViolations(string property, int allowed, int rejected, bool required)
    {
        var request = new ProviderAudioDeliveryRequest
        {
            Conditions = [new("flac", property, "LessThanEqual", [allowed.ToString(System.Globalization.CultureInfo.InvariantCulture)], required)]
        };
        ProviderMediaFormat Media(int value) => property switch
        {
            "AudioBitrate" => new("audio/flac", "flac", "flac", bitrate: value),
            "AudioSampleRate" => new("audio/flac", "flac", "flac", sampleRate: value),
            "AudioBitDepth" => new("audio/flac", "flac", "flac", bitDepth: value),
            _ => new("audio/flac", "flac", "flac", channels: value)
        };

        Assert.True(request.Evaluate(new("audio/flac", "flac", "flac")).Supported);
        Assert.True(request.Evaluate(Media(allowed)).Supported);
        Assert.False(request.Evaluate(Media(rejected)).Supported);
    }

    [Theory]
    [InlineData("unknown", "flac", true)]
    [InlineData("flac", "unknown", true)]
    [InlineData("unknown", "unknown", true)]
    [InlineData("mp3", "flac", false)]
    [InlineData("flac", "mp3", false)]
    public void AudioProfiles_RejectOnlyKnownFormatViolations(string container, string codec, bool compatible)
    {
        var request = new ProviderAudioDeliveryRequest { Profiles = [new(["flac"], ["flac"], ["http"])] };

        Assert.Equal(compatible, request.Evaluate(new("audio/flac", container, codec)).Supported);
    }

    [Theory]
    [InlineData("mp4", "aac", "m4a", "aac", true)]
    [InlineData("m4a", "aac", "mp4", "aac", true)]
    [InlineData("m4a", "aac", "m4a", "alac", false)]
    [InlineData("m4a", "alac", "flac", "flac", false)]
    [InlineData("opus", "", "ogg", "opus", true)]
    [InlineData("opus", "", "ogg", "unknown", true)]
    [InlineData("opus", "", "unknown", "mp3", false)]
    public void AudioProfiles_PreserveContainerAliasesAndCodecRestrictions(string container, string codec, string sourceContainer, string sourceCodec, bool compatible)
    {
        var request = new ProviderAudioDeliveryRequest { Profiles = [new([container], codec.Length == 0 ? [] : [codec], ["http"])] };

        Assert.Equal(compatible, request.Evaluate(new("audio/mp4", sourceContainer, sourceCodec)).Supported);
    }

    [Fact]
    public void CodecConditions_AllowUnavailableProperties()
    {
        var request = new ProviderAudioDeliveryRequest
        {
            Conditions = [new("aac", "AudioProfile", "NotEquals", ["HE-AAC"], true)]
        };

        Assert.True(request.Evaluate(new("audio/mp4", "m4a", "aac")).Supported);
        Assert.False(request.Evaluate(null).Supported);
    }
}
