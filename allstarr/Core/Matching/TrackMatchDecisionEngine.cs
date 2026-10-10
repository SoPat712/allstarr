using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Storage;
using allstarr.Services.Common;

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
    Guid UserId,
    string BackendInstanceId,
    Guid ProviderAccountId,
    int PolicyVersion,
    long SourceSnapshotVersion,
    IReadOnlySet<string> AccessibleLibraryIds);

public sealed record ExternalTrackMatchSnapshot(
    string SnapshotId,
    string ProviderId,
    string ExternalId,
    string Title,
    string Artist,
    string? Album,
    string? AlbumArtist,
    long? DurationMilliseconds,
    string? Isrc,
    string? MusicBrainzRecordingId,
    bool? IsExplicit,
    Guid? CanonicalRecordingId = null);

public sealed record LocalTrackMatchCandidate(
    Guid LibraryTrackId,
    Guid? OwnerUserId,
    string BackendInstanceId,
    string? BackendLibraryId,
    string BackendItemId,
    Guid? CanonicalRecordingId,
    string Title,
    string Artist,
    string? Album,
    string? AlbumArtist,
    long? DurationMilliseconds,
    string? Isrc,
    string? MusicBrainzRecordingId,
    bool? IsExplicit,
    IReadOnlyDictionary<string, string>? ProviderTrackIds = null,
    bool IsLocal = true);

public sealed record ScopedTrackMatchOverride(
    Guid UserId,
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
    IReadOnlyDictionary<string, double>? Components = null,
    string? Title = null,
    string? Artist = null,
    string? Album = null,
    long? DurationMilliseconds = null,
    string? SourceIsrc = null,
    string? CandidateIsrc = null,
    IReadOnlyDictionary<string, string>? ProviderTrackIds = null,
    string? NormalizedSourceTitle = null,
    string? NormalizedCandidateTitle = null,
    double? ArtistOverlap = null,
    double? AlbumEvidence = null,
    long? DurationDeltaMilliseconds = null,
    bool IsLocal = true);

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
    double AmbiguityDelta = 0.005,
    bool RequiresReview = false);

public sealed class TrackMatchPolicy
{
    public double LocalPriorityWindow { get; init; } = 0.07;

    public IReadOnlyList<double> ProviderPriorityWindows { get; init; } = [0.05, 0.03, 0.01];

    public double AcceptThreshold { get; init; } = 0.88;

    public double SuggestThreshold { get; init; } = 0.72;

    public double AmbiguityDelta { get; init; } = 0.005;

    public int DurationToleranceSeconds { get; init; } = 8;

