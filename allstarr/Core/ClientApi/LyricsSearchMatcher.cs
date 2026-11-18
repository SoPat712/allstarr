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

    internal static double Score(string line, string? next, string query) => new PreparedQuery(query).Score(line, next);

    // Words almost every lyric has; they count a fifth of a word, and a line holding only these is not a match.
    private static readonly HashSet<string> Filler = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "and", "or", "but", "so", "to", "of", "in", "on", "at", "for", "with", "from", "by", "up", "down", "out",
        "i", "im", "me", "my", "you", "youre", "your", "we", "us", "our", "he", "she", "it", "its", "they", "them", "his", "her",
        "is", "am", "are", "was", "were", "be", "been", "do", "dont", "did", "have", "has", "had", "will", "would", "can", "could",
        "just", "like", "oh", "ooh", "yeah", "hey", "la", "na", "no", "not", "all", "that", "this", "what", "when", "where", "how",
        "if", "then", "there", "now", "one", "some", "something", "s", "t", "m", "ll", "re", "ve", "d"
    };

    private static double Weight(string word) => Filler.Contains(word) ? 0.2 : 1;

    /// <summary>The words a line must hold one of to score at all: the search's other words, or all of them when every word is filler.</summary>
    internal static IReadOnlyList<string> CandidateWords(string query)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToArray();
        var content = words.Where(word => !Filler.Contains(word)).ToArray();
        return content.Length > 0 ? content : words;
    }

    internal sealed class PreparedQuery(string query)
    {
        private readonly string[] words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToArray();

        /// <summary>A song whose title words all appear in the search, such as "Walk" for "something you walk", ranks higher.</summary>
        public double TitleBoost(string? title)
        {
            var named = Normalize(title ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(word => !Filler.Contains(word)).ToArray();
            return named.Length > 0 && named.All(word => words.Contains(word, StringComparer.Ordinal)) ? 0.15 : 0;
        }

        public double Score(ReadOnlySpan<char> line, ReadOnlySpan<char> next)
        {
            if (query.Length == 0 || line.Length == 0) return 0;
            for (var offset = 0; offset < line.Length;)
            {
                var found = line[offset..].IndexOf(query[0]);
                if (found < 0) break;
                var start = offset + found;
                offset = start + 1;
                if (start > 0 && line[start - 1] != ' ') continue;
                var length = Math.Min(line.Length - start, query.Length);
                if (!line.Slice(start, length).SequenceEqual(query.AsSpan(0, length))) continue;
                if (length == query.Length)
                {
                    if (start + length == line.Length || line[start + length] == ' ') return 1;
                }
                else if (query[length] == ' ')
                {
                    var tail = query.AsSpan(length + 1);
                    if (next.StartsWith(tail, StringComparison.Ordinal) &&
                        (tail.Length == next.Length || next[tail.Length] == ' ')) return 1;
                }
            }
            var whole = 0;
            var partial = 0d;
            var content = false;
            foreach (var word in words)
            {
                var prefix = false;
                for (var offset = 0; offset < line.Length;)
                {
                    var found = line[offset..].IndexOf(word, StringComparison.Ordinal);
                    if (found < 0) break;
                    var start = offset + found;
                    offset = start + 1;
                    if (start > 0 && line[start - 1] != ' ') continue;
                    prefix = true;
                    if (start + word.Length == line.Length || line[start + word.Length] == ' ')
                    {
                        whole++;
                        break;
                    }
                }
                if (!prefix) continue;
                partial += Weight(word);
                content |= !Filler.Contains(word);
            }
            if (whole == words.Length) return 0.8;
            var total = words.Sum(Weight);
            var anyContent = words.Any(word => !Filler.Contains(word));
            return partial == 0 || anyContent && !content ? 0 : 0.2 + 0.5 * partial / total;
        }
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
