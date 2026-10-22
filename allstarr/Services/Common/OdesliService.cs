using allstarr.Services.MusicBrainz;
using Microsoft.Extensions.Options;

namespace allstarr.Services.Common;

public sealed class OdesliSettings
{
    public bool Enabled { get; set; }
    public string? ApiKey { get; set; }
    public int RateLimitMs { get; set; } = 6000;
}

public sealed class OdesliLookupException(bool retryable, TimeSpan? retryAfter = null)
    : Exception("Cross-provider links are unavailable.")
{
    public bool Retryable { get; } = retryable;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>Cached, bounded cross-provider track links.</summary>
public class OdesliService(
    IHttpClientFactory httpClientFactory,
    ILogger<OdesliService> logger,
    IApplicationCache cache,
    IOptions<OdesliSettings>? settings = null,
    ApplicationCacheRequestCoalescer? coalescer = null,
    TimeProvider? timeProvider = null,
    MusicBrainzService? musicBrainz = null)
{
    private readonly PlatformLinkResolverChain _chain = new(httpClientFactory.CreateClient("Odesli"),
        timeProvider ?? TimeProvider.System, musicBrainz);
    private readonly ApplicationCacheRequestCoalescer _coalescer = coalescer ?? new(new ApplicationCacheActivityMetrics());

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
        CancellationToken cancellationToken = default, bool allowMusicBrainz = true)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ParseTrackUrl(sourceUrl) is not { } source) return null;
        var key = LinkCacheKey(BuildTrackUrl(source.Provider, source.Id)!, allowMusicBrainz);
        var cached = await cache.GetAsync<Dictionary<string, string>>(key);
        cancellationToken.ThrowIfCancellationRequested();
        return cached?.GetValueOrDefault(NormalizeProviderId(targetProvider));
    }

    public async Task<IReadOnlyDictionary<string, string>> GetTrackLinksAsync(string sourceUrl, CancellationToken cancellationToken,
        string? isrc = null, string? title = null, string? artist = null, bool allowMusicBrainz = true)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (settings?.Value.Enabled != true || ParseTrackUrl(sourceUrl) is not { } source)
            return new Dictionary<string, string>();
        var canonicalUrl = BuildTrackUrl(source.Provider, source.Id)!;
        var key = LinkCacheKey(canonicalUrl, allowMusicBrainz);
        var cached = await cache.GetAsync<Dictionary<string, string>>(key);
        cancellationToken.ThrowIfCancellationRequested();
        if (cached != null) return cached;
        return await _coalescer.RunAsync(key, async () =>
        {
            var links = await _chain.ResolveAsync(canonicalUrl, settings.Value.ApiKey, settings.Value.RateLimitMs,
                isrc, title, artist, cancellationToken, allowMusicBrainz);
            cancellationToken.ThrowIfCancellationRequested();
            if (links.Count > 0)
                await cache.SetAsync(key, links, CacheExtensions.OdesliLookupTTL);
            return links;
        }, cancellationToken);
    }

    private static string LinkCacheKey(string canonicalUrl, bool allowMusicBrainz) =>
        CacheKeyBuilder.BuildOdesliTranslationKey(canonicalUrl,
            allowMusicBrainz ? "platform-links-v3:musicbrainz" : "platform-links-v3:without-musicbrainz");

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
            "amazon" => $"https://music.amazon.com/tracks/{id}",
            "youtube" => $"https://music.youtube.com/watch?v={id}",
            _ => null
        };
    }

    internal static (string Provider, string Id)? ParseTrackUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0) return null;
        var provider = uri.Host switch
        {
            "open.spotify.com" => "spotify",
            "www.deezer.com" or "deezer.com" => "deezer",
            "music.apple.com" or "geo.music.apple.com" => "apple-musickit",
            "open.qobuz.com" or "play.qobuz.com" => "qobuz",
            "tidal.com" or "www.tidal.com" or "listen.tidal.com" => "tidal",
            "music.amazon.com" => "amazon",
            "music.youtube.com" or "youtube.com" or "www.youtube.com" or "youtu.be" => "youtube",
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
        else if (provider == "amazon")
        {
            var position = Array.IndexOf(path, "tracks");
            if (position >= 0 && position == path.Length - 2) id = path[^1];
            else if (Array.IndexOf(path, "albums") >= 0 &&
                Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query).TryGetValue("trackAsin", out var asin) && asin.Count == 1)
                id = asin[0];
        }
        else if (provider == "youtube")
        {
            if (uri.Host == "youtu.be" && path.Length == 1) id = path[0];
            else if (path is ["watch"] &&
                Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query).TryGetValue("v", out var video) && video.Count == 1)
                id = video[0];
        }
        else
        {
            var position = Array.IndexOf(path, "track");
            if (position >= 0 && position == path.Length - 2) id = path[^1];
        }
        return id is { Length: > 0 and <= 128 } && id.All(character =>
            provider switch
            {
                "spotify" or "amazon" => char.IsAsciiLetterOrDigit(character),
                "youtube" => char.IsAsciiLetterOrDigit(character) || character is '_' or '-',
                _ => char.IsAsciiDigit(character)
            })
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
            "amazonmusic" or "amazon-music" => "amazon",
            "youtubemusic" or "youtube-music" => "youtube",
            "qobuz-web" => "qobuz",
            "tidal-web" => "tidal",
            "applemusic" or "apple-music" or "apple-download" => "apple-musickit",
            _ => normalized
        };
    }
}
