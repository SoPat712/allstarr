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
        new MusicBrainzRecordingIsrcRule(),
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
        TrackMatchPolicy policy,
        bool singleAlbumAgreement = false,
        bool allowVocalSourceAlbumMarkers = false)
    {
        candidate = candidate with { IsExplicit = TrackMatchSignals.Explicitness(candidate) };
        if ((source.IsExplicit == false || allstarr.Core.Music.CleanRecordingPolicy.RequiresCleanVersion(source.Title)) && candidate.IsExplicit == true)
            return Reject("explicit_edit_of_clean", "The candidate is explicit and the request requires a clean recording.", "explicitContent");
        var rawTitles = (source.Title, candidate.Title);
        (source, candidate) = TrackMatchSignals.WithTitleCredits(source, candidate);
        var outcome = EvaluateRules(source, candidate, policy, singleAlbumAgreement, allowVocalSourceAlbumMarkers);
        return rawTitles != (source.Title, candidate.Title) && !outcome.Reasons.Contains("title_decorators_ignored")
            ? outcome with { Reasons = outcome.Reasons.Append("title_decorators_ignored").ToArray() }
            : outcome;
    }

    private static TrackMatchRuleOutcome EvaluateRules(
        ExternalTrackMatchSnapshot source,
        LocalTrackMatchCandidate candidate,
        TrackMatchPolicy policy,
        bool singleAlbumAgreement,
        bool allowVocalSourceAlbumMarkers)
    {
        foreach (var rule in Default)
        {
            var outcome = rule.Evaluate(source, candidate, policy);
            if (outcome == null || outcome.Disposition == TrackMatchRuleDisposition.Continue) continue;
            if (singleAlbumAgreement && outcome.Reasons.Contains("weak_source_duration") ||
                allowVocalSourceAlbumMarkers && outcome.Disposition == TrackMatchRuleDisposition.Score)
                outcome = FieldScoreRule.Score(source, candidate, policy, singleAlbumAgreement, allowVocalSourceAlbumMarkers);
            if (outcome.Disposition == TrackMatchRuleDisposition.Accept &&
                !TrackMatchSignals.RecordingVersionsMatch(source, candidate, policy.DurationToleranceSeconds, allowVocalSourceAlbumMarkers))
                outcome = FieldScoreRule.Score(source, candidate, policy, singleAlbumAgreement, allowVocalSourceAlbumMarkers);
            if (outcome.Disposition != TrackMatchRuleDisposition.Reject &&
                TrackMatchSignals.SourceAlbumRequiresConfirmation(source, candidate, policy.DurationToleranceSeconds, allowVocalSourceAlbumMarkers))
                outcome = outcome with
                {
                    Reasons = outcome.Reasons.Append("source_album_marker_confirmation").ToArray(),
                    Components = new Dictionary<string, double>(outcome.Components) { ["sourceAlbumConfirmation"] = 0 }
                };
            if (allowVocalSourceAlbumMarkers && outcome.Disposition != TrackMatchRuleDisposition.Reject &&
                TrackMatchSignals.RecordingVersionsMatch(source, candidate, policy.DurationToleranceSeconds, allowVocalSourceAlbumMarkers: true))
                outcome = outcome with
                {
                    Reasons = outcome.Reasons.Append("source_album_vocal_evidence").ToArray(),
                    Components = new Dictionary<string, double>(outcome.Components) { ["sourceAlbumVocalEvidence"] = 1 }
                };
            if (outcome.Disposition != TrackMatchRuleDisposition.Reject && source.IsExplicit.HasValue &&
                candidate.IsExplicit.HasValue && source.IsExplicit != candidate.IsExplicit)
            {
                var components = new Dictionary<string, double>(outcome.Components) { ["explicitContent"] = 0 };
                if (source.IsExplicit == false) components["recordingAllowed"] = 0;
                outcome = outcome with
                {
                    Confidence = source.IsExplicit == false ? 0 : outcome.Confidence,
                    Reasons = outcome.Reasons.Append(source.IsExplicit == true ? "clean_edit_of_explicit" : "explicit_edit_of_clean").ToArray(),
                    Components = components
                };
            }
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
            .Except(FuzzyMatcher.BackingRecordingTags(string.Join(" ", source.Title, source.Album))).Any()
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
        source.IsExplicit == true && FuzzyMatcher.HasCleanRecordingLabel(candidate.Title)
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
        TrackMatchPolicy policy)
    {
        if (!TrackMatchSignals.DurationMismatch(source, candidate, policy.DurationToleranceSeconds)) return null;
        if (TrackMatchSignals.WeakDurationIdentityMatches(source, candidate) &&
            (string.IsNullOrWhiteSpace(source.Isrc) || string.IsNullOrWhiteSpace(candidate.Isrc) ||
             FuzzyMatcher.IdentifiersEqual(source.Isrc, candidate.Isrc)))
            return FieldScoreRule.Score(source, candidate, policy);
        return TrackMatchRuleSet.Reject("duration_mismatch",
            "The candidate duration differs from the source by more than the allowed tolerance.", "duration",
            TrackMatchSignals.DurationScore(source.DurationMilliseconds!.Value, candidate.DurationMilliseconds!.Value,
                policy.DurationToleranceSeconds), ["duration_exceeds_tolerance"]);
    }
}

