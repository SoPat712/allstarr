using System.Globalization;
using allstarr.Core.Capabilities;

namespace allstarr.Core.Music;

public sealed record ProviderAudioProfile(
    string[] Containers,
    string[] Codecs,
    string[] Protocols,
    int? MaxChannels = null);

public sealed record ProviderAudioCondition(string Codec, string Property, string Comparison, string[] Values, bool Required);

public sealed record ProviderAudioDeliveryRequest
{
    public IReadOnlyList<ProviderAudioProfile>? Profiles { get; init; }
    public IReadOnlyList<ProviderAudioProfile>? Formats { get; init; }
    public IReadOnlyList<ProviderAudioProfile>? TranscodingProfiles { get; init; }
    public IReadOnlyList<ProviderAudioCondition> Conditions { get; init; } = [];
    public long? MaxBitrate { get; init; }
    public int? MaxSampleRate { get; init; }
    public int? SampleRate { get; init; }
    public int? MaxChannels { get; init; }
    public int? Channels { get; init; }
    public int? MaxBitDepth { get; init; }
    public long StartTimeTicks { get; init; }
    public bool EnableDirectPlay { get; init; } = true;
    public bool EnableDirectStream { get; init; } = true;
    public bool EnableTranscoding { get; init; } = true;
    public string PreferredCodec { get; init; } = "aac";
    public bool RequireHls { get; init; }
    public bool AllowHls { get; init; }
    public string? Error { get; init; }
    public bool InvalidTimeOffset { get; init; }

    public ProviderAudioQuality Quality => LosslessOnly(Formats ?? Profiles)
        ? ProviderAudioQuality.Lossless
        : MaxBitrate is > 0 and < 320000 ? ProviderAudioQuality.DataSaver
            : MaxBitrate is > 0 and < 10000000 || LossyOnly(Formats ?? Profiles)
                ? ProviderAudioQuality.Lossy : ProviderAudioQuality.Any;

    private static bool LosslessOnly(IReadOnlyList<ProviderAudioProfile>? profiles) => profiles is { Count: > 0 } &&
        profiles.All(profile => profile.Codecs.Length > 0
            ? profile.Codecs.All(codec => codec.ToLowerInvariant() is "flac" or "alac" or "pcm_s16le" or "pcm_s24le")
            : profile.Containers.Length > 0 && profile.Containers.All(container => container.ToLowerInvariant() is "flac" or "wav"));

    private static bool LossyOnly(IReadOnlyList<ProviderAudioProfile>? profiles) => profiles is { Count: > 0 } &&
        profiles.All(profile => profile.Codecs.Length > 0
            ? profile.Codecs.All(codec => codec.ToLowerInvariant() is "mp3" or "aac" or "opus" or "vorbis")
            : profile.Containers.Length > 0 && profile.Containers.All(container => container.ToLowerInvariant() is "mp3" or "aac" or "m4a" or "opus" or "ogg"));

    public string? RequestError => Error ?? (StartTimeTicks < 0 ? "Invalid audio time offset."
        : !EnableDirectPlay && !EnableDirectStream && !EnableTranscoding ? "All audio delivery methods are disabled." : null);

    public ProviderAudioDeliveryDecision Decide(ProviderMediaFormat? media, long? size = null)
    {
        var direct = Evaluate(media, size);
        if (direct.Supported || media == null || ProviderAudioTranscoder.Plan(this, media) is not { } conversion) return direct;
        return new(conversion.Media, false, false, null)
        { SupportsTranscoding = true, Hls = conversion.Hls, StartTimeTicks = StartTimeTicks };
    }

    public ProviderAudioDeliveryDecision Evaluate(ProviderMediaFormat? media, long? size = null)
    {
        string? reason = RequestError;
        if (reason == null && media == null) reason = "No verified playback source is available.";
        if (reason == null && MaxBitrate is > 0 && media!.Bitrate == null &&
            (BitrateCeiling(media) is not { } ceiling || ceiling > MaxBitrate))
            reason = "The requested bitrate requires a bounded audio conversion.";
        if (reason == null && (Profiles != null && !Profiles.Any(profile => Matches(profile, media!)) ||
            Formats != null && !Formats.Any(profile => Matches(profile, media!)))) reason = "The source codec or container is not supported by this audio profile.";
        if (reason == null && (!Within(media!.Bitrate, MaxBitrate) || !Within(media.SampleRate, MaxSampleRate) ||
            !Within(media.Channels, MaxChannels) || !Within(media.BitDepth, MaxBitDepth) ||
            SampleRate.HasValue && media.SampleRate.HasValue && media.SampleRate != SampleRate ||
            Channels.HasValue && media.Channels.HasValue && media.Channels != Channels))
            reason = "The source does not meet the requested audio limits.";
        if (reason == null && !Conditions.All(condition => Matches(condition, media!))) reason = "The source violates the audio codec conditions.";
        if (reason == null && !EnableDirectPlay && !EnableDirectStream) reason = "Direct audio delivery is disabled.";
        return new(media, reason == null && EnableDirectPlay, reason == null && EnableDirectStream, reason) { Size = size, StartTimeTicks = StartTimeTicks };
    }

