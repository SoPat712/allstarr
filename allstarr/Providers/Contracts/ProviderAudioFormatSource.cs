using System.Text.RegularExpressions;
using allstarr.Core.Music;

namespace allstarr.Core.Capabilities;

public interface IProviderAudioFormatSource
{
    ProviderMediaFormat? PreferredFormat(ProviderAudioQuality quality);
}

public static class ProviderDeclaredAudio
{
    public static ProviderMediaFormat? Parse(string declaration, string? kind = null)
    {
        var text = declaration.ToLowerInvariant();
        kind = kind?.ToLowerInvariant();
        if (kind == "spatial") return null;
        if (kind == "lossless" || kind != "lossy" && (text.Contains("lossless") || text.Contains("flac") || text.Contains("alac")))
        {
            var codec = text.Contains("alac") ? "alac" : "flac";
            var depth = Regex.IsMatch(text, @"(?<!\d)24(?!\d)") ? 24 : 16;
            var rate = Regex.IsMatch(text, @"(?<!\d)192(?!\d)") ? 192000 : Regex.IsMatch(text, @"(?<!\d)96(?!\d)") ? 96000
                : Regex.IsMatch(text, @"(?<!\d)48(?!\d)") ? 48000 : 44100;
            return new(codec == "alac" ? "audio/mp4" : "audio/flac", codec == "alac" ? "m4a" : "flac", codec,
                sampleRate: rate, bitDepth: depth);
        }
        var bitrate = Regex.Match(text, @"(?<!\d)(32|64|96|128|160|192|256|320)(?:000)?(?!\d)");
        if (!bitrate.Success) return null;
        var lossy = text.Contains("aac") ? "aac" : text.Contains("opus") ? "opus" : "mp3";
        return new(lossy == "aac" ? "audio/mp4" : lossy == "opus" ? "audio/ogg" : "audio/mpeg",
            lossy == "aac" ? "m4a" : lossy == "opus" ? "ogg" : "mp3", lossy, int.Parse(bitrate.Groups[1].Value) * 1000);
    }

    public static ProviderMediaFormat? Best(IEnumerable<ProviderMediaFormat> formats, ProviderAudioQuality quality) =>
        formats.Where(format => quality is ProviderAudioQuality.Any or >= ProviderAudioQuality.Lossless || !PlaybackStartPolicy.IsLossless(format))
            .Select(format => quality == ProviderAudioQuality.Lossless && PlaybackStartPolicy.IsLossless(format)
                ? new ProviderMediaFormat(format.MimeType, format.Container, format.Codec, sampleRate: 44100, bitDepth: 16) : format)
            .OrderByDescending(format => format, Comparer<ProviderMediaFormat>.Create(PlaybackStartPolicy.CompareQuality)).FirstOrDefault();
}
