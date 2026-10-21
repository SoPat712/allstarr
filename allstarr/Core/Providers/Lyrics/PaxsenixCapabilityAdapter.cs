using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Matching;
using allstarr.Core.Music;
using allstarr.Core.Providers.AppleMusicKit;
using allstarr.Core.Storage;
using allstarr.Services.Common;

namespace allstarr.Core.Providers.Lyrics;

public sealed class PaxsenixCapabilityAdapter : PublicLyricsCapabilityAdapter
{
    public const string HttpClientName = "PaxsenixLyrics";
    public static readonly TimeSpan OverallLookupBudget = TimeSpan.FromSeconds(6);
    public static readonly TimeSpan PerRequestTimeout = TimeSpan.FromSeconds(4);
    private readonly PaxsenixRequestPacer _pacer;
    private readonly PaxsenixFailureCache _failures;
    private readonly TimeProvider _clock;
    private readonly AppleMusicKitMetadataCapabilityAdapter? _apple;
    private readonly Func<ITrackIdentityService?>? _identities;
    private readonly IApplicationCache? _cache;
    private readonly Func<TimeSpan>? _lyricsTtl;
    public override string ProviderId => "paxsenix";
    protected override TimeProvider Clock => _clock;
    protected override string CacheScope(ProviderLyricsRequest request) => $"apple:{request.CanonicalRecordingId:N}:";
    protected override TimeSpan LookupBudget => OverallLookupBudget;
    protected override TimeSpan RequestTimeout => PerRequestTimeout;
    protected override ProviderError LookupTimeoutError => ProviderError.LyricsUpstreamBusyOrTimedOut();

    public PaxsenixCapabilityAdapter(HttpClient http, IApplicationCache? cache,
        AppleMusicKitMetadataCapabilityAdapter? apple = null, Func<ITrackIdentityService?>? identities = null,
        Func<TimeSpan>? lyricsTtl = null)
        : this(http, cache, PaxsenixRequestPacer.Shared, TimeProvider.System, apple, identities, lyricsTtl, PaxsenixFailureCache.Shared) { }

    internal PaxsenixCapabilityAdapter(HttpClient http, IApplicationCache? cache,
        PaxsenixRequestPacer pacer, TimeProvider clock, AppleMusicKitMetadataCapabilityAdapter? apple = null,
        Func<ITrackIdentityService?>? identities = null, Func<TimeSpan>? lyricsTtl = null, PaxsenixFailureCache? failures = null)
        : base(http, cache, lyricsTtl)
    {
        _pacer = pacer;
        _failures = failures ?? new(clock);
        _clock = clock;
        _apple = apple;
        _identities = identities;
        _cache = cache;
        _lyricsTtl = lyricsTtl;
    }

    protected override Task<ProviderOutcome<ProviderLyricsResult>> ReadAsync(ProviderLyricsRequest request, CancellationToken token) =>
        ReadCoreAsync(null, request, token);

    protected override Task<ProviderOutcome<ProviderLyricsResult>> ReadAsync(
        ProviderExecutionContext context, ProviderLyricsRequest request, CancellationToken token) => ReadCoreAsync(context, request, token);

    private async Task<ProviderOutcome<ProviderLyricsResult>> ReadCoreAsync(
        ProviderExecutionContext? context, ProviderLyricsRequest request, CancellationToken token)
    {
        var appleContext = new ProviderExecutionContext(context?.Actor ?? new(ProviderActorKind.PublicRead, null),
            AppleMusicClient.ProviderId, null,
            new(new(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, true), ProviderExplicitContentPolicy.Allow,
                true, false, false, [AppleMusicClient.ProviderId]), "lyrics-lookup", context?.CorrelationId ?? "lyrics-lookup",
            context?.Deadline ?? DateTimeOffset.UtcNow.Add(LookupBudget), token);
        var attempted = new HashSet<string>(StringComparer.Ordinal);
        ProviderError? transient = null;
        var unavailable = false;
        var sent = 0;
        await foreach (var candidate in ResolveAppleIdsAsync(appleContext, request, token))
        {
            token.ThrowIfCancellationRequested();
            if (candidate.Error != null)
            {
                if (attempted.Count == 0) return ProviderOutcome<ProviderLyricsResult>.Failure(candidate.Error);
                break;
            }
            var id = candidate.Id!;
            if (!IsCatalogId(id) || !attempted.Add(id)) continue;
            if (_failures.Contains(id))
            {
                transient = ProviderError.LyricsUpstreamBusyOrTimedOut();
                continue;
            }
            await _pacer.WaitAsync(token);
            (string? Content, ProviderError? Error, HttpStatusCode? StatusCode) response;
            try
            {
                sent++;
                response = await GetResponseAsync(new($"https://lyrics.paxsenix.org/apple-music/lyrics?id={Uri.EscapeDataString(id)}"), token);
            }
            catch (OperationCanceledException) when (context?.CancellationToken.IsCancellationRequested != true)
            {
                _failures.Remember(id);
                throw;
            }
            if (response.Error != null)
            {
                // Busy songs answer 5xx or hang until the request timeout (no status); try the next copy.
                if (response.StatusCode is null or HttpStatusCode.RequestTimeout or >= HttpStatusCode.InternalServerError)
                {
                    _failures.Remember(id);
                    transient = ProviderError.LyricsUpstreamBusyOrTimedOut();
                }
                else return ProviderOutcome<ProviderLyricsResult>.Failure(response.Error);
            }
            else
            {
                var result = string.IsNullOrWhiteSpace(response.Content) ? Unavailable() : ParseLyrics(response.Content);
                if (!result.IsSuccess) return result;
                if (result.RequireValue().Availability == ProviderLyricsAvailabilityState.Available)
                {
                    await CacheAppleIdAsync(id, AppleIdCacheKeys(request), token);
                    return result;
                }
                unavailable = true;
            }
            if (sent == 3) break;
        }
        return unavailable || transient == null ? Unavailable() : ProviderOutcome<ProviderLyricsResult>.Failure(transient);
    }

