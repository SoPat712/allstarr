using allstarr.Core.Capabilities;

namespace allstarr.Core.Music;

public sealed record PlaybackStartCandidate(int Index, ProviderMediaFormat? Media, TimeSpan? MedianStart,
    bool IsCached = false, ProviderMediaFormat? AvailableMedia = null);

public static class PlaybackStartPolicy
{
    private static readonly TimeSpan LosslessPatience = TimeSpan.FromSeconds(10);

    public static TimeSpan HedgeDelay(string? mode) => TimeSpan.FromSeconds(mode == "BestQuality" ? 6 : 2);

    public static bool ShouldHedge(string? mode, ProviderMediaFormat? current, PlaybackStartCandidate next) =>
        CompareQuality(next.Media, current) >= 0 || CompareQuality(next.AvailableMedia, next.Media) > 0 ||
        (next.IsCached ? mode == "FastStart" : !IsLimitedLossy(next.Media));

    /// <summary>Waits longer before racing a source known to be lossy, so it can't beat a slower lossless start.</summary>
    public static TimeSpan HedgeDelay(string? mode, ProviderAudioQuality quality, ProviderMediaFormat? next) =>
        mode != "FastStart" && quality >= ProviderAudioQuality.Lossless && next != null && !IsLossless(next)
            ? LosslessPatience : HedgeDelay(mode);

    public static IReadOnlyList<int> Order(string? mode, IReadOnlyList<PlaybackStartCandidate> candidates)
    {
        if (candidates.Count == 0) return [];
        candidates = candidates.Select(item => mode != "FastStart" && item.IsCached && CompareQuality(item.AvailableMedia, item.Media) > 0
            ? item with { Media = item.AvailableMedia, IsCached = false } : item).ToArray();
        var quality = candidates.OrderByDescending(item => item.Media, Comparer<ProviderMediaFormat?>.Create(CompareQuality)).ToArray();
        var cached = quality.FirstOrDefault(item => item.IsCached);
        var best = quality[0];
        var starts = mode == "FastStart" ? candidates.ToArray() : candidates.Where(item => (!item.IsCached && (!IsLimitedLossy(item.Media) || IsLimitedLossy(best.Media))) ||
            CompareQuality(item.Media, best.Media) >= 0).ToArray();
        var fastest = (mode == "FastStart" ? [] : starts.Where(item => IsLossless(item.Media)))
            .OrderBy(item => item.IsCached ? TimeSpan.Zero : item.MedianStart ?? TimeSpan.MaxValue)
            .FirstOrDefault() ?? starts.OrderBy(item => item.IsCached ? TimeSpan.Zero : item.MedianStart ?? TimeSpan.MaxValue)
            .ThenByDescending(item => item.Media, Comparer<ProviderMediaFormat?>.Create(CompareQuality)).First();
        var selected = mode == "BestQuality" || mode != "FastStart" && best.MedianStart <= TimeSpan.FromSeconds(3) ? best : fastest;
        if (cached != null && (mode == "FastStart" || CompareQuality(cached.Media, best.Media) >= 0)) selected = cached;
        return new[] { selected.Index }.Concat(candidates.Where(item => item.Index != selected.Index)
            .OrderBy(item => item.Media != null && !IsLossless(item.Media))
            .ThenBy(item => IsLimitedLossy(item.Media))
            .ThenBy(item => item.IsCached ? TimeSpan.Zero : item.MedianStart ?? TimeSpan.MaxValue)
            .Select(item => item.Index)).ToArray();
    }

    public static bool IsLossless(ProviderMediaFormat? media) => media?.Codec is "flac" or "alac" or "wav" ||
        media?.Codec.StartsWith("pcm", StringComparison.Ordinal) == true;

    public static int CompareQuality(ProviderMediaFormat? left, ProviderMediaFormat? right) =>
        Quality(left).CompareTo(Quality(right));

    public static bool IsLimitedLossy(ProviderMediaFormat? media) =>
        media != null && !IsLossless(media) && media.Bitrate is > 0 and <= 160000;

    private static (bool Lossless, int Detail, int Rate) Quality(ProviderMediaFormat? media) =>
        IsLossless(media) ? (true, media?.BitDepth ?? 0, media?.SampleRate ?? 0)
            : (false, media?.Bitrate ?? 0, media?.SampleRate ?? 0);
}
