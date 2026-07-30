using allstarr.Models.Domain;
using allstarr.Services.Common;
using Microsoft.Extensions.Logging;

namespace allstarr.Services.Spotify;

/// <summary>
/// Provider-agnostic descriptor for a single source track coming from any
/// injected playlist provider (Spotify today, Apple MusicKit in the works,
/// Deezer/Qobuz/extension playlists later). The injected matcher only needs
/// these fields — it does not care whether the source is a Spotify track,
/// an Apple MusicKit song, or anything else.
/// </summary>
public sealed record InjectedSourceTrack(
    string SourceId,
    string SourceProvider,
    string Title,
    IReadOnlyList<string> Artists,
    string? Isrc,
    int? DurationMs,
    string? Album = null,
    string? AlbumArtUrl = null,
    int Position = 0);

/// <summary>
/// Outcome of a per-provider match walk for a single injected source track.
/// </summary>
public sealed record PerProviderMatchResult(
    Song? MatchedSong,
    string? MatchType,
    string? ProviderUsed,
    double Score,
    IReadOnlyList<PerProviderAttempt> Walked,
    IReadOnlyList<PerProviderAcceptedMatch> AcceptedMatches);

public sealed record PerProviderAcceptedMatch(
    Song Song,
    string MatchType,
    string Provider,
    double Score);

/// <summary>
/// One step in a per-provider walk. The outcome describes why the matcher
/// moved on (typed miss, low score, or a provider that was not callable).
/// </summary>
public sealed record PerProviderAttempt(
    string Provider,
    string Query,
    int CandidateCount,
    double? TopScore,
    string Outcome,
    string? ReasonCode);

/// <summary>
/// Per-track, per-provider matcher for injected playlist tracks. Walks the
/// configured playback priority list in order and stops on the first
/// verified identity (ISRC) or score above the per-provider accept
/// threshold. Local library is the implicit first stop and the caller is
/// expected to pass `hasLocalMatch=true` when the local pass already won.
///
/// The walker is intentionally provider-agnostic. The injected source
/// describes itself through <see cref="InjectedSourceTrack"/>; the host
/// (Spotify, Apple MusicKit, etc.) decides which concrete metadata
/// services to register with the resolver.
/// </summary>
public static class PerProviderTrackMatcher
{
    public const string MatchTypeLocal = "fuzzy-local";
    public const string MatchTypeIsrc = "isrc";
    public const string MatchTypeProviderFuzzy = "fuzzy-provider";
    public const string MatchTypeTitleOnly = "title-only";
    public const string MatchTypeNone = "none";

    public const string OutcomeAccepted = "accepted";
    public const string OutcomeMissNotFound = "miss:not-found";
    public const string OutcomeMissNotPlayable = "miss:not-playable";
    public const string OutcomeLowScore = "miss:low-score";
    public const string OutcomeNoService = "miss:no-service";
    public const string OutcomeError = "miss:error";
    public const string OutcomeEmpty = "miss:empty-results";
    public const string OutcomeSkipped = "skip:isrc-disabled";

    public static PerProviderMatchResult NoMatch(IReadOnlyList<PerProviderAttempt> walked) =>
        new(null, null, null, 0.0, walked, Array.Empty<PerProviderAcceptedMatch>());

    public static PerProviderMatchResult FromLocal(Song song, double score) =>
        new(song, MatchTypeLocal, "local", score, Array.Empty<PerProviderAttempt>(),
            Array.Empty<PerProviderAcceptedMatch>());

    public static PerProviderMatchResult FromProvider(
        Song song,
        string matchType,
        string provider,
        double score,
        IReadOnlyList<PerProviderAttempt> walked) =>
        new(song, matchType, provider, score, walked,
            new[] { new PerProviderAcceptedMatch(song, matchType, provider, score) });

