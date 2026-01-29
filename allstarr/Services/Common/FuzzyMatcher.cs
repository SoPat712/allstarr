namespace allstarr.Services.Common;

/// <summary>
/// Provides fuzzy string matching for search result scoring.
/// </summary>
public static class FuzzyMatcher
{
    /// <summary>
    /// Calculates a similarity score between two strings (0-100).
    /// Higher score means better match.
    /// </summary>
    public static int CalculateSimilarity(string query, string target)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(target))
        {
            return 0;
        }

        var queryLower = query.ToLowerInvariant().Trim();
        var targetLower = target.ToLowerInvariant().Trim();

        // Exact match
        if (queryLower == targetLower)
        {
            return 100;
        }

        // Starts with query
        if (targetLower.StartsWith(queryLower))
        {
            return 90;
        }

        // Contains query as whole word
        if (targetLower.Contains($" {queryLower} ") || 
            targetLower.StartsWith($"{queryLower} ") || 
            targetLower.EndsWith($" {queryLower}"))
        {
            return 80;
        }

        // Contains query anywhere
        if (targetLower.Contains(queryLower))
        {
            return 70;
        }

        // Calculate Levenshtein distance for fuzzy matching
        var distance = LevenshteinDistance(queryLower, targetLower);
        var maxLength = Math.Max(queryLower.Length, targetLower.Length);
        
        if (maxLength == 0)
        {
            return 100;
        }

        // Convert distance to similarity score (0-60 range for fuzzy matches)
        var similarity = (1.0 - (double)distance / maxLength) * 60;
        return (int)Math.Max(0, similarity);
    }

    /// <summary>
    /// Calculates Levenshtein distance between two strings.
    /// </summary>
    private static int LevenshteinDistance(string source, string target)
    {
        if (string.IsNullOrEmpty(source))
        {
            return target?.Length ?? 0;
        }

        if (string.IsNullOrEmpty(target))
        {
            return source.Length;
        }

        var sourceLength = source.Length;
        var targetLength = target.Length;
        var distance = new int[sourceLength + 1, targetLength + 1];

        for (var i = 0; i <= sourceLength; i++)
        {
            distance[i, 0] = i;
        }

        for (var j = 0; j <= targetLength; j++)
        {
            distance[0, j] = j;
        }

        for (var i = 1; i <= sourceLength; i++)
        {
            for (var j = 1; j <= targetLength; j++)
            {
                var cost = target[j - 1] == source[i - 1] ? 0 : 1;
                distance[i, j] = Math.Min(
                    Math.Min(distance[i - 1, j] + 1, distance[i, j - 1] + 1),
                    distance[i - 1, j - 1] + cost);
            }
        }

        return distance[sourceLength, targetLength];
    }
}
