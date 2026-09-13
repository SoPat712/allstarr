namespace allstarr.Services.Common;

public static partial class FuzzyMatcher
{
    private const int StackallocLevenshteinLimit = 128;

    [System.Text.RegularExpressions.GeneratedRegex(@"\s*[\(\[]?\s*(feat\.?|ft\.?|with|featuring)\s+[^\)\]]+[\)\]]?", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex FeatDecoratorRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"\s*[\(\[][^\)\]]*\b(feat\.?|ft\.?|with|featuring)\s+[^\)\]]+[\)\]]", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex ParenthesizedCreditsRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"\s*-\s*from\s+[""']?[^""']+[""']?", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex FromAlbumDecoratorRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"\s*-\s*((?:\d{4}\s+)?remaster(?:ed)?(?:\s+\d{4})?|single version|album version|bonus track|bonus|deluxe edition)[^\-]*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex VersionDecoratorRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"\s*[\[\(]((?:\d{4}\s+)?remaster(?:ed)?(?:\s+\d{4})?|single version|album version|bonus(?: track)?|deluxe(?: edition)?|official|audio|video|lyric)[^\]\)]*[\]\)]", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex TypeDecoratorRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"\b(live|acoustic|instrumental|stripped|remix|radio edit|extended(?: mix)?|original mix|clean|explicit)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex SemanticVersionRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"[^\w\s]")]
    private static partial System.Text.RegularExpressions.Regex PunctuationRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"\s+")]
    private static partial System.Text.RegularExpressions.Regex WhitespaceRegex();

    public static string StripDecorators(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var cleaned = title;

        cleaned = ParenthesizedCreditsRegex().Replace(cleaned, "");
        cleaned = FeatDecoratorRegex().Replace(cleaned, "");
        cleaned = FromAlbumDecoratorRegex().Replace(cleaned, "");
        cleaned = VersionDecoratorRegex().Replace(cleaned, "");
        cleaned = TypeDecoratorRegex().Replace(cleaned, "");

        cleaned = cleaned.Trim();

        return cleaned;
    }

    public static string SearchQuery(string title)
    {
        var cleaned = StripDecorators(title);
        var dash = cleaned.IndexOf(" - ", StringComparison.Ordinal);
        var bracket = cleaned.IndexOfAny(['(', '[']);
        var end = Math.Min(dash < 0 ? cleaned.Length : dash, bracket < 0 ? cleaned.Length : bracket);
        var query = cleaned[..end].Trim();
        return query.Length >= 2 ? query : cleaned;
    }

    public static string SearchQuery(string title, string? artist)
    {
        var primaryArtist = artist?.Split(',', 2, StringSplitOptions.TrimEntries)[0];
        return string.Join(' ', new[] { SearchQuery(title), primaryArtist }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    public static int CalculateSimilarity(string query, string target)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(target))
        {
            return 0;
        }

        var queryNorm = NormalizeForMatching(query);
        var targetNorm = NormalizeForMatching(target);

        if (queryNorm == targetNorm)
        {
            return 100;
        }

        if (targetNorm.Contains(queryNorm) || queryNorm.Contains(targetNorm))
        {
            return 95;
        }

        if (targetNorm.StartsWith(queryNorm) || queryNorm.StartsWith(targetNorm))
        {
            return 90;
        }

        if (targetNorm.Contains($" {queryNorm} ") ||
            targetNorm.StartsWith($"{queryNorm} ") ||
            targetNorm.EndsWith($" {queryNorm}") ||
            queryNorm.Contains($" {targetNorm} ") ||
            queryNorm.StartsWith($"{targetNorm} ") ||
            queryNorm.EndsWith($" {targetNorm}"))
        {
            return 85;
        }

        var tokens1 = queryNorm.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var tokens2 = targetNorm.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (tokens1.Length > 0 && tokens2.Length > 0)
        {
            var matchedTokens = 0.0;
            var usedTokens = new HashSet<int>();

            foreach (var token1 in tokens1)
            {
                for (int i = 0; i < tokens2.Length; i++)
                {
                    if (usedTokens.Contains(i)) continue;

                    var token2 = tokens2[i];

                    if (token1 == token2)
                    {
                        matchedTokens++;
                        usedTokens.Add(i);
                        break;
                    }
                    else if (token1.Contains(token2) || token2.Contains(token1))
                    {
                        matchedTokens += 0.8;
                        usedTokens.Add(i);
                        break;
                    }
                }
            }

            var maxTokens = Math.Max(tokens1.Length, tokens2.Length);
            var tokenMatchScore = (matchedTokens / maxTokens) * 100.0;

            if (tokenMatchScore >= 90)
            {
                return (int)Math.Round(tokenMatchScore, MidpointRounding.AwayFromZero);
            }

            if (tokenMatchScore >= 70)
            {
                var levenshteinScore = CalculateLevenshteinScore(queryNorm, targetNorm);
                return (int)Math.Max(tokenMatchScore, levenshteinScore);
            }
        }

        return CalculateLevenshteinScore(queryNorm, targetNorm);
    }

    private static int CalculateLevenshteinScore(string str1, string str2)
    {
        var distance = LevenshteinDistance(str1, str2);
        var maxLength = Math.Max(str1.Length, str2.Length);

        if (maxLength == 0)
        {
            return 100;
        }

        var normalizedSimilarity = 1.0 - ((double)distance / maxLength);
        var score = (int)(normalizedSimilarity * 75);

        return Math.Max(0, score);
    }

    public static int CalculateSimilarityAggressive(string query, string target)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(target))
        {
            return 0;
        }

        return Math.Max(
            CalculateSimilarity(query, target),
            CalculateSimilarity(StripDecorators(query), StripDecorators(target)));
    }

    public static string NormalizeForMatching(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = text.ToLowerInvariant().Trim();

        normalized = RemoveDiacritics(normalized);
        normalized = normalized.Replace('$', 's').Replace('-', ' ').Replace('_', ' ');

        normalized = PunctuationRegex().Replace(normalized, "");
        normalized = WhitespaceRegex().Replace(normalized, " ").Trim();
        normalized = string.Join(' ', normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(FoldLookalikeWord));

        return normalized;
    }

    private static string FoldLookalikeWord(string word)
    {
        // Limit lookalike folding to mixed words to avoid changing ordinary titles.
        if (word.Length < 4 || !word.Any(char.IsLetter) || !word.Any(char.IsDigit))
            return word;
        return string.Concat(word.Select(character => character switch
        {
            '0' => 'o',
            '1' => 'i',
            '3' => 'e',
            '4' => 'a',
            '5' => 's',
            '7' => 't',
            _ => character
        }));
    }

    public static IReadOnlySet<string> SemanticVersionTags(string? title) =>
        string.IsNullOrWhiteSpace(title)
            ? new HashSet<string>(StringComparer.Ordinal)
            : SemanticVersionRegex().Matches(RemoveDiacritics(title).ToLowerInvariant())
                .Select(match => WhitespaceRegex().Replace(match.Value, " ").Trim())
                .ToHashSet(StringComparer.Ordinal);

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
            currentRow.CopyTo(previousRow);
        }

        return previousRow[targetLength];
    }

    public static double CalculateArtistMatchScore(List<string> spotifyArtists, string songMainArtist, List<string> songContributors)
    {
        if (spotifyArtists.Count == 0 || string.IsNullOrEmpty(songMainArtist))
            return 0;

        var allSongArtists = new List<string> { songMainArtist };
        allSongArtists.AddRange(songContributors);

        var countDiff = Math.Abs(spotifyArtists.Count - allSongArtists.Count);
        if (countDiff > 1)
            return 0;

        var spotifyScores = new List<double>();
        foreach (var spotifyArtist in spotifyArtists)
        {
            var bestMatch = allSongArtists.Max(songArtist =>
                CalculateSimilarity(spotifyArtist, songArtist));
            spotifyScores.Add(bestMatch);
        }

        var songScores = new List<double>();
        foreach (var songArtist in allSongArtists)
        {
            var bestMatch = spotifyArtists.Max(spotifyArtist =>
                CalculateSimilarity(songArtist, spotifyArtist));
            songScores.Add(bestMatch);
        }

        var allScores = spotifyScores.Concat(songScores);
        var avgScore = allScores.Average();

        var minScore = allScores.Min();
        if (minScore < 70)
            avgScore *= 0.7;

        return avgScore;
    }
}
