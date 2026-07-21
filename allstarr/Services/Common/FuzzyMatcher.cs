namespace allstarr.Services.Common;

/// <summary>
/// Provides fuzzy string matching for search result scoring.
/// OPTIMAL ORDER: 1. Strip decorators → 2. Substring matching → 3. Levenshtein → 4. Greedy assignment
/// </summary>
public static partial class FuzzyMatcher
{
    private const int StackallocLevenshteinLimit = 128;

    [System.Text.RegularExpressions.GeneratedRegex(@"\s*[\(\[]?\s*(feat\.?|ft\.?|with|featuring)\s+[^\)\]]+[\)\]]?", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex FeatDecoratorRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"\s*-\s*from\s+[""']?[^""']+[""']?", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex FromAlbumDecoratorRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"\s*-\s*(remaster|radio edit|single version|album version|extended|original mix)[^\-]*", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex VersionDecoratorRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"\s*[\[\(](remix|remaster|live|acoustic|radio edit|explicit|clean|official|audio|video|lyric)[^\]\)]*[\]\)]", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex TypeDecoratorRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"[^\w\s]")]
    private static partial System.Text.RegularExpressions.Regex PunctuationRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"\s+")]
    private static partial System.Text.RegularExpressions.Regex WhitespaceRegex();

    /// <summary>
    /// STEP 1: Strips common decorators from track titles to improve matching.
    /// Removes: (feat. X), (with Y), (ft. Z), - From "Album", [Remix], etc.
    /// This MUST be done first to avoid systematic noise in matching.
    /// </summary>
    public static string StripDecorators(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var cleaned = title;

        cleaned = FeatDecoratorRegex().Replace(cleaned, "");
        cleaned = FromAlbumDecoratorRegex().Replace(cleaned, "");
        cleaned = VersionDecoratorRegex().Replace(cleaned, "");
        cleaned = TypeDecoratorRegex().Replace(cleaned, "");

        cleaned = cleaned.Trim();

        return cleaned;
    }

    /// <summary>
    /// Calculates similarity score following OPTIMAL ORDER:
    /// 1. Strip decorators (already done by caller)
    /// 2. Substring matching (cheap, high-precision)
    /// 3. Token-based matching (handles word order)
    /// 4. Levenshtein distance (expensive, fuzzy)
    /// Returns score 0-100.
    /// </summary>
    public static int CalculateSimilarity(string query, string target)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(target))
        {
            return 0;
        }

        var queryNorm = NormalizeForMatching(query);
        var targetNorm = NormalizeForMatching(target);

        // STEP 2: SUBSTRING MATCHING (cheap, high-precision)

        // Exact match
        if (queryNorm == targetNorm)
        {
            return 100;
        }

        // One string fully contains the other (substring match)
        // Example: "luther" ⊂ "luther remastered" → instant win
        if (targetNorm.Contains(queryNorm) || queryNorm.Contains(targetNorm))
        {
            return 95;
        }

        // Starts with query
        if (targetNorm.StartsWith(queryNorm) || queryNorm.StartsWith(targetNorm))
        {
            return 90;
        }

        // Contains query as whole word
        if (targetNorm.Contains($" {queryNorm} ") ||
            targetNorm.StartsWith($"{queryNorm} ") ||
            targetNorm.EndsWith($" {queryNorm}") ||
            queryNorm.Contains($" {targetNorm} ") ||
            queryNorm.StartsWith($"{targetNorm} ") ||
            queryNorm.EndsWith($" {targetNorm}"))
        {
            return 85;
        }

        // STEP 3: TOKEN-BASED MATCHING (handles word order)
        var tokens1 = queryNorm.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        var tokens2 = targetNorm.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

        if (tokens1.Length > 0 && tokens2.Length > 0)
        {
            // Calculate how many tokens match (order-independent)
            var matchedTokens = 0.0; // Use double for partial matches
            var usedTokens = new HashSet<int>();

            foreach (var token1 in tokens1)
            {
                for (int i = 0; i < tokens2.Length; i++)
                {
                    if (usedTokens.Contains(i)) continue;

                    var token2 = tokens2[i];

                    // Exact token match
                    if (token1 == token2)
                    {
                        matchedTokens++;
                        usedTokens.Add(i);
                        break;
                    }
                    // Partial token match (one contains the other)
                    else if (token1.Contains(token2) || token2.Contains(token1))
                    {
                        matchedTokens += 0.8; // Partial credit
                        usedTokens.Add(i);
                        break;
                    }
                }
            }

            // Calculate token match percentage
            var maxTokens = Math.Max(tokens1.Length, tokens2.Length);
            var tokenMatchScore = (matchedTokens / maxTokens) * 100.0;

            // If token match is very high (90%+), return it
            if (tokenMatchScore >= 90)
            {
                return (int)Math.Round(tokenMatchScore, MidpointRounding.AwayFromZero);
            }

            // If token match is decent (70%+), use it as a floor for Levenshtein
            if (tokenMatchScore >= 70)
            {
                var levenshteinScore = CalculateLevenshteinScore(queryNorm, targetNorm);
                return (int)Math.Max(tokenMatchScore, levenshteinScore);
            }
        }

        // STEP 4: LEVENSHTEIN DISTANCE (expensive, fuzzy)
        return CalculateLevenshteinScore(queryNorm, targetNorm);
    }

    /// <summary>
    /// Calculates similarity score based on Levenshtein distance.
    /// Returns score 0-75 (reserve 75-100 for substring/token matches).
    /// </summary>
    private static int CalculateLevenshteinScore(string str1, string str2)
    {
        var distance = LevenshteinDistance(str1, str2);
        var maxLength = Math.Max(str1.Length, str2.Length);

        if (maxLength == 0)
        {
            return 100;
        }

        // Normalize distance by length: score = 1 - (distance / max_length)
        var normalizedSimilarity = 1.0 - ((double)distance / maxLength);

        // Convert to 0-75 range (reserve 75-100 for substring/token matches)
        // Using 75 instead of 80 to be slightly stricter
        var score = (int)(normalizedSimilarity * 75);

        return Math.Max(0, score);
    }

    /// <summary>
    /// AGGRESSIVE matching that follows optimal order:
    /// 1. Strip decorators FIRST
    /// 2. Substring matching
    /// 3. Levenshtein distance
    /// Returns the best score.
    /// </summary>
    public static int CalculateSimilarityAggressive(string query, string target)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(target))
        {
            return 0;
        }

        // STEP 1: Strip decorators FIRST (always)
        var queryStripped = StripDecorators(query);
        var targetStripped = StripDecorators(target);

        // STEP 2-3: Substring matching + Levenshtein
        var strippedScore = CalculateSimilarity(queryStripped, targetStripped);

        // Also try without stripping in case decorators are part of the actual title
        var rawScore = CalculateSimilarity(query, target);

        // Return the best score
        return Math.Max(rawScore, strippedScore);
    }

    /// <summary>
    /// Normalizes a string for matching by lowercasing, stripping accents, converting
    /// punctuation to spaces/removing it, and cleaning extra whitespace.
    /// </summary>
    private static string NormalizeForMatching(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = text.ToLowerInvariant().Trim();

        normalized = RemoveDiacritics(normalized);
        normalized = normalized.Replace('-', ' ').Replace('_', ' ');

        normalized = PunctuationRegex().Replace(normalized, "");
        // Collapse internal whitespace sequences to a single space, then trim edge whitespace
        normalized = WhitespaceRegex().Replace(normalized, " ").Trim();

        return normalized;
    }

    /// <summary>
    /// Removes diacritics (accents) from characters.
    /// Example: é -> e, ñ -> n, ü -> u
    /// </summary>
    private static string RemoveDiacritics(string text)
    {
        var normalizedString = text.Normalize(System.Text.NormalizationForm.FormD);
        var stringBuilder = new System.Text.StringBuilder();

        foreach (var c in normalizedString)
        {
            var unicodeCategory = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c);
            if (unicodeCategory != System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                stringBuilder.Append(c);
            }
        }

        return stringBuilder.ToString().Normalize(System.Text.NormalizationForm.FormC);
    }

    /// <summary>
    /// Calculates Levenshtein distance between two strings using a space-optimized
    /// rolling buffer (O(min(m, n))) and stackalloc when strings are under 128 characters.
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

        if (sourceLength < targetLength)
        {
            return LevenshteinDistance(target, source);
        }

        // Allocate rows on stack for typical song lengths (<128 chars) to avoid GC pressure
        Span<int> previousRow = targetLength + 1 <= StackallocLevenshteinLimit
            ? stackalloc int[targetLength + 1]
            : new int[targetLength + 1];

        Span<int> currentRow = targetLength + 1 <= StackallocLevenshteinLimit
            ? stackalloc int[targetLength + 1]
            : new int[targetLength + 1];

        for (var j = 0; j <= targetLength; j++)
        {
            previousRow[j] = j;
        }

        for (var i = 1; i <= sourceLength; i++)
        {
            currentRow[0] = i;
            for (var j = 1; j <= targetLength; j++)
            {
                var cost = target[j - 1] == source[i - 1] ? 0 : 1;
                currentRow[j] = Math.Min(
                    Math.Min(currentRow[j - 1] + 1, previousRow[j] + 1),
                    previousRow[j - 1] + cost);
            }
            // currentRow is the freshly-computed row; copying it into previousRow is
            // safe because the next i reads previousRow before writing currentRow.
            currentRow.CopyTo(previousRow);
        }

        return previousRow[targetLength];
    }

    /// <summary>
    /// Calculates artist match score between Spotify artists and local song artists.
    /// Checks bidirectional matching and penalizes mismatches.
    /// Penalizes if artist counts don't match or if any artist is missing.
    /// Returns score 0-100.
    /// </summary>
    public static double CalculateArtistMatchScore(List<string> spotifyArtists, string songMainArtist, List<string> songContributors)
    {
        if (spotifyArtists.Count == 0 || string.IsNullOrEmpty(songMainArtist))
            return 0;

        // Build list of all song artists (main + contributors)
        var allSongArtists = new List<string> { songMainArtist };
        allSongArtists.AddRange(songContributors);

        // If artist counts differ significantly, penalize
        var countDiff = Math.Abs(spotifyArtists.Count - allSongArtists.Count);
        if (countDiff > 1) // Allow 1 artist difference (sometimes features are listed differently)
            return 0;

        // Check that each Spotify artist has a good match in song artists
        var spotifyScores = new List<double>();
        foreach (var spotifyArtist in spotifyArtists)
        {
            var bestMatch = allSongArtists.Max(songArtist =>
                CalculateSimilarity(spotifyArtist, songArtist));
            spotifyScores.Add(bestMatch);
        }

        // Check that each song artist has a good match in Spotify artists
        var songScores = new List<double>();
        foreach (var songArtist in allSongArtists)
        {
            var bestMatch = spotifyArtists.Max(spotifyArtist =>
                CalculateSimilarity(songArtist, spotifyArtist));
            songScores.Add(bestMatch);
        }

        // Average all scores - this ensures ALL artists must match well
        var allScores = spotifyScores.Concat(songScores);
        var avgScore = allScores.Average();

        // Penalize if any individual artist match is poor (< 70)
        var minScore = allScores.Min();
        if (minScore < 70)
            avgScore *= 0.7; // 30% penalty for poor individual match

        return avgScore;
    }
}
