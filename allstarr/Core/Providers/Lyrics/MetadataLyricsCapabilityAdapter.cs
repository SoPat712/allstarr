using allstarr.Core.Capabilities;
using allstarr.Core.Matching;
using allstarr.Services.Common;

namespace allstarr.Core.Providers.Lyrics;

public abstract class MetadataLyricsCapabilityAdapter : PublicLyricsCapabilityAdapter
{
    public static readonly TimeSpan OverallLookupBudget = TimeSpan.FromSeconds(6);
    public static readonly TimeSpan PerRequestTimeout = TimeSpan.FromSeconds(4);
    private readonly TimeProvider _clock;
    private readonly MetadataLyricsFailureCache _failures;
    private readonly ILogger? _logger;
    protected override TimeProvider Clock => _clock;
    protected override TimeSpan LookupBudget => OverallLookupBudget;
    protected override TimeSpan RequestTimeout => PerRequestTimeout;
    protected override ProviderError LookupTimeoutError => ProviderError.LyricsUpstreamBusyOrTimedOut();

    private protected MetadataLyricsCapabilityAdapter(HttpClient http, IApplicationCache? cache, TimeProvider clock,
        MetadataLyricsFailureCache failures, ILogger? logger, Func<TimeSpan>? lyricsTtl)
        : base(http, cache, lyricsTtl)
    {
        _clock = clock;
        _failures = failures;
        _logger = logger;
    }

    protected sealed override Task<ProviderOutcome<ProviderLyricsResult>> ReadAsync(ProviderLyricsRequest request, CancellationToken token) =>
        ReadLookupAsync(request, token, CancellationToken.None);

    protected sealed override Task<ProviderOutcome<ProviderLyricsResult>> ReadAsync(
        ProviderExecutionContext context, ProviderLyricsRequest request, CancellationToken token) =>
        ReadLookupAsync(request, token, context.CancellationToken);

    private async Task<ProviderOutcome<ProviderLyricsResult>> ReadLookupAsync(
        ProviderLyricsRequest request, CancellationToken token, CancellationToken caller)
    {
        var key = Hash(System.Text.Json.JsonSerializer.Serialize(new
        {
            request.Isrc,
            request.TrackTitle,
            request.ArtistNames,
            request.AlbumTitle,
            request.DurationSeconds
        }));
        var outcome = "failed";
        try
        {
            if (_failures.Find(key) is { } cached)
            {
                outcome = "cached-upstream-failure";
                return ProviderOutcome<ProviderLyricsResult>.Failure(cached);
            }
            var result = await ReadLyricsAsync(request, token);
            if (result.Error is { Kind: ProviderErrorKind.TransientFailure or ProviderErrorKind.RateLimited } error)
            {
                if (error.Kind == ProviderErrorKind.TransientFailure)
                    result = ProviderOutcome<ProviderLyricsResult>.Failure(error = ProviderError.LyricsUpstreamBusyOrTimedOut());
                _failures.Remember(key, error);
            }
            outcome = result.IsSuccess ? result.RequireValue().Availability.ToString() : result.Error!.Code;
            return result;
        }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested)
        {
            _failures.Remember(key, ProviderError.LyricsUpstreamBusyOrTimedOut());
            outcome = "timed-out";
            throw;
        }
        finally { _logger?.LogInformation("Lyrics lookup {Provider} completed with {Outcome}", ProviderId, outcome); }
    }

    protected abstract Task<ProviderOutcome<ProviderLyricsResult>> ReadLyricsAsync(ProviderLyricsRequest request, CancellationToken token);

    protected static bool MatchesIdentity(ProviderLyricsRequest request, string? title, string? artist, double? duration = null)
    {
        if (string.IsNullOrWhiteSpace(request.TrackTitle) || request.ArtistNames.Count == 0 ||
            string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist)) return false;
        var expectedArtists = string.Join(", ", request.ArtistNames);
        if (!FuzzyMatcher.TitleAnnotationsMatch(request.TrackTitle, expectedArtists, title, artist) ||
            !FuzzyMatcher.SemanticVersionTags(request.TrackTitle).SetEquals(FuzzyMatcher.SemanticVersionTags(title))) return false;
        var expected = FuzzyMatcher.NormalizeTitleForMatching(request.TrackTitle, expectedArtists, artist);
        var actual = FuzzyMatcher.NormalizeTitleForMatching(title, artist, expectedArtists);
        if (Math.Min(expected.Length, actual.Length) < Math.Max(expected.Length, actual.Length) * 0.9 ||
            FuzzyMatcher.CalculateSimilarity(expected, actual) < 90) return false;
        var artists = FuzzyMatcher.SplitCredits(artist).ToList();
        if (artists.Count == 0 || !request.ArtistNames.All(name => artists.Any(value =>
        {
            var expectedArtist = FuzzyMatcher.NormalizeForMatching(name);
            var actualArtist = FuzzyMatcher.NormalizeForMatching(value);
            return expectedArtist.Length > 0 && Math.Min(expectedArtist.Length, actualArtist.Length) >=
                Math.Max(expectedArtist.Length, actualArtist.Length) * 0.9 && FuzzyMatcher.CalculateSimilarity(expectedArtist, actualArtist) >= 90;
        }))) return false;
        return request.DurationSeconds is not > 0 || duration is null || double.IsFinite(duration.Value) &&
            Math.Abs(request.DurationSeconds.Value - duration.Value) <= 5;
    }
}

internal sealed class MetadataLyricsFailureCache(TimeProvider clock, int capacity = 1024)
{
    private readonly Dictionary<string, (ProviderError Error, DateTimeOffset Expires)> _entries = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private readonly object _gate = new();

    public ProviderError? Find(string key)
    {
        lock (_gate)
            return _entries.TryGetValue(key, out var entry) && entry.Expires > clock.GetUtcNow() ? entry.Error : null;
    }

    public void Remember(string key, ProviderError error)
    {
        lock (_gate)
        {
            if (!_entries.ContainsKey(key))
            {
                while (_entries.Count >= capacity) _entries.Remove(_order.Dequeue());
                _order.Enqueue(key);
            }
            _entries[key] = (error, clock.GetUtcNow().AddMinutes(10));
        }
    }
}
