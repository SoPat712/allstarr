namespace allstarr.Services.Common;

public static partial class FuzzyMatcher
{
    public static string StripBestVersionPrefix(string title) => title.TrimStart().TrimStart('⭐', '\uFE0F').TrimStart();

    private const int StackallocLevenshteinLimit = 128;

    [System.Text.RegularExpressions.GeneratedRegex(@"(?:\s*[\(\[]\s*|(?<=\S)\s+(?:[-–—]\s*)?)\b(feat\.?|ft\.?|with|featuring)\s+[^\)\]]+[\)\]]?", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex FeatDecoratorRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"\s*[\(\[][^\)\]]*\b(feat\.?|ft\.?|with|featuring)\s+[^\)\]]+[\)\]]", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex ParenthesizedCreditsRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"\s*(?:[-–—]\s*|[\(\[]\s*)from\s+(?:[""“‘'][^""”’']+[""”’']|[^\(\)\[\]]+?)(?:[\)\]]|(?=\s*[\(\[]|$))", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex FromAlbumDecoratorRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"\s*(?:[-–—]\s*|[\(\[]\s*)from\s+(?:the\s+)?(?:animated\s+)?(?:film|movie|series)\s+[^\)\]]+[\)\]]?", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex SeriesDecoratorRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"\s*-\s*((?:\d{4}\s+)?remaster(?:ed)?(?:\s+\d{4})?|single version|album version|bonus track|bonus|deluxe edition)[^\-]*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex VersionDecoratorRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"\s*[\[\(]((?:\d{4}\s+)?remaster(?:ed)?(?:\s+\d{4})?|single version|album version|bonus(?: track)?|deluxe(?: edition)?|official|audio|video|lyric)\b[^\]\)]*[\]\)]", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex TypeDecoratorRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"\b(live|acoustic|instrumental|stripped|remix|radio edit|extended(?:[ -]+(?:mix|version))?|club[ -]+mix|original mix|clean|explicit|sped[ -]+up|slowed(?:[ -]+down)?|karaoke|drumless(?:[ -]+edition)?|a[ -]?cap{1,2}ella|nightcore|reverb|8d|bass[ -]*boosted|tribute|cover)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex SemanticVersionRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"[\(\[]([^\)\]]+)[\)\]]")]
    private static partial System.Text.RegularExpressions.Regex BracketedTitlePartRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"\s+[-–—]\s+([^\(\)\[\]]+?)(?=\s+[-–—]\s+|\s*[\(\[]|$)")]
    private static partial System.Text.RegularExpressions.Regex DashTitlePartRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"\s+[-–—]\s+(from\b[^\(\)\[\]]+?)(?=\s*[\(\[]|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex FromDashTitlePartRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"\b(?:dj[\s-]+mix|mixed\s+by|continuous[\s-]+mix)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex ContinuousMixRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"\bmixed\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex MixedRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"\s+mixed$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex MixedSuffixRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"\b(karaoke|instrumental|originally (?:performed )?by|in the style of|backing track|sing[- ]?along)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex BackingRecordingRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"\b(?:tribute|made famous by|covers?|lullaby|music[ -]+box|bossa[ -]+nova[ -]+version|8[ -]?bit(?:[ -]+version)?)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex CoverRecordingRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"\b(?:clean|edited|radio[\s-]+edit|censored)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex CleanRecordingRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"^\s*(?:feat(?:uring)?\.?|ft\.?|with)\s+", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex CreditPrefixRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"(?:[\(\[](?<label>[^\(\)\[\]]*?\bremix)\b|\s+[-–—]\s+(?<label>(?:(?!\s+[-–—]\s+)[^\(\)\[\]])*?\bremix)\b)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex RemixLabelRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"(?:[\(\[](?<label>[^\(\)\[\]]*?\b(?:mix|edit))\b|\s+[-–—]\s+(?<label>(?:(?!\s+[-–—]\s+)[^\(\)\[\]])*?\b(?:mix|edit))\b)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex MixLabelRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"[^\w\s]")]
    private static partial System.Text.RegularExpressions.Regex PunctuationRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"\s+")]
    private static partial System.Text.RegularExpressions.Regex WhitespaceRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"(?:\s+[-–—]\s*(?:single|e\.?p\.?)|\s*\(\s*(?:single|e\.?p\.?)\s*\)|\s*\[\s*(?:single|e\.?p\.?)\s*\])\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex AlbumFormatSuffixRegex();

    public static string NormalizeAlbumForMatching(string? album) =>
        NormalizeForMatching(StripAlbumFormatSuffix(album));

    private static string StripAlbumFormatSuffix(string? album) =>
        AlbumFormatSuffixRegex().Replace(album ?? string.Empty, string.Empty);

    [System.Text.RegularExpressions.GeneratedRegex(@"\b(?:ao vivo|en vivo|en directo)\b|\blive\s+(?:at|from)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex LocalizedLiveRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"\b(?:demo|mix|edit)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex AdditionalRecordingVersionRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"\s*[\(\[](?:(?:\d+(?:st|nd|rd|th)\s+)?anniversary|expanded|deluxe)[^\)\]]*[\)\]]|\s+[-–—]\s+(?:(?:\d+(?:st|nd|rd|th)\s+)?anniversary|expanded|deluxe)\b.*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex EditionDecoratorRegex();

    public static IReadOnlySet<string> RecordingVersionTags(string? text)
    {
        var tags = SemanticVersionTags(text).ToHashSet(StringComparer.Ordinal);
        var normalized = RemoveDiacritics(text ?? string.Empty).ToLowerInvariant();
        tags.UnionWith(AdditionalRecordingVersionRegex().Matches(normalized).Select(match => match.Value));
        if (LocalizedLiveRegex().IsMatch(normalized))
            tags.Add("live");
        tags.UnionWith(MixLabelRegex().Matches(normalized)
            .Select(match => "mix:" + NormalizeForMatching(match.Groups["label"].Value)));
        return tags;
    }

    private static IEnumerable<string> AlbumRecordingTags(string? album) =>
        BracketedTitlePartRegex().Matches(album ?? string.Empty)
            .Concat(DashTitlePartRegex().Matches(album ?? string.Empty))
            .SelectMany(match => RecordingVersionTags("(" + match.Groups[1].Value + ")")
                .Concat(LocalizedLiveRegex().IsMatch(match.Groups[1].Value) ? new[] { "live" } : []))
            .Concat(LocalizedLiveRegex().IsMatch(album ?? string.Empty) ? new[] { "live" } : [])
            .Where(tag => tag is not ("clean" or "explicit"));

    internal static bool? AlbumExplicitness(string? album)
    {
        var labels = BracketedTitlePartRegex().Matches(album ?? string.Empty)
            .Concat(DashTitlePartRegex().Matches(album ?? string.Empty))
            .SelectMany(match => SemanticVersionTags(match.Groups[1].Value)).ToHashSet(StringComparer.Ordinal);
        return labels.Contains("clean") == labels.Contains("explicit") ? null : labels.Contains("explicit");
    }

    private static bool RequiresVocalArrangementConfirmation(string tag) =>
        tag is "instrumental" or "karaoke" or "acappella";

    public static bool RecordingVersionsMatch(string sourceTitle, string? sourceAlbum, string candidateTitle,
        string? candidateAlbum, bool allowSourceAlbumOnlyMarkers = false,
        bool allowVocalSourceAlbumMarkers = false)
    {
        var titleTags = RecordingVersionTags(sourceTitle);
        var candidateTags = RecordingVersionTags(candidateTitle).Concat(AlbumRecordingTags(candidateAlbum))
            .ToHashSet(StringComparer.Ordinal);
        var albumTags = AlbumRecordingTags(sourceAlbum);
        return titleTags.Concat(albumTags
                .Where(tag => !allowVocalSourceAlbumMarkers ||
                    tag is not ("instrumental" or "karaoke") ||
                    titleTags.Contains(tag) || candidateTags.Contains(tag))
                .Where(tag => !allowSourceAlbumOnlyMarkers ||
                    RequiresVocalArrangementConfirmation(tag) || candidateTags.Contains(tag)))
            .ToHashSet(StringComparer.Ordinal).SetEquals(candidateTags);
    }

    internal static bool SourceAlbumRequiresConfirmation(string sourceTitle, string? sourceAlbum, string candidateTitle,
        string? candidateAlbum, bool allowSourceAlbumOnlyMarkers,
        bool allowVocalSourceAlbumMarkers = false)
    {
        var titleTags = RecordingVersionTags(sourceTitle);
        var candidateTags = RecordingVersionTags(candidateTitle).Concat(AlbumRecordingTags(candidateAlbum))
            .ToHashSet(StringComparer.Ordinal);
        var albumTags = AlbumRecordingTags(sourceAlbum).ToArray();
        var missing = albumTags.Except(titleTags).Except(candidateTags)
            .Where(RequiresVocalArrangementConfirmation)
            .Where(tag => !allowVocalSourceAlbumMarkers || tag is not ("instrumental" or "karaoke"))
            .ToArray();
        return missing.Length > 0 && titleTags.Concat(albumTags.Except(missing)
                .Where(tag => !allowSourceAlbumOnlyMarkers || candidateTags.Contains(tag)))
            .ToHashSet(StringComparer.Ordinal).SetEquals(candidateTags);
    }

    internal static bool SourceAlbumMarkersMatch(string sourceTitle, string? sourceAlbum, string? candidateAlbum)
    {
        var markers = AlbumRecordingTags(sourceAlbum).Except(RecordingVersionTags(sourceTitle)).ToArray();
        return markers.Length > 0 && !markers.Except(AlbumRecordingTags(candidateAlbum)).Any();
    }

    public static bool IdentifiersEqual(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) &&
        !string.IsNullOrWhiteSpace(right) &&
        left.Replace("-", string.Empty, StringComparison.Ordinal)
            .Equals(right.Replace("-", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<string> SplitCredits(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        return System.Text.RegularExpressions.Regex.Split(
                value,
                @"\s*(?:,|&|;|\band\b|\bx\b|\bfeat(?:uring)?\b\.?|\bft\b\.?|\bwith\b)\s*",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                System.Text.RegularExpressions.RegexOptions.CultureInvariant)
            .Select(artist => artist.Trim())
            .Where(artist => artist.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string StripDecorators(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var cleaned = StripBestVersionPrefix(title);

        cleaned = ParenthesizedCreditsRegex().Replace(cleaned, "");
        cleaned = FeatDecoratorRegex().Replace(cleaned, "");
        cleaned = FromAlbumDecoratorRegex().Replace(cleaned, "");
        cleaned = VersionDecoratorRegex().Replace(cleaned, "");
        cleaned = TypeDecoratorRegex().Replace(cleaned, "");

        cleaned = cleaned.Trim();

        return cleaned;
    }

    public static string StripDecorators(string title, string? creditedArtists, bool allowTitleFeatures = false)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;
        var artists = SplitCredits(creditedArtists)
            .Select(NormalizeForMatching)
            .ToHashSet(StringComparer.Ordinal);
        if (allowTitleFeatures || artists.Count == 1 || SeriesDecoratorRegex().IsMatch(title)) artists.UnionWith(TitleFeatureCredits(title));

        bool IsOwnCredit(string value)
        {
            if (SemanticVersionRegex().IsMatch(value)) return false;
            var credits = SplitCredits(CreditPrefixRegex().Replace(value.Trim(), ""));
            return credits.Count > 0 && credits.All(credit => artists.Contains(NormalizeForMatching(credit)));
        }

        var cleaned = BracketedTitlePartRegex().Replace(
            SeriesDecoratorRegex().Replace(StripBestVersionPrefix(title), " "),
            match => IsOwnCredit(match.Groups[1].Value) ? " " : match.Value);
        cleaned = DashTitlePartRegex().Replace(cleaned,
            match => IsOwnCredit(match.Groups[1].Value) ? " " : match.Value);
        cleaned = FeatDecoratorRegex().Replace(cleaned,
            match => IsOwnCredit(match.Value.Trim(' ', '(', ')', '[', ']')) ? " " : match.Value);
        cleaned = FromAlbumDecoratorRegex().Replace(cleaned, "");
        cleaned = VersionDecoratorRegex().Replace(cleaned, "");
        cleaned = TypeDecoratorRegex().Replace(cleaned, "");
        cleaned = EditionDecoratorRegex().Replace(cleaned, "");
        return cleaned.Trim();
    }

    public static string NormalizeTitleForMatching(
        string title, string? creditedArtists, string? additionalArtists = null) =>
        NormalizeForMatching(StripDecorators(title, string.Join(", ", creditedArtists, additionalArtists),
            CanCompareTitleFeatures(creditedArtists, additionalArtists)));

    private static bool CanCompareTitleFeatures(string? left, string? right)
    {
        var leftArtists = SplitCredits(left);
        var rightArtists = SplitCredits(right);
        var primary = NormalizeForMatching(leftArtists.FirstOrDefault() ?? "");
        return (leftArtists.Count == 1 || rightArtists.Count == 1) && primary.Length > 0 &&
               primary == NormalizeForMatching(rightArtists.FirstOrDefault() ?? "");
    }

    public static bool IsContinuousMixExcerpt(string? title, string? album = null) =>
        ContinuousMixRegex().IsMatch(album ?? string.Empty) ||
        ContinuousMixRegex().IsMatch(title ?? string.Empty) ||
        MixedSuffixRegex().IsMatch(title ?? string.Empty) ||
        BracketedTitlePartRegex().Matches(title ?? string.Empty)
            .Concat(DashTitlePartRegex().Matches(title ?? string.Empty))
            .Any(match => MixedRegex().IsMatch(match.Groups[1].Value));

    public static IReadOnlySet<string> BackingRecordingTags(string? text) =>
        BackingRecordingRegex().Matches(text ?? string.Empty)
            .Select(match => NormalizeForMatching(match.Value))
            .ToHashSet(StringComparer.Ordinal);

    public static bool HasCoverRecordingLabel(string? text) =>
        CoverRecordingRegex().IsMatch(text ?? string.Empty);

    public static bool HasCleanRecordingLabel(string? title) =>
        CleanRecordingRegex().IsMatch(title ?? string.Empty);

    private static HashSet<string> TitleFeatureCredits(string title) =>
        FeatDecoratorRegex().Matches(title)
            .Where(match => RecordingVersionTags(match.Value).Count == 0 &&
                !match.Value.TrimStart().StartsWith("with ", StringComparison.OrdinalIgnoreCase))
            .SelectMany(match => SplitCredits(CreditPrefixRegex().Replace(match.Value.Trim(' ', '(', ')', '[', ']'), "")))
            .Select(NormalizeForMatching).ToHashSet(StringComparer.Ordinal);

    internal static string ArtistWithoutSeriesCredits(string title, string artist)
    {
        var description = SeriesDecoratorRegex().Match(title);
        if (!description.Success) return artist;
        var normalized = NormalizeForMatching(description.Value);
        return string.Join(", ", SplitCredits(artist).Where((credit, index) =>
            index == 0 || !normalized.Contains(
                NormalizeForMatching(credit).Replace(" music", "", StringComparison.Ordinal), StringComparison.Ordinal)));
    }

    public static bool TitleAnnotationsMatch(
        string sourceTitle, string? sourceArtist, string candidateTitle, string? candidateArtist)
    {
        var sourceFromAnnotations = FromAnnotationKeys(sourceTitle);
        var candidateFromAnnotations = FromAnnotationKeys(candidateTitle);
        if (sourceFromAnnotations.Count > 0 && candidateFromAnnotations.Count > 0 &&
            !sourceFromAnnotations.SetEquals(candidateFromAnnotations)) return false;

        var creditedArtists = string.Join(", ", sourceArtist, candidateArtist);
        var knownCredits = SplitCredits(creditedArtists).Select(NormalizeForMatching).ToHashSet(StringComparer.Ordinal);
        var sourceFeatures = TitleFeatureCredits(sourceTitle);
        var candidateFeatures = TitleFeatureCredits(candidateTitle);
        if (sourceFeatures.Count > 0 && candidateFeatures.Count > 0 && !sourceFeatures.SetEquals(candidateFeatures) &&
            !sourceFeatures.Concat(candidateFeatures).All(knownCredits.Contains)) return false;
        var allowTitleFeatures = CanCompareTitleFeatures(sourceArtist, candidateArtist);
        var source = StripDecorators(sourceTitle, creditedArtists, allowTitleFeatures);
        var candidate = StripDecorators(candidateTitle, creditedArtists, allowTitleFeatures);

        static bool AnnotationsOccurIn(string title, string counterpart)
        {
            var normalizedCounterpart = " " + NormalizeForMatching(counterpart) + " ";
            var annotations = BracketedTitlePartRegex().Matches(title)
                .Concat(DashTitlePartRegex().Matches(title))
                .Select(match => match.Groups[1].Value);
            return annotations.All(annotation =>
                RecordingVersionTags(annotation).Count > 0 ||
                normalizedCounterpart.Contains(
                    " " + NormalizeForMatching(CreditPrefixRegex().Replace(annotation, "")) + " ",
                    StringComparison.Ordinal));
        }

        return AnnotationsOccurIn(source, candidate) && AnnotationsOccurIn(candidate, source);
    }

    private static IReadOnlySet<string> FromAnnotationKeys(string title) =>
        BracketedTitlePartRegex().Matches(title ?? string.Empty)
            .Concat(FromDashTitlePartRegex().Matches(title ?? string.Empty))
            .Select(match => FromAnnotationKey(match.Groups[1].Value))
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

    private static string? FromAnnotationKey(string annotation)
    {
        var value = annotation.Trim();
        if (!value.StartsWith("from", StringComparison.OrdinalIgnoreCase) ||
            value.Length > 4 && !char.IsWhiteSpace(value[4])) return null;

        value = value[4..].TrimStart();
        var description = value;
        if (description.StartsWith("the ", StringComparison.OrdinalIgnoreCase)) description = description[4..].TrimStart();
        if (description.StartsWith("animated ", StringComparison.OrdinalIgnoreCase)) description = description[9..].TrimStart();
        foreach (var descriptor in new[] { "film", "movie", "series" })
        {
            if (!description.StartsWith(descriptor, StringComparison.OrdinalIgnoreCase) ||
                description.Length <= descriptor.Length || !char.IsWhiteSpace(description[descriptor.Length])) continue;
            value = description[(descriptor.Length + 1)..].TrimStart();
            break;
        }

        value = value.Trim().Trim('"', '\'', '“', '”', '‘', '’').Trim();
        var normalized = NormalizeForMatching(value);
        return normalized.Length == 0 ? null : normalized;
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

    public static int CalculateTitleSimilarity(string source, string candidate)
    {
        var left = NormalizeForMatching(source);
        var right = NormalizeForMatching(candidate);
        if (left != right && (CreditPrefixRegex().Replace(left, "") == right ||
                              CreditPrefixRegex().Replace(right, "") == left))
            return CalculateLevenshteinScore(left, right);
        return CalculateSimilarity(source, candidate);
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
        normalized = normalized.Replace('$', 's').Replace('-', ' ').Replace('_', ' ')
            .Replace("&", " and ", StringComparison.Ordinal);

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

    public static IReadOnlySet<string> SemanticVersionTags(string? title)
    {
        var tags = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(title)) return tags;
        var normalized = RemoveDiacritics(title).ToLowerInvariant();
        tags.UnionWith(SemanticVersionRegex().Matches(normalized)
            .Select(match => NormalizeVersionTag(match.Value)));
        tags.UnionWith(RemixLabelRegex().Matches(normalized)
            .Select(match => "remix:" + NormalizeForMatching(match.Groups["label"].Value)));
        return tags;
    }

    private static string NormalizeVersionTag(string value)
    {
        var normalized = NormalizeForMatching(value.Replace('-', ' '));
        return normalized switch
        {
            "drumless edition" => "drumless",
            "acapella" or "a capella" or "a cappella" => "acappella",
            "slowed down" => "slowed",
            "bassboosted" => "bass boosted",
            _ => normalized
        };
    }

    public static IReadOnlySet<string> SpecialEditionTags(string? title) =>
        SemanticVersionTags(title).Where(tag => tag is not
            ("live" or "acoustic" or "stripped" or "radio edit" or "original mix" or "clean" or "explicit"))
            .ToHashSet(StringComparer.Ordinal);

    public static bool HasOtherRemixer(string title, string? artist, string? albumArtist = null)
    {
        var artists = new[] { artist, albumArtist }
            .Select(value => SplitCredits(value).FirstOrDefault() ?? "")
            .Select(NormalizeForMatching).ToHashSet(StringComparer.Ordinal);
        return SemanticVersionTags(title).Where(tag => tag.StartsWith("remix:", StringComparison.Ordinal))
            .Select(tag => tag[6..].Replace("remix", "", StringComparison.Ordinal).Trim())
            .Any(credit => credit.Length > 0 && !artists.Contains(credit));
    }

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
