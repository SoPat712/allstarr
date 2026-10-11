namespace allstarr.Services.Common;

public static class SearchRelevanceRanker
{
    public static double Score(string query, string name, string? artist = null)
    {
        var score = FuzzyMatcher.CalculateSimilarityAggressive(query, name);
        return string.IsNullOrWhiteSpace(artist)
            ? score
            : Math.Max(score, FuzzyMatcher.CalculateSimilarityAggressive(query, $"{name} {artist}"));
    }

    // Stable sorting keeps local results first only on ties, then preserves each source's order.
    public static List<T> Merge<T>(IEnumerable<T> local, IEnumerable<T> external, Func<T, double> score) =>
        local.Concat(external).OrderByDescending(score).ToList();
}
