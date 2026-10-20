namespace allstarr.Services.Common;

public static partial class SearchRelevanceRanker
{
    [System.Text.RegularExpressions.GeneratedRegex(@"\b(nightcore|bass\s*boosted|sped\s*up|slowed|reverb|8d|tribute|karaoke)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex UploadVariantRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"(?:[#(\[]|\s[-–—:]\s)([^)\]]+)")]
    private static partial System.Text.RegularExpressions.Regex VersionSegmentRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"\b(remix|live|acoustic|instrumental|karaoke|cover)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex RecordingVariantRegex();

    public static double Score(string query, string name, string? artist = null, string? album = null)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(name)) return 0;
        var queryTokens = Tokens(query);
        var nameTokens = Tokens(name);
        if (queryTokens.Count == 0 || nameTokens.Count == 0) return 0;

        // A title substring alone must not satisfy the artist portion of a combined query.
        var titleCoverage = (double)queryTokens.Count(nameTokens.Contains) / queryTokens.Count;
        double titleScore = FuzzyMatcher.CalculateSimilarityAggressive(query, name);
        if (nameTokens.IsProperSubsetOf(queryTokens)) titleScore *= titleCoverage;
        var combined = Tokens($"{name} {artist}");
        var matched = queryTokens.Count(combined.Contains);
        var coverageScore = 100 * (0.9 * matched / queryTokens.Count + 0.1 * matched / combined.Count);
        double combinedScore = FuzzyMatcher.CalculateSimilarityAggressive(query, $"{name} {artist}");
        if (combined.IsProperSubsetOf(queryTokens)) combinedScore *= (double)matched / queryTokens.Count;
        var score = Math.Max(titleScore, Math.Max(coverageScore, combinedScore));

        var requestedVariants = VariantTags(query, requireDecoration: false);
        var unrequested = VariantTags(name, requireDecoration: true).Except(requestedVariants).Count();
        score = Math.Max(0, score - Math.Min(30, unrequested * 15));
        if (FuzzyMatcher.IsContinuousMixExcerpt(name, album) &&
            !FuzzyMatcher.IsContinuousMixExcerpt(query) && !queryTokens.Contains("mixed"))
            score = Math.Max(0, score - 15);

        // Small scoring differences are comparable; stable merges and provider priority resolve them.
        return Math.Round(score / 5, MidpointRounding.AwayFromZero) * 5;
    }

    private static HashSet<string> Tokens(string text) =>
        FuzzyMatcher.NormalizeForMatching(FuzzyMatcher.StripDecorators(text))
            .Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> VariantTags(string text, bool requireDecoration)
    {
        var tags = UploadVariantRegex().Matches(text)
            .Select(match => Compact(match.Value)).ToHashSet(StringComparer.Ordinal);
        var segments = requireDecoration
            ? VersionSegmentRegex().Matches(text).Select(match => match.Groups[1].Value)
            : [text];
        foreach (var segment in segments)
            foreach (System.Text.RegularExpressions.Match match in RecordingVariantRegex().Matches(segment))
                tags.Add(Compact(match.Value));
        return tags;
    }

    private static string Compact(string text) =>
        string.Concat(text.Where(character => !char.IsWhiteSpace(character))).ToLowerInvariant();

    // Stable sorting keeps local results first only on ties, then preserves each source's order.
    public static List<T> Merge<T>(IEnumerable<T> local, IEnumerable<T> external, Func<T, double> score) =>
        local.Concat(external).OrderByDescending(score).ToList();
}
