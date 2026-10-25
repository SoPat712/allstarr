using allstarr.Services.Common;

namespace allstarr.Core.Matching;

internal enum TrackMatchRuleDisposition
{
    Continue,
    Accept,
    Reject,
    Score
}

internal sealed record TrackMatchRuleOutcome(
    TrackMatchRuleDisposition Disposition,
    double Confidence,
    string ReasonCode,
    string Reason,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<string> Warnings,
    IReadOnlyDictionary<string, double> Components);

internal interface ITrackMatchRule
{
    string Id { get; }

    TrackMatchRuleOutcome? Evaluate(
        ExternalTrackMatchSnapshot source,
        LocalTrackMatchCandidate candidate,
        TrackMatchPolicy policy);
}

internal static class TrackMatchRuleSet
{
    internal static TrackMatchRuleOutcome Accept(
        string reasonCode, string reason, string component, double confidence = 1) =>
        new(TrackMatchRuleDisposition.Accept, confidence, reasonCode, reason,
            [reasonCode], [], new Dictionary<string, double> { [component] = 1 });

    internal static TrackMatchRuleOutcome Reject(string reasonCode, string reason, string component,
        double score = 0, IReadOnlyList<string>? warnings = null) =>
        new(TrackMatchRuleDisposition.Reject, 0, reasonCode, reason,
            [reasonCode], warnings ?? [], new Dictionary<string, double> { [component] = score, ["recordingAllowed"] = 0 });

    public static IReadOnlyList<ITrackMatchRule> Default { get; } =
    [
        new ContinuousMixRule(),
        new BackingRecordingRule(),
        new CoverArtistRule(),
        new ExplicitRecordingRule(),
        new RecordingDurationRule(),
        new CanonicalRecordingExactRule(),
        new MusicBrainzExactRule(),
        new IsrcExactRule(),
        new IsrcConflictRule(),
        new OdesliAliasRule(),
        new ProviderTrackIdExactRule(),
        new FieldScoreRule()
    ];

    public static TrackMatchRuleOutcome Evaluate(
        ExternalTrackMatchSnapshot source,
        LocalTrackMatchCandidate candidate,
        TrackMatchPolicy policy)
    {
        foreach (var rule in Default)
        {
            var outcome = rule.Evaluate(source, candidate, policy);
            if (outcome != null && outcome.Disposition != TrackMatchRuleDisposition.Continue)
                return outcome;
        }

        return FieldScoreRule.Score(source, candidate, policy);
    }
}

internal sealed class ContinuousMixRule : ITrackMatchRule
{
    public string Id => "continuous-mix";

    public TrackMatchRuleOutcome? Evaluate(
        ExternalTrackMatchSnapshot source,
        LocalTrackMatchCandidate candidate,
        TrackMatchPolicy policy) =>
        !FuzzyMatcher.IsContinuousMixExcerpt(source.Title) &&
        FuzzyMatcher.IsContinuousMixExcerpt(candidate.Title, candidate.Album)
            ? TrackMatchRuleSet.Reject("continuous_mix_excerpt",
                "The candidate is an excerpt from a DJ or continuous mix that the source did not request.", "continuousMix")
            : null;
}

internal sealed class BackingRecordingRule : ITrackMatchRule
{
    public string Id => "backing-recording";

    public TrackMatchRuleOutcome? Evaluate(
        ExternalTrackMatchSnapshot source,
        LocalTrackMatchCandidate candidate,
        TrackMatchPolicy policy) =>
        FuzzyMatcher.BackingRecordingTags(string.Join(" ", candidate.Title, candidate.Artist, candidate.Album))
            .Except(FuzzyMatcher.BackingRecordingTags(source.Title)).Any()
            ? TrackMatchRuleSet.Reject("backing_recording_mismatch",
                "The candidate is a karaoke, instrumental, or backing recording that the source did not request.",
                "backingRecording", warnings: ["semantic_version_mismatch"])
            : null;
}