    internal bool AcceptsConversion(ProviderMediaFormat media, bool hls = false)
    {
        return EnableTranscoding && RequestError == null &&
            AcceptsConversionFormat(media, hls) &&
            Within(media.Bitrate, MaxBitrate) && Within(media.SampleRate, MaxSampleRate) &&
            Within(media.Channels, MaxChannels) && Within(media.BitDepth, MaxBitDepth) &&
            (!SampleRate.HasValue || media.SampleRate == SampleRate) &&
            (!Channels.HasValue || media.Channels == Channels) && Conditions.All(condition => Matches(condition, media) && MatchesGeneratedProfile(condition, media));
    }

    internal bool AcceptsConversionFormat(ProviderMediaFormat media, bool hls = false) =>
        (!hls || media.Codec == "aac" && Container(media.Container) == "mp4") &&
        (Profiles == null && TranscodingProfiles == null || (Profiles ?? []).Any(profile => Matches(profile, media)) ||
            (TranscodingProfiles ?? []).Any(profile => Matches(profile, media, hls))) &&
        (Formats == null || Formats.Any(profile => Matches(profile, media)));

    private static bool MatchesGeneratedProfile(ProviderAudioCondition condition, ProviderMediaFormat media)
    {
        if (media.Codec != "aac" || !condition.Property.Equals("AudioProfile", StringComparison.OrdinalIgnoreCase) ||
            condition.Codec.Length > 0 && !condition.Codec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Contains(media.Codec, StringComparer.OrdinalIgnoreCase)) return true;
        bool LowComplexity(string value) => value.Replace("-", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal)
            .ToLowerInvariant() is "lc" or "aaclc" or "lowcomplexity";
        return condition.Comparison.ToLowerInvariant() switch
        {
            "equals" => condition.Values.Any(LowComplexity),
            "notequals" => condition.Values.All(value => !LowComplexity(value)),
            _ => !condition.Required
        };
    }

    private static bool Within(long? actual, long? limit) => !limit.HasValue || !actual.HasValue || actual <= limit;

    private static long? BitrateCeiling(ProviderMediaFormat media) => media.Codec switch
    {
        "mp3" => 320000,
        "opus" => 510000,
        "aac" => (media.SampleRate ?? 192000) * (long)(media.Channels ?? 32) * 6,
        "flac" or "alac" or "pcm_s16le" or "pcm_s24le" or "pcm_s32le" when
            media.SampleRate is { } rate && media.Channels is { } channels && media.BitDepth is { } depth =>
            (long)Math.Ceiling(rate * (double)channels * depth * 1.1),
        _ => null
    };

    private static bool Unknown(string value) => value.Equals("unknown", StringComparison.OrdinalIgnoreCase);

    private static bool Matches(ProviderAudioProfile profile, ProviderMediaFormat media, bool hls = false) =>
        (profile.Containers.Length == 0 || profile.Containers.Any(value => (Unknown(media.Container) || Container(value) == Container(media.Container)) &&
            (!value.Equals("opus", StringComparison.OrdinalIgnoreCase) || Unknown(media.Codec) || media.Codec.Equals("opus", StringComparison.OrdinalIgnoreCase)))) &&
        (profile.Codecs.Length == 0 || Unknown(media.Codec) || profile.Codecs.Any(value => value.Equals(media.Codec, StringComparison.OrdinalIgnoreCase))) &&
        (profile.Protocols.Length == 0 || profile.Protocols.Contains(hls ? "hls" : "http", StringComparer.OrdinalIgnoreCase)) &&
        Within(media.Channels, profile.MaxChannels);

    internal static string Container(string value) => value.ToLowerInvariant() switch
    {
        "m4a" or "m4b" or "mp4" => "mp4",
        "opus" or "ogg" => "ogg",
        _ => value.ToLowerInvariant()
    };

    private static bool Matches(ProviderAudioCondition condition, ProviderMediaFormat media)
    {
        if (condition.Codec.Length > 0 && !condition.Codec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains(media.Codec, StringComparer.OrdinalIgnoreCase)) return true;
        long? actual = condition.Property.ToLowerInvariant() switch
        {
            "audiochannels" => media.Channels,
            "audiosamplerate" => media.SampleRate,
            "audiobitrate" => media.Bitrate,
            "audiobitdepth" => media.BitDepth,
            _ => null
        };
        if (!actual.HasValue) return true;
        var values = condition.Comparison.Equals("NotEquals", StringComparison.OrdinalIgnoreCase)
            ? condition.Values : condition.Comparison.Equals("Equals", StringComparison.OrdinalIgnoreCase)
                ? condition.Values : condition.Values.Take(1);
        bool Compare(string value) => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var expected) &&
            (condition.Comparison.ToLowerInvariant() switch
            {
                "equals" => actual == expected,
                "notequals" => actual != expected,
                "lessthanequal" => actual <= expected,
                "lessthan" => actual < expected,
                "greaterthanequal" => actual >= expected,
                "greaterthan" => actual > expected,
                _ => false
            });
        return condition.Comparison.Equals("NotEquals", StringComparison.OrdinalIgnoreCase) ? values.All(Compare) : values.Any(Compare);
    }
}

public sealed record ProviderAudioDeliveryDecision(
    ProviderMediaFormat? Media,
    bool SupportsDirectPlay,
    bool SupportsDirectStream,
    string? Error)
{
    public long? Size { get; init; }
    public long StartTimeTicks { get; init; }
    public bool Supported => SupportsDirectPlay || SupportsDirectStream || SupportsTranscoding;
    public bool SupportsTranscoding { get; init; }
    public bool Hls { get; init; }
}

public sealed class ProviderAudioProfileException(string message) : Exception(message);
