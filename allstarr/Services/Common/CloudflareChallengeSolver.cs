using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Settings;

namespace allstarr.Services.Common;

public sealed record ChallengeSolverOptions(string Url, int TimeoutSeconds);
public sealed record ChallengeSolverTestResult(bool Ok, string Message, string? Version = null);
public sealed class CloudflareChallengeException() : Exception(ProviderError.BlockedByChallenge().SafeMessage);

public sealed class CloudflareChallengeSolver(HttpClient http,
    Func<CancellationToken, Task<ChallengeSolverOptions>> options, TimeProvider? timeProvider = null)
{
    public const string HttpClientName = "ChallengeSolver";
    public const int MaximumBytes = 2_000_000;
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, HostState> hosts = new(StringComparer.OrdinalIgnoreCase);

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
        if ((int)response.StatusCode is not (403 or 503 or 429 or 307)) return false;
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
        var state = hosts.GetOrAdd(original.RequestUri!.IdnHost, _ => new());
        var previous = enabled ? state.Current : null;
        using var first = Copy(original, previous, configured.Url);
        var response = await send(first, token);
        if (!await IsChallengeAsync(response, token)) return response;
        response.Dispose();
        if (!enabled) throw new CloudflareChallengeException();

        Solution? solution;
        await state.Gate.WaitAsync(token);
        try
        {
            solution = state.Current;
            if (solution == previous || solution == null || solution.Expires <= clock.GetUtcNow() || solution.SolverUrl != configured.Url)
            {
                state.Current = null;
                if (state.RetryAfter > clock.GetUtcNow()) throw new CloudflareChallengeException();
                try
                {
                    solution = await SolveAsync(baseUri!, configured, original.RequestUri, token);
                    state.Current = solution;
                    state.RetryAfter = default;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch
                {
                    state.RetryAfter = clock.GetUtcNow().AddSeconds(30);
                    throw new CloudflareChallengeException();
                }
            }
        }
        finally { state.Gate.Release(); }

        using var retry = Copy(original, solution, configured.Url);
        response = await send(retry, token);
        if (!await IsChallengeAsync(response, token)) return response;
        response.Dispose();
        if (original.Method == HttpMethod.Get && solution!.Url == original.RequestUri && solution.Status == 200 &&
            IsText(solution.ContentType) && Encoding.UTF8.GetByteCount(solution.Body) <= MaximumBytes)
        {
            return new(HttpStatusCode.OK)
            {
                RequestMessage = original,
                Content = new StringContent(solution.Body, Encoding.UTF8, solution.ContentType.Split(';')[0])
            };
        }
        throw new CloudflareChallengeException();
    }

    private HttpRequestMessage Copy(HttpRequestMessage original, Solution? solution, string solverUrl)
    {
        var copy = new HttpRequestMessage(original.Method, original.RequestUri);
        foreach (var header in original.Headers) copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
        if (solution != null && solution.SolverUrl == solverUrl && solution.Expires > clock.GetUtcNow())
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

    private async Task<Solution> SolveAsync(Uri baseUri, ChallengeSolverOptions configured, Uri target, CancellationToken token)
    {
        using var timer = new CancellationTokenSource(TimeSpan.FromSeconds(configured.TimeoutSeconds), clock);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, timer.Token);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "v1"))
        {
            Content = JsonContent.Create(new { cmd = "request.get", url = target.AbsoluteUri, maxTimeout = configured.TimeoutSeconds * 1000 })
        };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        // JSON escaping and cookie metadata can expand the envelope beyond the target body limit.
        await response.Content.LoadIntoBufferAsync(MaximumBytes * 6 + 65536, timeout.Token);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(timeout.Token), new JsonDocumentOptions { MaxDepth = 16 });
        var root = json.RootElement;
        if (root.GetProperty("status").GetString() != "ok") throw new CloudflareChallengeException();
        var result = root.GetProperty("solution");
        if (!Uri.TryCreate(result.GetProperty("url").GetString(), UriKind.Absolute, out var actual) || actual != target)
            throw new CloudflareChallengeException();
        var userAgent = result.GetProperty("userAgent").GetString() ?? "";
        if (userAgent.Length is 0 or > 2048 || userAgent.Any(char.IsControl)) throw new CloudflareChallengeException();
        var body = result.GetProperty("response").GetString() ?? "";
        if (Encoding.UTF8.GetByteCount(body) > MaximumBytes) throw new CloudflareChallengeException();
        var contentType = result.TryGetProperty("headers", out var headers) && headers.TryGetProperty("content-type", out var type)
            ? type.GetString() ?? "text/plain" : "text/plain";
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
        return new(actual, result.GetProperty("status").GetInt32(), body, contentType, userAgent, cookies,
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

    private static bool IsText(string type) => type.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
        type.Split(';')[0] is "application/json" or "application/ttml+xml" or "application/xml";
    private static bool PathMatches(string requestPath, string cookiePath) => requestPath == cookiePath ||
        requestPath.StartsWith(cookiePath, StringComparison.Ordinal) && (cookiePath.EndsWith('/') || requestPath[cookiePath.Length] == '/');
    private sealed class HostState
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public Solution? Current;
        public DateTimeOffset RetryAfter;
    }
    private sealed record SolverCookie(string Name, string Value, string Path, DateTimeOffset Expires, bool Secure);
    private sealed record Solution(Uri Url, int Status, string Body, string ContentType, string UserAgent,
        IReadOnlyList<SolverCookie> Cookies, DateTimeOffset Expires, string SolverUrl);
}

public sealed class CloudflareChallengeHandler(CloudflareChallengeSolver solver, IReadOnlyCollection<string> allowedHosts) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        request.Method == HttpMethod.Get && request.Content == null && request.RequestUri is { Scheme: "https", IsDefaultPort: true, UserInfo.Length: 0 } uri &&
        allowedHosts.Contains(uri.IdnHost, StringComparer.OrdinalIgnoreCase)
            ? solver.SendAsync(request, (outbound, token) => base.SendAsync(outbound, token), cancellationToken)
            : base.SendAsync(request, cancellationToken);
}
