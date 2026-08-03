using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using allstarr.Models.Settings;
using allstarr.Services.Common;
using Microsoft.Extensions.Options;

namespace allstarr.Services.MusicBrainz;

public sealed record MusicBrainzRecordingMatch(
    MusicBrainzRecording Recording,
    double Confidence,
    string SourceRevision);

public sealed class MusicBrainzLookupException(
    string code,
    string message,
    bool retryable,
    TimeSpan? retryAfter = null,
    Exception? innerException = null) : Exception(message, innerException)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>
/// Bounded public MusicBrainz read client. Public reads never use stored credentials.
/// </summary>
public sealed class MusicBrainzService
{
    public const string HttpClientName = "MusicBrainz";
    public const string SourceRevision = "musicbrainz:ws2";
    public const int MaximumResponseBytes = 1024 * 1024;
    public static readonly string UserAgent =
        $"Allstarr/{AppVersion.Version} (https://github.com/SoPat712/allstarr)";

    private static readonly TimeSpan NegativeCacheDuration = TimeSpan.FromHours(6);
    private readonly HttpClient _httpClient;
    private readonly MusicBrainzSettings _settings;
    private readonly CacheSettings _cacheSettings;
    private readonly IApplicationCache _cache;
    private readonly ApplicationCacheRequestCoalescer _coalescer;
    private readonly ILogger<MusicBrainzService> _logger;
    private readonly SemaphoreSlim _rateLimitSemaphore = new(1, 1);
    private DateTimeOffset _lastRequestAt = DateTimeOffset.MinValue;