internal sealed class MusicBrainzRecordingIsrcRule : ITrackMatchRule
{
    public string Id => "musicbrainz-recording-isrc";

    public TrackMatchRuleOutcome? Evaluate(
        ExternalTrackMatchSnapshot source,
        LocalTrackMatchCandidate candidate,
        TrackMatchPolicy policy)
    {
        if (string.IsNullOrWhiteSpace(source.MusicBrainzRecordingId) ||
            source.MusicBrainzRecordingIsrcs is not { Count: > 0 } || string.IsNullOrWhiteSpace(candidate.Isrc))
            return null;
        if (!TrackMatchSignals.MusicBrainzIsrcMatches(source, candidate))
            return TrackMatchRuleSet.Reject("musicbrainz_recording_isrc_conflict",
                "The candidate ISRC conflicts with the stored MusicBrainz recording identifiers.", "musicbrainzIsrc");
        return TrackMatchSignals.ExactRecordingFieldsMatch(source, candidate)
            ? TrackMatchRuleSet.Accept("musicbrainz_recording_isrc_exact",
                "The candidate ISRC belongs to the source MusicBrainz recording.", "musicbrainzIsrc", 0.99)
            : null;
    }
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
        FuzzyMatcher.IdentifiersEqual(TrackMatchSignals.RecordingIdentity(source), TrackMatchSignals.RecordingIdentity(candidate))
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

    private const double LimitedMetadataCeiling = 0.94;