    private ProviderOutcome<ProviderLyricsResult> ParseLyrics(string payload)
    {
        using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) ||
            type.ValueKind != JsonValueKind.String) return ContractFailure();
        if (type.GetString() == "None") return Unavailable();
        if (type.GetString() is not ("Syllable" or "Line")) return ContractFailure();
        if (!root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return ContractFailure();
        if (content.GetArrayLength() == 0) return Unavailable();
        var foundFormat = false;
        foreach (var field in new[] { "ttmlContent", "elrc", "lrc", "plain" })
        {
            if (!root.TryGetProperty(field, out var value)) continue;
            foundFormat = true;
            if (value.ValueKind == JsonValueKind.Null) continue;
            if (value.ValueKind != JsonValueKind.String) return ContractFailure();
            var text = value.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (field == "ttmlContent") return ParseTtml(text);
            if (text.StartsWith('<') || field != "plain" && MusicLyrics.ParseSyncedLines(text).Count == 0) return ContractFailure();
            return ProviderOutcome<ProviderLyricsResult>.Success(new(ProviderLyricsAvailabilityState.Available, ProviderId,
                field == "plain" ? ProviderLyricsFormat.PlainText : ProviderLyricsFormat.LineTimed, text, "sha256:" + Hash(text)));
        }
        return foundFormat ? Unavailable() : ContractFailure();
    }

    private async IAsyncEnumerable<(string? Id, ProviderError? Error)> ResolveAppleIdsAsync(
        ProviderExecutionContext context, ProviderLyricsRequest request, [EnumeratorCancellation] CancellationToken token)
    {
        var keys = AppleIdCacheKeys(request);
        var cached = await CachedAppleIdAsync(keys[0], token);
        if (cached != null) yield return (cached, null);
        var identities = _identities?.Invoke();
        if (identities != null)
        {
            var identity = await identities.ResolveTargetAsync(context, request.CanonicalRecordingId,
                new(AppleMusicClient.ProviderId), token).WaitAsync(token);
            if (identity is { Scope: ProviderIdentityScope.Catalog, Verification: ProviderIdentityVerification.Verified or ProviderIdentityVerification.Pinned } &&
                IsCatalogId(identity.ExternalId.Value)) yield return (identity.ExternalId.Value, null);
        }
        if (keys.Count > 1)
        {
            cached = await CachedAppleIdAsync(keys[1], token);
            if (cached != null) yield return (cached, null);
        }
        if (string.IsNullOrWhiteSpace(request.Isrc) &&
            (string.IsNullOrWhiteSpace(request.TrackTitle) || request.ArtistNames.Count == 0)) yield break;
        if (_apple == null)
        {
            yield return (null, ProviderError.AppleWebTokenUnavailable());
            yield break;
        }
        if (!string.IsNullOrWhiteSpace(request.Isrc))
        {
            var lookup = await _apple.LookupAllByIsrcAsync(context, new(request.Isrc)).WaitAsync(token);
            token.ThrowIfCancellationRequested();
            if (lookup.IsSuccess)
            {
                foreach (var track in lookup.RequireValue()) yield return (track.Id.Value, null);
            }
            else if (lookup.Error!.Kind != ProviderErrorKind.NotFound)
            {
                yield return (null, lookup.Error);
                yield break;
            }
        }
        if (!string.IsNullOrWhiteSpace(request.TrackTitle) && request.ArtistNames.Count > 0)
        {
            var search = await _apple.SearchTracksAsync(context,
                new(request.TrackTitle + " " + string.Join(" ", request.ArtistNames), new(25))).WaitAsync(token);
            token.ThrowIfCancellationRequested();
            if (!search.IsSuccess)
            {
                if (search.Error!.Kind != ProviderErrorKind.NotFound) yield return (null, search.Error);
                yield break;
            }
            foreach (var match in search.RequireValue().Items.Select(item => (Item: item, Score: MatchScore(item, request)))
                .Where(item => item.Score >= 0)
                .OrderByDescending(item => !string.IsNullOrWhiteSpace(request.Isrc) &&
                    string.Equals(item.Item.Isrc, request.Isrc.Trim(), StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(item => item.Score))
                yield return (match.Item.Id.Value, null);
        }
    }

    private static List<string> AppleIdCacheKeys(ProviderLyricsRequest request)
    {
        var keys = new List<string> { $"lyrics:paxsenix:apple-id:recording:{request.CanonicalRecordingId:N}" };
        if (!string.IsNullOrWhiteSpace(request.Isrc)) keys.Add("lyrics:paxsenix:apple-id:isrc:" + Hash(request.Isrc.Trim().ToUpperInvariant()));
        return keys;
    }

    private async Task<string?> CachedAppleIdAsync(string key, CancellationToken token)
    {
        try
        {
            var cached = _cache == null ? null : await _cache.GetAsync<string>(key).WaitAsync(token);
            return cached != null && IsCatalogId(cached) ? cached : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException) { return null; }
    }

    private async Task CacheAppleIdAsync(string id, IReadOnlyList<string> keys, CancellationToken token)
    {
        if (_cache != null)
        {
            foreach (var key in keys)
            {
                try { await _cache.SetAsync(key, id, _lyricsTtl?.Invoke() ?? TimeSpan.FromDays(7)).WaitAsync(token); }
                catch (Exception exception) when (exception is not OperationCanceledException) { }
            }
        }
    }

    private static bool IsCatalogId(string id) => id.Length > 0 && id.All(char.IsAsciiDigit);

    private static double MatchScore(ProviderTrackMetadata item, ProviderLyricsRequest request)
    {
        var title = item.Title;
        var artists = item.Artists.SelectMany(artist => FuzzyMatcher.SplitCredits(artist.Name)).ToList();
        if (artists.Count == 0) return -1;
        var expectedArtists = string.Join(", ", request.ArtistNames);
        var actualArtists = string.Join(", ", artists);
        if (!FuzzyMatcher.TitleAnnotationsMatch(request.TrackTitle!, expectedArtists, title, actualArtists) ||
            !FuzzyMatcher.SemanticVersionTags(request.TrackTitle).SetEquals(FuzzyMatcher.SemanticVersionTags(title))) return -1;
        var titleScore = FuzzyMatcher.CalculateSimilarity(request.TrackTitle!, title);
        var expectedTitle = FuzzyMatcher.NormalizeTitleForMatching(request.TrackTitle!, expectedArtists);
        var actualTitle = FuzzyMatcher.NormalizeTitleForMatching(title, actualArtists);
        if (Math.Min(expectedTitle.Length, actualTitle.Length) < Math.Max(expectedTitle.Length, actualTitle.Length) * 0.9) return -1;
        var artistScore = FuzzyMatcher.CalculateArtistMatchScore(request.ArtistNames.ToList(), artists[0], artists.Skip(1).ToList());
        // A request often names only the main artist while the catalog also credits featured artists.
        if (artistScore < 90 && request.ArtistNames.All(expected => artists.Any(actual => FuzzyMatcher.CalculateSimilarity(expected, actual) >= 90)))
            artistScore = FuzzyMatcher.CalculateSimilarity(request.ArtistNames[0], artists[0]);
        if (titleScore < 90 || artistScore < 90) return -1;
        double durationScore = 0;
        if (request.DurationSeconds is > 0 && item.Duration is { } duration)
        {
            var difference = Math.Abs(duration.TotalSeconds - request.DurationSeconds.Value);
            if (difference > 5) return -1;
            durationScore = 5 - difference;
        }
        return titleScore + artistScore + durationScore;
    }
}

internal sealed class PaxsenixRequestPacer(TimeProvider clock, TimeSpan? interval = null)
{
    internal static readonly PaxsenixRequestPacer Shared = new(TimeProvider.System);
    private readonly SemaphoreSlim _requests = new(1, 1);
    private long? _lastRequest;

    public async Task WaitAsync(CancellationToken token)
    {
        await _requests.WaitAsync(token);
        try
        {
            var spacing = interval ?? TimeSpan.FromSeconds(2);
            if (_lastRequest is { } previous)
            {
                var elapsed = clock.GetElapsedTime(previous);
                if (elapsed < spacing) await Task.Delay(spacing - elapsed, clock, token);
            }
            token.ThrowIfCancellationRequested();
            _lastRequest = clock.GetTimestamp();
        }
        finally { _requests.Release(); }
    }
}