    public static PerProviderMatchResult FromCollectedProviders(
        Song? localMatch,
        double? localMatchScore,
        IReadOnlyList<PerProviderAcceptedMatch> acceptedMatches,
        IReadOnlyList<PerProviderAttempt> walked)
    {
        if (localMatch != null)
        {
            return new PerProviderMatchResult(
                localMatch,
                MatchTypeLocal,
                "local",
                localMatchScore ?? 100,
                walked,
                acceptedMatches);
        }

        var primary = acceptedMatches.FirstOrDefault();
        return primary == null
            ? NoMatch(walked)
            : new PerProviderMatchResult(
                primary.Song,
                primary.MatchType,
                primary.Provider,
                primary.Score,
                walked,
                acceptedMatches);
    }

    public static async Task<IReadOnlyList<Song>> SearchPlayableAsync(
        IEnumerable<IConcreteMetadataService> services,
        string providerId,
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        var service = PerProviderServiceResolver.Resolve(services, providerId);
        if (service == null)
        {
            return Array.Empty<Song>();
        }

        var songs = await service.SearchSongsAsync(query, limit, cancellationToken);
        return songs
            .Where(ExternalTrackPlaybackPolicy.CanUseForPlayback)
            .Where(song => !string.IsNullOrWhiteSpace(song.ExternalId))
            .GroupBy(song => song.ExternalId!, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(limit)
            .ToArray();
    }
}

/// <summary>
/// Per-provider accept thresholds and helper scoring logic. Centralized so
/// providers can be tuned without touching the walk loop.
/// </summary>
public sealed class PerProviderAcceptThresholds
{
    public double ProviderAcceptScore { get; init; } = 40;
    public double ArtistOverrideScore { get; init; } = 70;
    public double ArtistOverrideTitleScore { get; init; } = 30;
    public double TitleSubstringScore { get; init; } = 85;
    public int TitleOnlyProviderCount { get; init; } = 2;
}

public static class PerProviderTrackScorer
{
    public static (
        double TotalScore,
        int TitleScore,
        double ArtistScore,
        int AlbumScore,
        double DurationScore) Score(
        Song candidate,
        InjectedSourceTrack source)
    {
        var titleScore = FuzzyMatcher.CalculateSimilarityAggressive(source.Title, candidate.Title);
        var artistList = source.Artists is List<string> list ? list : source.Artists.ToList();
        var contributors = candidate.Contributors is List<string> contribs
            ? contribs
            : candidate.Contributors.ToList();
        var artistScore = FuzzyMatcher.CalculateArtistMatchScore(artistList, candidate.Artist, contributors);
        var albumScore = string.IsNullOrWhiteSpace(source.Album) || string.IsNullOrWhiteSpace(candidate.Album)
            ? 0
            : FuzzyMatcher.CalculateSimilarityAggressive(source.Album, candidate.Album);
        var durationScore = CalculateDurationScore(source.DurationMs, candidate.Duration);
        var total = (titleScore * 0.42) +
                    (artistScore * 0.30) +
                    (albumScore * 0.12) +
                    (durationScore * 0.16);
        return (total, titleScore, artistScore, albumScore, durationScore);
    }

    public static bool IsAcceptable(
        (
            double TotalScore,
            int TitleScore,
            double ArtistScore,
            int AlbumScore,
            double DurationScore) score,
        PerProviderAcceptThresholds thresholds)
    {
        if (score.TotalScore >= thresholds.ProviderAcceptScore)
        {
            return true;
        }

        if (score.ArtistScore >= thresholds.ArtistOverrideScore
            && score.TitleScore >= thresholds.ArtistOverrideTitleScore)
        {
            return true;
        }

        if (score.TitleScore >= thresholds.TitleSubstringScore)
        {
            return true;
        }

        return false;
    }

    public static string Explain(
        (
            double TotalScore,
            int TitleScore,
            double ArtistScore,
            int AlbumScore,
            double DurationScore) score) =>
        $"title={score.TitleScore};artist={score.ArtistScore:F1};album={score.AlbumScore};" +
        $"duration={score.DurationScore:F1};total={score.TotalScore:F1}";