    public static TrackMatchRuleOutcome Score(
        ExternalTrackMatchSnapshot source,
        LocalTrackMatchCandidate candidate,
        TrackMatchPolicy policy,
        bool singleAlbumAgreement = false,
        bool allowVocalSourceAlbumMarkers = false)
    {
        var reasons = new List<string>();
        var warnings = new List<string>();
        var sourceTitle = FuzzyMatcher.NormalizeTitleForMatching(source.Title, source.Artist, candidate.Artist);
        var candidateTitle = FuzzyMatcher.NormalizeTitleForMatching(candidate.Title, candidate.Artist, source.Artist);
        var title = FuzzyMatcher.CalculateTitleSimilarity(sourceTitle, candidateTitle, source.Title, candidate.Title) / 100d;
        var seriesContext = source.Title + " | " + candidate.Title;
        var (sourceArtist, candidateArtist) = (FuzzyMatcher.ArtistWithoutSeriesCredits(seriesContext, source.Artist),
            FuzzyMatcher.ArtistWithoutSeriesCredits(seriesContext, candidate.Artist));
        var artist = Math.Max(TrackMatchSignals.ArtistSimilarity(sourceArtist, candidateArtist), TrackMatchSignals.ArtistSimilarity(
            TrackMatchSignals.ArtistsWithTitleCredits(sourceArtist, source.Title), TrackMatchSignals.ArtistsWithTitleCredits(candidateArtist, candidate.Title)));
        var album = TrackMatchSignals.ComparableSimilarity(source.Album, candidate.Album);
        var albumArtist = TrackMatchSignals.ComparableSimilarity(source.AlbumArtist, candidate.AlbumArtist);
        var duration = source.DurationMilliseconds is > 0 && candidate.DurationMilliseconds is > 0
            ? TrackMatchSignals.DurationScore(
                source.DurationMilliseconds.Value, candidate.DurationMilliseconds.Value,
                policy.DurationToleranceSeconds)
            : (double?)null;
        var titleAnnotationsMatch = FuzzyMatcher.TitleAnnotationsMatch(
            source.Title, source.Artist, candidate.Title, candidate.Artist);
        var versionMatch = TrackMatchSignals.RecordingVersionsMatch(source, candidate, policy.DurationToleranceSeconds, allowVocalSourceAlbumMarkers) ||
                           TrackMatchSignals.SourceAlbumRequiresConfirmation(source, candidate, policy.DurationToleranceSeconds, allowVocalSourceAlbumMarkers);
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
        if (TrackMatchSignals.LibraryCopyArtistFloor(source, candidate, sourceTitle == candidateTitle) is { } artistFloor &&
            artist < artistFloor.Score)
        {
            artist = artistFloor.Score;
            components["artist"] = artistFloor.Score;
            reasons.Add(artistFloor.Reason);
        }
        TrackMatchSignals.AddReason(reasons, "artist", artist);
        var weakDuration = TrackMatchSignals.DurationMismatch(source, candidate, policy.DurationToleranceSeconds) &&
                           TrackMatchSignals.WeakDurationIdentityMatches(source, candidate);
        var weakAlbum = weakDuration && (TrackMatchSignals.WeakDurationAlbumMatches(source, candidate) || singleAlbumAgreement);
        if (weakDuration)
        {
            components["weakDurationAlbumAgreement"] = weakAlbum ? 1 : 0;
            reasons.Add("weak_source_duration");
            if (singleAlbumAgreement && !TrackMatchSignals.WeakDurationAlbumMatches(source, candidate))
                reasons.Add("single_album_recording_corroborated");
            artist = Math.Max(artist, components["primaryArtist"]);
            components["artist"] = Math.Round(artist, 4);
        }
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
            weightedScore += (weakDuration ? Math.Max(0.5, duration.Value) : duration.Value) * 0.16;
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
            // Title and artist alone can't prove a recording, so the score stays under the limit. Better evidence
            // must still rank higher, or two different recordings tie just because both reach it.
            var floor = Math.Min(policy.AcceptThreshold, LimitedMetadataCeiling);
            if (confidence > floor) confidence = floor + (confidence - floor) * (LimitedMetadataCeiling - floor) / (1 - floor);
            warnings.Add("limited_recording_metadata");
        }
        if (weakDuration && !weakAlbum) confidence = Math.Min(confidence, policy.AcceptThreshold - 0.01);
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
    public static bool DurationMismatch(ExternalTrackMatchSnapshot source, LocalTrackMatchCandidate candidate,
        int toleranceSeconds) =>
        source.DurationMilliseconds is > 0 && candidate.DurationMilliseconds is > 0 &&
        Math.Abs(source.DurationMilliseconds.Value - candidate.DurationMilliseconds.Value) > toleranceSeconds * 1000L;

    private static bool HasWeakDuration(ExternalTrackMatchSnapshot source)
    {
        if (string.IsNullOrWhiteSpace(source.DurationProvenance)) return false;
        var provenance = TrackMatchDecisionEngine.NormalizeProvider(source.DurationProvenance);
        return provenance != "spotify" &&
               (provenance is "listenbrainz" or "lastfm" or "musicbrainz" ||
                provenance != TrackMatchDecisionEngine.NormalizeProvider(source.ProviderId));
    }

    public static bool HasStrongDuration(ExternalTrackMatchSnapshot source) =>
        !string.IsNullOrWhiteSpace(source.DurationProvenance) && !HasWeakDuration(source);

    public static bool WeakDurationIdentityMatches(ExternalTrackMatchSnapshot source, LocalTrackMatchCandidate candidate) =>
        HasWeakDuration(source) && ExactRecordingFieldsMatch(source, candidate);

