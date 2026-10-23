using allstarr.Core.Capabilities;

namespace allstarr.Core.Music;

public sealed record PlaybackStartCandidate(int Index, ProviderMediaFormat? Media, TimeSpan? MedianStart,
    bool IsCached = false);

public static class PlaybackStartPolicy
{
    public static TimeSpan HedgeDelay(string? mode) => TimeSpan.FromSeconds(mode == "BestQuality" ? 6 : 2);

    public static IReadOnlyList<int> Order(string? mode, IReadOnlyList<PlaybackStartCandidate> candidates)
    {
        if (candidates.Count == 0) return [];
        var quality = candidates.OrderByDescending(item => IsLossless(item.Media))
            .ThenByDescending(item => item.Media?.BitDepth ?? 0)
            .ThenByDescending(item => item.Media?.SampleRate ?? 0)
            .ThenByDescending(item => item.Media?.Bitrate ?? 0).ToArray();
        var cached = quality.FirstOrDefault(item => item.IsCached);
        var best = quality[0];
        var fastest = candidates.Where(item => IsLossless(item.Media))
            .OrderBy(item => item.IsCached ? TimeSpan.Zero : item.MedianStart ?? TimeSpan.MaxValue)
            .FirstOrDefault() ?? candidates.OrderBy(item => item.IsCached ? TimeSpan.Zero : item.MedianStart ?? TimeSpan.MaxValue).First();
        var selected = mode == "BestQuality" || mode != "FastStart" && best.MedianStart <= TimeSpan.FromSeconds(3) ? best : fastest;
        if (cached != null && CompareQuality(cached.Media, selected.Media) >= 0) selected = cached;
        return new[] { selected.Index }.Concat(candidates.Where(item => item.Index != selected.Index)
            .OrderBy(item => item.IsCached ? TimeSpan.Zero : item.MedianStart ?? TimeSpan.MaxValue)
            .Select(item => item.Index)).ToArray();
    }

    public static bool IsLossless(ProviderMediaFormat? media) => media?.Codec is "flac" or "alac" or "wav" ||
        media?.Codec.StartsWith("pcm", StringComparison.Ordinal) == true;

    private static int CompareQuality(ProviderMediaFormat? left, ProviderMediaFormat? right) =>
        (IsLossless(left), left?.BitDepth ?? 0, left?.SampleRate ?? 0)
            .CompareTo((IsLossless(right), right?.BitDepth ?? 0, right?.SampleRate ?? 0));
}
