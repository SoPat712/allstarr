using allstarr.Core.Capabilities;
using allstarr.Models.Domain;

namespace allstarr.Core.Music;

public sealed record TrackMediaFacts(ProviderMediaFormat Media, long? Size = null,
    double? DurationSeconds = null, ProviderReplayGain? ReplayGain = null)
{
    public TrackMediaFacts WithFallback(TrackMediaFacts fallback)
    {
        var compatible = (Media.Container == "unknown" || fallback.Media.Container == "unknown" ||
            ProviderAudioDeliveryRequest.Container(Media.Container) == ProviderAudioDeliveryRequest.Container(fallback.Media.Container)) &&
            (Media.Codec == "unknown" || fallback.Media.Codec == "unknown" || Media.Codec == fallback.Media.Codec);
        return new(new(Media.Container == "unknown" && compatible ? fallback.Media.MimeType : Media.MimeType,
            Media.Container == "unknown" && compatible ? fallback.Media.Container : Media.Container,
            Media.Codec == "unknown" && compatible ? fallback.Media.Codec : Media.Codec,
            Media.Bitrate ?? (compatible ? fallback.Media.Bitrate : null),
            Media.SampleRate ?? (compatible ? fallback.Media.SampleRate : null),
            Media.Codec is "mp3" or "aac" or "opus" or "vorbis" ? null : Media.BitDepth ?? (compatible ? fallback.Media.BitDepth : null),
            Media.Channels ?? (compatible ? fallback.Media.Channels : null)),
            Size ?? (compatible ? fallback.Size : null),
            DurationSeconds ?? (compatible ? fallback.DurationSeconds : null),
            ReplayGain ?? (compatible ? fallback.ReplayGain : null));
    }

    public void Apply(Song song)
    {
        if (song.IsLocal) return;
        var compatible = (string.IsNullOrWhiteSpace(song.Container) || Media.Container == "unknown" ||
            ProviderAudioDeliveryRequest.Container(song.Container) == ProviderAudioDeliveryRequest.Container(Media.Container)) &&
            (string.IsNullOrWhiteSpace(song.Codec) || Media.Codec == "unknown" || song.Codec == Media.Codec);
        song.Container = Media.Container == "unknown" ? compatible ? song.Container : null : Media.Container;
        song.Codec = Media.Codec == "unknown" ? compatible ? song.Codec : null : Media.Codec;
        song.Bitrate = Media.Bitrate ?? (compatible ? song.Bitrate : null);
        song.Size = Size is > 0 ? Size : compatible ? song.Size : null;
        song.SampleRate = Media.SampleRate ?? (compatible ? song.SampleRate : null);
        song.BitDepth = Media.Codec is "mp3" or "aac" or "opus" or "vorbis" ? null : Media.BitDepth ?? (compatible ? song.BitDepth : null);
        song.Channels = Media.Channels ?? (compatible ? song.Channels : null);
        if (ReplayGain != null) song.ReplayGain = ReplayGain;
        if (DurationSeconds is > 0 and < 86400)
        {
            song.PreciseDuration = TimeSpan.FromSeconds(DurationSeconds.Value);
            song.Duration = (int)DurationSeconds.Value;
        }
    }
}