    public static bool ExactRecordingFieldsMatch(ExternalTrackMatchSnapshot source, LocalTrackMatchCandidate candidate) =>
        !string.IsNullOrWhiteSpace(source.Title) && !string.IsNullOrWhiteSpace(source.Artist) &&
        FuzzyMatcher.NormalizeTitleForMatching(source.Title, source.Artist, candidate.Artist) ==
        FuzzyMatcher.NormalizeTitleForMatching(candidate.Title, candidate.Artist, source.Artist) &&
        FuzzyMatcher.NormalizeForMatching(FuzzyMatcher.SplitCredits(source.Artist).FirstOrDefault() ?? "") ==
        FuzzyMatcher.NormalizeForMatching(FuzzyMatcher.SplitCredits(candidate.Artist).FirstOrDefault() ?? "") &&
        FuzzyMatcher.TitleAnnotationsMatch(source.Title, source.Artist, candidate.Title, candidate.Artist) &&
        FuzzyMatcher.RecordingVersionsMatch(source.Title, source.Album, candidate.Title, candidate.Album);

    public static bool SourceAlbumIsSingle(ExternalTrackMatchSnapshot source) =>
        !string.IsNullOrWhiteSpace(source.Album) &&
        FuzzyMatcher.NormalizeAlbumForMatching(FuzzyMatcher.StripDecorators(source.Album, source.Artist)) ==
        FuzzyMatcher.NormalizeTitleForMatching(source.Title, source.Artist);

    public static bool MusicBrainzIsrcMatches(ExternalTrackMatchSnapshot source, LocalTrackMatchCandidate candidate) =>
        !string.IsNullOrWhiteSpace(source.MusicBrainzRecordingId) &&
        source.MusicBrainzRecordingIsrcs?.Any(isrc => FuzzyMatcher.IdentifiersEqual(isrc, candidate.Isrc)) == true;

