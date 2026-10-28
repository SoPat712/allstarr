using allstarr.Core.Capabilities;
using allstarr.Core.Music;

namespace allstarr.Tests;

public sealed class ProviderAudioDeliveryTests
{
    [Theory]
    [InlineData("opus", "ogg", "vorbis")]
    [InlineData("aac", "mp4", "alac")]
    public void SubsonicDecisionParametersPreserveThePromisedCodec(string codec, string container, string originalCodec)
    {
        var delivery = new ProviderAudioDeliveryDecision(new("audio/" + container, container, codec, 128000), false, false, null)
        { SupportsTranscoding = true };
        var response = Assert.IsType<Microsoft.AspNetCore.Mvc.JsonResult>(
            new allstarr.Services.Subsonic.SubsonicResponseBuilder().CreateTranscodeDecisionResponse("json", delivery));
        using var document = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(response.Value));
        var parameters = document.RootElement.GetProperty("subsonic-response").GetProperty("transcodeDecision").GetProperty("transcodeParams").GetString()!;
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery("?" + parameters);
        Assert.Equal(codec, query["audioCodec"].ToString());
        var request = ProviderAudioDeliveryRequests.Subsonic(new allstarr.Services.Subsonic.SubsonicRequestParameters("GET", null, null,
            query.Select(pair => new allstarr.Services.Subsonic.SubsonicParameter(pair.Key, pair.Value.ToString(), allstarr.Services.Subsonic.SubsonicParameterSource.Query)).ToArray()));
        Assert.False(request.Evaluate(new("audio/" + container, container, originalCodec, 64000, 44100, channels: 2)).Supported);
        Assert.Equal(codec, ProviderAudioTranscoder.Plan(request, new("audio/" + container, container, originalCodec, 64000, 44100, channels: 2))!.Media.Codec);
    }

    [Fact]
    public void UnknownSourceFormatDoesNotBecomeARequiredPlaybackCodecOrContainer()
    {
        var source = new Dictionary<string, object?>
        { ["MediaStreams"] = new Dictionary<string, object?>[] { new() { ["Type"] = "Audio" } } };
        allstarr.Services.Jellyfin.JellyfinAudioDeliveryShape.Apply(source, "ext-apple-download-song-42",
            new ProviderAudioDeliveryRequest().Decide(new("application/octet-stream", "unknown", "unknown")));
        var url = Assert.IsType<string>(source["DirectStreamUrl"]);
        Assert.StartsWith("/Audio/ext-apple-download-song-42/stream?", url);
        Assert.DoesNotContain("unknown", url);
        Assert.DoesNotContain("AudioCodec", url);
        Assert.False(source.ContainsKey("Container"));
        Assert.False(Assert.Single(Assert.IsType<Dictionary<string, object?>[]>(source["MediaStreams"])).ContainsKey("Codec"));
    }

    [Theory]
    [InlineData("bitrate")]
    [InlineData("sampleRate")]
    [InlineData("bitDepth")]
    [InlineData("channels")]
    [InlineData("exactSampleRate")]
    [InlineData("exactChannels")]
    [InlineData("profileChannels")]
    public void AudioLimitsKeepUnknownFactsPlayableAndRejectKnownDirectViolations(string fact)
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

        Assert.Equal(fact != "bitrate", request.Evaluate(new("audio/flac", "flac", "flac")).Supported);
        Assert.True(request.Decide(new("audio/flac", "flac", "flac")).Supported);
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
