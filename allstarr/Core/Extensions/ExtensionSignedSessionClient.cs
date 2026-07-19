using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;

namespace allstarr.Core.Extensions;

public sealed record ExtensionSignedSessionStatus(
    bool Authenticated,
    string? ExpiresAt,
    string InstallId,
    string? SessionId,
    string AppVersion,
    string Platform,
    string? AuthUrl = null,
    string? Error = null);

internal sealed class ExtensionSignedSessionClient
{
    private const int MaximumResponseBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromHours(1);
    private readonly ExtensionSignedSessionConfig _config;
    private readonly IHttpClientFactory _clients;
    private readonly IDataProtector _protector;
    private readonly IReadOnlySet<string> _allowedOrigins;
    private readonly string _statePath;
    private readonly string _extensionId;
    private readonly object _gate = new();
    private string? _pendingAuthUrl;

    public ExtensionSignedSessionClient(
        ExtensionSignedSessionConfig config,
        IHttpClientFactory clients,
        IDataProtector protector,
        IReadOnlySet<string> allowedOrigins,
        string runtimeStateDirectory,
        string extensionId)
    {
        _config = config;
        _clients = clients;
        _protector = protector;
        _allowedOrigins = allowedOrigins;
        _extensionId = extensionId;
        var scope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{config.Namespace}\n{config.BaseUrl}\n{config.AppVersion}\n{config.Platform}"))).ToLowerInvariant()[..16];
        _statePath = Path.Combine(runtimeStateDirectory, $"signed-session-{scope}.protected");
    }

    public ExtensionSignedSessionStatus Status()
    {
        lock (_gate)
        {
            var record = Load();
            var authenticated = HasUsableSession(record);
            return new(authenticated, record.ExpiresAt, record.InstallId,
                authenticated ? record.SessionId : null, _config.AppVersion, _config.Platform, _pendingAuthUrl);
        }
    }

    public object Clear()
    {
        lock (_gate)
        {
            var record = Load();
            Save(record with { SessionId = null, SessionSecret = null, ExpiresAt = null });
            _pendingAuthUrl = null;
            return new { success = true };
        }
    }

    public object CompleteGrant(string? grant)
    {
        grant = grant?.Trim();
        if (string.IsNullOrWhiteSpace(grant)) return new { success = false, error = "no pending grant" };
        lock (_gate)
        {
            try
            {
                var record = Load();
                var payload = JsonSerializer.Serialize(new
                {
                    grant,
                    install_id = record.InstallId,
                    app_version = _config.AppVersion,
                    platform = _config.Platform
                });
                var response = SendUnsigned(HttpMethod.Post, Resolve(_config.Endpoints.Exchange), payload);
                if (!response.IsSuccessStatusCode)
                    return new { success = false, error = $"session exchange failed: HTTP {(int)response.StatusCode}" };
                var exchanged = JsonSerializer.Deserialize<SessionExchange>(response.Body,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (string.IsNullOrWhiteSpace(exchanged?.SessionId) ||
                    string.IsNullOrWhiteSpace(exchanged.SessionSecret) ||
                    string.IsNullOrWhiteSpace(exchanged.ExpiresAt))
                    return new { success = false, error = "session exchange response missing session fields" };
                Save(record with
                {
                    SessionId = exchanged.SessionId,
                    SessionSecret = exchanged.SessionSecret,
                    ExpiresAt = exchanged.ExpiresAt
                });
                _pendingAuthUrl = null;
                return new { success = true };
            }
            catch (Exception exception)
            {
                return new { success = false, error = exception.Message };
            }
        }
    }

    public object StartVerification()
    {
        lock (_gate)
        {
            try
            {
                var record = Load();
                var bootstrap = Resolve(_config.Endpoints.Bootstrap);
                var builder = new UriBuilder(bootstrap);
                var separator = string.IsNullOrEmpty(builder.Query) ? "" : builder.Query.TrimStart('?') + "&";
                builder.Query = $"{separator}app_version={Uri.EscapeDataString(_config.AppVersion)}&install_id={Uri.EscapeDataString(record.InstallId)}";
                var response = SendUnsigned(HttpMethod.Get, builder.Uri, null);
                if (!response.IsSuccessStatusCode)
                    return new { success = false, error = $"session bootstrap failed: HTTP {(int)response.StatusCode}" };
                var boot = JsonSerializer.Deserialize<SessionExchange>(response.Body,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (!string.IsNullOrWhiteSpace(boot?.SessionId) && !string.IsNullOrWhiteSpace(boot.SessionSecret) &&
                    !string.IsNullOrWhiteSpace(boot.ExpiresAt))
                {
                    Save(record with { SessionId = boot.SessionId, SessionSecret = boot.SessionSecret, ExpiresAt = boot.ExpiresAt });
                    _pendingAuthUrl = null;
                    return new { success = true, authenticated = true };
                }
                var authUrl = boot?.AuthUrl ?? boot?.ChallengeUrl;
                if (string.IsNullOrWhiteSpace(authUrl) && !string.IsNullOrWhiteSpace(boot?.ChallengeId))
                    authUrl = BuildChallengeUrl(boot.ChallengeId);
                if (string.IsNullOrWhiteSpace(authUrl))
                    return new { success = false, error = "session bootstrap did not return a challenge" };
                _pendingAuthUrl = authUrl;
                return VerificationRequired(authUrl);
            }
            catch (Exception exception)
            {
                return new { success = false, error = exception.Message };
            }
        }
    }