internal sealed class CoverArtistRule : ITrackMatchRule
{
    public string Id => "cover-artist";

    public TrackMatchRuleOutcome? Evaluate(
        ExternalTrackMatchSnapshot source,
        LocalTrackMatchCandidate candidate,
        TrackMatchPolicy policy)
    {
        if (!FuzzyMatcher.HasCoverRecordingLabel(string.Join(" ", candidate.Title, candidate.Artist, candidate.Album)))
            return null;
        var sourceArtist = FuzzyMatcher.NormalizeForMatching(FuzzyMatcher.SplitCredits(source.Artist).FirstOrDefault() ?? "");
        var candidateArtist = FuzzyMatcher.NormalizeForMatching(FuzzyMatcher.SplitCredits(candidate.Artist).FirstOrDefault() ?? "");
        return sourceArtist.Length > 0 && sourceArtist == candidateArtist
            ? null
            : TrackMatchRuleSet.Reject("cover_artist_mismatch",
                "The candidate is a cover or tribute credited to a different main artist.", "primaryArtist");
    }
}

internal sealed class ExplicitRecordingRule : ITrackMatchRule
{
    public string Id => "explicit-recording";

    public TrackMatchRuleOutcome? Evaluate(
        ExternalTrackMatchSnapshot source,
        LocalTrackMatchCandidate candidate,
        TrackMatchPolicy policy) =>
        source.IsExplicit == true &&
        (candidate.IsExplicit == false || FuzzyMatcher.HasCleanRecordingLabel(candidate.Title))
            ? TrackMatchRuleSet.Reject("clean_edit_of_explicit",
                "The candidate is a clean or edited version of an explicit source recording.", "explicitContent")
            : null;
}

internal sealed class RecordingDurationRule : ITrackMatchRule
{
    public string Id => "recording-duration";

    public TrackMatchRuleOutcome? Evaluate(
        ExternalTrackMatchSnapshot source,
        LocalTrackMatchCandidate candidate,
        TrackMatchPolicy policy) =>
        source.DurationMilliseconds is > 0 && candidate.DurationMilliseconds is > 0 &&
        Math.Abs(source.DurationMilliseconds.Value - candidate.DurationMilliseconds.Value) > policy.DurationToleranceSeconds * 1000L
            ? TrackMatchRuleSet.Reject("duration_mismatch",
                "The candidate duration differs from the source by more than the allowed tolerance.", "duration",
                TrackMatchSignals.DurationScore(source.DurationMilliseconds.Value, candidate.DurationMilliseconds.Value,
                    policy.DurationToleranceSeconds), ["duration_exceeds_tolerance"])
            : null;
}

internal sealed class CanonicalRecordingExactRule : ITrackMatchRule
{
    public string Id => "canonical-recording";

    public TrackMatchRuleOutcome? Evaluate(
        ExternalTrackMatchSnapshot source,
        LocalTrackMatchCandidate candidate,
        TrackMatchPolicy policy) =>
        source.CanonicalRecordingId.HasValue &&
        source.CanonicalRecordingId == candidate.CanonicalRecordingId
            ? TrackMatchRuleSet.Accept("canonical_recording_id_exact", "Canonical recording IDs match.",
                "canonicalRecordingId")
            : null;
}

internal sealed class MusicBrainzExactRule : ITrackMatchRule
{
    public string Id => "musicbrainz";

    public TrackMatchRuleOutcome? Evaluate(
        ExternalTrackMatchSnapshot source,
        LocalTrackMatchCandidate candidate,
        TrackMatchPolicy policy) =>
        FuzzyMatcher.IdentifiersEqual(source.MusicBrainzRecordingId, TrackMatchSignals.RecordingIdentity(candidate))
            ? TrackMatchRuleSet.Accept("musicbrainz_recording_id_exact", "MusicBrainz recording IDs match.",
                "musicbrainzRecordingId")
            : null;
}

