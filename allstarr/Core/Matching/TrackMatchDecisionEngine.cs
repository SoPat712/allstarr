using allstarr.Services.Common;
using System.Text.RegularExpressions;

namespace allstarr.Core.Matching;

public enum TrackMatchReviewState
{
    Unresolved = 0,
    Suggested = 1,
    Accepted = 2,
    Rejected = 3,
    Pinned = 4,
    Ambiguous = 5
}

public sealed record TrackMatchScope(
    Guid TenantId,
    Guid UserId,
    string BackendInstanceId,
    string LibraryScopeId,
    Guid ProviderAccountId,
    int PolicyVersion,
    long SourceSnapshotVersion);

public sealed record ExternalTrackMatchSnapshot(
    string SnapshotId,
    string ProviderId,
    string ExternalId,
    string Title,
    string Artist,
    string? Album,
    string? AlbumArtist,
    int? DurationSeconds,
    string? Isrc,
    string? MusicBrainzRecordingId,
    bool? IsExplicit);

public sealed record LocalTrackMatchCandidate(
    Guid LibraryTrackId,
    Guid TenantId,
    Guid? OwnerUserId,
    string BackendInstanceId,
    string LibraryScopeId,
    string BackendItemId,
    Guid? CanonicalRecordingId,
    string Title,
    string Artist,
    string? Album,
    string? AlbumArtist,
    int? DurationSeconds,
    string? Isrc,
    string? MusicBrainzRecordingId,
    bool? IsExplicit,
    IReadOnlyDictionary<string, string>? ProviderTrackIds = null);

public sealed record ScopedTrackMatchOverride(
    Guid TenantId,
    Guid UserId,
    string LibraryScopeId,
    string ProviderId,
    string ExternalId,
    Guid? PinnedLibraryTrackId,
    IReadOnlySet<Guid>? RejectedLibraryTrackIds = null);

public sealed record TrackMatchCandidateScore(
    Guid LibraryTrackId,
    string BackendItemId,
    double Confidence,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<string> Warnings,
    IReadOnlyDictionary<string, double>? Components = null);

public sealed record TrackMatchDecision(
    TrackMatchReviewState State,
    Guid? SelectedLibraryTrackId,
    string? SelectedBackendItemId,
    double Confidence,
    IReadOnlyList<TrackMatchCandidateScore> Candidates,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<string> Warnings,
    int PolicyVersion,
    long SourceSnapshotVersion,
    double AcceptThreshold = 0.88,
    double SuggestThreshold = 0.72,
    double AmbiguityDelta = 0.03,
    bool RequiresReview = false);

public sealed class TrackMatchPolicy
{
    public double AcceptThreshold { get; init; } = 0.88;

    public double SuggestThreshold { get; init; } = 0.72;

    public double AmbiguityDelta { get; init; } = 0.03;

    public int DurationToleranceSeconds { get; init; } = 8;

    public void Validate()
    {
        if (AcceptThreshold is <= 0 or > 1 ||
            SuggestThreshold is < 0 or > 1 ||
            SuggestThreshold > AcceptThreshold ||
            AmbiguityDelta is < 0 or > 1 ||
            DurationToleranceSeconds < 0)
        {
            throw new InvalidOperationException("The track match policy is invalid.");
        }
    }
}

public sealed class TrackMatchDecisionEngine
{
    private readonly TrackMatchPolicy _policy;

    public TrackMatchDecisionEngine(TrackMatchPolicy? policy = null)
    {
        _policy = policy ?? new TrackMatchPolicy();
        _policy.Validate();
    }

    public TrackMatchDecision Decide(
        TrackMatchScope scope,
        ExternalTrackMatchSnapshot source,
        IReadOnlyList<LocalTrackMatchCandidate> candidates,
        ScopedTrackMatchOverride? manualOverride = null)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(candidates);
        ValidateScope(scope);
        ValidateSource(source);
        ValidateOverride(scope, source, manualOverride);

        var scopedCandidates = candidates
            .Where(candidate => IsVisible(scope, candidate))
            .ToList();
        if (scopedCandidates.Select(candidate => candidate.LibraryTrackId).Distinct().Count() != scopedCandidates.Count)
        {
            throw new ArgumentException("A library track candidate may appear only once.", nameof(candidates));
        }

        var visible = scopedCandidates
            .Where(candidate => manualOverride?.RejectedLibraryTrackIds?.Contains(candidate.LibraryTrackId) != true)
            .ToList();
        if (manualOverride?.PinnedLibraryTrackId is { } pinnedId)
        {
            var pinned = visible.SingleOrDefault(candidate => candidate.LibraryTrackId == pinnedId);
            return pinned == null
                ? Result(
                    TrackMatchReviewState.Unresolved,
                    null,
                    0,
                    [],
                    [],
                    ["manual_override_target_not_visible"],
                    scope)
                : Result(
                    TrackMatchReviewState.Pinned,
                    pinned,
                    1,
                    [Score(pinned, 1, ["manual_override_pinned"], [])],
                    ["manual_override_pinned"],
                    [],
                    scope);
        }