    public object SignedFetch(string method, string path, string? body, object? headers)
    {
        lock (_gate)
        {
            var record = Load();
            if (!HasUsableSession(record)) return StartVerification();
            if (TryExpiry(record, out var expiry) && expiry - DateTimeOffset.UtcNow <= RefreshSkew &&
                !string.IsNullOrWhiteSpace(_config.Endpoints.Refresh))
                record = TryRefresh(record);
            var payload = body ?? string.Empty;
            var extraHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (headers is Jint.Native.Object.ObjectInstance headerObject)
                foreach (var entry in headerObject.GetOwnProperties())
                    extraHeaders[entry.Key.ToString()] = headerObject.Get(entry.Key).ToString();
            var response = SendSigned(record, method, path, payload, extraHeaders);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.PreconditionRequired)
            {
                Save(record with { SessionId = null, SessionSecret = null, ExpiresAt = null });
                return StartVerification();
            }
            return response.ToHostResponse();
        }
    }

    private SignedResponse SendSigned(SessionRecord record, string method, string path, string payload,
        IReadOnlyDictionary<string, string>? extraHeaders)
    {
            var target = Resolve(path);
            var requestMethod = method.Trim().ToUpperInvariant();
            var timestamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
            var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
            var bodyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
            var window = DateTimeOffset.Parse(timestamp).ToUnixTimeSeconds() / _config.TimeWindowSeconds;
            var rolling = Base64Url(Hmac(Encoding.UTF8.GetBytes(record.SessionSecret!), $"{window}:{record.SessionId}"));
            var signingInput = string.Join('\n', _config.SchemeLabel, requestMethod, target.AbsolutePath, "", bodyHash,
                timestamp, nonce, record.SessionId, _config.AppVersion, _config.Platform);
            var signature = Base64Url(Hmac(Encoding.UTF8.GetBytes(rolling), signingInput));
            var signedHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [_config.HeaderPrefix + "Session"] = record.SessionId!,
                [_config.HeaderPrefix + "Timestamp"] = timestamp,
                [_config.HeaderPrefix + "Nonce"] = nonce,
                [_config.HeaderPrefix + "Body-SHA256"] = bodyHash,
                [_config.HeaderPrefix + "Signature"] = signature,
                [_config.HeaderPrefix + "App-Version"] = _config.AppVersion,
                [_config.HeaderPrefix + "Platform"] = _config.Platform
            };
            if (extraHeaders != null)
                foreach (var (key, value) in extraHeaders) signedHeaders[key] = value;
            var response = Send(requestMethod, target, payload, signedHeaders);
            return new(target, response);
    }

