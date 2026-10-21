using allstarr.Models.Domain;

namespace allstarr.Services.Common;

public static partial class SearchRelevanceRanker
{
    [System.Text.RegularExpressions.GeneratedRegex(@"(?:[#(\[]|\s[-–—:]\s)([^)\]]+)")]
    private static partial System.Text.RegularExpressions.Regex VersionSegmentRegex();

    public static double Score(string query, string name, string? artist = null, string? album = null,
        string? albumArtist = null)
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

        // Only complete main-artist credits supply artist context; title annotations cannot supply it.
        var mainArtists = new[] { artist, albumArtist }
            .Select(value => FuzzyMatcher.SplitCredits(value).FirstOrDefault())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => Tokens(value!));
        var artistTitleCoverage = mainArtists.Where(tokens => tokens.Count > 0 && tokens.IsProperSubsetOf(queryTokens))
            .Select(tokens => queryTokens.Except(tokens).ToArray())
            .Select(tokens => (double)tokens.Count(nameTokens.Contains) / tokens.Length)
            .DefaultIfEmpty(0).Max();
        var matchesMainArtist = artistTitleCoverage > 0;
        score = Math.Max(score, 100 * artistTitleCoverage) + 30 * artistTitleCoverage;

        var requestedVariants = VariantTags(query, requireDecoration: false);
        var variants = VariantTags(name, requireDecoration: true);
        if (!FuzzyMatcher.HasOtherRemixer(name, artist, albumArtist)) variants.Remove("remix");
        var unrequested = variants.Except(requestedVariants).Count();
        score = Math.Max(0, score - Math.Min(30, unrequested * 15));
        var special = FuzzyMatcher.SpecialEditionTags(name).Where(tag =>
            !tag.StartsWith("remix:", StringComparison.Ordinal) &&
            (tag != "remix" || FuzzyMatcher.HasOtherRemixer(name, artist, albumArtist)));
        if (special.Select(Compact).Where(variants.Contains).Except(requestedVariants).Any()) score = Math.Max(0, score - 15);
        score += Math.Min(15, variants.Intersect(requestedVariants).Count() * 10);
        var recordingText = string.Join(" ", name, artist, albumArtist, album);
        if (FuzzyMatcher.BackingRecordingTags(recordingText).Except(FuzzyMatcher.BackingRecordingTags(query)).Any() ||
            FuzzyMatcher.HasCoverRecordingLabel(recordingText) &&
            !FuzzyMatcher.HasCoverRecordingLabel(query))
            score = Math.Max(0, score - 25);
        if (!matchesMainArtist && FuzzyMatcher.NormalizeForMatching(artist ?? "") == "various artists")
            score = Math.Max(0, score - 15);
        if (FuzzyMatcher.IsContinuousMixExcerpt(name, album) &&
            !FuzzyMatcher.IsContinuousMixExcerpt(query) && !queryTokens.Contains("mixed"))
            score = Math.Max(0, score - 15);

        // Small scoring differences are comparable; stable merges and provider priority resolve them.
        return Math.Round(score / 5, MidpointRounding.AwayFromZero) * 5;
    }

    public static double ScoreSong(string query, Song song, bool hasKnownDurationVersion = false) =>
        ScoreSong(query, song.Title, song.Artist, song.Album, song.AlbumArtist,
            song.PreciseDuration?.TotalSeconds ?? song.Duration,
            song.CanonicalRecordingId is { } recording && recording != Guid.Empty ||
            song.Isrc?.Count(char.IsLetterOrDigit) == 12, hasKnownDurationVersion);

    public static double ScoreSong(string query, string title, string? artist, string? album = null,
        string? albumArtist = null, double? durationSeconds = null, bool provenRecording = false,
        bool hasKnownDurationVersion = false)
    {
        var score = Score(query, title, artist, album, albumArtist);
        if (durationSeconds is > 0 and < 60) score = Math.Max(0, score - 30);
        else if (durationSeconds is not > 0 && hasKnownDurationVersion) score = Math.Max(0, score - 25);
        // Recording evidence breaks relevance ties without overtaking the next relevance band.
        return score + (provenRecording && score > 0 ? 1 : 0);
    }

    public static string RecordingTitle(string title, string? artist = null, string? albumArtist = null)
    {
        var separator = title.IndexOf(" - ", StringComparison.Ordinal);
        if (separator > 0)
        {
            var prefix = FuzzyMatcher.NormalizeForMatching(title[..separator]);
            if (new[] { artist, albumArtist }.Any(value =>
                    FuzzyMatcher.NormalizeForMatching(FuzzyMatcher.SplitCredits(value).FirstOrDefault() ?? "") == prefix))
                title = title[(separator + 3)..];
        }
        return FuzzyMatcher.NormalizeForMatching(FuzzyMatcher.SearchQuery(title));
    }

    private static HashSet<string> Tokens(string text) =>
        FuzzyMatcher.NormalizeForMatching(FuzzyMatcher.StripDecorators(text))
            .Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> VariantTags(string text, bool requireDecoration)
    {
        var tags = FuzzyMatcher.SpecialEditionTags(text)
            .Where(tag => tag is not ("remix" or "cover") && !tag.StartsWith("remix:", StringComparison.Ordinal))
            .Select(Compact).ToHashSet(StringComparer.Ordinal);
        var segments = requireDecoration
            ? VersionSegmentRegex().Matches(text).Select(match => match.Groups[1].Value)
            : [text];
        foreach (var segment in segments)
        {
            tags.UnionWith(FuzzyMatcher.SemanticVersionTags(segment)
                .Where(tag => tag != "original mix" && !tag.StartsWith("remix:", StringComparison.Ordinal)).Select(Compact));
        }
        return tags;
    }

    private static string Compact(string text) =>
        string.Concat(text.Where(character => !char.IsWhiteSpace(character))).ToLowerInvariant();

    // Stable sorting keeps local results first only on ties, then preserves each source's order.
    public static List<T> Merge<T>(IEnumerable<T> local, IEnumerable<T> external, Func<T, double> score) =>
        local.Concat(external).OrderByDescending(score).ToList();
}
