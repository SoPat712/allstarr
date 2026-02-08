namespace allstarr.Services.Common;

/// <summary>
/// Provides fuzzy string matching for search result scoring.
/// OPTIMAL ORDER: 1. Strip decorators → 2. Substring matching → 3. Levenshtein → 4. Greedy assignment
/// </summary>
public static class FuzzyMatcher
{
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
        
        // Remove (feat. ...), (ft. ...), (with ...), (featuring ...)
        cleaned = System.Text.RegularExpressions.Regex.Replace(
            cleaned, 
            @"\s*[\(\[]?\s*(feat\.?|ft\.?|with|featuring)\s+[^\)\]]+[\)\]]?", 
            "", 
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        
        // Remove - From "Album Name" or - From Album Name
        cleaned = System.Text.RegularExpressions.Regex.Replace(
            cleaned, 
            @"\s*-\s*from\s+[""']?[^""']+[""']?", 
            "", 
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        
        // Remove - Remastered, - Radio Edit, etc.
        cleaned = System.Text.RegularExpressions.Regex.Replace(
            cleaned, 
            @"\s*-\s*(remaster|radio edit|single version|album version|extended|original mix)[^\-]*", 
            "", 
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        
        // Remove [Remix], [Remaster], [Live], [Explicit], etc.
        cleaned = System.Text.RegularExpressions.Regex.Replace(
            cleaned, 
            @"\s*[\[\(](remix|remaster|live|acoustic|radio edit|explicit|clean|official|audio|video|lyric)[^\]\)]*[\]\)]", 
            "", 
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        
        // Remove trailing/leading whitespace and normalize
        cleaned = cleaned.Trim();
        
        return cleaned;
    }
    
    /// <summary>
    /// Calculates similarity score following OPTIMAL ORDER:
    /// 1. Strip decorators (already done by caller)
    /// 2. Substring matching (cheap, high-precision)
    /// 3. Levenshtein distance (expensive, fuzzy)
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

        // STEP 3: LEVENSHTEIN DISTANCE (expensive, fuzzy)
        // Only use this for candidates that survived substring checks
        
        var distance = LevenshteinDistance(queryNorm, targetNorm);
        var maxLength = Math.Max(queryNorm.Length, targetNorm.Length);
        
        if (maxLength == 0)
        {
            return 100;
        }

        // Normalize distance by length: score = 1 - (distance / max_length)
        var normalizedSimilarity = 1.0 - ((double)distance / maxLength);
        
        // Convert to 0-80 range (reserve 80-100 for substring matches)
        var score = (int)(normalizedSimilarity * 80);
        
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
    /// Normalizes a string for matching by:
    /// - Converting to lowercase
    /// - Normalizing apostrophes (', ', ') to standard '
    /// - Removing extra whitespace
    /// </summary>
    private static string NormalizeForMatching(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }
        
        var normalized = text.ToLowerInvariant().Trim();
        
        // Normalize different apostrophe types to standard apostrophe
        normalized = normalized
            .Replace("\u2019", "'")  // Right single quotation mark (')
            .Replace("\u2018", "'")  // Left single quotation mark (')
            .Replace("`", "'")       // Grave accent
            .Replace("\u00B4", "'"); // Acute accent (´)
        
        // Normalize whitespace
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"\s+", " ");
        
        return normalized;
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