    public void Validate()
    {
        if (LocalPriorityWindow is < 0 or > 1 ||
            ProviderPriorityWindows == null ||
            ProviderPriorityWindows.Any(window => window is < 0 or > 1) ||
            AcceptThreshold is <= 0 or > 1 ||
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
    public const string AlgorithmVersion = "accepted-routing-v19";
    private const double ScoreEpsilon = 0.0000001;

    private readonly TrackMatchPolicy _policy;

    public TrackMatchDecisionEngine(TrackMatchPolicy? policy = null)
    {
        _policy = policy ?? new TrackMatchPolicy();
        _policy.Validate();
    }

    public TrackMatchDecisionEngine WithLocalPriorityWindow(double value) => new(new TrackMatchPolicy
    {
        LocalPriorityWindow = value,
        ProviderPriorityWindows = _policy.ProviderPriorityWindows,
        AcceptThreshold = _policy.AcceptThreshold,
        SuggestThreshold = _policy.SuggestThreshold,
        AmbiguityDelta = _policy.AmbiguityDelta,
        DurationToleranceSeconds = _policy.DurationToleranceSeconds
    });

    public static long LibraryIndexRevision(IEnumerable<LocalTrackMatchCandidate> candidates)
    {
        var json = JsonSerializer.Serialize(candidates
            .OrderBy(candidate => candidate.LibraryTrackId)
            .Select(candidate => new
            {
                candidate.LibraryTrackId,
                candidate.CanonicalRecordingId,
                candidate.Title,
                candidate.Artist,
                candidate.Album,
                candidate.AlbumArtist,
                candidate.DurationMilliseconds,
                candidate.Isrc,
                candidate.MusicBrainzRecordingId,
                candidate.IsExplicit,
                candidate.IsLocal,
                ProviderTrackIds = candidate.ProviderTrackIds?.OrderBy(item => item.Key)
            }));
        return BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    public TrackMatchCandidateSet PrepareCandidates(IEnumerable<LocalTrackMatchCandidate> candidates)
    {
        var items = candidates.ToArray();
        return new(new TrackMatchCandidateIndex(items), LibraryIndexRevision(items));
    }

    public IReadOnlyList<TrackMatchCandidateScore> ScoreCandidates(
        ExternalTrackMatchSnapshot source,
        IEnumerable<LocalTrackMatchCandidate> candidates,
        IReadOnlyList<string>? providerPriority = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(candidates);
        ValidateSource(source);
        var ranked = candidates
            .Select(candidate => new RankedCandidate(
                candidate,
                ScoreCandidate(source, candidate),
                Priority(candidate, providerPriority)))
            .OrderByDescending(candidate => candidate.Score.Confidence)
            .ThenBy(candidate => candidate.Score.LibraryTrackId)
            .ToArray();
        if (ranked.Length == 0) return [];

        var highestConfidence = ranked[0].Score.Confidence;
        var accepted = ranked.Where(candidate => IsAcceptanceQualified(candidate.Score)).ToArray();
        var eligible = accepted.Length > 0 ? accepted : ranked
            .Where(candidate => candidate.Score.Confidence + candidate.Priority.Window + ScoreEpsilon >=
                                highestConfidence);
        var selected = eligible
            .OrderBy(candidate => candidate.Priority.Rank)
            .ThenByDescending(candidate => candidate.Score.Confidence)
            .ThenBy(candidate => candidate.Score.LibraryTrackId)
            .First();
        return ranked
            .OrderBy(candidate => candidate.Score.LibraryTrackId == selected.Score.LibraryTrackId ? 0 : 1)
            .ThenByDescending(candidate => candidate.Score.Confidence)
            .ThenBy(candidate => candidate.Score.LibraryTrackId)
            .Select(candidate => ExplainPrioritySelection(
                candidate,
                candidate.Score.LibraryTrackId == selected.Score.LibraryTrackId &&
                candidate.Score.LibraryTrackId != ranked[0].Score.LibraryTrackId))
            .ToArray();
    }

    private TrackMatchCandidateScore ExplainPrioritySelection(
        RankedCandidate candidate,
        bool displacedHigherConfidence)
    {
        var components = new Dictionary<string, double>(
            candidate.Score.Components ?? new Dictionary<string, double>())
        {
            ["priorityWindow"] = candidate.Priority.Window,
            ["routingPriority"] = candidate.Priority.Rank,
            ["acceptanceQualified"] = IsAcceptanceQualified(candidate.Score) ? 1 : 0
        };
        return candidate.Score with
        {
            Components = components,
            Reasons = displacedHigherConfidence
                ? candidate.Score.Reasons
                    .Append(IsAcceptanceQualified(candidate.Score)
                        ? "accepted_route_priority_selected"
                        : candidate.Candidate.IsLocal
                        ? "local_priority_window_selected"
                        : "provider_priority_window_selected")
                    .ToArray()
                : candidate.Score.Reasons
        };
    }

    private CandidatePriority Priority(
        LocalTrackMatchCandidate candidate,
        IReadOnlyList<string>? providerPriority)
    {
        if (candidate.IsLocal) return new(0, _policy.LocalPriorityWindow);
        if (candidate.ProviderTrackIds == null || providerPriority == null) return CandidatePriority.Fallback;

        var providers = candidate.ProviderTrackIds.Keys
            .Select(NormalizeProvider)
            .ToHashSet(StringComparer.Ordinal);
        var ordered = providerPriority
            .Select(NormalizeProvider)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var index = Array.FindIndex(ordered, providers.Contains);
        return index < 0
            ? CandidatePriority.Fallback
            : new(index + 1, index < _policy.ProviderPriorityWindows.Count
                ? _policy.ProviderPriorityWindows[index]
                : 0);
    }

    private static string NormalizeProvider(string provider)
    {
        var normalized = provider.Trim().ToLowerInvariant()
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal);
        return normalized == "applemusic" ? "appledownload" : normalized;
    }

    public bool CanSkipProviderComparison(TrackMatchDecision decision) =>
        decision.State == TrackMatchReviewState.Accepted &&
        decision.Candidates.FirstOrDefault() is { IsLocal: true };

    private bool IsAcceptanceQualified(TrackMatchCandidateScore score) =>
        score.Confidence >= _policy.AcceptThreshold &&
        HasStrongArtistEvidence(score) &&
        HasDurationWithinTolerance(score);

    private static bool HasDurationWithinTolerance(TrackMatchCandidateScore score) =>
        score.Components == null ||
        !score.Components.TryGetValue("duration", out var duration) ||
        duration >= 0.5;

    public TrackMatchDecision Decide(
        TrackMatchScope scope,
        ExternalTrackMatchSnapshot source,
        TrackMatchCandidateSet candidates,
        ScopedTrackMatchOverride? manualOverride = null,
        IReadOnlyList<string>? providerPriority = null) =>
        Decide(scope, source, candidates.Select(source), manualOverride, providerPriority);

    public TrackMatchDecision Decide(
        TrackMatchScope scope,
        ExternalTrackMatchSnapshot source,
        IReadOnlyList<LocalTrackMatchCandidate> candidates,
        ScopedTrackMatchOverride? manualOverride = null,
        IReadOnlyList<string>? providerPriority = null)
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
                    [Score(source, pinned, 1, ["manual_override_pinned"], [])],
                    ["manual_override_pinned"],
                    [],
                    scope);
        }

