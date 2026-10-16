using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace allstarr.Services.Common;

public sealed class OdesliSettings
{
    public bool Enabled { get; set; }
    public int RateLimitMs { get; set; } = 6000;
}

public sealed class OdesliLookupException(bool retryable, TimeSpan? retryAfter = null)
    : Exception("Cross-provider links are unavailable.")
{
    public bool Retryable { get; } = retryable;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>Cached, bounded public track links from Odesli/song.link.</summary>
public class OdesliService(
    IHttpClientFactory httpClientFactory,
    ILogger<OdesliService> logger,
    IApplicationCache cache,
    IOptions<OdesliSettings>? settings = null,
    ApplicationCacheRequestCoalescer? coalescer = null,
    TimeProvider? timeProvider = null)
{
    private readonly HttpClient _httpClient = PublicClient(httpClientFactory);
    private readonly SemaphoreSlim _requests = new(1, 1);
    private readonly ApplicationCacheRequestCoalescer _coalescer = coalescer ?? new(new ApplicationCacheActivityMetrics());
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private DateTimeOffset _nextRequestAt;
    private long _optionalUnavailableUntilUtcTicks;

    private static HttpClient PublicClient(IHttpClientFactory factory)
    {
        var client = factory.CreateClient("Odesli");
        client.DefaultRequestHeaders.Authorization = null;
        return client;
    }

    public Task<string?> ConvertTidalToSpotifyIdAsync(string tidalTrackId, CancellationToken cancellationToken = default) =>
        TranslateTrackUrlAsync(BuildTrackUrl("tidal", tidalTrackId)!, "spotify", cancellationToken);

    public Task<string?> ConvertUrlToSpotifyIdAsync(string musicUrl, CancellationToken cancellationToken = default) =>
        TranslateTrackUrlAsync(musicUrl, "spotify", cancellationToken);

    public async Task<string?> TranslateTrackUrlAsync(string sourceUrl, string targetProvider, CancellationToken cancellationToken = default)
    {
        try
        {
            return (await GetTrackLinksAsync(sourceUrl, cancellationToken)).GetValueOrDefault(NormalizeProviderId(targetProvider));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            logger.LogDebug("Cross-provider translation unavailable ({ErrorType})", error.GetType().Name);
            return null;
        }
    }

    public async Task<string?> TranslateCachedTrackUrlAsync(string sourceUrl, string targetProvider,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ParseTrackUrl(sourceUrl) is not { } source) return null;
        var key = CacheKeyBuilder.BuildOdesliTranslationKey(BuildTrackUrl(source.Provider, source.Id)!, "aliases-v1");
        var cached = await cache.GetAsync<Dictionary<string, string>>(key);
        cancellationToken.ThrowIfCancellationRequested();
        return cached?.GetValueOrDefault(NormalizeProviderId(targetProvider));
    }

    private void ThrowIfOptionalUnavailable()
    {
        if (Interlocked.Read(ref _optionalUnavailableUntilUtcTicks) > _timeProvider.GetUtcNow().UtcTicks)
            throw new OdesliLookupException(false);
    }

    public async Task<IReadOnlyDictionary<string, string>> GetTrackLinksAsync(string sourceUrl, CancellationToken cancellationToken)
    {
        if (ParseTrackUrl(sourceUrl) is not { } source) return new Dictionary<string, string>();
        var canonicalUrl = BuildTrackUrl(source.Provider, source.Id)!;
        var key = CacheKeyBuilder.BuildOdesliTranslationKey(canonicalUrl, "aliases-v1");
        cancellationToken.ThrowIfCancellationRequested();
        var cached = await cache.GetAsync<Dictionary<string, string>>(key);
        if (cached != null) return cached;
        ThrowIfOptionalUnavailable();
        return await _coalescer.RunAsync(key, async () =>
        {
            ThrowIfOptionalUnavailable();
            await _requests.WaitAsync(cancellationToken);
            try
            {
                ThrowIfOptionalUnavailable();
                var now = _timeProvider.GetUtcNow();
                var delay = _nextRequestAt - now;
                if (delay > TimeSpan.Zero) await Task.Delay(delay, _timeProvider, cancellationToken);
                _nextRequestAt = _timeProvider.GetUtcNow().AddMilliseconds(Math.Clamp(settings?.Value.RateLimitMs ?? 6000, 1000, 60000));
                using var request = new HttpRequestMessage(HttpMethod.Get,
                    "https://api.song.link/v1-alpha.1/links?url=" + Uri.EscapeDataString(canonicalUrl) + "&userCountry=US");
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
                {
                    var retry = response.Headers.RetryAfter?.Delta ??
                        (response.Headers.RetryAfter?.Date - _timeProvider.GetUtcNow()) ?? TimeSpan.FromMinutes(1);
                    retry = TimeSpan.FromSeconds(Math.Clamp(retry.TotalSeconds, 1, 3600));
                    _nextRequestAt = _timeProvider.GetUtcNow().Add(retry);
                    throw new OdesliLookupException(true, retry);
                }
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.Gone)
                {
                    Interlocked.Exchange(ref _optionalUnavailableUntilUtcTicks,
                        _timeProvider.GetUtcNow().AddHours(1).UtcTicks);
                    throw new OdesliLookupException(false);
                }
                var links = new Dictionary<string, string>(StringComparer.Ordinal);
                if (response.IsSuccessStatusCode)
                {
                    await response.Content.LoadIntoBufferAsync(1024 * 1024, cancellationToken);
                    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                    if (json.RootElement.TryGetProperty("linksByPlatform", out var platforms) && platforms.ValueKind == JsonValueKind.Object)
                        foreach (var item in platforms.EnumerateObject())
                        {
                            if (item.Value.ValueKind != JsonValueKind.Object || !item.Value.TryGetProperty("url", out var url) ||
                                url.ValueKind != JsonValueKind.String || ParseTrackUrl(url.GetString()) is not { } target)
                                continue;
                            if (links.TryGetValue(target.Provider, out var existing) && existing != target.Id)
                                throw new OdesliLookupException(false);
                            links[target.Provider] = target.Id;
                        }
                    // A cross-link response must still identify the exact requested source track.
                    if (links.GetValueOrDefault(source.Provider) != source.Id) links.Clear();
                }
                await cache.SetAsync(key, links, links.Count == 0 ? TimeSpan.FromHours(6) : CacheExtensions.OdesliLookupTTL);
                return links;
            }
            finally { _requests.Release(); }
        }, cancellationToken);
    }

    public static string? BuildTrackUrl(string provider, string trackId)
    {
        if (string.IsNullOrWhiteSpace(trackId)) return null;
        var id = Uri.EscapeDataString(trackId);
        return NormalizeProviderId(provider) switch
        {
            "spotify" => $"https://open.spotify.com/track/{id}",
            "deezer" => $"https://www.deezer.com/track/{id}",
            "apple-musickit" => $"https://music.apple.com/us/song/{id}",
            "qobuz" => $"https://open.qobuz.com/track/{id}",
            "tidal" => $"https://tidal.com/browse/track/{id}",
            _ => null
        };
    }

    private static (string Provider, string Id)? ParseTrackUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0) return null;
        var provider = uri.Host switch
        {
            "open.spotify.com" => "spotify",
            "www.deezer.com" or "deezer.com" => "deezer",
            "music.apple.com" => "apple-musickit",
            "open.qobuz.com" or "play.qobuz.com" => "qobuz",
            "tidal.com" or "listen.tidal.com" => "tidal",
            _ => null
        };
        if (provider == null) return null;
        var path = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string? id = null;
        if (provider == "apple-musickit")
        {
            if (Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query).TryGetValue("i", out var queryId) && queryId.Count == 1)
                id = queryId[0];
            else if (Array.IndexOf(path, "song") >= 0) id = path.LastOrDefault();
        }
        else
        {
            var position = Array.IndexOf(path, "track");
            if (position >= 0 && position == path.Length - 2) id = path[^1];
        }
        return id is { Length: > 0 and <= 128 } && id.All(character =>
            provider == "spotify" ? char.IsAsciiLetterOrDigit(character) : char.IsAsciiDigit(character))
            ? (provider, id) : null;
    }

    internal static string NormalizeProviderId(string provider)
    {
        const string prefix = "spotiflac-";
        var normalized = provider.Trim().ToLowerInvariant();
        if (normalized.StartsWith(prefix, StringComparison.Ordinal)) normalized = normalized[prefix.Length..];
        return normalized switch
        {
            "spotify-web" => "spotify",
            "qobuz-web" => "qobuz",
            "tidal-web" => "tidal",
            "applemusic" or "apple-music" or "apple-download" => "apple-musickit",
            _ => normalized
        };
    }
}