internal sealed class IsrcExactRule : ITrackMatchRule
{
    public string Id => "isrc";

    public TrackMatchRuleOutcome? Evaluate(
        ExternalTrackMatchSnapshot source,
        LocalTrackMatchCandidate candidate,
        TrackMatchPolicy policy) =>
        FuzzyMatcher.IdentifiersEqual(source.Isrc, candidate.Isrc)
            ? TrackMatchRuleSet.Accept("isrc_exact", "ISRCs match.", "isrc", 0.99)
            : null;
}

internal sealed class IsrcConflictRule : ITrackMatchRule
{
    public string Id => "isrc-conflict";

    public TrackMatchRuleOutcome? Evaluate(
        ExternalTrackMatchSnapshot source,
        LocalTrackMatchCandidate candidate,
        TrackMatchPolicy policy)
    {
        if (string.IsNullOrWhiteSpace(source.Isrc) || string.IsNullOrWhiteSpace(candidate.Isrc) ||
            FuzzyMatcher.IdentifiersEqual(source.Isrc, candidate.Isrc))
            return null;
        return new TrackMatchRuleOutcome(
            TrackMatchRuleDisposition.Reject,
            0,
            "isrc_conflict",
            "The ISRCs identify different recordings.",
            ["isrc_conflict"],
            [],
            new Dictionary<string, double> { ["isrc"] = 0 });
    }
}

internal sealed class OdesliAliasRule : ITrackMatchRule
{
    public string Id => "odesli";

    public TrackMatchRuleOutcome? Evaluate(
        ExternalTrackMatchSnapshot source,
        LocalTrackMatchCandidate candidate,
        TrackMatchPolicy policy) =>
        source.ExactProviderAliases?.Any(alias =>
            TrackMatchSignals.TryGetProviderTrackId(candidate.ProviderTrackIds, alias.Key, out var id) && id == alias.Value) == true
            ? TrackMatchRuleSet.Accept("odesli_alias_exact", "An exact cross-provider link identifies this recording.", "providerAlias")
            : null;
}

internal sealed class ProviderTrackIdExactRule : ITrackMatchRule
{
    public string Id => "provider-track";

    public TrackMatchRuleOutcome? Evaluate(
        ExternalTrackMatchSnapshot source,
        LocalTrackMatchCandidate candidate,
        TrackMatchPolicy policy) =>
        TrackMatchSignals.TryGetProviderTrackId(candidate.ProviderTrackIds, source.ProviderId, out var providerId) &&
        providerId.Equals(source.ExternalId, StringComparison.Ordinal)
            ? TrackMatchRuleSet.Accept("provider_track_id_exact", "Provider track IDs match.", "providerTrackId")
            : null;
}

internal sealed class FieldScoreRule : ITrackMatchRule
{
    public string Id => "fields";

    public TrackMatchRuleOutcome? Evaluate(
        ExternalTrackMatchSnapshot source,
        LocalTrackMatchCandidate candidate,
        TrackMatchPolicy policy) =>
        Score(source, candidate, policy);