    private SessionRecord TryRefresh(SessionRecord record)
    {
        try
        {
            var body = JsonSerializer.Serialize(new { install_id = record.InstallId });
            var result = SendSigned(record, "POST", _config.Endpoints.Refresh!, body, null);
            if (!result.Response.IsSuccessStatusCode) return record;
            var refreshed = JsonSerializer.Deserialize<SessionExchange>(result.Response.Body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            var next = record with
            {
                SessionId = refreshed?.SessionId ?? record.SessionId,
                SessionSecret = refreshed?.SessionSecret ?? record.SessionSecret,
                ExpiresAt = refreshed?.ExpiresAt ?? record.ExpiresAt
            };
            Save(next);
            return next;
        }
        catch { return record; }
    }

    private string BuildChallengeUrl(string challengeId)
    {
        var callback = new UriBuilder(_config.CallbackUrl);
        callback.Query = $"cb_version=v2grant&state={Uri.EscapeDataString(_extensionId)}";
        var challenge = new UriBuilder(Resolve(_config.Endpoints.Challenge));
        challenge.Query = $"id={Uri.EscapeDataString(challengeId)}&cb={Uri.EscapeDataString(callback.Uri.AbsoluteUri)}";
        return challenge.Uri.AbsoluteUri;
    }

    private object VerificationRequired(string authUrl) => new
    {
        ok = false, needsVerification = true, error = "VERIFY_REQUIRED", open_auth_url = authUrl, auth_url = authUrl
    };

    private ResponseData SendUnsigned(HttpMethod method, Uri target, string? body) =>
        Send(method.Method, target, body ?? string.Empty, new Dictionary<string, string>());

    private ResponseData Send(string method, Uri target, string body, IReadOnlyDictionary<string, string> headers)
    {
        EnsureAllowed(target);
        using var client = _clients.CreateClient("ExtensionSdkV1");
        using var request = new HttpRequestMessage(new HttpMethod(method), target);
        if (body.Length > 0) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.TryAddWithoutValidation("User-Agent", $"SpotiFLAC-Mobile/{_config.AppVersion}");
        foreach (var (key, value) in headers)
        {
            if (key.Equals("Host", StringComparison.OrdinalIgnoreCase) || key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                key.Contains('\r') || key.Contains('\n') || value.Contains('\r') || value.Contains('\n')) continue;
            if (key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) && request.Content != null)
                request.Content.Headers.TryAddWithoutValidation(key, value);
            else request.Headers.TryAddWithoutValidation(key, value);
        }
        using var response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead)
            .GetAwaiter().GetResult();
        if (response.RequestMessage?.RequestUri is not { } finalUri) throw new HttpRequestException("Signed session response URL is unavailable.");
        EnsureAllowed(finalUri);
        using var stream = response.Content.ReadAsStream();
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            if (output.Length + read > MaximumResponseBytes) throw new InvalidDataException("Signed session response exceeds 4 MiB.");
            output.Write(buffer, 0, read);
        }
        var responseHeaders = response.Headers.Concat(response.Content.Headers)
            .ToDictionary(item => item.Key, item => (object)string.Join(", ", item.Value), StringComparer.OrdinalIgnoreCase);
        var retry = response.Headers.RetryAfter?.Delta is { } delta ? Math.Max(0, (int)delta.TotalSeconds) : 0;
        return new(response.StatusCode, Encoding.UTF8.GetString(output.ToArray()), responseHeaders, retry);
    }

    private Uri Resolve(string endpoint)
    {
        if (endpoint.Contains("://", StringComparison.Ordinal) && Uri.TryCreate(endpoint, UriKind.Absolute, out var absolute))
        { EnsureAllowed(absolute); return absolute; }
        var result = new Uri(new Uri(_config.BaseUrl.AbsoluteUri.TrimEnd('/') + "/"), endpoint.TrimStart('/'));
        EnsureAllowed(result);
        return result;
    }

    private void EnsureAllowed(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps) throw new UnauthorizedAccessException("Signed session requests require HTTPS.");
        var origin = uri.GetLeftPart(UriPartial.Authority) + "/";
        if (_allowedOrigins.Contains(origin)) return;
        foreach (var permission in _allowedOrigins)
        {
            if (!permission.StartsWith("https://*.", StringComparison.OrdinalIgnoreCase)) continue;
            var suffix = permission["https://*".Length..].TrimEnd('/');
            if (uri.Host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && uri.Host.Length > suffix.Length) return;
        }
        throw new UnauthorizedAccessException("Signed session origin is not approved.");
    }

    private SessionRecord Load()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        if (File.Exists(_statePath))
        {
            try
            {
                var json = _protector.Unprotect(File.ReadAllText(_statePath));
                var loaded = JsonSerializer.Deserialize<SessionRecord>(json);
                if (loaded != null) return loaded;
            }
            catch { }
        }
        var record = new SessionRecord(Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(), null, null, null);
        Save(record);
        return record;
    }

    private void Save(SessionRecord record) =>
        File.WriteAllText(_statePath, _protector.Protect(JsonSerializer.Serialize(record)));

    private static bool HasUsableSession(SessionRecord record) =>
        !string.IsNullOrWhiteSpace(record.SessionId) && !string.IsNullOrWhiteSpace(record.SessionSecret) &&
        (!TryExpiry(record, out var expiry) || expiry > DateTimeOffset.UtcNow);

    private static bool TryExpiry(SessionRecord record, out DateTimeOffset expiry) =>
        DateTimeOffset.TryParse(record.ExpiresAt, out expiry);

    private static byte[] Hmac(byte[] key, string value) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(value));
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record SessionRecord(string InstallId, string? SessionId, string? SessionSecret, string? ExpiresAt);
    private sealed record ResponseData(HttpStatusCode StatusCode, string Body, IReadOnlyDictionary<string, object> Headers, int RetryAfterSeconds)
    {
        public bool IsSuccessStatusCode => (int)StatusCode is >= 200 and < 300;
    }
    private sealed record SignedResponse(Uri Target, ResponseData Response)
    {
        public HttpStatusCode StatusCode => Response.StatusCode;
        public object ToHostResponse() => new
        {
            statusCode = (int)Response.StatusCode,
            status = (int)Response.StatusCode,
            ok = Response.IsSuccessStatusCode,
            url = Target.AbsoluteUri,
            body = Response.Body,
            headers = Response.Headers,
            retryAfterSeconds = Response.RetryAfterSeconds
        };
    }
    private sealed class SessionExchange
    {
        [JsonPropertyName("session_id")]
        public string? SessionId { get; set; }
        [JsonPropertyName("session_secret")]
        public string? SessionSecret { get; set; }
        [JsonPropertyName("expires_at")]
        public string? ExpiresAt { get; set; }
        [JsonPropertyName("challenge_id")]
        public string? ChallengeId { get; set; }
        [JsonPropertyName("challenge_url")]
        public string? ChallengeUrl { get; set; }
        [JsonPropertyName("auth_url")]
        public string? AuthUrl { get; set; }
    }
}
