using System.Globalization;
using System.Text;

namespace allstarr.Core.ClientApi;

public static class LyricsSearchMatcher
{
    public sealed record MatchResult(double Score, IReadOnlyList<LyricsSearchHighlight> Highlights);
    private sealed record Normalized(string Text, int[] Starts, int[] Ends);

    public static string Normalize(string text) => Map(text).Text;

    public static MatchResult Match(string line, string? next, string query)
    {
        var mapped = Map(line);
        var normalized = Normalize(query);
        var score = Score(mapped.Text, next == null ? null : Normalize(next), normalized);
        if (score == 0) return new(0, []);
        var ranges = new List<(int Start, int End)>();
        var joined = mapped.Text + " " + (next == null ? "" : Normalize(next));
        var phrase = (" " + joined + " ").IndexOf(" " + normalized + " ", StringComparison.Ordinal);
        if (score == 1 && phrase >= 0 && phrase < mapped.Text.Length)
            Add(phrase, Math.Min(mapped.Text.Length, phrase + normalized.Length));
        else
            foreach (var word in normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal))
            {
                var start = (" " + mapped.Text + " ").IndexOf(" " + word + (score == 0.8 ? " " : ""), StringComparison.Ordinal);
                if (start >= 0) Add(start, start + word.Length);
            }
        var merged = new List<LyricsSearchHighlight>();
        foreach (var range in ranges.OrderBy(range => range.Start))
        {
            if (merged.Count > 0 && range.Start <= merged[^1].Start + merged[^1].Length)
            {
                var previous = merged[^1];
                merged[^1] = new(previous.Start, Math.Max(previous.Start + previous.Length, range.End) - previous.Start);
            }
            else merged.Add(new(range.Start, range.End - range.Start));
        }
        return new(score, merged);

        void Add(int start, int end)
        {
            if (end > start && start < mapped.Starts.Length)
                ranges.Add((mapped.Starts[start], mapped.Ends[end - 1]));
        }
    }

    internal static double Score(string line, string? next, string query)
    {
        if (query.Length == 0 || line.Length == 0) return 0;
        var joined = line + " " + next;
        var phrase = (" " + joined + " ").IndexOf(" " + query + " ", StringComparison.Ordinal);
        if (phrase >= 0 && phrase < line.Length) return 1;
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToArray();
        var whole = words.Count(word => (" " + line + " ").Contains(" " + word + " ", StringComparison.Ordinal));
        if (whole == words.Length) return 0.8;
        var partial = words.Count(word => (" " + line).Contains(" " + word, StringComparison.Ordinal));
        return partial == 0 ? 0 : 0.2 + 0.5 * partial / words.Length;
    }

    private static Normalized Map(string text)
    {
        var output = new StringBuilder();
        var starts = new List<int>();
        var ends = new List<int>();
        var pendingSpace = false;
        var position = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var start = position;
            position += rune.Utf16SequenceLength;
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
            {
                if (ends.Count > 0) ends[^1] = position;
                continue;
            }
            if (rune.Value is '\'' or 0x2019 or 0x2018 or 0x02bc) continue;
            if (!Rune.IsLetterOrDigit(rune)) { pendingSpace = output.Length > 0; continue; }
            if (pendingSpace) { output.Append(' '); starts.Add(start); ends.Add(start); pendingSpace = false; }
            foreach (var component in rune.ToString().Normalize(NormalizationForm.FormD).EnumerateRunes())
            {
                if (!Rune.IsLetterOrDigit(component)) continue;
                var value = Rune.ToLowerInvariant(Rune.ToUpperInvariant(component)).ToString();
                output.Append(value);
                for (var index = 0; index < value.Length; index++) { starts.Add(start); ends.Add(position); }
            }
        }
        return new(output.ToString(), starts.ToArray(), ends.ToArray());
    }
}
