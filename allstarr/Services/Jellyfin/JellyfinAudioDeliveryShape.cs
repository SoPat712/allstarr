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
        source["Container"] = media.Container;
        audio["Codec"] = media.Codec;
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
            ["static"] = "true",
            ["AudioCodec"] = media.Codec,
            ["MaxStreamingBitrate"] = media.Bitrate?.ToString(CultureInfo.InvariantCulture),
            ["AudioSampleRate"] = media.SampleRate?.ToString(CultureInfo.InvariantCulture),
            ["AudioChannels"] = media.Channels?.ToString(CultureInfo.InvariantCulture),
            ["MaxAudioBitDepth"] = media.BitDepth?.ToString(CultureInfo.InvariantCulture)
        };
        source["DirectStreamUrl"] = QueryHelpers.AddQueryString(
            $"/Audio/{Uri.EscapeDataString(itemId)}/stream.{Uri.EscapeDataString(media.Container)}", parameters);
        source["Path"] = source["DirectStreamUrl"];
        source["Protocol"] = "Http";
    }
}