    public static TrackMatchRuleOutcome Score(
        ExternalTrackMatchSnapshot source,
        LocalTrackMatchCandidate candidate,
        TrackMatchPolicy policy)
    {
        var reasons = new List<string>();
        var warnings = new List<string>();
        var sourceTitle = FuzzyMatcher.NormalizeTitleForMatching(source.Title, source.Artist, candidate.Artist);
        var candidateTitle = FuzzyMatcher.NormalizeTitleForMatching(candidate.Title, candidate.Artist, source.Artist);
        var title = FuzzyMatcher.CalculateSimilarity(sourceTitle, candidateTitle) / 100d;
        var artist = TrackMatchSignals.ArtistSimilarity(source.Artist, candidate.Artist);
        var album = TrackMatchSignals.ComparableSimilarity(source.Album, candidate.Album);
        var albumArtist = TrackMatchSignals.ComparableSimilarity(source.AlbumArtist, candidate.AlbumArtist);
        var duration = source.DurationMilliseconds is > 0 && candidate.DurationMilliseconds is > 0
            ? TrackMatchSignals.DurationScore(
                source.DurationMilliseconds.Value, candidate.DurationMilliseconds.Value,
                policy.DurationToleranceSeconds)
            : (double?)null;
        var titleAnnotationsMatch = FuzzyMatcher.TitleAnnotationsMatch(
            source.Title, source.Artist, candidate.Title, candidate.Artist);
        var versionMatch = FuzzyMatcher.SemanticVersionTags(source.Title)
            .SetEquals(FuzzyMatcher.SemanticVersionTags(candidate.Title));
        var components = new Dictionary<string, double>
        {
            ["title"] = Math.Round(title, 4),
            ["artist"] = Math.Round(artist, 4),
            ["primaryArtist"] = TrackMatchSignals.Similarity(
                FuzzyMatcher.SplitCredits(source.Artist).FirstOrDefault(),
                FuzzyMatcher.SplitCredits(candidate.Artist).FirstOrDefault()),
            ["versionTags"] = versionMatch ? 1 : 0,
            ["titleAnnotations"] = titleAnnotationsMatch ? 1 : 0
        };
        TrackMatchSignals.AddReason(reasons, "title", title);
        if (TrackMatchSignals.TitleComparisonReason(source.Title, candidate.Title, source.Artist, candidate.Artist) is { } titleReason)
            reasons.Add(titleReason);
        TrackMatchSignals.AddReason(reasons, "artist", artist);
        var weightedScore = (title * 0.42) + (artist * 0.30);
        var totalWeight = 0.72;
        if (album.HasValue)
        {
            components["album"] = Math.Round(album.Value, 4);
            TrackMatchSignals.AddReason(reasons, "album", album.Value);
        }
        if (albumArtist.HasValue)
        {
            components["albumArtist"] = Math.Round(albumArtist.Value, 4);
            TrackMatchSignals.AddReason(reasons, "album_artist", albumArtist.Value);
        }
        if (duration.HasValue)
        {
            components["duration"] = Math.Round(duration.Value, 4);
            weightedScore += duration.Value * 0.16;
            totalWeight += 0.16;
            reasons.Add(duration.Value >= 0.9 ? "duration_close" : "duration_partial");
            if (duration.Value < 0.5) warnings.Add("duration_exceeds_tolerance");
        }

        var confidence = weightedScore / totalWeight;
        if (album.HasValue || albumArtist.HasValue)
        {
            var withAlbum = (weightedScore + (Math.Max(album ?? 0, albumArtist ?? 0) * 0.12)) /
                            (totalWeight + 0.12);
            confidence = Math.Max(confidence, withAlbum);
        }
        if (title >= 0.98 && artist >= 0.88 && duration >= 0.9)
            confidence = Math.Max(confidence, 0.9);
        if (!versionMatch)
        {
            confidence = Math.Max(0, confidence - 0.18);
            warnings.Add("semantic_version_mismatch");
        }
        if (!titleAnnotationsMatch) warnings.Add("title_annotation_mismatch");
        if (!duration.HasValue && Math.Max(album ?? 0, albumArtist ?? 0) < 0.85)
        {
            confidence = Math.Min(confidence, 0.94);
            warnings.Add("limited_recording_metadata");
        }
        return new TrackMatchRuleOutcome(
            TrackMatchRuleDisposition.Score,
            Math.Round(confidence, 4),
            "field_score",
            "Scored from title, artist, album, duration, and version tags.",
            reasons,
            warnings,
            components);
    }
}

internal static class TrackMatchSignals
{
    public static string? RecordingIdentity(LocalTrackMatchCandidate candidate) =>
        TryGetProviderTrackId(candidate.ProviderTrackIds, "musicbrainzrecording", out var recordingId)
            ? recordingId
            : candidate.MusicBrainzRecordingId;

