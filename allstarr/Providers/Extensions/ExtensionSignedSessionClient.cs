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

public sealed record ExtensionSessionView(
    string State,
    string? ExpiresAt,
    string? VerificationUrl,
    string? ReasonCode)
{
    public Guid? AccountId { get; init; }
    public bool RenewsAutomatically { get; init; }
    public bool Restored { get; init; }
    public string? LastRenewalFailure { get; init; }
}

public static class ExtensionSessionStates
{
    public const string SignedOut = "signed_out";
    public const string VerificationPending = "verification_pending";
    public const string SignedIn = "signed_in";
    public const string Expired = "expired";
}

internal sealed class ExtensionSignedSessionClient
{
    private const int MaximumResponseBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromHours(1);
    private string? _restoreAttemptedFor;
    private static readonly TimeSpan AdministratorRequestLimit = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ScriptRequestLimit = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan GateWait = TimeSpan.FromSeconds(35);
    private readonly ExtensionSignedSessionConfig _config;
    private readonly IHttpClientFactory _clients;
    private readonly IDataProtector _protector;
    private readonly IReadOnlySet<string> _allowedOrigins;
    private readonly string _statePath;
    private readonly string _extensionId;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _pendingAuthUrl;
    private string? _lastRenewalFailure;

    public ExtensionSignedSessionClient(
        ExtensionSignedSessionConfig config,
        IHttpClientFactory clients,
        IDataProtector protector,
        IReadOnlySet<string> allowedOrigins,
        string runtimeStateDirectory,
        string extensionId,
        string? legacyStateDirectory = null)
    {
        _config = config;
        _clients = clients;
        _protector = protector;
        _allowedOrigins = allowedOrigins;
        _extensionId = extensionId;
        var scope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{config.Namespace}\n{config.BaseUrl}\n{config.AppVersion}\n{config.Platform}"))).ToLowerInvariant()[..16];
        var fileName = $"signed-session-{scope}.protected";
        _statePath = Path.Combine(runtimeStateDirectory, fileName);
        if (legacyStateDirectory != null) AdoptLegacyState(legacyStateDirectory, fileName);
    }