    public static bool WeakDurationAlbumMatches(ExternalTrackMatchSnapshot source, LocalTrackMatchCandidate candidate)
    {
        var primary = FuzzyMatcher.SplitCredits(source.Artist).FirstOrDefault() ?? "";
        string Album(string? value)
        {
            var album = value ?? "";
            var prefix = primary + " Presents:";
            if (primary.Length > 0 && album.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                album = album[prefix.Length..].Trim();
            return FuzzyMatcher.StripDecorators(album, source.Artist);
        }
        return TrackMatchDecisionEngine.SameAlbumEdition(Album(source.Album), Album(candidate.Album)) == true;
    }

    public static bool? Explicitness(LocalTrackMatchCandidate candidate) =>
        candidate.IsExplicit ?? FuzzyMatcher.AlbumExplicitness(candidate.Album);

    public static bool RecordingVersionsMatch(ExternalTrackMatchSnapshot source, LocalTrackMatchCandidate candidate,
        int durationToleranceSeconds, bool allowVocalSourceAlbumMarkers = false) =>
        FuzzyMatcher.RecordingVersionsMatch(source.Title, source.Album, candidate.Title, candidate.Album,
            DurationsMatch(source, candidate, durationToleranceSeconds), allowVocalSourceAlbumMarkers);

    public static bool SourceAlbumRequiresConfirmation(ExternalTrackMatchSnapshot source, LocalTrackMatchCandidate candidate,
        int durationToleranceSeconds, bool allowVocalSourceAlbumMarkers = false) =>
        FuzzyMatcher.SourceAlbumRequiresConfirmation(source.Title, source.Album, candidate.Title, candidate.Album,
            DurationsMatch(source, candidate, durationToleranceSeconds), allowVocalSourceAlbumMarkers);

    private static bool DurationsMatch(ExternalTrackMatchSnapshot source, LocalTrackMatchCandidate candidate,
        int durationToleranceSeconds) =>
        source.DurationMilliseconds is > 0 && candidate.DurationMilliseconds is > 0 &&
        Math.Abs(source.DurationMilliseconds.Value - candidate.DurationMilliseconds.Value) <= durationToleranceSeconds * 1000L;

    // Last.fm recording IDs remain supporting metadata.
    public static string? RecordingIdentity(ExternalTrackMatchSnapshot source) =>
        source.ProviderId.Equals("lastfm", StringComparison.OrdinalIgnoreCase) ? null : source.MusicBrainzRecordingId;

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

    // Apple prints the artists as one line and the featured ones in the title; a library file lists them all as
    // artists. Both copies get the title's performers, and the title loses notes that are not a version of the song.
    public static (ExternalTrackMatchSnapshot Source, LocalTrackMatchCandidate Candidate) WithTitleCredits(
        ExternalTrackMatchSnapshot source, LocalTrackMatchCandidate candidate)
    {
        static string WithCredits(string artist, string title) => ArtistsWithTitleCredits(artist, title);
        // Two different guests ("feat. SZA" and "feat. Khalid") are two different recordings.
        var sourceCredits = FuzzyMatcher.TitleCreditNames(source.Title).Select(FuzzyMatcher.NormalizeForMatching).ToHashSet(StringComparer.Ordinal);
        var candidateCredits = FuzzyMatcher.TitleCreditNames(candidate.Title).Select(FuzzyMatcher.NormalizeForMatching).ToHashSet(StringComparer.Ordinal);
        if (sourceCredits.Count > 0 && candidateCredits.Count > 0 &&
            !sourceCredits.IsSubsetOf(candidateCredits) && !candidateCredits.IsSubsetOf(sourceCredits)) return (source, candidate);
        var title = SharedBareTitle(source, candidate) ?? LibrarySpellingOfTitle(source, candidate);
        if (title == null) return (source, candidate);
        // A title's credits can only help. A guest one copy names under another alias ("Quasimoto", "Lord Quas") must
        // not undo the match the artists alone make.
        var (sourceArtists, candidateArtists) = (WithCredits(source.Artist, source.Title), WithCredits(candidate.Artist, candidate.Title));
        var helps = ArtistSimilarity(sourceArtists, candidateArtists) >= ArtistSimilarity(source.Artist, candidate.Artist);
        return (source with { Artist = helps ? sourceArtists : source.Artist, Title = title },
            candidate with { Artist = helps ? candidateArtists : candidate.Artist, Title = title });
    }

    /// <summary>The artists plus the performers the title credits, for comparing a copy's credits as one set.</summary>
    public static string ArtistsWithTitleCredits(string artist, string title)
    {
        var known = FuzzyMatcher.SplitCredits(artist).Select(FuzzyMatcher.NormalizeForMatching).ToHashSet(StringComparer.Ordinal);
        var extra = FuzzyMatcher.TitleCreditNames(title).Where(name => known.Add(FuzzyMatcher.NormalizeForMatching(name))).ToArray();
        return extra.Length == 0 || string.IsNullOrWhiteSpace(artist) ? artist : string.Join(", ", extra.Prepend(artist));
    }

    private static string? SharedBareTitle(ExternalTrackMatchSnapshot sourceTrack, LocalTrackMatchCandidate candidateTrack)
    {
        var (source, candidate) = (sourceTrack.Title, candidateTrack.Title);
        // Titles that already normalize alike keep the existing comparison and its explanations.
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(candidate) ||
            FuzzyMatcher.NormalizeForMatching(source) == FuzzyMatcher.NormalizeForMatching(candidate)) return null;
        var left = FuzzyMatcher.WithoutCreditNotes(source);
        var right = FuzzyMatcher.WithoutCreditNotes(candidate);
        if (FuzzyMatcher.TitlesEqualAllowingCensoring(left, right)) return right.Contains('*') ? left : right;
        // A film after a dash or a subtitle only one copy prints, on a copy of the same length.
        if (!DurationsMatch(sourceTrack, candidateTrack, 2)) return null;
        if (FuzzyMatcher.WithoutTrailingNote(left, sourceTrack.Album, candidateTrack.Album) is { } shorterLeft &&
            FuzzyMatcher.TitlesEqualAllowingCensoring(shorterLeft, right))
            return right;
        return FuzzyMatcher.WithoutTrailingNote(right, sourceTrack.Album, candidateTrack.Album) is { } shorterRight &&
               FuzzyMatcher.TitlesEqualAllowingCensoring(left, shorterRight)
            ? left
            : null;
    }