        var rankedScores = ScoreCandidates(source, visible, providerPriority);
        if (rankedScores.Count == 0)
        {
            return Result(
                scopedCandidates.Count > 0 && manualOverride?.RejectedLibraryTrackIds?.Count > 0
                    ? TrackMatchReviewState.Rejected
                    : TrackMatchReviewState.Unresolved,
                null,
                0,
                [],
                [],
                [scopedCandidates.Count > 0 ? "manual_override_rejected_all" : "no_indexed_candidate"],
                scope);
        }

        var scores = rankedScores.Take(20).ToList();
        var best = scores[0];
        var selected = visible.Single(candidate => candidate.LibraryTrackId == best.LibraryTrackId);
        var selectedPriority = Priority(selected, providerPriority);
        var runnerUp = rankedScores.FirstOrDefault(score =>
            score.LibraryTrackId != best.LibraryTrackId &&
            Priority(
                visible.Single(candidate => candidate.LibraryTrackId == score.LibraryTrackId),
                providerPriority).Rank == selectedPriority.Rank &&
            !SameRecordingIdentity(
                selected,
                visible.Single(candidate => candidate.LibraryTrackId == score.LibraryTrackId)));
        if (runnerUp != null &&
            best.Confidence >= _policy.SuggestThreshold &&
            best.Confidence - runnerUp.Confidence <= _policy.AmbiguityDelta)
        {
            return Result(
                TrackMatchReviewState.Ambiguous,
                null,
                best.Confidence,
                scores,
                best.Reasons,
                ["ambiguous_top_candidates"],
                scope);
        }