    private void AdoptLegacyState(string legacyStateDirectory, string fileName)
    {
        if (!Directory.Exists(legacyStateDirectory)) return;
        foreach (var legacyPath in Directory.EnumerateFiles(legacyStateDirectory, "signed-session-*.protected"))
        {
            var target = Path.Combine(Path.GetDirectoryName(_statePath)!, Path.GetFileName(legacyPath));
            if (string.Equals(Path.GetFullPath(legacyPath), Path.GetFullPath(target), StringComparison.Ordinal))
                continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target) || File.Exists(_statePath) && !Path.GetFileName(legacyPath).Equals(fileName, StringComparison.Ordinal))
                File.Delete(legacyPath);
            else
                File.Move(legacyPath, target);
        }
    }

    public ExtensionSignedSessionStatus Status() => RunScript(_ =>
    {
        var record = Load();
        var authenticated = HasUsableSession(record);
        return Task.FromResult(new ExtensionSignedSessionStatus(authenticated, record.ExpiresAt, record.InstallId,
            authenticated ? record.SessionId : null, _config.AppVersion, _config.Platform, _pendingAuthUrl));
    });

    public Task<ExtensionSessionView> ViewAsync(CancellationToken cancellationToken) =>
        RunAdministratorAsync(_ => Task.FromResult(ViewOf(Load(), null)), cancellationToken);

    public Task<ExtensionSessionView?> RefreshDueAsync(CancellationToken cancellationToken) =>
        RunAdministratorAsync<ExtensionSessionView?>(async limit =>
        {
            if (string.IsNullOrWhiteSpace(_config.Endpoints.Refresh)) return null;
            var record = Load();
            if (ExpiredSignIn(record)) return await TryRestoreAsync(record, limit, cancellationToken);
            if (!RefreshDue(record)) return null;
            record = await TryRefreshAsync(record, limit);
            cancellationToken.ThrowIfCancellationRequested();
            return ViewOf(record, null);
        }, cancellationToken);

    private bool ExpiredSignIn(SessionRecord record) =>
        !string.IsNullOrWhiteSpace(record.SessionId) && !string.IsNullOrWhiteSpace(record.SessionSecret) &&
        _pendingAuthUrl == null && TryExpiry(record, out var expiry) && expiry <= DateTimeOffset.UtcNow;

    // Services can cap a sign-in's total lifetime; once it lapses, ask once whether this install may resume
    // without verification, as the mobile client does, instead of waiting for playback to fail.
    private async Task<ExtensionSessionView?> TryRestoreAsync(SessionRecord record, CancellationToken limit,
        CancellationToken cancellationToken)
    {
        if (_restoreAttemptedFor == record.ExpiresAt) return null;
        _restoreAttemptedFor = record.ExpiresAt;
        var result = await BeginVerificationAsync(limit, cancellationToken, allowChallenge: false);
        cancellationToken.ThrowIfCancellationRequested();
        var current = Load();
        _lastRenewalFailure = HasUsableSession(current) ? null : result.ReasonCode ?? "verification_required";
        return ViewOf(current, null) with { Restored = HasUsableSession(current) };
    }

    // Adapted from SpotiFLAC-Mobile (MIT), signed_session/ss_protocol.rs refresh_due.
    private bool RefreshDue(SessionRecord record) =>
        !string.IsNullOrWhiteSpace(_config.Endpoints.Refresh) && HasUsableSession(record) &&
        TryExpiry(record, out var expiry) && expiry > DateTimeOffset.UtcNow &&
        expiry - DateTimeOffset.UtcNow <= RefreshSkew;

    public object Clear() => RunScript(_ =>
    {
        ClearRecord();
        return Task.FromResult<object>(new { success = true });
    });

    public Task<ExtensionSessionView> ClearViewAsync(CancellationToken cancellationToken) =>
        RunAdministratorAsync(_ =>
        {
            ClearRecord();
            return Task.FromResult(ViewOf(Load(), null));
        }, cancellationToken);

    public object CompleteGrant(string? grant) => RunScript<object>(async limit =>
    {
        var result = await ExchangeGrantAsync(grant, limit);
        return result.ReasonCode == null ? new { success = true } : new { success = false, error = result.Detail };
    });

    public Task<ExtensionSessionView> CompleteGrantViewAsync(string? grant, CancellationToken cancellationToken) =>
        RunAdministratorAsync(async limit =>
        {
            var result = await ExchangeGrantAsync(grant, limit, cancellationToken);
            return ViewOf(Load(), result.ReasonCode);
        }, cancellationToken);

    public object StartVerification() =>
        RunScript(async limit => HostResponse(await BeginVerificationAsync(limit)));

    public Task<ExtensionSessionView> StartVerificationViewAsync(CancellationToken cancellationToken) =>
        RunAdministratorAsync(async limit =>
        {
            var result = await BeginVerificationAsync(limit, cancellationToken);
            return ViewOf(Load(), result.ReasonCode);
        }, cancellationToken);

    private T RunScript<T>(Func<CancellationToken, Task<T>> operation)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ExtensionInvocationBudget.Current?.Token ?? CancellationToken.None);
        limit.CancelAfter(ScriptRequestLimit);
        if (!_gate.Wait(GateWait)) throw new TimeoutException("The extension session is busy.");
        try { return operation(limit.Token).GetAwaiter().GetResult(); }
        finally { _gate.Release(); }
    }

    private async Task<T> RunAdministratorAsync<T>(
        Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(AdministratorRequestLimit);
        if (!await _gate.WaitAsync(GateWait, cancellationToken))
            throw new TimeoutException("The extension session is busy.");
        try { return await operation(limit.Token); }
        finally { _gate.Release(); }
    }

    private void ClearRecord()
    {
        var record = Load();
        Save(record with { SessionId = null, SessionSecret = null, ExpiresAt = null });
        _pendingAuthUrl = null;
        _lastRenewalFailure = null;
    }

    private ExtensionSessionView ViewOf(SessionRecord record, string? reasonCode)
    {
        if (HasUsableSession(record))
            return new(ExtensionSessionStates.SignedIn, record.ExpiresAt, null, reasonCode)
            {
                RenewsAutomatically = !string.IsNullOrWhiteSpace(_config.Endpoints.Refresh) && TryExpiry(record, out _),
                LastRenewalFailure = _lastRenewalFailure
            };
        if (_pendingAuthUrl != null)
            return new(ExtensionSessionStates.VerificationPending, null, _pendingAuthUrl, reasonCode);
        var expired = !string.IsNullOrWhiteSpace(record.SessionId) && !string.IsNullOrWhiteSpace(record.SessionSecret);
        return expired
            ? new(ExtensionSessionStates.Expired, record.ExpiresAt, null, reasonCode) { LastRenewalFailure = _lastRenewalFailure }
            : new(ExtensionSessionStates.SignedOut, null, null, reasonCode);
    }

    private async Task<SessionResult> ExchangeGrantAsync(string? grant, CancellationToken cancellationToken,
        CancellationToken callerToken = default)
    {
        var normalized = NormalizeGrant(grant, out var grantError, out var grantReason);
        if (normalized == null) return SessionResult.Failed(grantReason, grantError);
        try
        {
            var record = Load();
            var payload = JsonSerializer.Serialize(new
            {
                grant = normalized,
                install_id = record.InstallId,
                app_version = _config.AppVersion,
                platform = _config.Platform
            });
            var response = await SendUnsignedAsync(HttpMethod.Post, Resolve(_config.Endpoints.Exchange), payload, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return SessionResult.Failed(ResponseReason(response.StatusCode, "grant_rejected"),
                    $"session exchange failed: HTTP {(int)response.StatusCode}");
            var exchanged = JsonSerializer.Deserialize<SessionExchange>(response.Body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (string.IsNullOrWhiteSpace(exchanged?.SessionId) ||
                string.IsNullOrWhiteSpace(exchanged.SessionSecret) ||
                string.IsNullOrWhiteSpace(exchanged.ExpiresAt))
                return SessionResult.Failed("unexpected_response", "session exchange response missing session fields");
            Save(record with
            {
                SessionId = exchanged.SessionId,
                SessionSecret = exchanged.SessionSecret,
                ExpiresAt = exchanged.ExpiresAt
            });
            _pendingAuthUrl = null;
            _lastRenewalFailure = null;
            return SessionResult.Succeeded;
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return SessionResult.Failed(ExceptionReason(exception), exception.Message);
        }
    }

    private string? NormalizeGrant(string? value, out string error, out string reasonCode)
    {
        error = "A session grant is required.";
        reasonCode = "grant_required";
        var candidate = value?.Trim();
        if (string.IsNullOrWhiteSpace(candidate)) return null;

        if (!candidate.Contains("://", StringComparison.Ordinal)) return candidate;
        reasonCode = "grant_invalid";
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var callback))
        {
            error = "The session callback URL is invalid.";
            return null;
        }

        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in callback.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            var key = Uri.UnescapeDataString(pair[0].Replace('+', ' '));
            if (parameters.ContainsKey(key)) continue;
            parameters[key] = pair.Length > 1
                ? Uri.UnescapeDataString(pair[1].Replace('+', ' '))
                : string.Empty;
        }
        if (parameters.TryGetValue("state", out var state) &&
            !string.IsNullOrWhiteSpace(state) &&
            !string.Equals(state, _extensionId, StringComparison.OrdinalIgnoreCase))
        {
            error = "This session callback belongs to a different extension.";
            reasonCode = "grant_wrong_extension";
            return null;
        }
        if (!parameters.TryGetValue("grant", out var parsedGrant) || string.IsNullOrWhiteSpace(parsedGrant))
        {
            error = "The session callback URL does not contain a grant.";
            return null;
        }

        return parsedGrant.Trim();
    }

    private async Task<SessionResult> BeginVerificationAsync(CancellationToken cancellationToken,
        CancellationToken callerToken = default, bool allowChallenge = true)
    {
        try
        {
            var record = Load();
            var bootstrap = Resolve(_config.Endpoints.Bootstrap);
            var builder = new UriBuilder(bootstrap);
            var separator = string.IsNullOrEmpty(builder.Query) ? "" : builder.Query.TrimStart('?') + "&";
            builder.Query = $"{separator}app_version={Uri.EscapeDataString(_config.AppVersion)}&install_id={Uri.EscapeDataString(record.InstallId)}";
            var response = await SendUnsignedAsync(HttpMethod.Get, builder.Uri, null, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return SessionResult.Failed(ResponseReason(response.StatusCode, "session_rejected"),
                    $"session bootstrap failed: HTTP {(int)response.StatusCode}");
            var boot = JsonSerializer.Deserialize<SessionExchange>(response.Body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (!string.IsNullOrWhiteSpace(boot?.SessionId) && !string.IsNullOrWhiteSpace(boot.SessionSecret) &&
                !string.IsNullOrWhiteSpace(boot.ExpiresAt))
            {
                Save(record with { SessionId = boot.SessionId, SessionSecret = boot.SessionSecret, ExpiresAt = boot.ExpiresAt });
                _pendingAuthUrl = null;
                return SessionResult.Authenticated;
            }
            if (!allowChallenge)
                return SessionResult.Failed("verification_required", "the service requires signing in again");
            var authUrl = boot?.AuthUrl ?? boot?.ChallengeUrl;
            if (string.IsNullOrWhiteSpace(authUrl) && !string.IsNullOrWhiteSpace(boot?.ChallengeId))
                authUrl = BuildChallengeUrl(boot.ChallengeId);
            if (string.IsNullOrWhiteSpace(authUrl))
                return SessionResult.Failed("unexpected_response", "session bootstrap did not return a challenge");
            if (!Uri.TryCreate(authUrl, UriKind.Absolute, out var verification) || verification.Scheme != Uri.UriSchemeHttps ||
                !string.IsNullOrEmpty(verification.UserInfo))
                return SessionResult.Failed("unexpected_response", "session bootstrap returned an unsafe verification link");
            _pendingAuthUrl = verification.AbsoluteUri;
            return SessionResult.Pending(_pendingAuthUrl);
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return SessionResult.Failed(ExceptionReason(exception), exception.Message);
        }
    }

    private object HostResponse(SessionResult result) =>
        result.VerificationUrl != null ? VerificationRequired(result.VerificationUrl)
        : result.ReasonCode == null ? new { success = true, authenticated = true }
        : new { success = false, error = result.Detail };

    private static string ResponseReason(HttpStatusCode status, string rejected) =>
        (int)status >= 500 || status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
            ? "provider_unavailable"
            : rejected;

    private static string ExceptionReason(Exception exception) => exception switch
    {
        UnauthorizedAccessException => "origin_not_approved",
        HttpRequestException or OperationCanceledException or IOException => "provider_unavailable",
        _ => "unexpected_response"
    };

    private sealed record SessionResult(string? VerificationUrl, string? ReasonCode, string? Detail)
    {
        public static readonly SessionResult Succeeded = new(null, null, null);
        public static readonly SessionResult Authenticated = Succeeded;
        public static SessionResult Pending(string url) => new(url, null, null);
        public static SessionResult Failed(string reasonCode, string detail) => new(null, reasonCode, detail);
    }

    // Adapted from SpotiFLAC-Mobile (MIT), rust_backend/crates/extensions/src/signed_session/fetch.rs.
    public object SignedFetch(string method, string path, string? body, object? headers)
    {
        var payload = body ?? string.Empty;
        var extraHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var headerValues = headers switch
        {
            Jint.Native.Object.ObjectInstance value => value.GetOwnProperties().Select(entry =>
                new KeyValuePair<string, string>(entry.Key.ToString(), value.Get(entry.Key).ToString())),
            IDictionary<string, object?> values => values.Select(entry =>
                new KeyValuePair<string, string>(entry.Key, entry.Value?.ToString() ?? string.Empty)),
            IDictionary<string, string> values => values,
            _ => []
        };
        foreach (var (key, value) in headerValues) extraHeaders[key] = value;
        try
        {
            return RunScript(async limit =>
            {
                var record = Load();
                if (!HasUsableSession(record)) return HostResponse(await BeginVerificationAsync(limit));
                if (RefreshDue(record))
                    record = await TryRefreshAsync(record, limit);
                for (var retry = 0; ; retry++)
                {
                    var response = await SendSignedAsync(record, method, path, payload, extraHeaders, limit);
                    var contract = ErrorContract.Parse(response.Response.Body);
                    if (contract.ProviderRetry(response.StatusCode) && retry < 2)
                    {
                        var delay = response.Response.RetryAfterSeconds > 0 ? response.Response.RetryAfterSeconds :
                            contract.RetryAfterSeconds > 0 ? contract.RetryAfterSeconds : 1;
                        await Task.Delay(TimeSpan.FromSeconds(Math.Min(10, delay)), limit);
                        continue;
                    }
                    var reset = contract.GatewayAction(response.StatusCode).Length > 0 ||
                        contract.Origin.Length == 0 && response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.PreconditionRequired;
                    if (reset)
                    {
                        Save(record with { SessionId = null, SessionSecret = null, ExpiresAt = null });
                        return HostResponse(await BeginVerificationAsync(limit));
                    }
                    return response.ToHostResponse();
                }
            });
        }
        catch (TimeoutException)
        {
            return new { ok = false, statusCode = 0, status = 0, error = "SESSION_BUSY" };
        }
    }

    private async Task<SignedResponse> SendSignedAsync(SessionRecord record, string method, string path, string payload,
        IReadOnlyDictionary<string, string>? extraHeaders, CancellationToken cancellationToken)
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
        var response = await SendAsync(requestMethod, target, payload, signedHeaders, cancellationToken);
        return new(target, response);
    }

    private async Task<SessionRecord> TryRefreshAsync(SessionRecord record, CancellationToken cancellationToken)
    {
        try
        {
            var body = JsonSerializer.Serialize(new { install_id = record.InstallId });
            var result = await SendSignedAsync(record, "POST", _config.Endpoints.Refresh!, body, null, cancellationToken);
            if (!result.Response.IsSuccessStatusCode)
            {
                _lastRenewalFailure = ResponseReason(result.StatusCode, "renewal_rejected");
                return record;
            }
            var refreshed = JsonSerializer.Deserialize<SessionExchange>(result.Response.Body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            var next = record with
            {
                SessionId = refreshed?.SessionId ?? record.SessionId,
                SessionSecret = refreshed?.SessionSecret ?? record.SessionSecret,
                ExpiresAt = refreshed?.ExpiresAt ?? record.ExpiresAt
            };
            if (string.IsNullOrWhiteSpace(refreshed?.ExpiresAt) || !TryExpiry(next, out var expiry) ||
                expiry <= DateTimeOffset.UtcNow || !HasUsableSession(next))
            {
                _lastRenewalFailure = "unexpected_response";
                return record;
            }
            Save(next);
            // A renewal that cannot move the expiry means the service has reached its sign-in lifetime.
            _lastRenewalFailure = TryExpiry(record, out var previous) && expiry <= previous ? "renewal_capped" : null;
            return next;
        }
        catch (Exception exception)
        {
            _lastRenewalFailure = ExceptionReason(exception);
            return record;
        }
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
        ok = false,
        needsVerification = true,
        error = "VERIFY_REQUIRED",
        open_auth_url = authUrl,
        auth_url = authUrl
    };

    private Task<ResponseData> SendUnsignedAsync(HttpMethod method, Uri target, string? body, CancellationToken cancellationToken) =>
        SendAsync(method.Method, target, body ?? string.Empty, new Dictionary<string, string>(), cancellationToken);

    private async Task<ResponseData> SendAsync(string method, Uri target, string body, IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellationToken)
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
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.RequestMessage?.RequestUri is not { } finalUri) throw new HttpRequestException("Signed session response URL is unavailable.");
        EnsureAllowed(finalUri);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (output.Length + read > MaximumResponseBytes) throw new InvalidDataException("Signed session response exceeds 4 MiB.");
            output.Write(buffer, 0, read);
        }
        var responseHeaders = response.Headers.Concat(response.Content.Headers)
            .ToDictionary(item => item.Key, item => (object)string.Join(", ", item.Value), StringComparer.OrdinalIgnoreCase);
        var retry = response.Headers.RetryAfter?.Delta is { } delta ? Math.Max(0, (int)delta.TotalSeconds) :
            response.Headers.RetryAfter?.Date is { } date ? Math.Max(0, (int)(date - DateTimeOffset.UtcNow).TotalSeconds) : 0;
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

    private void Save(SessionRecord record)
    {
        var temporary = _statePath + ".tmp";
        try
        {
            File.WriteAllText(temporary, _protector.Protect(JsonSerializer.Serialize(record)));
            File.Move(temporary, _statePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

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
        public object ToHostResponse()
        {
            var contract = ErrorContract.Parse(Response.Body);
            var result = new Dictionary<string, object>
            {
                ["statusCode"] = (int)Response.StatusCode,
                ["status"] = (int)Response.StatusCode,
                ["ok"] = Response.IsSuccessStatusCode,
                ["url"] = Target.AbsoluteUri,
                ["body"] = Response.Body,
                ["headers"] = Response.Headers,
                ["retryAfterSeconds"] = Response.RetryAfterSeconds > 0 ? Response.RetryAfterSeconds : contract.RetryAfterSeconds
            };
            if (contract.Code.Length > 0 || contract.Origin.Length > 0 || contract.Action.Length > 0)
            {
                result["error"] = contract.Error;
                result["code"] = contract.Code;
                result["origin"] = contract.Origin;
                result["action"] = contract.Action;
                result["retryable"] = contract.Retryable;
                result["retryMode"] = contract.RetryMode;
            }
            return result;
        }
    }

    // Adapted from SpotiFLAC-Mobile (MIT), rust_backend/crates/extensions/src/signed_session/protocol.rs.
    private sealed record ErrorContract(string Error = "", string Code = "", string Origin = "", string Action = "",
        bool Retryable = false, string RetryMode = "", int RetryAfterSeconds = 0)
    {
        public string GatewayAction(HttpStatusCode status) => (status, Origin, Code, Action) switch
        {
            (HttpStatusCode.Unauthorized, "gateway", "SESSION_INVALID", "bootstrap_session") => "bootstrap_session",
            (HttpStatusCode.PreconditionRequired, "gateway", "VERIFY_REQUIRED", "verify") => "verify",
            _ => ""
        };

        public bool ProviderRetry(HttpStatusCode status) => status == HttpStatusCode.ServiceUnavailable &&
            Origin == "provider" && Code == "PROVIDER_UNAVAILABLE" && Retryable && RetryMode == "same_operation";

        public static ErrorContract Parse(string body)
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return new();
                string Text(string key) => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString()!.Trim() : "";
                return new(Text("error"), Text("code").ToUpperInvariant(), Text("origin").ToLowerInvariant(),
                    Text("action").ToLowerInvariant(), root.TryGetProperty("retryable", out var retryable) && retryable.ValueKind == JsonValueKind.True,
                    Text("retry_mode").ToLowerInvariant(), root.TryGetProperty("retry_after_seconds", out var seconds) &&
                    seconds.ValueKind == JsonValueKind.Number && seconds.TryGetInt32(out var count) ? Math.Max(0, count) : 0);
            }
            catch (JsonException) { return new(); }
        }
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