        var scores = visible
            .Select(candidate => ScoreCandidate(source, candidate))
            .OrderByDescending(candidate => candidate.Confidence)
            .ThenBy(candidate => candidate.LibraryTrackId)
            .ToList();
        if (scores.Count == 0)
        {
            return Result(
                scopedCandidates.Count > 0 && manualOverride?.RejectedLibraryTrackIds?.Count > 0
                    ? TrackMatchReviewState.Rejected
                    : TrackMatchReviewState.Unresolved,
                null,
                0,
                [],
                [],
                [scopedCandidates.Count > 0 ? "manual_override_rejected_all" : "no_visible_candidates"],
                scope);
        }

        var best = scores[0];
        var selected = visible.Single(candidate => candidate.LibraryTrackId == best.LibraryTrackId);
        if (scores.Count > 1 &&
            best.Confidence >= _policy.SuggestThreshold &&
            best.Confidence - scores[1].Confidence <= _policy.AmbiguityDelta)
        {
            return Result(
                TrackMatchReviewState.Ambiguous,
                null,
                best.Confidence,
                scores,
                best.Reasons,
                ["top_candidates_within_ambiguity_delta"],
                scope);
        }

        var state = best.Confidence >= _policy.AcceptThreshold
            ? TrackMatchReviewState.Accepted
            : best.Confidence >= _policy.SuggestThreshold
                ? TrackMatchReviewState.Suggested
                : TrackMatchReviewState.Unresolved;
        return Result(
            state,
            state == TrackMatchReviewState.Accepted ? selected : null,
            best.Confidence,
            scores,
            best.Reasons,
            state == TrackMatchReviewState.Unresolved ? ["below_suggestion_threshold"] : [],
            scope);
    }

    private TrackMatchCandidateScore ScoreCandidate(
        ExternalTrackMatchSnapshot source,
        LocalTrackMatchCandidate candidate)
    {
        var reasons = new List<string>();
        var warnings = new List<string>();
        if (EqualsNormalized(source.MusicBrainzRecordingId, candidate.MusicBrainzRecordingId))
        {
            return Score(candidate, 1, ["musicbrainz_recording_id_exact"], warnings,
                new Dictionary<string, double> { ["musicbrainzRecordingId"] = 1 });
        }

        if (EqualsNormalized(source.Isrc, candidate.Isrc))
        {
            return Score(candidate, 0.99, ["isrc_exact"], warnings,
                new Dictionary<string, double> { ["isrc"] = 1 });
        }

        if (TryGetProviderTrackId(candidate.ProviderTrackIds, source.ProviderId, out var providerId) &&
            providerId.Equals(source.ExternalId, StringComparison.Ordinal))
        {
            return Score(candidate, 1, ["provider_track_id_exact"], warnings,
                new Dictionary<string, double> { ["providerTrackId"] = 1 });
        }

        var title = Similarity(source.Title, candidate.Title);
        var artist = ArtistSimilarity(source.Artist, candidate.Artist);
        var album = Similarity(source.Album, candidate.Album);
        var albumArtist = Similarity(source.AlbumArtist, candidate.AlbumArtist);
        var duration = DurationScore(source.DurationSeconds, candidate.DurationSeconds);
        AddReason(reasons, "title", title);
        AddReason(reasons, "artist", artist);
        AddReason(reasons, "album", album);
        AddReason(reasons, "album_artist", albumArtist);
        if (duration > 0)
        {
            reasons.Add(duration >= 0.9 ? "duration_close" : "duration_partial");
        }

        var confidence = (title * 0.42) +
                         (artist * 0.30) +
                         (Math.Max(album, albumArtist) * 0.12) +
                         (duration * 0.16);
        if (source.IsExplicit.HasValue &&
            candidate.IsExplicit.HasValue &&
            source.IsExplicit != candidate.IsExplicit)
        {
            confidence = Math.Max(0, confidence - 0.12);
            warnings.Add("explicit_flag_mismatch");
        }

        return Score(candidate, Math.Round(confidence, 4), reasons, warnings,
            new Dictionary<string, double>
            {
                ["title"] = Math.Round(title, 4),
                ["artist"] = Math.Round(artist, 4),
                ["album"] = Math.Round(album, 4),
                ["albumArtist"] = Math.Round(albumArtist, 4),
                ["duration"] = Math.Round(duration, 4)
            });
    }

    private static bool TryGetProviderTrackId(
        IReadOnlyDictionary<string, string>? providerTrackIds,
        string providerId,
        out string trackId)
    {
        trackId = string.Empty;
        if (providerTrackIds == null || string.IsNullOrWhiteSpace(providerId))
        {
            return false;
        }

        if (providerTrackIds.TryGetValue(providerId, out var exact) &&
            !string.IsNullOrWhiteSpace(exact))
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

    private double DurationScore(int? source, int? candidate)
    {
        if (!source.HasValue || !candidate.HasValue)
        {
            return 0.5;
        }

        var delta = Math.Abs(source.Value - candidate.Value);
        return delta == 0
            ? 1
            : delta <= 2
                ? 0.95
                : delta <= _policy.DurationToleranceSeconds
                    ? 1 - (0.5 * delta / _policy.DurationToleranceSeconds)
                    : 0;
    }

    private static double Similarity(string? left, string? right) =>
        string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)
            ? 0
            : FuzzyMatcher.CalculateSimilarityAggressive(left, right) / 100d;

    private static double ArtistSimilarity(string? left, string? right)
    {
        var sourceArtists = SplitArtists(left);
        var candidateArtists = SplitArtists(right);
        if (sourceArtists.Count == 0 || candidateArtists.Count == 0)
        {
            return 0;
        }

        return FuzzyMatcher.CalculateArtistMatchScore(
            sourceArtists,
            candidateArtists[0],
            candidateArtists.Skip(1).ToList()) / 100d;
    }

    private static List<string> SplitArtists(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : Regex.Split(
                    value,
                    @"\s*(?:,|&|;|\bfeat(?:uring)?\.?\b|\bft\.?\b|\bwith\b)\s*",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                .Select(artist => artist.Trim())
                .Where(artist => artist.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

    private static bool EqualsNormalized(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) &&
        !string.IsNullOrWhiteSpace(right) &&
        left.Replace("-", string.Empty, StringComparison.Ordinal)
            .Equals(
                right.Replace("-", string.Empty, StringComparison.Ordinal),
                StringComparison.OrdinalIgnoreCase);

    private static bool IsVisible(TrackMatchScope scope, LocalTrackMatchCandidate candidate) =>
        candidate.TenantId == scope.TenantId &&
        candidate.BackendInstanceId.Equals(scope.BackendInstanceId, StringComparison.Ordinal) &&
        candidate.LibraryScopeId.Equals(scope.LibraryScopeId, StringComparison.Ordinal) &&
        (!candidate.OwnerUserId.HasValue || candidate.OwnerUserId == scope.UserId);

    private static void ValidateScope(TrackMatchScope scope)
    {
        if (scope.TenantId == Guid.Empty ||
            scope.UserId == Guid.Empty ||
            scope.ProviderAccountId == Guid.Empty ||
            scope.PolicyVersion <= 0 ||
            scope.SourceSnapshotVersion <= 0 ||
            string.IsNullOrWhiteSpace(scope.BackendInstanceId) ||
            string.IsNullOrWhiteSpace(scope.LibraryScopeId))
        {
            throw new ArgumentException("A complete scoped match context is required.", nameof(scope));
        }
    }

    private static void ValidateSource(ExternalTrackMatchSnapshot source)
    {
        if (string.IsNullOrWhiteSpace(source.SnapshotId) ||
            string.IsNullOrWhiteSpace(source.ProviderId) ||
            string.IsNullOrWhiteSpace(source.ExternalId) ||
            string.IsNullOrWhiteSpace(source.Title) ||
            string.IsNullOrWhiteSpace(source.Artist))
        {
            throw new ArgumentException("The external track snapshot is incomplete.", nameof(source));
        }
    }

    private static void ValidateOverride(
        TrackMatchScope scope,
        ExternalTrackMatchSnapshot source,
        ScopedTrackMatchOverride? value)
    {
        if (value != null &&
            (value.TenantId != scope.TenantId ||
             value.UserId != scope.UserId ||
             !value.LibraryScopeId.Equals(scope.LibraryScopeId, StringComparison.Ordinal) ||
             !value.ProviderId.Equals(source.ProviderId, StringComparison.Ordinal) ||
             !value.ExternalId.Equals(source.ExternalId, StringComparison.Ordinal)))
        {
            throw new UnauthorizedAccessException("The manual override is outside the match scope.");
        }
    }

    private static void AddReason(List<string> reasons, string signal, double score)
    {
        if (score >= 1)
        {
            reasons.Add($"{signal}_exact");
        }
        else if (score >= 0.85)
        {
            reasons.Add($"{signal}_strong");
        }
        else if (score >= 0.65)
        {
            reasons.Add($"{signal}_partial");
        }
    }

    private static TrackMatchCandidateScore Score(
        LocalTrackMatchCandidate candidate,
        double confidence,
        IReadOnlyList<string> reasons,
        IReadOnlyList<string> warnings,
        IReadOnlyDictionary<string, double>? components = null) => new(
        candidate.LibraryTrackId,
        candidate.BackendItemId,
        confidence,
        reasons,
        warnings,
        components);

    private TrackMatchDecision Result(
        TrackMatchReviewState state,
        LocalTrackMatchCandidate? selected,
        double confidence,
        IReadOnlyList<TrackMatchCandidateScore> candidates,
        IReadOnlyList<string> reasons,
        IReadOnlyList<string> warnings,
        TrackMatchScope scope) => new(
        state,
        selected?.LibraryTrackId,
        selected?.BackendItemId,
        confidence,
        candidates,
        reasons,
        warnings,
        scope.PolicyVersion,
        scope.SourceSnapshotVersion,
        _policy.AcceptThreshold,
        _policy.SuggestThreshold,
        _policy.AmbiguityDelta,
        state is TrackMatchReviewState.Suggested or TrackMatchReviewState.Ambiguous or
            TrackMatchReviewState.Unresolved or TrackMatchReviewState.Rejected);
}