        var decisionScore = best.Confidence;
        var strongArtistEvidence = HasStrongArtistEvidence(best);
        var durationWithinTolerance = HasDurationWithinTolerance(best);
        var state = decisionScore >= _policy.AcceptThreshold && strongArtistEvidence && durationWithinTolerance
            ? TrackMatchReviewState.Accepted
            : decisionScore >= _policy.SuggestThreshold && strongArtistEvidence
                ? TrackMatchReviewState.Suggested
                : TrackMatchReviewState.Unresolved;
        return Result(
            state,
            state is TrackMatchReviewState.Accepted or TrackMatchReviewState.Suggested
                ? selected
                : null,
            decisionScore,
            scores,
            best.Reasons,
            state switch
            {
                TrackMatchReviewState.Suggested => ["below_accept_threshold_review"],
                TrackMatchReviewState.Unresolved when
                    decisionScore >= _policy.SuggestThreshold && !strongArtistEvidence => ["weak_artist_evidence_review"],
                TrackMatchReviewState.Unresolved => ["below_suggestion_threshold"],
                _ => []
            },
            scope);
    }

    private sealed record RankedCandidate(
        LocalTrackMatchCandidate Candidate,
        TrackMatchCandidateScore Score,
        CandidatePriority Priority);

    private readonly record struct CandidatePriority(int Rank, double Window)
    {
        public static CandidatePriority Fallback { get; } = new(int.MaxValue, 0);
    }

    private bool HasStrongArtistEvidence(TrackMatchCandidateScore score) =>
        score.Components == null ||
        !score.Components.TryGetValue("artist", out var artistScore) ||
        artistScore >= 0.7 ||
        (score.Confidence >= _policy.AcceptThreshold &&
         score.Components.GetValueOrDefault("primaryArtist") >= 0.98 &&
         score.Components.GetValueOrDefault("title") >= 0.98 &&
         score.Components.GetValueOrDefault("album") >= 0.98 &&
         score.Components.GetValueOrDefault("duration") >= 0.95);

    private TrackMatchCandidateScore ScoreCandidate(
        ExternalTrackMatchSnapshot source,
        LocalTrackMatchCandidate candidate)
    {
        var outcome = TrackMatchRuleSet.Evaluate(source, candidate, _policy);
        return Score(source, candidate, outcome.Confidence, outcome.Reasons, outcome.Warnings, outcome.Components);
    }

    internal static bool SameRecordingIdentity(
        LocalTrackMatchCandidate left,
        LocalTrackMatchCandidate right)
    {
        if (left.CanonicalRecordingId.HasValue &&
            left.CanonicalRecordingId == right.CanonicalRecordingId)
            return true;

        var leftRecording = TrackMatchSignals.RecordingIdentity(left);
        var rightRecording = TrackMatchSignals.RecordingIdentity(right);
        if (!string.IsNullOrWhiteSpace(leftRecording) &&
            !string.IsNullOrWhiteSpace(rightRecording))
            return FuzzyMatcher.IdentifiersEqual(leftRecording, rightRecording);

        if (left.CanonicalRecordingId.HasValue &&
            right.CanonicalRecordingId.HasValue &&
            string.IsNullOrWhiteSpace(leftRecording) &&
            string.IsNullOrWhiteSpace(rightRecording))
            return false;
        if (!FuzzyMatcher.SemanticVersionTags(left.Title)
                .SetEquals(FuzzyMatcher.SemanticVersionTags(right.Title)) ||
            !left.DurationMilliseconds.HasValue ||
            !right.DurationMilliseconds.HasValue ||
            Math.Abs(left.DurationMilliseconds.Value - right.DurationMilliseconds.Value) > 3_000)
            return false;

        var leftTitle = FuzzyMatcher.NormalizeForMatching(
            FuzzyMatcher.StripDecorators(left.Title));
        var rightTitle = FuzzyMatcher.NormalizeForMatching(
            FuzzyMatcher.StripDecorators(right.Title));
        var sameTitle = leftTitle.Equals(rightTitle, StringComparison.Ordinal);
        var sameArtist = FuzzyMatcher.SplitCredits(left.Artist)
            .Select(FuzzyMatcher.NormalizeForMatching)
            .ToHashSet(StringComparer.Ordinal)
            .SetEquals(
                FuzzyMatcher.SplitCredits(right.Artist)
                    .Select(FuzzyMatcher.NormalizeForMatching)
                    .ToHashSet(StringComparer.Ordinal));
        var sameAlbum = !string.IsNullOrWhiteSpace(left.Album) &&
                        !string.IsNullOrWhiteSpace(right.Album) &&
                        FuzzyMatcher.NormalizeForMatching(left.Album)
                            .Equals(
                                FuzzyMatcher.NormalizeForMatching(right.Album),
                                StringComparison.Ordinal);
        return sameTitle && (sameArtist || sameAlbum) ||
               TrackMatchSignals.Similarity(leftTitle, rightTitle) >= 0.9 &&
               (sameAlbum ||
                TrackMatchSignals.ArtistSimilarity(left.Artist, right.Artist) >= 0.85 &&
                TrackMatchSignals.ComparableSimilarity(left.Album, right.Album) >= 0.9);
    }

    private static bool IsVisible(TrackMatchScope scope, LocalTrackMatchCandidate candidate) =>
        candidate.BackendInstanceId.Equals(scope.BackendInstanceId, StringComparison.Ordinal) &&
        (candidate.IsLocal
            ? candidate.BackendLibraryId != null && scope.AccessibleLibraryIds.Contains(candidate.BackendLibraryId)
            : (!candidate.OwnerUserId.HasValue || candidate.OwnerUserId == scope.UserId));

    private static void ValidateScope(TrackMatchScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope.AccessibleLibraryIds);
        if (scope.UserId == Guid.Empty ||
            scope.ProviderAccountId == Guid.Empty ||
            scope.PolicyVersion <= 0 ||
            scope.SourceSnapshotVersion <= 0 ||
            string.IsNullOrWhiteSpace(scope.BackendInstanceId))
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
            (value.UserId != scope.UserId ||
             !value.ProviderId.Equals(source.ProviderId, StringComparison.Ordinal) ||
             !value.ExternalId.Equals(source.ExternalId, StringComparison.Ordinal)))
        {
            throw new UnauthorizedAccessException("The manual override is outside the match scope.");
        }
    }

    private static TrackMatchCandidateScore Score(
        ExternalTrackMatchSnapshot source,
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
        components,
        candidate.Title,
        candidate.Artist,
        candidate.Album,
        candidate.DurationMilliseconds,
        source.Isrc,
        candidate.Isrc,
        candidate.ProviderTrackIds,
        FuzzyMatcher.NormalizeForMatching(FuzzyMatcher.StripDecorators(source.Title)),
        FuzzyMatcher.NormalizeForMatching(FuzzyMatcher.StripDecorators(candidate.Title)),
        TrackMatchSignals.ArtistSimilarity(source.Artist, candidate.Artist),
        AlbumEvidence(source, candidate),
        source.DurationMilliseconds.HasValue && candidate.DurationMilliseconds.HasValue
            ? Math.Abs(source.DurationMilliseconds.Value - candidate.DurationMilliseconds.Value)
            : null,
        candidate.IsLocal);

    private static double? AlbumEvidence(
        ExternalTrackMatchSnapshot source,
        LocalTrackMatchCandidate candidate)
    {
        var album = TrackMatchSignals.ComparableSimilarity(source.Album, candidate.Album);
        var albumArtist = TrackMatchSignals.ComparableSimilarity(source.AlbumArtist, candidate.AlbumArtist);
        return album.HasValue || albumArtist.HasValue
            ? Math.Max(album ?? 0, albumArtist ?? 0)
            : null;
    }

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

