using allstarr.Models.Lyrics;

namespace allstarr.Core.Music;

internal static class LyricsQuality
{
    internal static int Rank(LyricsInfo? lyrics) => Rank(RichLyricsMapper.Map(lyrics)?.Format);

    internal static int Rank(string? format) => format switch { "word" => 3, "line" => 2, "plain" => 1, _ => 0 };

    internal static int Maximum(string sourceId) => sourceId switch
    {
        "genius" => 1,
        "spotify" or "lrclib" => 2,
        _ => 3
    };

    internal static bool CanImprove(string sourceId, int rank, int quality, int bestRank) =>
        Maximum(sourceId) > quality || Maximum(sourceId) == quality && rank < bestRank;
}