    public MusicBrainzService(
        IHttpClientFactory httpClientFactory,
        IOptions<MusicBrainzSettings> settings,
        IOptions<CacheSettings> cacheSettings,
        IApplicationCache cache,
        ApplicationCacheRequestCoalescer coalescer,
        ILogger<MusicBrainzService> logger)
    {
        _httpClient = httpClientFactory.CreateClient(HttpClientName);
        _httpClient.DefaultRequestHeaders.Authorization = null;
        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        if (_httpClient.DefaultRequestHeaders.Accept.Count == 0)
            _httpClient.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));
        _settings = settings.Value;
        _cacheSettings = cacheSettings.Value;
        _cache = cache;
        _coalescer = coalescer;
        _logger = logger;
    }

    public Task<MusicBrainzRecording?> LookupByIsrcAsync(
        string isrc,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeIsrc(isrc) ??
            throw new ArgumentException("ISRC must contain a valid 12-character recording code.", nameof(isrc));
        if (!_settings.Enabled) return Task.FromResult<MusicBrainzRecording?>(null);
        var key = CacheKeyBuilder.BuildMusicBrainzIsrcKey(normalized);
        return GetCachedAsync(key, token => LookupIsrcUncachedAsync(normalized, token), cancellationToken);
    }

    public Task<IReadOnlyList<MusicBrainzRecording>> SearchRecordingsAsync(
        string title,
        string artist,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        var normalizedTitle = NormalizeRequiredText(title, nameof(title));
        var normalizedArtist = NormalizeRequiredText(artist, nameof(artist));
        if (limit is < 1 or > 25)
            throw new ArgumentOutOfRangeException(nameof(limit), "MusicBrainz search limit must be between 1 and 25.");
        if (!_settings.Enabled) return Task.FromResult<IReadOnlyList<MusicBrainzRecording>>([]);
        var key = CacheKeyBuilder.BuildMusicBrainzSearchKey(normalizedTitle, normalizedArtist, limit);
        return GetCachedListAsync(key,
            token => SearchUncachedAsync(normalizedTitle, normalizedArtist, limit, token), cancellationToken);
    }

    public Task<MusicBrainzRecording?> LookupByMbidAsync(
        string mbid,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeMbid(mbid) ??
            throw new ArgumentException("MusicBrainz recording ID must be a non-empty UUID.", nameof(mbid));
        if (!_settings.Enabled) return Task.FromResult<MusicBrainzRecording?>(null);
        var key = CacheKeyBuilder.BuildMusicBrainzMbidKey(normalized);
        return GetCachedAsync(key, token => LookupMbidUncachedAsync(normalized, token), cancellationToken);
    }

    public async Task<MusicBrainzRecordingMatch?> ResolveRecordingAsync(
        string? recordingMbid,
        string? isrc,
        string? title,
        string? artist,
        long? durationMilliseconds = null,
        CancellationToken cancellationToken = default)
    {
        if (durationMilliseconds is <= 0 or > 24 * 60 * 60 * 1000L)
            throw new ArgumentOutOfRangeException(nameof(durationMilliseconds));

        if (!string.IsNullOrWhiteSpace(recordingMbid))
        {
            var recording = await LookupByMbidAsync(recordingMbid, cancellationToken);
            if (recording != null) return new(recording, 1, SourceRevision);
        }

        if (!string.IsNullOrWhiteSpace(isrc))
        {
            var recording = await LookupByIsrcAsync(isrc, cancellationToken);
            if (recording != null) return new(recording, .98, SourceRevision);
        }

        var normalizedTitle = NormalizeRequiredText(title, nameof(title));
        var normalizedArtist = NormalizeRequiredText(artist, nameof(artist));
        var candidates = await SearchRecordingsAsync(
            normalizedTitle, normalizedArtist, cancellationToken: cancellationToken);
        var selected = SelectCandidate(candidates, normalizedTitle, normalizedArtist, durationMilliseconds);
        if (selected == null) return null;
        var full = await LookupByMbidAsync(selected.Value.Recording.Id!, cancellationToken);
        return full == null ? null : new(full, selected.Value.Confidence, SourceRevision);
    }

    public async Task<IReadOnlyList<string>> GetGenresForSongAsync(
        string title,
        string artist,
        string? isrc = null,
        string? recordingMbid = null,
        long? durationMilliseconds = null,
        CancellationToken cancellationToken = default)
    {
        if (!_settings.Enabled) return [];
        var match = await ResolveRecordingAsync(
            recordingMbid, isrc, title, artist, durationMilliseconds, cancellationToken);
        if (match?.Recording.Genres is not { Count: > 0 } genres) return [];
        return genres
            .Where(genre => !string.IsNullOrWhiteSpace(genre.Name))
            .OrderByDescending(genre => genre.Count)
            .ThenBy(genre => genre.Name, StringComparer.OrdinalIgnoreCase)
            .Select(genre => genre.Name!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToArray();
    }

    private async Task<MusicBrainzRecording?> LookupIsrcUncachedAsync(
        string isrc,
        CancellationToken cancellationToken)
    {
        var response = await GetJsonAsync<MusicBrainzIsrcResponse>(
            $"isrc/{isrc}?fmt=json&inc=artists+releases+release-groups+isrcs+genres+tags",
            cancellationToken);
        var recordings = response?.Recordings;
        if (recordings is not { Count: > 0 }) return null;
        var exact = recordings.Where(recording =>
            recording.Isrcs?.Any(value => NormalizeIsrc(value) == isrc) == true).ToArray();
        IEnumerable<MusicBrainzRecording> eligible = exact.Length > 0 ? exact : recordings;
        return eligible
            .Where(recording => NormalizeMbid(recording.Id) != null)
            .OrderBy(recording => recording.Id, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private async Task<IReadOnlyList<MusicBrainzRecording>?> SearchUncachedAsync(
        string title,
        string artist,
        int limit,
        CancellationToken cancellationToken)
    {
        var query = $"recording:\"{EscapeLucene(title)}\" AND artist:\"{EscapeLucene(artist)}\"";
        var response = await GetJsonAsync<MusicBrainzSearchResponse>(
            $"recording?query={Uri.EscapeDataString(query)}&fmt=json&limit={limit}",
            cancellationToken);
        return response?.Recordings;
    }

    private Task<MusicBrainzRecording?> LookupMbidUncachedAsync(
        string mbid,
        CancellationToken cancellationToken) =>
        GetJsonAsync<MusicBrainzRecording>(
            $"recording/{mbid}?fmt=json&inc=artists+releases+release-groups+isrcs+genres+tags",
            cancellationToken);

    private async Task<T?> GetJsonAsync<T>(string relativeUrl, CancellationToken cancellationToken)
        where T : class
    {
        await RateLimitAsync(cancellationToken);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(relativeUrl));
            using var response = await _httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            if (response.StatusCode == HttpStatusCode.TooManyRequests ||
                response.StatusCode == HttpStatusCode.ServiceUnavailable ||
                (int)response.StatusCode is >= 500 and <= 599)
                throw new MusicBrainzLookupException(
                    "musicbrainz_temporarily_unavailable",
                    "MusicBrainz is temporarily unavailable.",
                    true,
                    RetryAfter(response));
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("MusicBrainz read failed with {StatusCode}.", response.StatusCode);
                return null;
            }
            if (response.Content.Headers.ContentLength > MaximumResponseBytes)
                throw new MusicBrainzLookupException(
                    "musicbrainz_response_too_large",
                    "MusicBrainz returned more metadata than Allstarr can safely process.",
                    false);
            try
            {
                await response.Content.LoadIntoBufferAsync(MaximumResponseBytes, cancellationToken);
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken);
            }
            catch (HttpRequestException exception)
            {
                throw new MusicBrainzLookupException(
                    "musicbrainz_response_too_large",
                    "MusicBrainz returned more metadata than Allstarr can safely process.",
                    false,
                    innerException: exception);
            }
            catch (JsonException exception)
            {
                throw new MusicBrainzLookupException(
                    "musicbrainz_response_invalid",
                    "MusicBrainz returned invalid metadata.",
                    false,
                    innerException: exception);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (MusicBrainzLookupException)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            throw new MusicBrainzLookupException(
                "musicbrainz_temporarily_unavailable",
                "MusicBrainz is temporarily unavailable.",
                true,
                innerException: exception);
        }
    }

    private async Task<MusicBrainzRecording?> GetCachedAsync(
        string key,
        Func<CancellationToken, Task<MusicBrainzRecording?>> fetch,
        CancellationToken cancellationToken)
    {
        var cached = await _cache.GetAsync<MusicBrainzCacheEntry<MusicBrainzRecording>>(key);
        if (cached is { SourceRevision: SourceRevision }) return cached.Value;
        var negativeKey = CacheKeyBuilder.BuildMusicBrainzNegativeKey(key);
        if (await _cache.ExistsAsync(negativeKey)) return null;
        return await _coalescer.RunAsync(key, async () =>
        {
            cached = await _cache.GetAsync<MusicBrainzCacheEntry<MusicBrainzRecording>>(key);
            if (cached is { SourceRevision: SourceRevision }) return cached.Value;
            if (await _cache.ExistsAsync(negativeKey)) return null;
            var result = await fetch(cancellationToken);
            if (result == null)
                await _cache.SetStringAsync(negativeKey, SourceRevision, NegativeCacheDuration);
            else
                await _cache.SetAsync(key, new MusicBrainzCacheEntry<MusicBrainzRecording>(
                    result, SourceRevision), _cacheSettings.GenreTTL);
            return result;
        }, cancellationToken);
    }

    private async Task<IReadOnlyList<MusicBrainzRecording>> GetCachedListAsync(
        string key,
        Func<CancellationToken, Task<IReadOnlyList<MusicBrainzRecording>?>> fetch,
        CancellationToken cancellationToken)
    {
        var cached = await _cache.GetAsync<MusicBrainzCacheEntry<List<MusicBrainzRecording>>>(key);
        if (cached is { SourceRevision: SourceRevision }) return cached.Value;
        var negativeKey = CacheKeyBuilder.BuildMusicBrainzNegativeKey(key);
        if (await _cache.ExistsAsync(negativeKey)) return [];
        return await _coalescer.RunAsync<IReadOnlyList<MusicBrainzRecording>>(key, async () =>
        {
            cached = await _cache.GetAsync<MusicBrainzCacheEntry<List<MusicBrainzRecording>>>(key);
            if (cached is { SourceRevision: SourceRevision }) return cached.Value;
            if (await _cache.ExistsAsync(negativeKey)) return [];
            var result = (await fetch(cancellationToken))?.ToList() ?? [];
            if (result.Count == 0)
                await _cache.SetStringAsync(negativeKey, SourceRevision, NegativeCacheDuration);
            else
                await _cache.SetAsync(key, new MusicBrainzCacheEntry<List<MusicBrainzRecording>>(
                    result, SourceRevision), _cacheSettings.GenreTTL);
            return result;
        }, cancellationToken);
    }

    private async Task RateLimitAsync(CancellationToken cancellationToken)
    {
        await _rateLimitSemaphore.WaitAsync(cancellationToken);
        try
        {
            var delay = TimeSpan.FromMilliseconds(Math.Clamp(_settings.RateLimitMs, 1000, 60_000)) -
                        (DateTimeOffset.UtcNow - _lastRequestAt);
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
            _lastRequestAt = DateTimeOffset.UtcNow;
        }
        finally
        {
            _rateLimitSemaphore.Release();
        }
    }

    private Uri BuildUri(string relativeUrl)
    {
        if (!Uri.TryCreate(_settings.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme != Uri.UriSchemeHttps && baseUri.Scheme != Uri.UriSchemeHttp ||
            !string.IsNullOrEmpty(baseUri.UserInfo))
            throw new InvalidOperationException("MusicBrainz base URL is invalid.");
        return new Uri(baseUri, relativeUrl);
    }

    private static (MusicBrainzRecording Recording, double Confidence)? SelectCandidate(
        IReadOnlyList<MusicBrainzRecording> candidates,
        string title,
        string artist,
        long? durationMilliseconds)
    {
        var normalizedTitle = CanonicalText(title);
        var normalizedArtist = CanonicalText(artist);
        var tolerance = durationMilliseconds.HasValue
            ? Math.Max(5_000L, durationMilliseconds.Value / 10)
            : 0;
        var ranked = candidates
            .Where(candidate => NormalizeMbid(candidate.Id) != null &&
                                candidate.Score is >= 80 &&
                                CanonicalText(candidate.Title) == normalizedTitle &&
                                CanonicalText(ArtistCredit(candidate)) == normalizedArtist &&
                                (!durationMilliseconds.HasValue || candidate.Length.HasValue &&
                                 Math.Abs((long)candidate.Length.Value - durationMilliseconds.Value) <= tolerance))
            .Select(candidate => new
            {
                Recording = candidate,
                Difference = durationMilliseconds.HasValue
                    ? Math.Abs((long)candidate.Length!.Value - durationMilliseconds.Value)
                    : 0L,
                Confidence = durationMilliseconds.HasValue
                    ? .9 + .1 * (1 - Math.Min(1, (double)Math.Abs(
                        (long)candidate.Length!.Value - durationMilliseconds.Value) / tolerance))
                    : .9
            })
            .OrderBy(candidate => candidate.Difference)
            .ThenByDescending(candidate => candidate.Recording.Score)
            .ThenBy(candidate => candidate.Recording.Id, StringComparer.Ordinal)
            .ToArray();
        if (ranked.Length == 0) return null;
        if (ranked.Length > 1 &&
            ranked[0].Difference == ranked[1].Difference &&
            ranked[0].Recording.Score == ranked[1].Recording.Score)
            return null;
        return (ranked[0].Recording, ranked[0].Confidence);
    }

    private static string ArtistCredit(MusicBrainzRecording recording) =>
        string.Concat(recording.ArtistCredit?.Select(credit =>
            (credit.Name ?? credit.Artist?.Name ?? string.Empty) + (credit.JoinPhrase ?? string.Empty)) ?? []);

    internal static string? NormalizeMbid(string? value) =>
        Guid.TryParseExact(value?.Trim(), "D", out var id) && id != Guid.Empty
            ? id.ToString("D")
            : null;

    internal static string? NormalizeIsrc(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim().Replace("-", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        return normalized.Length == 12 &&
               normalized[..2].All(char.IsAsciiLetter) &&
               normalized[2..5].All(char.IsAsciiLetterOrDigit) &&
               normalized[5..].All(char.IsAsciiDigit)
            ? normalized
            : null;
    }

    internal static string EscapeLucene(string value)
    {
        const string special = "+-&|!(){}[]^\"~*?:\\/";
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (special.Contains(character, StringComparison.Ordinal)) builder.Append('\\');
            builder.Append(character);
        }
        return builder.ToString();
    }

    private static string NormalizeRequiredText(string? value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("MusicBrainz title and artist are required.", parameter);
        var normalized = string.Join(' ', value.Split((char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (normalized.Length > 500) throw new ArgumentException("MusicBrainz title and artist are limited to 500 characters.", parameter);
        return normalized;
    }

    private static string CanonicalText(string? value) =>
        string.Concat((value ?? string.Empty).Normalize(NormalizationForm.FormKC)
            .Where(char.IsLetterOrDigit)).ToUpperInvariant();

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var retry = response.Headers.RetryAfter;
        if (retry?.Delta is { } delta) return delta > TimeSpan.FromHours(1) ? TimeSpan.FromHours(1) : delta;
        if (retry?.Date is { } date)
        {
            var delay = date - DateTimeOffset.UtcNow;
            return delay <= TimeSpan.Zero ? TimeSpan.Zero :
                delay > TimeSpan.FromHours(1) ? TimeSpan.FromHours(1) : delay;
        }
        return null;
    }

    private sealed record MusicBrainzCacheEntry<T>(T Value, string SourceRevision) where T : class;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        MaxDepth = 64
    };
}

public sealed class MusicBrainzIsrcResponse
{
    [JsonPropertyName("recordings")]
    public List<MusicBrainzRecording>? Recordings { get; set; }
}

public sealed class MusicBrainzSearchResponse
{
    [JsonPropertyName("recordings")]
    public List<MusicBrainzRecording>? Recordings { get; set; }
    [JsonPropertyName("count")]
    public int Count { get; set; }
    [JsonPropertyName("created")]
    public DateTimeOffset? Created { get; set; }
}

public sealed class MusicBrainzRecording
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }
    [JsonPropertyName("title")]
    public string? Title { get; set; }
    [JsonPropertyName("disambiguation")]
    public string? Disambiguation { get; set; }
    [JsonPropertyName("length")]
    public int? Length { get; set; }
    [JsonPropertyName("score")]
    public int? Score { get; set; }
    [JsonPropertyName("first-release-date")]
    public string? FirstReleaseDate { get; set; }
    [JsonPropertyName("video")]
    public bool? Video { get; set; }
    [JsonPropertyName("artist-credit")]
    public List<MusicBrainzArtistCredit>? ArtistCredit { get; set; }
    [JsonPropertyName("releases")]
    public List<MusicBrainzRelease>? Releases { get; set; }
    [JsonPropertyName("isrcs")]
    public List<string>? Isrcs { get; set; }
    [JsonPropertyName("aliases")]
    public List<MusicBrainzAlias>? Aliases { get; set; }
    [JsonPropertyName("genres")]
    public List<MusicBrainzGenre>? Genres { get; set; }
    [JsonPropertyName("tags")]
    public List<MusicBrainzTag>? Tags { get; set; }
}