    public static bool TryGetProviderTrackId(
        IReadOnlyDictionary<string, string>? providerTrackIds,
        string providerId,
        out string trackId)
    {
        trackId = string.Empty;
        if (providerTrackIds == null || string.IsNullOrWhiteSpace(providerId))
            return false;
        if (providerTrackIds.TryGetValue(providerId, out var exact) && !string.IsNullOrWhiteSpace(exact))
        {
            trackId = exact;
            return true;
        }

        foreach (var (candidateProviderId, candidateTrackId) in providerTrackIds)
        {
            if (candidateProviderId.Equals(providerId, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(candidateTrackId))
            {
                trackId = candidateTrackId;
                return true;
            }
        }

        return false;
    }

    public static double DurationScore(long source, long candidate, int toleranceSeconds)
    {
        var delta = Math.Abs(source - candidate) / 1000d;
        var duration = Math.Max(source, candidate) / 1000d;
        return delta == 0
            ? 1
            : delta <= 2
                ? 0.95
                : delta <= toleranceSeconds
                    ? 1 - (0.5 * delta / toleranceSeconds)
                    : Math.Max(0, 0.5 - ((delta - toleranceSeconds) / Math.Max(1, duration)));
    }

    public static double Similarity(string? left, string? right) =>
        string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)
            ? 0
            : FuzzyMatcher.CalculateSimilarityAggressive(left, right) / 100d;

    public static string? TitleComparisonReason(
        string? source, string? candidate, string? sourceArtist = null, string? candidateArtist = null)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(candidate) || source == candidate)
            return null;
        if (string.Equals(source, candidate, StringComparison.OrdinalIgnoreCase))
            return "title_case_folded";
        if (FuzzyMatcher.NormalizeForMatching(source) == FuzzyMatcher.NormalizeForMatching(candidate))
            return "title_normalized";
        var sourceBase = FuzzyMatcher.NormalizeTitleForMatching(source, sourceArtist, candidateArtist);
        var candidateBase = FuzzyMatcher.NormalizeTitleForMatching(candidate, candidateArtist, sourceArtist);
        return sourceBase.Length > 0 && sourceBase == candidateBase
            ? "title_decorators_ignored"
            : "title_fuzzy";
    }

    public static double? ComparableSimilarity(string? left, string? right) =>
        string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)
            ? null
            : Similarity(left, right);

    public static double ArtistSimilarity(string? left, string? right)
    {
        var sourceArtists = FuzzyMatcher.SplitCredits(left);
        var candidateArtists = FuzzyMatcher.SplitCredits(right);
        if (sourceArtists.Count == 0 || candidateArtists.Count == 0)
            return 0;

        static double BestScore(string artist, IReadOnlyList<string> candidates) =>
            candidates.Max(candidate =>
                FuzzyMatcher.CalculateSimilarityAggressive(artist, candidate) / 100d);

        var sourceCoverage = sourceArtists.Average(artist => BestScore(artist, candidateArtists));
        var candidatePrecision = candidateArtists.Average(artist => BestScore(artist, sourceArtists));
        var primaryArtist = FuzzyMatcher.CalculateSimilarityAggressive(
            sourceArtists[0], candidateArtists[0]) / 100d;
        var asymmetricCreditScore = (candidatePrecision * 0.75) + (sourceCoverage * 0.25);
        return Math.Round(
            sourceArtists.Count > 1 && candidateArtists.Count > 1
                ? asymmetricCreditScore
                : Math.Max(asymmetricCreditScore, primaryArtist * 0.85),
            4);
    }

    public static void AddReason(List<string> reasons, string signal, double score)
    {
        if (score >= 1) reasons.Add($"{signal}_exact");
        else if (score >= 0.85) reasons.Add($"{signal}_strong");
        else if (score >= 0.65) reasons.Add($"{signal}_partial");
    }
}
