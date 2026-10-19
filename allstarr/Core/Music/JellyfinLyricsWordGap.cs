using System.Text.RegularExpressions;

namespace allstarr.Core.Music;

public static partial class JellyfinLyricsWordGap
{
    // Approach from SpotiFLAC-Module-Version (MIT). Braille blank survives Jellyfin's per-word Trim().
    public static string Apply(string lyrics) => SeparatorRegex().Replace(lyrics, match =>
    {
        var word = match.Groups[2].Value;
        var trimmed = word.TrimEnd();
        return trimmed.Length > 0 && trimmed.Length != word.Length && !trimmed.EndsWith('\u2800')
            ? match.Groups[1].Value + trimmed + '\u2800'
            : match.Value;
    });

    [GeneratedRegex(@"(\[\d{1,4}:[0-5]?\d(?:[.:]\d{1,3})?\]|<\d{1,4}:[0-5]?\d(?:[.:]\d{1,3})?>)([^\r\n<>]*)(?=<\d{1,4}:[0-5]?\d(?:[.:]\d{1,3})?>[^\r\n<>]*[^\s<>])")]
    private static partial Regex SeparatorRegex();
}