public sealed class TrackMatchCandidateSet(TrackMatchCandidateIndex index, long revision)
{
    public long Revision { get; } = revision;

    internal IReadOnlyList<LocalTrackMatchCandidate> Select(ExternalTrackMatchSnapshot source) =>
        index.Select(source);
}

public static class TrackMatchOverridePolicy
{
    public static Guid? TopCandidateLibraryTrackId(string? candidatesJson)
    {
        try
        {
            using var document = JsonDocument.Parse(candidatesJson ?? "[]");
            if (document.RootElement.ValueKind != JsonValueKind.Array ||
                document.RootElement.GetArrayLength() == 0)
                return null;
            var candidate = document.RootElement[0];
            return candidate.TryGetProperty("LibraryTrackId", out var id) &&
                   id.TryGetGuid(out var value)
                ? value
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static bool IsEffectiveRejection(
        ManualTrackOverrideRecord? manual,
        TrackMatchRecord? decision)
    {
        if (manual?.Decision != ManualOverrideDecision.Reject)
            return false;
        if (!manual.LibraryTrackId.HasValue)
            return true;
        if (manual.MatcherVersion != TrackMatchDecisionEngine.AlgorithmVersion)
            return false;
        return decision == null ||
               decision.State == TrackMatchState.Rejected ||
               decision.LibraryTrackId == manual.LibraryTrackId ||
               TopCandidateLibraryTrackId(decision.CandidateResultsJson) == manual.LibraryTrackId;
    }
}

public sealed class TrackMatchCandidateIndex
{
    private readonly IReadOnlyDictionary<Guid, IReadOnlyList<LocalTrackMatchCandidate>> _byCanonical;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<LocalTrackMatchCandidate>> _byIsrc;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<LocalTrackMatchCandidate>> _byMatchKey;

    public TrackMatchCandidateIndex(IEnumerable<LocalTrackMatchCandidate> candidates)
    {
        var items = candidates.ToArray();
        _byCanonical = items
            .Where(item => item.CanonicalRecordingId.HasValue)
            .GroupBy(item => item.CanonicalRecordingId!.Value)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<LocalTrackMatchCandidate>)group.ToArray());
        _byIsrc = items
            .Where(item => NormalizeIsrc(item.Isrc) != null)
            .GroupBy(item => NormalizeIsrc(item.Isrc)!, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<LocalTrackMatchCandidate>)group.ToArray(),
                StringComparer.Ordinal);
        _byMatchKey = items
            .SelectMany(candidate => BuildMatchKeys(candidate.Title, candidate.Artist)
                .Select(key => new { Key = key, Candidate = candidate }))
            .GroupBy(item => item.Key, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<LocalTrackMatchCandidate>)group
                    .Select(item => item.Candidate)
                    .DistinctBy(item => item.LibraryTrackId)
                    .ToArray(),
                StringComparer.Ordinal);
    }

    public IReadOnlyList<LocalTrackMatchCandidate> Select(ExternalTrackMatchSnapshot source)
    {
        if (source.CanonicalRecordingId.HasValue &&
            _byCanonical.TryGetValue(source.CanonicalRecordingId.Value, out var canonicalCandidates))
            return canonicalCandidates;

        var isrc = NormalizeIsrc(source.Isrc);
        if (isrc != null && _byIsrc.TryGetValue(isrc, out var isrcCandidates))
            return isrcCandidates;

        var keys = BuildMatchKeys(source.Title, source.Artist).ToArray();
        if (keys.Length == 0) return [];
        var exactPair = keys.FirstOrDefault(key => key.StartsWith("title-artist:", StringComparison.Ordinal));
        if (exactPair != null && _byMatchKey.TryGetValue(exactPair, out var pairCandidates))
            return pairCandidates;
        var exactTitle = keys.FirstOrDefault(key => key.StartsWith("title:", StringComparison.Ordinal));
        if (exactTitle != null && _byMatchKey.TryGetValue(exactTitle, out var titleCandidates))
            return titleCandidates;

        var selected = new Dictionary<Guid, LocalTrackMatchCandidate>();
        foreach (var prefix in new[] { "token-pair:", "title-token:" })
        {
            foreach (var key in keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)))
            {
                if (!_byMatchKey.TryGetValue(key, out var candidates)) continue;
                foreach (var candidate in candidates)
                {
                    selected.TryAdd(candidate.LibraryTrackId, candidate);
                    if (selected.Count >= 300) return selected.Values.ToArray();
                }
            }
        }
        return selected.Values.ToArray();
    }

    private static IEnumerable<string> BuildMatchKeys(string? titleValue, string? artistValue)
    {
        var title = FuzzyMatcher.NormalizeForMatching(FuzzyMatcher.StripDecorators(titleValue ?? string.Empty));
        var artist = FuzzyMatcher.NormalizeForMatching(artistValue ?? string.Empty);
        if (title.Length == 0) yield break;
        yield return $"title:{title}";
        if (artist.Length > 0) yield return $"title-artist:{title}|{artist}";

        var titleTokens = title.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(IsMeaningfulToken).Distinct(StringComparer.Ordinal).Take(8).ToArray();
        var artistTokens = artist.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(IsMeaningfulToken).Distinct(StringComparer.Ordinal).Take(4).ToArray();
        foreach (var token in titleTokens) yield return $"title-token:{token}";
        foreach (var titleToken in titleTokens)
            foreach (var artistToken in artistTokens)
                yield return $"token-pair:{titleToken}|{artistToken}";
    }

    private static string? NormalizeIsrc(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Replace("-", string.Empty, StringComparison.Ordinal).Trim().ToUpperInvariant();
        return normalized.Length == 12 && normalized.All(char.IsLetterOrDigit) ? normalized : null;
    }

    private static bool IsMeaningfulToken(string token) => token.Length >= 3 && token is not
        ("the" or "and" or "feat" or "with" or "from" or "remaster" or "remastered" or "version" or "edit" or "mix");
}
