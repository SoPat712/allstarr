using System.Globalization;
using allstarr.Core.Music;
using Microsoft.AspNetCore.WebUtilities;

namespace allstarr.Services.Jellyfin;

internal static class JellyfinAudioDeliveryShape
{
    internal static void Apply(Dictionary<string, object?> source, string itemId, ProviderAudioDeliveryDecision decision)
    {
        source["SupportsDirectPlay"] = decision.SupportsDirectPlay;
        source["SupportsDirectStream"] = decision.SupportsDirectStream;
        source["SupportsTranscoding"] = decision.SupportsTranscoding;
        source.Remove("TranscodingUrl");
        source.Remove("DirectStreamUrl");
        source.Remove("Path");
        source.Remove("Container");
        source.Remove("Bitrate");
        source.Remove("Size");
        if (source.GetValueOrDefault("MediaStreams") is not Dictionary<string, object?>[] streams) return;
        var audio = streams.First(stream => Equals(stream.GetValueOrDefault("Type"), "Audio"));
        foreach (var key in new[] { "Codec", "BitRate", "SampleRate", "BitDepth", "Channels", "ChannelLayout", "DisplayTitle", "TimeBase" }) audio.Remove(key);
        if (decision.Media is not { } media) return;
        var knownContainer = !string.IsNullOrWhiteSpace(media.Container) && !media.Container.Equals("unknown", StringComparison.OrdinalIgnoreCase);
        var knownCodec = !string.IsNullOrWhiteSpace(media.Codec) && !media.Codec.Equals("unknown", StringComparison.OrdinalIgnoreCase);
        if (knownContainer) source["Container"] = media.Container;
        if (knownCodec) audio["Codec"] = media.Codec;
        void Fact(string key, int? value) { if (value is > 0) audio[key] = value.Value; }
        Fact("BitRate", media.Bitrate);
        Fact("SampleRate", media.SampleRate);
        Fact("BitDepth", media.BitDepth);
        Fact("Channels", media.Channels);
        if (media.Bitrate is > 0) source["Bitrate"] = media.Bitrate.Value;
        if (decision.Size is > 0) source["Size"] = decision.Size.Value;
        if (!decision.Supported) return;
        var parameters = new Dictionary<string, string?>
        {
            ["static"] = decision.SupportsTranscoding ? "false" : "true",
            ["EnableDirectPlay"] = decision.SupportsTranscoding ? "false" : null,
            ["EnableDirectStream"] = decision.SupportsTranscoding ? "false" : null,
            ["segmentContainer"] = decision.Hls ? "mp4" : null,
            ["TranscodingProtocol"] = decision.Hls ? "hls" : null,
            ["StartTimeTicks"] = decision.StartTimeTicks > 0 ? decision.StartTimeTicks.ToString(CultureInfo.InvariantCulture) : null,
            ["AudioCodec"] = knownCodec ? media.Codec : null,
            ["MaxStreamingBitrate"] = media.Bitrate?.ToString(CultureInfo.InvariantCulture),
            ["AudioSampleRate"] = media.SampleRate?.ToString(CultureInfo.InvariantCulture),
            ["AudioChannels"] = media.Channels?.ToString(CultureInfo.InvariantCulture),
            ["MaxAudioBitDepth"] = media.BitDepth?.ToString(CultureInfo.InvariantCulture)
        };
        var url = QueryHelpers.AddQueryString(
            decision.Hls ? $"/Audio/{Uri.EscapeDataString(itemId)}/main.m3u8" :
                $"/Audio/{Uri.EscapeDataString(itemId)}/stream{(knownContainer ? "." + Uri.EscapeDataString(media.Container) : "")}", parameters);
        source[decision.SupportsTranscoding ? "TranscodingUrl" : "DirectStreamUrl"] = url;
        if (decision.SupportsTranscoding)
        {
            source["TranscodingContainer"] = media.Container;
            source["TranscodingSubProtocol"] = decision.Hls ? "hls" : "http";
        }
        source["Path"] = url;
        source["Protocol"] = "Http";
    }
}
