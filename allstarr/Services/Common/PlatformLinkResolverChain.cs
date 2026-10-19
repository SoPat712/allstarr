using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using allstarr.Services.MusicBrainz;

namespace allstarr.Services.Common;

// Adapted from SpotiFLAC-Mobile (MIT), rust_backend/crates/providers/src/resolver
internal sealed class PlatformLinkResolverChain
{
    private const int MaximumHtmlBytes = 8 * 1024 * 1024;
    private const int MaximumJsonBytes = 2 * 1024 * 1024;
    private const int MaximumNodes = 100000;
    private static readonly ConditionalWeakTable<TimeProvider, ServiceRates> Rates = new();
    private static readonly Regex Tags = new(
        "<!--.*?-->|<(?<closing>/)?(?<name>[a-zA-Z][a-zA-Z0-9:-]*)(?<attributes>(?:[^<>\"']|\"[^\"]*\"|'[^']*')*)>",
        RegexOptions.Singleline | RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1));
    private static readonly Regex Attributes = new(
        "(?<name>[^\\s=/>]+)(?:\\s*=\\s*(?:\"(?<value>[^\"]*)\"|'(?<value>[^']*)'|(?<value>[^\\s>]+)))?",
        RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1));
    private static readonly string[] BrowserHeaders =
    [
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10.15; rv:132.0) Gecko/20100101 Firefox/132.0"
    ];
    private readonly HttpClient _http;
    private readonly TimeProvider _clock;
    private readonly MusicBrainzService? _musicBrainz;
    private readonly ServiceRates _rates;
    private long _officialUnavailableUntil;

    public PlatformLinkResolverChain(HttpClient http, TimeProvider clock, MusicBrainzService? musicBrainz)
    {
        _http = http;
        _http.DefaultRequestHeaders.Clear();
        _clock = clock;
        _musicBrainz = musicBrainz;
        _rates = Rates.GetValue(clock, value => new(value));
    }

    public async Task<Dictionary<string, string>> ResolveAsync(string sourceUrl, string? apiKey, int apiIntervalMs,
        string? isrc, string? title, string? artist, CancellationToken cancellationToken)
    {
        if (sourceUrl.Length > 65536) throw new OdesliLookupException(false);
        if (title?.Length > 500) title = null;
        if (artist?.Length > 500) artist = null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12), _clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var token = linked.Token;
        var links = new Dictionary<string, string>(StringComparer.Ordinal);
        var source = OdesliService.ParseTrackUrl(sourceUrl)!.Value;
        var failed = false;
        for (var step = string.IsNullOrWhiteSpace(apiKey) ? 1 : 0; step < 5; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (token.IsCancellationRequested) break;
            try
            {
                Dictionary<string, string> found;
                switch (step)
                {
                    case 0:
                        if (Interlocked.Read(ref _officialUnavailableUntil) > _clock.GetUtcNow().UtcTicks) continue;
                        var official = await RequestAsync(new Uri("https://api.song.link/v1-alpha.1/links?url=" +
                            Uri.EscapeDataString(sourceUrl) + "&userCountry=US&key=" + Uri.EscapeDataString(apiKey!)),
                            _rates.Official, false, token, apiInterval: TimeSpan.FromMilliseconds(Math.Clamp(apiIntervalMs, 1000, 60000)));
                        found = ParsePlatformJson(official, out _, out _);
                        if (found.GetValueOrDefault(source.Provider) != source.Id) throw new OdesliLookupException(false);
                        if (found.Count > 1) return found;
                        continue;
                    case 1:
                        found = ParseHtml(await RequestAsync(new Uri("https://song.link/" + Uri.EscapeDataString(sourceUrl)),
                            _rates.Web, true, token, songLink: true), sourceUrl, token);
                        break;
                    case 2:
                        found = ParsePlatformJson(await RequestAsync(new Uri("https://api.unitune.art/v1-alpha.1/links?url=" +
                            Uri.EscapeDataString(sourceUrl)), _rates.UniTune, false, token), out var foundTitle, out var foundArtist);
                        if (string.IsNullOrWhiteSpace(title)) title = foundTitle;
                        if (string.IsNullOrWhiteSpace(artist)) artist = foundArtist;
                        break;
                    case 3:
                        if (_musicBrainz == null) continue;
                        found = new(StringComparer.Ordinal);
                        foreach (var url in await _musicBrainz.LookupPlatformLinksAsync(isrc, title, artist, token))
                            AddLink(found, url);
                        break;
                    default:
                        found = await SquiglyAsync(sourceUrl, token);
                        break;
                }
                if (found.TryGetValue(source.Provider, out var sourceId) && sourceId != source.Id)
                    throw new OdesliLookupException(false);
                foreach (var pair in found) links.TryAdd(pair.Key, pair.Value);
                if (links.Count >= 4 && new[] { "deezer", "tidal", "amazon", "qobuz" }.Count(links.ContainsKey) >= 2) break;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is OperationCanceledException or HttpRequestException or JsonException or
                OdesliLookupException or MusicBrainzLookupException or RegexMatchTimeoutException)
            {
                failed = true;
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (links.Count > 0)
        {
            links.TryAdd(source.Provider, source.Id);
            return links;
        }
        if (failed || deadline.IsCancellationRequested) throw new OdesliLookupException(true);
        return links;
    }

    private async Task<Dictionary<string, string>> SquiglyAsync(string sourceUrl, CancellationToken cancellationToken)
    {
        var created = await RequestAsync(new Uri("https://squigly.link/api/create"), _rates.Squigly, false,
            cancellationToken, body: JsonSerializer.Serialize(new { url = sourceUrl }));
        using var json = JsonDocument.Parse(created);
        var pageUrl = Text(json.RootElement, "full_url");
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var page) || !IsPublicEndpoint(page) || page.Host != "squigly.link")
            throw new OdesliLookupException(false);
        var html = await RequestAsync(page, _rates.Squigly, true, cancellationToken);
        const string marker = "window.__SQUIGLY_LINK__ =";
        var offset = html.IndexOf(marker, StringComparison.Ordinal);
        if (offset < 0) throw new OdesliLookupException(false);
        var bytes = Encoding.UTF8.GetBytes(html[(offset + marker.Length)..]);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 32 });
        using var payload = JsonDocument.ParseValue(ref reader);
        var links = new Dictionary<string, string>(StringComparer.Ordinal);
        if (payload.RootElement.ValueKind == JsonValueKind.Object && payload.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("services", out var services) && services.ValueKind == JsonValueKind.Object)
            foreach (var service in services.EnumerateObject().Take(64))
                AddLink(links, Text(service.Value, "url"));
        return links;
    }

    private async Task<string> RequestAsync(Uri uri, PlatformLinkRateLimiter limiter, bool html,
        CancellationToken cancellationToken, bool songLink = false, string? body = null, TimeSpan? apiInterval = null)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(6), _clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var token = linked.Token;
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            if (!IsPublicEndpoint(uri) || !(songLink ? IsSongLinkHost(uri.Host) :
                uri.Host is "api.song.link" or "api.unitune.art" or "squigly.link"))
                throw new OdesliLookupException(false);
            await limiter.WaitAsync(token, apiInterval);
            using var request = new HttpRequestMessage(body == null ? HttpMethod.Get : HttpMethod.Post, uri);
            request.Headers.Accept.ParseAdd(html ? "text/html" : "application/json");
            request.Headers.UserAgent.ParseAdd(BrowserHeaders[Random.Shared.Next(BrowserHeaders.Length)]);
            if (body != null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                if (!html || response.Headers.Location == null || redirects == 5) throw new OdesliLookupException(false);
                var next = new Uri(uri, response.Headers.Location);
                if (!IsPublicEndpoint(next) || !(songLink ? IsSongLinkHost(next.Host) : next.Host == "squigly.link"))
                    throw new OdesliLookupException(false);
                uri = next;
                continue;
            }
            if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
            {
                var retry = response.Headers.RetryAfter?.Delta ??
                    (response.Headers.RetryAfter?.Date - _clock.GetUtcNow()) ?? TimeSpan.FromMinutes(1);
                await limiter.BackoffAsync(retry, token);
                throw new OdesliLookupException(true);
            }
            if (!response.IsSuccessStatusCode)
            {
                if (uri.Host == "api.song.link" && response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.Gone)
                    Interlocked.Exchange(ref _officialUnavailableUntil, _clock.GetUtcNow().AddHours(1).UtcTicks);
                throw new OdesliLookupException(false);
            }
            var limit = html ? MaximumHtmlBytes : MaximumJsonBytes;
            if (response.Content.Headers.ContentLength > limit) throw new OdesliLookupException(false);
            await response.Content.LoadIntoBufferAsync(limit, token);
            return await response.Content.ReadAsStringAsync(token);
        }
        throw new OdesliLookupException(false);
    }

    internal static Dictionary<string, string> ParseHtml(string html, string sourceUrl, CancellationToken cancellationToken)
    {
        if (Encoding.UTF8.GetByteCount(html) > MaximumHtmlBytes) throw new OdesliLookupException(false);
        var links = new Dictionary<string, string>(StringComparer.Ordinal);
        var nodes = 0;
        var skipUntil = 0;
        foreach (Match tag in Tags.Matches(html))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++nodes > MaximumNodes) throw new OdesliLookupException(false);
            if (tag.Index < skipUntil || tag.Value.StartsWith("<!--", StringComparison.Ordinal)) continue;
            var name = tag.Groups["name"].Value;
            if (tag.Groups["closing"].Success) continue;
            if (name.Equals("script", StringComparison.OrdinalIgnoreCase) || name.Equals("style", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("textarea", StringComparison.OrdinalIgnoreCase) || name.Equals("title", StringComparison.OrdinalIgnoreCase))
            {
                var end = html.IndexOf("</" + name, tag.Index + tag.Length, StringComparison.OrdinalIgnoreCase);
                skipUntil = end < 0 ? html.Length : end;
                continue;
            }
            if (!name.Equals("a", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (Match attribute in Attributes.Matches(tag.Groups["attributes"].Value))
                if (attribute.Groups["name"].Value.Equals("href", StringComparison.OrdinalIgnoreCase))
                {
                    AddLink(links, WebUtility.HtmlDecode(attribute.Groups["value"].Value));
                    break;
                }
        }
        if (OdesliService.ParseTrackUrl(sourceUrl) is { } source)
        {
            if (links.TryGetValue(source.Provider, out var id) && id != source.Id) throw new OdesliLookupException(false);
            links.TryAdd(source.Provider, source.Id);
        }
        if (links.Count < 2) throw new OdesliLookupException(false);
        return links;
    }

    private static Dictionary<string, string> ParsePlatformJson(string body, out string? title, out string? artist)
    {
        using var json = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 32 });
        var root = json.RootElement;
        var links = new Dictionary<string, string>(StringComparer.Ordinal);
        title = artist = null;
        if (root.ValueKind != JsonValueKind.Object) return links;
        if (root.TryGetProperty("linksByPlatform", out var platforms) && platforms.ValueKind == JsonValueKind.Object)
            foreach (var platform in platforms.EnumerateObject().Take(64)) AddLink(links, Text(platform.Value, "url"));
        if (root.TryGetProperty("entitiesByUniqueId", out var entities) && entities.ValueKind == JsonValueKind.Object)
        {
            var entityId = Text(root, "entityUniqueId");
            var entity = entityId != null && entities.TryGetProperty(entityId, out var exact) ? exact :
                entities.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal).FirstOrDefault().Value;
            title = Text(entity, "title") is { Length: <= 500 } foundTitle ? foundTitle : null;
            artist = Text(entity, "artistName") is { Length: <= 500 } foundArtist ? foundArtist : null;
        }
        return links;
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static void AddLink(Dictionary<string, string> links, string? url)
    {
        if (OdesliService.ParseTrackUrl(url) is not { } target) return;
        if (links.TryGetValue(target.Provider, out var id) && id != target.Id) throw new OdesliLookupException(false);
        links[target.Provider] = target.Id;
    }

    private static bool IsPublicEndpoint(Uri uri) => uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.AbsoluteUri.Length <= 65536;
    private static bool IsSongLinkHost(string host) => host is "song.link" or "album.link" or "artist.link" or "odesli.co" or "www.odesli.co";

    private sealed class ServiceRates(TimeProvider clock)
    {
        public PlatformLinkRateLimiter Official { get; } = new(1, TimeSpan.FromSeconds(6), clock);
        public PlatformLinkRateLimiter Web { get; } = new(20, TimeSpan.FromMinutes(1), clock);
        public PlatformLinkRateLimiter UniTune { get; } = new(30, TimeSpan.FromMinutes(1), clock);
        public PlatformLinkRateLimiter Squigly { get; } = new(18, TimeSpan.FromMinutes(1), clock);
    }
}