    private static double CalculateDurationScore(int? sourceDurationMs, int? candidateDurationSeconds)
    {
        if (!sourceDurationMs.HasValue || !candidateDurationSeconds.HasValue)
        {
            return 50;
        }

        var sourceSeconds = sourceDurationMs.Value / 1000d;
        var delta = Math.Abs(sourceSeconds - candidateDurationSeconds.Value);
        return delta switch
        {
            <= 2 => 100,
            <= 8 => 100 - (50 * delta / 8),
            _ => 0
        };
    }
}

/// <summary>
/// Resolves a provider id (e.g. "deezer", "applemusic", "spotiflac-tidal-web")
/// to a concrete metadata service so a walk step can call a single provider
/// without fanning out to every enabled provider at once.
/// </summary>
public static class PerProviderServiceResolver
{
    public static IConcreteMetadataService? Resolve(
        IEnumerable<IConcreteMetadataService> services,
        string providerId)
    {
        var normalized = providerId.Trim().ToLowerInvariant();
        return services.FirstOrDefault(service =>
            Resolves(service, normalized));
    }

    private static bool Resolves(IConcreteMetadataService service, string normalized)
    {
        var typeName = service.GetType().Name;
        if (normalized == "applemusic" || normalized == "apple-download")
        {
            return typeName.StartsWith("AppleMusic", StringComparison.OrdinalIgnoreCase);
        }
        if (normalized == "squidwtf")
        {
            return typeName.StartsWith("SquidWTF", StringComparison.OrdinalIgnoreCase);
        }
        return typeName.StartsWith(normalized, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Per-track, per-provider walk for an injected source track. This is the
/// shared engine that Spotify, Apple MusicKit, and any future injected
/// source use. Local library is always the implicit first stop. The walker
/// stops on the first verified identity (ISRC) or per-provider accept and
/// only falls back to title-only retries when no provider crossed the
/// threshold.
/// </summary>
public sealed class PerProviderTrackWalker
{
    private readonly IReadOnlyList<IConcreteMetadataService> _concreteServices;
    private readonly PerProviderAcceptThresholds _thresholds;
    private readonly ILogger _logger;
    private readonly int _searchLimit;

    public PerProviderTrackWalker(
        IReadOnlyList<IConcreteMetadataService> concreteServices,
        PerProviderAcceptThresholds thresholds,
        ILogger logger,
        int searchLimit = 24)
    {
        _concreteServices = concreteServices;
        _thresholds = thresholds;
        _logger = logger;
        _searchLimit = searchLimit;
    }

    /// <summary>
    /// Walks the configured playback priority list for one source track.
    /// Pass `localMatch` when the local pass already won to short-circuit
    /// the walk. Returns the accepted match, if any, and a list of every
    /// provider step that was attempted.
    /// </summary>
    public async Task<PerProviderMatchResult> WalkAsync(
        InjectedSourceTrack source,
        IReadOnlyList<string> playbackProviders,
        Song? localMatch,
        double? localMatchScore,
        CancellationToken cancellationToken,
        bool collectAllProviderMatches = false)
    {
        var walked = new List<PerProviderAttempt>();
        var acceptedMatches = new List<PerProviderAcceptedMatch>();
        var acceptedProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (localMatch != null && !collectAllProviderMatches)
        {
            return PerProviderTrackMatcher.FromLocal(localMatch, localMatchScore ?? 100);
        }

        var titleStripped = FuzzyMatcher.SearchQuery(source.Title);
        var primaryArtist = source.Artists.FirstOrDefault() ?? string.Empty;
        var artistQuery = $"{titleStripped} {primaryArtist}".Trim();
        var titleOnlyQuery = titleStripped;

        // 1. Per-provider walk in configured order.
        // We try each provider's own concrete search directly so the walk is
        // deterministic and we can short-circuit on the first accept.
        for (var index = 0; index < playbackProviders.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var providerId = playbackProviders[index];
            var normalizedProvider = providerId.Trim().ToLowerInvariant();

            if (normalizedProvider == "jellyfin-local" || normalizedProvider == "subsonic-local")
            {
                walked.Add(new PerProviderAttempt(
                    providerId, artistQuery, 0, null,
                    PerProviderTrackMatcher.OutcomeNoService,
                    "pinned-local"));
                continue;
            }

            var providerService = PerProviderServiceResolver.Resolve(_concreteServices, providerId);
            if (providerService == null)
            {
                walked.Add(new PerProviderAttempt(
                    providerId, artistQuery, 0, null,
                    PerProviderTrackMatcher.OutcomeNoService,
                    "no-concrete-service"));
                continue;
            }

            // ISRC verified identity on this provider's catalog, if the source has one.
            if (!string.IsNullOrWhiteSpace(source.Isrc))
            {
                var isrcStep = await TryIsrcStepAsync(
                    providerService, providerId, source.Isrc!, cancellationToken);
                walked.Add(isrcStep.Attempt);

                if (isrcStep.AcceptedSong != null)
                {
                    acceptedProviders.Add(providerId);
                    acceptedMatches.Add(new PerProviderAcceptedMatch(
                        isrcStep.AcceptedSong,
                        PerProviderTrackMatcher.MatchTypeIsrc,
                        providerId,
                        100));
                    if (!collectAllProviderMatches)
                    {
                        return PerProviderTrackMatcher.FromProvider(
                            isrcStep.AcceptedSong,
                            PerProviderTrackMatcher.MatchTypeIsrc,
                            providerId,
                            100,
                            walked);
                    }
                    continue;
                }
            }

            // Fuzzy search on this provider only.
            var stepResult = await StepProviderAsync(
                providerService,
                providerId,
                artistQuery,
                source,
                cancellationToken);
            walked.Add(stepResult.Attempt);

            if (stepResult.AcceptedSong != null)
            {
                var matchType = stepResult.MatchType ?? PerProviderTrackMatcher.MatchTypeProviderFuzzy;
                acceptedProviders.Add(providerId);
                acceptedMatches.Add(new PerProviderAcceptedMatch(
                    stepResult.AcceptedSong,
                    matchType,
                    providerId,
                    stepResult.Score));
                if (!collectAllProviderMatches)
                {
                    return PerProviderTrackMatcher.FromProvider(
                        stepResult.AcceptedSong,
                        matchType,
                        providerId,
                        stepResult.Score,
                        walked);
                }
            }
        }

        // 2. Title-only retry on the first N providers only when no provider
        // produced an accepted match. Once any provider accepted the track,
        // alternate title-only searches add traffic without adding a fallback
        // that the normal provider walk did not already collect.
        if (acceptedMatches.Count == 0)
        {
            var titleOnlyRetry = 0;
            foreach (var providerId in playbackProviders)
            {
                if (titleOnlyRetry >= _thresholds.TitleOnlyProviderCount) break;
                titleOnlyRetry++;

                cancellationToken.ThrowIfCancellationRequested();
                var normalizedProvider = providerId.Trim().ToLowerInvariant();
                if (normalizedProvider == "jellyfin-local" || normalizedProvider == "subsonic-local") continue;

                var providerService = PerProviderServiceResolver.Resolve(_concreteServices, providerId);
                if (providerService == null) continue;

                var stepResult = await StepProviderAsync(
                    providerService,
                    providerId,
                    titleOnlyQuery,
                    source,
                    cancellationToken,
                    matchType: PerProviderTrackMatcher.MatchTypeTitleOnly);
                // Title-only is a distinct conservative alternate query. Record both
                // accepts and misses so operators can see why the walk fell through.
                walked.Add(stepResult.Attempt);
                if (stepResult.AcceptedSong != null)
                {
                    acceptedMatches.Add(new PerProviderAcceptedMatch(
                        stepResult.AcceptedSong,
                        PerProviderTrackMatcher.MatchTypeTitleOnly,
                        providerId,
                        stepResult.Score));
                    if (!collectAllProviderMatches)
                    {
                        return PerProviderTrackMatcher.FromProvider(
                            stepResult.AcceptedSong,
                            PerProviderTrackMatcher.MatchTypeTitleOnly,
                            providerId,
                            stepResult.Score,
                            walked);
                    }
                }
            }
        }

        return PerProviderTrackMatcher.FromCollectedProviders(
            localMatch,
            localMatchScore,
            acceptedMatches,
            walked);
    }

    private async Task<PerProviderStepResult> StepProviderAsync(
        IConcreteMetadataService service,
        string providerId,
        string query,
        InjectedSourceTrack source,
        CancellationToken cancellationToken,
        string matchType = PerProviderTrackMatcher.MatchTypeProviderFuzzy)
    {
        try
        {
            var candidates = await PerProviderTrackMatcher.SearchPlayableAsync(
                new[] { service },
                providerId,
                query,
                _searchLimit,
                cancellationToken);

            if (candidates.Count == 0)
            {
                return new PerProviderStepResult(
                    AcceptedSong: null,
                    Score: 0,
                    MatchType: matchType,
                        Attempt: new PerProviderAttempt(
                            providerId, query, 0, null,
                            PerProviderTrackMatcher.OutcomeEmpty, "no-playable-candidates"));
            }

            var scored = candidates
                .Select(song => (Song: song, Score: PerProviderTrackScorer.Score(song, source)))
                .OrderByDescending(entry => entry.Score.TotalScore)
                .ToList();

            var top = scored[0];
            var explanation = PerProviderTrackScorer.Explain(top.Score);
            if (PerProviderTrackScorer.IsAcceptable(top.Score, _thresholds))
            {
                return new PerProviderStepResult(
                    AcceptedSong: top.Song,
                    Score: top.Score.TotalScore,
                    MatchType: matchType,
                    Attempt: new PerProviderAttempt(
                        providerId, query, candidates.Count, top.Score.TotalScore,
                        PerProviderTrackMatcher.OutcomeAccepted,
                        $"score-accepted:{explanation}"));
            }

            return new PerProviderStepResult(
                AcceptedSong: null,
                Score: top.Score.TotalScore,
                MatchType: matchType,
                Attempt: new PerProviderAttempt(
                    providerId, query, candidates.Count, top.Score.TotalScore,
                    PerProviderTrackMatcher.OutcomeLowScore,
                    $"score-rejected:{explanation}"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "Per-provider search failed for {Provider} on query '{Query}'",
                providerId, query);
            return new PerProviderStepResult(
                AcceptedSong: null,
                Score: 0,
                MatchType: matchType,
                Attempt: new PerProviderAttempt(
                    providerId, query, 0, null,
                    PerProviderTrackMatcher.OutcomeError, ex.GetType().Name));
        }
    }

    private async Task<PerProviderStepResult> TryIsrcStepAsync(
        IConcreteMetadataService service,
        string providerId,
        string isrc,
        CancellationToken cancellationToken)
    {
        try
        {
            var isrcSong = await service.FindSongByIsrcAsync(isrc, cancellationToken);
            if (isrcSong != null
                && ExternalTrackPlaybackPolicy.CanUseForPlayback(isrcSong.ExternalProvider, isrcSong.Id))
            {
                return new PerProviderStepResult(
                    AcceptedSong: isrcSong,
                    Score: 100,
                    MatchType: PerProviderTrackMatcher.MatchTypeIsrc,
                    Attempt: new PerProviderAttempt(
                        providerId, $"isrc:{isrc}", 1, 100,
                        PerProviderTrackMatcher.OutcomeAccepted, "isrc-exact"));
            }

            return new PerProviderStepResult(
                AcceptedSong: null,
                Score: 0,
                MatchType: PerProviderTrackMatcher.MatchTypeIsrc,
                Attempt: new PerProviderAttempt(
                    providerId, $"isrc:{isrc}", 0, null,
                    PerProviderTrackMatcher.OutcomeMissNotFound, "isrc-not-found"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "ISRC lookup failed for {Provider}",
                providerId);
            return new PerProviderStepResult(
                AcceptedSong: null,
                Score: 0,
                MatchType: PerProviderTrackMatcher.MatchTypeIsrc,
                Attempt: new PerProviderAttempt(
                    providerId, $"isrc:{isrc}", 0, null,
                    PerProviderTrackMatcher.OutcomeError, ex.GetType().Name));
        }
    }

    private readonly record struct PerProviderStepResult(
        Song? AcceptedSong,
        double Score,
        string? MatchType,
        PerProviderAttempt Attempt);
}