public sealed class MusicBrainzArtistCredit
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }
    [JsonPropertyName("joinphrase")]
    public string? JoinPhrase { get; set; }
    [JsonPropertyName("artist")]
    public MusicBrainzArtist? Artist { get; set; }
}

public sealed class MusicBrainzArtist
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }
    [JsonPropertyName("name")]
    public string? Name { get; set; }
    [JsonPropertyName("sort-name")]
    public string? SortName { get; set; }
    [JsonPropertyName("disambiguation")]
    public string? Disambiguation { get; set; }
    [JsonPropertyName("aliases")]
    public List<MusicBrainzAlias>? Aliases { get; set; }
}

public sealed class MusicBrainzAlias
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }
    [JsonPropertyName("sort-name")]
    public string? SortName { get; set; }
    [JsonPropertyName("locale")]
    public string? Locale { get; set; }
    [JsonPropertyName("primary")]
    public bool? Primary { get; set; }
}

public sealed class MusicBrainzRelease
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }
    [JsonPropertyName("title")]
    public string? Title { get; set; }
    [JsonPropertyName("date")]
    public string? Date { get; set; }
    [JsonPropertyName("country")]
    public string? Country { get; set; }
    [JsonPropertyName("status")]
    public string? Status { get; set; }
    [JsonPropertyName("barcode")]
    public string? Barcode { get; set; }
    [JsonPropertyName("release-group")]
    public MusicBrainzReleaseGroup? ReleaseGroup { get; set; }
    [JsonPropertyName("artist-credit")]
    public List<MusicBrainzArtistCredit>? ArtistCredit { get; set; }
    [JsonPropertyName("label-info")]
    public List<MusicBrainzLabelInfo>? LabelInfo { get; set; }
}

public sealed class MusicBrainzReleaseGroup
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }
    [JsonPropertyName("title")]
    public string? Title { get; set; }
    [JsonPropertyName("primary-type")]
    public string? PrimaryType { get; set; }
    [JsonPropertyName("secondary-types")]
    public List<string>? SecondaryTypes { get; set; }
    [JsonPropertyName("first-release-date")]
    public string? FirstReleaseDate { get; set; }
}

public sealed class MusicBrainzLabelInfo
{
    [JsonPropertyName("catalog-number")]
    public string? CatalogNumber { get; set; }
    [JsonPropertyName("label")]
    public MusicBrainzLabel? Label { get; set; }
}

public sealed class MusicBrainzLabel
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }
    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

public sealed class MusicBrainzGenre
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }
    [JsonPropertyName("name")]
    public string? Name { get; set; }
    [JsonPropertyName("count")]
    public int Count { get; set; }
}

public sealed class MusicBrainzTag
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }
    [JsonPropertyName("count")]
    public int Count { get; set; }
}
