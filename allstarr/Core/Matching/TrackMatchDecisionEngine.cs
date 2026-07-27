using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Storage;
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
    long? DurationMilliseconds,
    string? Isrc,
    string? MusicBrainzRecordingId,
    bool? IsExplicit,
    Guid? CanonicalRecordingId = null);

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
    long? DurationMilliseconds,
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
    long? DurationDeltaMilliseconds = null);

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
    public const string AlgorithmVersion = "normalized-v4";

    private readonly TrackMatchPolicy _policy;

    public TrackMatchDecisionEngine(TrackMatchPolicy? policy = null)
    {
        _policy = policy ?? new TrackMatchPolicy();
        _policy.Validate();
    }

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
                ProviderTrackIds = candidate.ProviderTrackIds?.OrderBy(item => item.Key)
            }));
        return BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    public TrackMatchCandidateSet PrepareCandidates(IEnumerable<LocalTrackMatchCandidate> candidates)
    {
        var items = candidates.ToArray();
        return new(new TrackMatchCandidateIndex(items), LibraryIndexRevision(items));
    }

    public TrackMatchDecision Decide(
        TrackMatchScope scope,
        ExternalTrackMatchSnapshot source,
        TrackMatchCandidateSet candidates,
        ScopedTrackMatchOverride? manualOverride = null) =>
        Decide(scope, source, candidates.Select(source), manualOverride);

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
                    [Score(source, pinned, 1, ["manual_override_pinned"], [])],
                    ["manual_override_pinned"],
                    [],
                    scope);
        }

        var scores = visible
            .Select(candidate => ScoreCandidate(source, candidate))
            .OrderByDescending(candidate => candidate.Confidence)
            .ThenBy(candidate => candidate.LibraryTrackId)
            .Take(20)
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
                [scopedCandidates.Count > 0 ? "manual_override_rejected_all" : "no_indexed_candidate"],
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
                ["ambiguous_top_candidates"],
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
        if (source.CanonicalRecordingId.HasValue &&
            source.CanonicalRecordingId == candidate.CanonicalRecordingId)
        {
            return Score(source, candidate, 1, ["canonical_recording_id_exact"], warnings,
                new Dictionary<string, double> { ["canonicalRecordingId"] = 1 });
        }
        if (EqualsNormalized(source.MusicBrainzRecordingId, candidate.MusicBrainzRecordingId))
        {
            return Score(source, candidate, 1, ["musicbrainz_recording_id_exact"], warnings,
                new Dictionary<string, double> { ["musicbrainzRecordingId"] = 1 });
        }

        if (EqualsNormalized(source.Isrc, candidate.Isrc))
        {
            return Score(source, candidate, 0.99, ["isrc_exact"], warnings,
                new Dictionary<string, double> { ["isrc"] = 1 });
        }

        if (TryGetProviderTrackId(candidate.ProviderTrackIds, source.ProviderId, out var providerId) &&
            providerId.Equals(source.ExternalId, StringComparison.Ordinal))
        {
            return Score(source, candidate, 1, ["provider_track_id_exact"], warnings,
                new Dictionary<string, double> { ["providerTrackId"] = 1 });
        }

        var title = Similarity(source.Title, candidate.Title);
        var artist = ArtistSimilarity(source.Artist, candidate.Artist);
        var album = Similarity(source.Album, candidate.Album);
        var albumArtist = Similarity(source.AlbumArtist, candidate.AlbumArtist);
        var duration = DurationScore(source.DurationMilliseconds, candidate.DurationMilliseconds);
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
        if (title >= 0.98 && artist >= 0.88 && duration >= 0.9)
            confidence = Math.Max(confidence, 0.9);
        if (!FuzzyMatcher.SemanticVersionTags(source.Title)
                .SetEquals(FuzzyMatcher.SemanticVersionTags(candidate.Title)))
        {
            confidence = Math.Max(0, confidence - 0.18);
            warnings.Add("semantic_version_mismatch");
        }
        if (source.IsExplicit.HasValue &&
            candidate.IsExplicit.HasValue &&
            source.IsExplicit != candidate.IsExplicit)
        {
            confidence = Math.Max(0, confidence - 0.12);
            warnings.Add("explicit_flag_mismatch");
        }

        return Score(source, candidate, Math.Round(confidence, 4), reasons, warnings,
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

    private double DurationScore(long? source, long? candidate)
    {
        if (!source.HasValue || !candidate.HasValue)
        {
            return 0.5;
        }

        var delta = Math.Abs(source.Value - candidate.Value) / 1000d;
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

        static double BestScore(string artist, IReadOnlyList<string> candidates) =>
            candidates.Max(candidate =>
                FuzzyMatcher.CalculateSimilarityAggressive(artist, candidate) / 100d);

        var sourceCoverage = sourceArtists
            .Average(artist => BestScore(artist, candidateArtists));
        var candidatePrecision = candidateArtists
            .Average(artist => BestScore(artist, sourceArtists));
        var primaryArtist = FuzzyMatcher.CalculateSimilarityAggressive(
            sourceArtists[0],
            candidateArtists[0]) / 100d;

        // Backend libraries often retain only the primary artist while source
        // providers expose every featured artist. Treat a strong primary match as
        // authoritative supporting evidence instead of rejecting the candidate
        // because the credit-list lengths differ.
        var asymmetricCreditScore = (candidatePrecision * 0.75) + (sourceCoverage * 0.25);
        return Math.Round(
            sourceArtists.Count > 1 && candidateArtists.Count > 1
                ? asymmetricCreditScore
                : Math.Max(asymmetricCreditScore, primaryArtist * 0.85),
            4);
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
        ArtistSimilarity(source.Artist, candidate.Artist),
        Math.Max(Similarity(source.Album, candidate.Album),
            Similarity(source.AlbumArtist, candidate.AlbumArtist)),
        source.DurationMilliseconds.HasValue && candidate.DurationMilliseconds.HasValue
            ? Math.Abs(source.DurationMilliseconds.Value - candidate.DurationMilliseconds.Value)
            : null);

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
