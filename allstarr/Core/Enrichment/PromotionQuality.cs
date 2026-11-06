using allstarr.Models.Domain;

namespace allstarr.Core.Enrichment;

public static class PromotionQuality
{
    public static bool IsLossless(string? codec) => codec?.ToLowerInvariant() is "flac" or "alac" or "wavpack" or "ape" ||
        codec?.StartsWith("pcm_", StringComparison.OrdinalIgnoreCase) == true;

    public static bool Accepts(PromotionProfile profile, string? codec, int? bits, int? rate)
    {
        if (string.IsNullOrWhiteSpace(codec) || rate is null || rate < profile.QualityFloor.MinSampleRate) return false;
        if (IsLossless(codec)) return bits >= profile.QualityFloor.MinBitDepth;
        return profile.AllowLossy && profile.QualityFloor.CodecFamily is "lossy" or "any" &&
            codec.ToLowerInvariant() is "mp3" or "aac" or "opus" or "vorbis";
    }

    public static IEnumerable<Song> Order(PromotionProfile profile, IEnumerable<Song> sources) => sources
        .OrderByDescending(song => IsLossless(song.Codec))
        .ThenByDescending(song => Math.Min(song.BitDepth ?? 0, profile.HiResCeiling.MaxBitDepth))
        .ThenByDescending(song => Math.Min(song.SampleRate ?? 0, profile.HiResCeiling.MaxSampleRate))
        .ThenByDescending(song => song.Bitrate ?? 0);

    public static bool CanImprove(PromotionProfile profile, Song candidate, string? codec, int? bits, int? rate, int? bitrate)
    {
        if (IsLossless(codec) && bits >= profile.HiResCeiling.MaxBitDepth && rate >= profile.HiResCeiling.MaxSampleRate) return false;
        if (string.IsNullOrWhiteSpace(candidate.Codec)) return true;
        if (IsLossless(candidate.Codec) != IsLossless(codec)) return IsLossless(candidate.Codec);
        if (!IsLossless(codec)) return candidate.Bitrate == null || candidate.Bitrate > bitrate;
        if (candidate.BitDepth == null || candidate.SampleRate == null) return true;
        var depth = Math.Min(candidate.BitDepth.Value, profile.HiResCeiling.MaxBitDepth).CompareTo(Math.Min(bits ?? 0, profile.HiResCeiling.MaxBitDepth));
        return depth > 0 || depth == 0 && Math.Min(candidate.SampleRate.Value, profile.HiResCeiling.MaxSampleRate) > Math.Min(rate ?? 0, profile.HiResCeiling.MaxSampleRate);
    }
}