    // A library file spelled a letter or two differently ("Kamakazi" for "Kamikaze") by the same main artist and of
    // the same length is the same song. Streaming results stay strict, since a search returns many near titles.
    private static string? LibrarySpellingOfTitle(ExternalTrackMatchSnapshot source, LocalTrackMatchCandidate candidate)
    {
        if (!candidate.IsLocal || source.DurationMilliseconds is not > 0 || candidate.DurationMilliseconds is not > 0 ||
            Math.Abs(source.DurationMilliseconds.Value - candidate.DurationMilliseconds.Value) > 1500 ||
            !SameName(FuzzyMatcher.SplitCredits(source.Artist).FirstOrDefault(), FuzzyMatcher.SplitCredits(candidate.Artist).FirstOrDefault()))
            return null;
        var left = FuzzyMatcher.NormalizeForMatching(FuzzyMatcher.WithoutCreditNotes(source.Title));
        var right = FuzzyMatcher.NormalizeForMatching(FuzzyMatcher.WithoutCreditNotes(candidate.Title));
        if (left.Length == 0 || right.Length == 0 || left == right ||
            FuzzyMatcher.RecordingVersionTags(left).Count > 0 || FuzzyMatcher.RecordingVersionTags(right).Count > 0) return null;
        var letters = Math.Min(left.Count(char.IsLetterOrDigit), right.Count(char.IsLetterOrDigit));
        var edits = FuzzyMatcher.EditDistance(left, right);
        // About a fifth of the letters, so short titles ("A Sng", "Fade") stay strict.
        return letters >= 5 && edits <= Math.Min(2, Math.Round(letters / 5d, MidpointRounding.AwayFromZero))
            ? FuzzyMatcher.WithoutCreditNotes(candidate.Title)
            : null;
    }

    // A library file with the exact title and length is held back only by its artists being written in another
    // script ("סטילה, נס" for "Ness & Stilla", "전정국" for "Jung Kook"): that says nothing either way.
    public static (double Score, string Reason)? LibraryCopyArtistFloor(
        ExternalTrackMatchSnapshot source, LocalTrackMatchCandidate candidate, bool exactTitle)
    {
        if (!candidate.IsLocal || !exactTitle || source.DurationMilliseconds is not > 0 || candidate.DurationMilliseconds is not > 0)
            return null;
        return Math.Abs(source.DurationMilliseconds.Value - candidate.DurationMilliseconds.Value) <= 1500 &&
               IsLatinScript(source.Artist) is { } sourceLatin && IsLatinScript(candidate.Artist) is { } candidateLatin &&
               sourceLatin != candidateLatin
            ? (0.88, "artist_script_differs")
            : null;
    }

    private static bool? IsLatinScript(string? value)
    {
        var letters = (value ?? "").Where(char.IsLetter).ToArray();
        if (letters.Length == 0) return null;
        static bool Latin(char c) => c < '\u0250' || c is >= '\u1E00' and <= '\u1EFF';
        var latin = letters.Count(Latin);
        return latin == letters.Length ? true : latin == 0 ? false : null;
    }

    private static bool SameName(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) &&
        (WithoutArticle(left) == WithoutArticle(right) || FuzzyMatcher.CalculateSimilarityAggressive(left, right) >= 95);

    private static string WithoutArticle(string value)
    {
        var normalized = FuzzyMatcher.NormalizeForMatching(value);
        return normalized.StartsWith("the ", StringComparison.Ordinal) ? normalized[4..] : normalized;
    }

    // "Simon & Garfunkel" stays one name when the other copy credits it that way.
    private static IReadOnlyList<string> CreditNames(string value, string other)
    {
        var otherNames = other.Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Append(other).Select(FuzzyMatcher.NormalizeForMatching).ToHashSet(StringComparer.Ordinal);
        if (otherNames.Contains(FuzzyMatcher.NormalizeForMatching(value))) return [value.Trim()];
        return value.Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(part => otherNames.Contains(FuzzyMatcher.NormalizeForMatching(part)) ? [part] : FuzzyMatcher.SplitCredits(part))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static double ArtistSimilarity(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return 0;
        var sourceArtists = CreditNames(left, right);
        var candidateArtists = CreditNames(right, left);
        if (sourceArtists.Count == 0 || candidateArtists.Count == 0)
            return 0;
        // Every name on the shorter list is credited on the other: the same people, one copy names more of them.
        // The copy that names them all still ranks a little higher.
        var (fewer, more) = sourceArtists.Count <= candidateArtists.Count
            ? (sourceArtists, candidateArtists) : (candidateArtists, sourceArtists);
        if (fewer.All(name => more.Any(other => SameName(name, other))))
            return more.All(name => fewer.Any(other => SameName(name, other))) ? 1 : 0.97;

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
