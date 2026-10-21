using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using allstarr.Core.Capabilities;
using allstarr.Core.Settings;

namespace allstarr.Services.Common;

public sealed record ChallengeSolverOptions(string Url, int TimeoutSeconds);
public sealed record ChallengeSolverTestResult(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("version")] string? Version = null);
public sealed class CloudflareChallengeException() : Exception(ProviderError.BlockedByChallenge().SafeMessage);

public sealed class CloudflareChallengeSolver(HttpClient http,
    Func<CancellationToken, Task<ChallengeSolverOptions>> options, TimeProvider? timeProvider = null,
    ILogger<CloudflareChallengeSolver>? logger = null)
{
    public const string HttpClientName = "ChallengeSolver";
    public const int MaximumBytes = 2_000_000;
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly Dictionary<string, HostState> hosts = new(StringComparer.OrdinalIgnoreCase);
    private ChallengeSolverOptions? currentOptions;

    public static async Task<ChallengeSolverOptions> ReadOptionsAsync(IDurableRuntimeSettings settings, CancellationToken token)
    {
        var values = await settings.GetManyAsync(["ChallengeSolver:Url", "ChallengeSolver:TimeoutSeconds"], token);
        return new(values["ChallengeSolver:Url"].NormalizedValue, (int)values["ChallengeSolver:TimeoutSeconds"].Value);
    }

    public async Task<TimeSpan?> ConfiguredTimeoutAsync(CancellationToken token)
    {
        var configured = await options(token);
        return string.IsNullOrWhiteSpace(configured.Url) ? null : TimeSpan.FromSeconds(configured.TimeoutSeconds);
    }

    public static async Task<bool> IsChallengeAsync(HttpResponseMessage response, CancellationToken token)
    {
        if ((int)response.StatusCode is not (200 or 403 or 503 or 429 or 307)) return false;
        var cloudflare = response.Headers.Server.Any(value => value.Product?.Name is { } name &&
            (name.Equals("cloudflare", StringComparison.OrdinalIgnoreCase) || name.Equals("cloudflare-nginx", StringComparison.OrdinalIgnoreCase)));
        if (cloudflare && response.Headers.TryGetValues("cf-mitigated", out var values) &&
            values.Any(value => value.Equals("challenge", StringComparison.OrdinalIgnoreCase))) return true;
        if (response.Headers.Location?.OriginalString.Contains("/cdn-cgi/challenge-platform", StringComparison.OrdinalIgnoreCase) == true)
            return true;
        if (response.Content.Headers.ContentLength > MaximumBytes) return false;
        await response.Content.LoadIntoBufferAsync(MaximumBytes, token);
        var body = await response.Content.ReadAsStringAsync(token);
        return body.Contains("/cdn-cgi/challenge-platform", StringComparison.OrdinalIgnoreCase) || cloudflare &&
            (body.Contains("<title>Just a moment...</title>", StringComparison.OrdinalIgnoreCase) ||
             body.Contains("<title>Access denied</title>", StringComparison.OrdinalIgnoreCase) ||
             body.Contains("<title>Attention Required! | Cloudflare</title>", StringComparison.OrdinalIgnoreCase) ||
             body.Trim().Equals("error code: 1020", StringComparison.OrdinalIgnoreCase));
    }

    internal async Task<HttpResponseMessage> SendAsync(HttpRequestMessage original,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send, CancellationToken token)
    {
        var configured = await options(token);
        var enabled = OutboundRequestGuard.TryCreateConfiguredServiceUri(configured.Url, out var baseUri, out _);
        var state = StateFor(original.RequestUri!.IdnHost, configured);
        var previous = enabled ? state.Current : null;
        if (previous is { CookiesRejected: true } && IsCurrent(previous, configured) &&
            BodyResponse(original, previous) is { } cached) return cached;
        using var first = Copy(original, previous, configured.Url);
        var response = await send(first, token);
        if (!await IsChallengeAsync(response, token)) return response;
        response.Dispose();
        if (previous != null && first.Headers.Contains("Cookie")) previous.CookiesRejected = true;
        if (!enabled) throw new CloudflareChallengeException();

        await state.Gate.WaitAsync(token);
        try
        {
            var solution = state.Current;
            if (solution != null && IsCurrent(solution, configured))
            {
                if (BodyResponse(original, solution) is { } body) return body;
                if (solution != previous && !solution.CookiesRejected && solution.Cookies.Count > 0)
                {
                    using var sharedRetry = Copy(original, solution, configured.Url);
                    response = await send(sharedRetry, token);
                    if (!await IsChallengeAsync(response, token)) return response;
                    response.Dispose();
                    solution.CookiesRejected = true;
                }
            }
            if (state.RetryAfter > clock.GetUtcNow()) throw new CloudflareChallengeException();
            state.Current = null;
            var diagnostic = new SolveDiagnostic();
            var started = Stopwatch.GetTimestamp();
            try
            {
                solution = await SolveAsync(baseUri!, configured, original.RequestUri, diagnostic, token);
                state.Current = solution;
                if (BodyResponse(original, solution) is { } body)
                {
                    state.RetryAfter = default;
                    diagnostic.DeliveryPath = "solver-body";
                    return body;
                }
                if (solution.Cookies.Count > 0)
                {
                    using var retry = Copy(original, solution, configured.Url);
                    response = await send(retry, token);
                    diagnostic.CookieRetryChallenged = await IsChallengeAsync(response, token);
                    if (diagnostic.CookieRetryChallenged == false)
                    {
                        state.RetryAfter = default;
                        diagnostic.DeliveryPath = "cookies";
                        return response;
                    }
                    response.Dispose();
                    solution.CookiesRejected = true;
                }
                throw new CloudflareChallengeException();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { state.Current = null; throw; }
            catch
            {
                state.Current = null;
                state.RetryAfter = clock.GetUtcNow().AddSeconds(30);
                throw new CloudflareChallengeException();
            }
            finally
            {
                logger?.LogInformation("Challenge solve host={Host} solverHttpStatus={SolverHttpStatus} solutionStatus={SolutionStatus} contentKind={ContentKind} cookieCount={CookieCount} cookieRetryChallenged={CookieRetryChallenged} deliveryPath={DeliveryPath} elapsedMs={ElapsedMs}",
                    original.RequestUri.IdnHost, diagnostic.HttpStatus, diagnostic.SolutionStatus, diagnostic.ContentKind,
                    diagnostic.CookieCount, diagnostic.CookieRetryChallenged?.ToString() ?? "not-attempted", diagnostic.DeliveryPath,
                    (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
        }
        finally { state.Gate.Release(); }
    }

    private HostState StateFor(string host, ChallengeSolverOptions configured)
    {
        lock (hosts)
        {
            if (currentOptions != configured)
            {
                hosts.Clear();
                currentOptions = configured;
            }
            if (!hosts.TryGetValue(host, out var state)) hosts[host] = state = new();
            return state;
        }
    }

    private bool IsCurrent(Solution solution, ChallengeSolverOptions configured) =>
        solution.Expires > clock.GetUtcNow() && solution.SolverUrl == configured.Url;

    private static HttpResponseMessage? BodyResponse(HttpRequestMessage original, Solution solution)
    {
        if (original.Method != HttpMethod.Get || solution.Target != original.RequestUri || solution.Status != 200 ||
            !TryReadBody(original, solution, out var body, out var type)) return null;
        return new(HttpStatusCode.OK) { RequestMessage = original, Content = new StringContent(body, Encoding.UTF8, type) };
    }

    private HttpRequestMessage Copy(HttpRequestMessage original, Solution? solution, string solverUrl)
    {
        var copy = new HttpRequestMessage(original.Method, original.RequestUri);
        foreach (var header in original.Headers) copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
        if (solution is { CookiesRejected: false } && solution.SolverUrl == solverUrl && solution.Expires > clock.GetUtcNow())
        {
            var cookies = solution.Cookies.Where(cookie => cookie.Expires > clock.GetUtcNow() &&
                (!cookie.Secure || original.RequestUri!.Scheme == "https") && PathMatches(original.RequestUri!.AbsolutePath, cookie.Path));
            copy.Headers.Remove("Cookie");
            var value = string.Join("; ", cookies.Select(cookie => cookie.Name + "=" + cookie.Value));
            if (value.Length > 0) copy.Headers.TryAddWithoutValidation("Cookie", value);
            copy.Headers.UserAgent.Clear();
            copy.Headers.UserAgent.ParseAdd(solution.UserAgent);
        }
        return copy;
    }

    private async Task<Solution> SolveAsync(Uri baseUri, ChallengeSolverOptions configured, Uri target, SolveDiagnostic diagnostic, CancellationToken token)
    {
        using var timer = new CancellationTokenSource(TimeSpan.FromSeconds(configured.TimeoutSeconds), clock);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, timer.Token);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "v1"))
        {
            Content = JsonContent.Create(new { cmd = "request.get", url = target.AbsoluteUri, maxTimeout = configured.TimeoutSeconds * 1000 })
        };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        diagnostic.HttpStatus = (int)response.StatusCode;
        response.EnsureSuccessStatusCode();
        // JSON escaping and cookie metadata can expand the envelope beyond the target body limit.
        await response.Content.LoadIntoBufferAsync(MaximumBytes * 6 + 65536, timeout.Token);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(timeout.Token), new JsonDocumentOptions { MaxDepth = 16 });
        var root = json.RootElement;
        if (root.GetProperty("status").GetString() != "ok") throw new CloudflareChallengeException();
        var result = root.GetProperty("solution");
        diagnostic.SolutionStatus = result.GetProperty("status").GetInt32();
        var body = result.GetProperty("response").GetString() ?? "";
        diagnostic.ContentKind = ContentKind(body);
        if (!Uri.TryCreate(result.GetProperty("url").GetString(), UriKind.Absolute, out var actual) ||
            actual.Scheme != target.Scheme || actual.IdnHost != target.IdnHost || actual.Port != target.Port || actual.UserInfo.Length != 0)
            throw new CloudflareChallengeException();
        var userAgent = result.GetProperty("userAgent").GetString() ?? "";
        if (userAgent.Length is 0 or > 2048 || userAgent.Any(char.IsControl)) throw new CloudflareChallengeException();
        if (Encoding.UTF8.GetByteCount(body) > MaximumBytes) throw new CloudflareChallengeException();
        var contentType = "text/plain";
        if (result.TryGetProperty("headers", out var headers))
            foreach (var header in headers.EnumerateObject())
                if (header.Name.Equals("content-type", StringComparison.OrdinalIgnoreCase)) contentType = header.Value.GetString() ?? "text/plain";
        if (contentType.Length > 200 || contentType.Any(char.IsControl)) throw new CloudflareChallengeException();
        var cookies = new List<SolverCookie>();
        var now = clock.GetUtcNow();
        foreach (var item in result.GetProperty("cookies").EnumerateArray())
        {
            if (cookies.Count >= 100) break;
            var domain = item.GetProperty("domain").GetString()?.TrimStart('.') ?? "";
            if (!target.IdnHost.Equals(domain, StringComparison.OrdinalIgnoreCase) &&
                !target.IdnHost.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase)) continue;
            var name = item.GetProperty("name").GetString() ?? "";
            var value = item.GetProperty("value").GetString() ?? "";
            if (name.Length == 0 || name.Length + value.Length > 4096 ||
                name.Any(ch => !char.IsAsciiLetterOrDigit(ch) && !"!#$%&'*+-.^_`|~".Contains(ch)) ||
                value.Any(ch => ch < 0x21 || ch > 0x7e || ch is ';' or ',' or '"' or '\\')) continue;
            var path = item.TryGetProperty("path", out var pathValue) ? pathValue.GetString() ?? "/" : "/";
            if (!path.StartsWith('/')) continue;
            var expires = now.AddMinutes(15);
            if (item.TryGetProperty("expires", out var expiry) && expiry.TryGetDouble(out var seconds) && seconds > 0)
                expires = DateTimeOffset.FromUnixTimeMilliseconds(checked((long)(seconds * 1000)));
            if (expires <= now) continue;
            cookies.Add(new(name, value, path, expires, item.TryGetProperty("secure", out var secure) && secure.ValueKind == JsonValueKind.True));
        }
        diagnostic.CookieCount = cookies.Count;
        return new(target, diagnostic.SolutionStatus.Value, body, contentType, userAgent, cookies,
            cookies.Count == 0 ? now.AddMinutes(1) : cookies.Min(cookie => cookie.Expires), configured.Url);
    }

    public async Task<ChallengeSolverTestResult> TestAsync(ChallengeSolverOptions configured, CancellationToken token)
    {
        if (configured.Url.Any(char.IsControl) || !OutboundRequestGuard.TryCreateConfiguredServiceUri(configured.Url, out var baseUri, out _) || configured.TimeoutSeconds is < 10 or > 180)
            return new(false, "Use an HTTP or HTTPS solver URL without credentials, query or fragment, and a timeout from 10 to 180 seconds.");
        try
        {
            using var timer = new CancellationTokenSource(TimeSpan.FromSeconds(configured.TimeoutSeconds), clock);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, timer.Token);
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri!, "health"));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode) return new(false, "Solver health check returned HTTP " + (int)response.StatusCode + ".");
            await response.Content.LoadIntoBufferAsync(16384, timeout.Token);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(timeout.Token));
            var root = json.RootElement;
            var ok = root.TryGetProperty("status", out var status) && status.GetString() == "ok" ||
                root.TryGetProperty("msg", out var msg) && msg.GetString() == "Byparr is working!";
            var version = root.TryGetProperty("version", out var versionValue) && versionValue.ValueKind == JsonValueKind.String ? versionValue.GetString() : null;
            if (version != null && (version.Length > 40 || !version.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '+'))) version = null;
            return ok ? new(true, "Solver reachable", version) : new(false, "Solver returned an invalid health response.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return new(false, "Solver health check timed out."); }
        catch { return new(false, "Could not reach the solver or read its health response."); }
    }

    private static bool HasChallengeMarkers(string body) =>
        body.Contains("/cdn-cgi/challenge-platform", StringComparison.OrdinalIgnoreCase) ||
        body.Contains("<title>Just a moment...</title>", StringComparison.OrdinalIgnoreCase) ||
        body.Contains("<title>Access denied</title>", StringComparison.OrdinalIgnoreCase) ||
        body.Contains("<title>Attention Required! | Cloudflare</title>", StringComparison.OrdinalIgnoreCase) ||
        body.Contains("cf-challenge-running", StringComparison.OrdinalIgnoreCase) ||
        body.Contains("cf-turnstile-response", StringComparison.OrdinalIgnoreCase) ||
        body.Trim().Equals("error code: 1020", StringComparison.OrdinalIgnoreCase);

    private static bool TryReadBody(HttpRequestMessage request, Solution solution, out string body, out string type)
    {
        body = solution.Body;
        type = solution.ContentType.Split(';')[0].Trim().ToLowerInvariant();
        if (!(type.StartsWith("text/", StringComparison.Ordinal) || type is "application/json" or "application/ttml+xml" or "application/xml") ||
            HasChallengeMarkers(body)) return false;
        var accepted = request.Headers.Accept.Select(value => value.MediaType).ToArray();
        if (accepted.Length == 1 && accepted[0] is "application/json" or "application/ttml+xml" or "application/xml" or "text/plain") type = accepted[0]!;
        if (!IsJson(body) && !IsXml(body)) body = Unwrap(body);
        if (HasChallengeMarkers(body) || Encoding.UTF8.GetByteCount(body) > MaximumBytes) return false;
        if (type == "application/json") return IsJson(body);
        if (type is "application/xml" or "application/ttml+xml") return IsXml(body, type == "application/ttml+xml");
        if (IsJson(body)) { type = "application/json"; return true; }
        if (IsXml(body)) { type = "application/xml"; return true; }
        if (body.TrimStart().StartsWith('<')) return false;
        type = "text/plain";
        return true;
    }

    private static string ContentKind(string body)
    {
        if (HasChallengeMarkers(body)) return "challenge";
        if (IsJson(body)) return "json";
        if (IsXml(body)) return "xml";
        var unwrapped = Unwrap(body);
        if (unwrapped != body && IsJson(unwrapped)) return "html-wrapped-json";
        if (unwrapped != body && IsXml(unwrapped)) return "xml";
        return body.TrimStart().StartsWith('<') ? "html" : "text";
    }

    private static bool IsJson(string body)
    {
        try { using var json = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 16 }); return true; }
        catch (JsonException) { return false; }
    }

    private static bool IsXml(string body, bool ttml = false)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(body), new XmlReaderSettings
            { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumBytes });
            var document = XDocument.Load(reader);
            return document.Root is { } root && !root.Name.LocalName.Equals("html", StringComparison.OrdinalIgnoreCase) &&
                (!ttml || root.Name.LocalName == "tt");
        }
        catch (XmlException) { return false; }
    }

    private static string Unwrap(string body)
    {
        var pre = Regex.Match(body, @"<pre\b[^>]*>(.*?)</pre\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (pre.Success) return WebUtility.HtmlDecode(pre.Groups[1].Value);
        var source = Regex.Match(body, @"<div\b[^>]*\bid\s*=\s*[""']webkit-xml-viewer-source-xml[""'][^>]*>",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (!source.Success) return body;
        var start = source.Index + source.Length;
        var depth = 1;
        foreach (Match tag in Regex.Matches(body[start..], @"<!\[CDATA\[.*?\]\]>|<!--.*?-->|</?div\b[^>]*>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
        {
            if (tag.Value.StartsWith("<!", StringComparison.Ordinal) || tag.Value.EndsWith("/>", StringComparison.Ordinal)) continue;
            depth += tag.Value.StartsWith("</", StringComparison.Ordinal) ? -1 : 1;
            if (depth == 0)
            {
                var xml = body.Substring(start, tag.Index);
                return IsXml(xml) ? xml : WebUtility.HtmlDecode(xml);
            }
        }
        return body;
    }

    private static bool PathMatches(string requestPath, string cookiePath) => requestPath == cookiePath ||
        requestPath.StartsWith(cookiePath, StringComparison.Ordinal) && (cookiePath.EndsWith('/') || requestPath[cookiePath.Length] == '/');
    private sealed class HostState
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public Solution? Current;
        public DateTimeOffset RetryAfter;
    }
    private sealed record SolverCookie(string Name, string Value, string Path, DateTimeOffset Expires, bool Secure);
    private sealed record Solution(Uri Target, int Status, string Body, string ContentType, string UserAgent,
        IReadOnlyList<SolverCookie> Cookies, DateTimeOffset Expires, string SolverUrl)
    {
        public volatile bool CookiesRejected;
    }
    private sealed class SolveDiagnostic
    {
        public int? HttpStatus;
        public int? SolutionStatus;
        public string? ContentKind;
        public int CookieCount;
        public bool? CookieRetryChallenged;
        public string DeliveryPath = "none";
    }
}

public sealed class CloudflareChallengeHandler(CloudflareChallengeSolver solver, IReadOnlyCollection<string> allowedHosts) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        request.Method == HttpMethod.Get && request.Content == null && request.RequestUri is { Scheme: "https", IsDefaultPort: true, UserInfo.Length: 0 } uri &&
        allowedHosts.Contains(uri.IdnHost, StringComparer.OrdinalIgnoreCase)
            ? solver.SendAsync(request, (outbound, token) => base.SendAsync(outbound, token), cancellationToken)
            : base.SendAsync(request, cancellationToken);
}
