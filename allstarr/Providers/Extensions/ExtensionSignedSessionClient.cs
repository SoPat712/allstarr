using System.Globalization;
using System.IO.Compression;
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
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _signedDownloadRequestLimit;
    private readonly TimeSpan? _networkRequestTimeout;
    private readonly ExtensionSignedSessionConfig _config;
    private readonly IHttpClientFactory _clients;
    private readonly IDataProtector _protector;
    private readonly IReadOnlySet<string> _allowedOrigins;
    private readonly string _statePath;
    private readonly string _extensionId;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _pendingAuthUrl;
    private string? _lastRenewalFailure;
    private readonly Action<string, string, string>? _logSink;

    public ExtensionSignedSessionClient(
        ExtensionSignedSessionConfig config,
        IHttpClientFactory clients,
        IDataProtector protector,
        IReadOnlySet<string> allowedOrigins,
        string runtimeStateDirectory,
        string extensionId,
        string? legacyStateDirectory = null,
        TimeProvider? timeProvider = null,
        ExtensionExecutionOptions? executionOptions = null,
        Action<string, string, string>? logSink = null,
        TimeSpan? networkRequestTimeout = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logSink = logSink;
        _networkRequestTimeout = networkRequestTimeout;
        _signedDownloadRequestLimit = (executionOptions ?? new()).GetSignedDownloadRequestTimeout();
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
        _pendingAuthUrl == null && TryExpiry(record, out var expiry) && expiry <= _timeProvider.GetUtcNow();

    // Services can cap a sign-in's total lifetime; once it lapses, ask once whether this install may resume
    // without verification, as the mobile client does, instead of waiting for playback to fail.
    private async Task<ExtensionSessionView?> TryRestoreAsync(SessionRecord record, CancellationToken limit,
        CancellationToken cancellationToken)
    {
        if (_restoreAttemptedFor == record.ExpiresAt) return null;
        _restoreAttemptedFor = record.ExpiresAt;
        var result = await BeginVerificationAsync(limit, cancellationToken, allowChallenge: false, call: "restore");
        if (result.ReasonCode is not (null or "verification_required")) _restoreAttemptedFor = null;
        cancellationToken.ThrowIfCancellationRequested();
        var current = Load();
        _lastRenewalFailure = HasUsableSession(current) ? null : result.ReasonCode ?? "verification_required";
        return ViewOf(current, null) with { Restored = HasUsableSession(current) };
    }

    // Adapted from SpotiFLAC-Mobile (MIT), signed_session/ss_protocol.rs refresh_due.
    private bool RefreshDue(SessionRecord record) =>
        !string.IsNullOrWhiteSpace(_config.Endpoints.Refresh) && HasUsableSession(record) &&
        TryExpiry(record, out var expiry) && expiry > _timeProvider.GetUtcNow() &&
        expiry - _timeProvider.GetUtcNow() <= RefreshSkew;

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

    private T RunScript<T>(Func<CancellationToken, Task<T>> operation, bool retryRequests = false)
    {
        WaitForGateAsync(CancellationToken.None).GetAwaiter().GetResult();
        try
        {
            var budget = ExtensionInvocationBudget.Current;
            if (retryRequests && budget is { IsDownload: true })
                return operation(budget.Token).GetAwaiter().GetResult();
            var remaining = budget?.RemainingMilliseconds;
            var requestLimit = budget is { IsDownload: true } && remaining > ScriptRequestLimit.TotalMilliseconds
                ? _signedDownloadRequestLimit : ScriptRequestLimit;
            if (remaining.HasValue)
                requestLimit = TimeSpan.FromMilliseconds(Math.Min(remaining.Value, requestLimit.TotalMilliseconds));
            using var timeout = new CancellationTokenSource(requestLimit, _timeProvider);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(
                budget?.Token ?? CancellationToken.None, timeout.Token);
            return operation(limit.Token).GetAwaiter().GetResult();
        }
        finally { _gate.Release(); }
    }

    private async Task<T> RunAdministratorAsync<T>(
        Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(AdministratorRequestLimit, _timeProvider);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        await WaitForGateAsync(cancellationToken);
        try { return await operation(limit.Token); }
        finally { _gate.Release(); }
    }

    private async Task WaitForGateAsync(CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(GateWait, _timeProvider);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try { await _gate.WaitAsync(limit.Token); }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The extension session is busy.");
        }
    }

    private void ClearRecord()
    {
        var record = Load();
        ResetRecord(record, "user_clear");
        _pendingAuthUrl = null;
        _lastRenewalFailure = null;
    }

    private SessionRecord ResetRecord(SessionRecord record, string call, HttpStatusCode? status = null, ErrorContract? contract = null)
    {
        LogSession("session.reset", record, call, call == "user_clear" ? "user_requested" : "session_invalid", status, contract);
        var cleared = record with { SessionId = null, SessionSecret = null, ExpiresAt = null };
        Save(cleared);
        _pendingAuthUrl = null;
        _lastRenewalFailure = "verification_required";
        return cleared;
    }

    private void RetainSession(SessionRecord record, string call, string reason, HttpStatusCode? status = null,
        ErrorContract? contract = null)
    {
        _lastRenewalFailure = reason;
        if (call == "restore") _restoreAttemptedFor = null;
        LogSession("session.retained", record, call, reason, status, contract);
    }

    private void LogSession(string code, SessionRecord record, string call, string reason, HttpStatusCode? status = null,
        ErrorContract? contract = null)
    {
        // Only recognized protocol labels enter durable logs; response text and request URLs may contain private data.
        var origin = contract?.Origin is "gateway" or "provider" ? contract.Origin : string.IsNullOrEmpty(contract?.Origin) ? "none" : "unrecognized";
        var errorCode = contract?.Code is "SESSION_INVALID" or "VERIFY_REQUIRED" or "PROVIDER_UNAVAILABLE" ? contract.Code : string.IsNullOrEmpty(contract?.Code) ? "none" : "unrecognized";
        var action = contract?.Action is "bootstrap_session" or "verify" ? contract.Action : string.IsNullOrEmpty(contract?.Action) ? "none" : "unrecognized";
        var timeLeft = TryExpiry(record, out var expiry) ? (expiry > _timeProvider.GetUtcNow() ? "true" : "false") : "unknown";
        _logSink?.Invoke("warning", code,
            $"Session outcome: call={call}; reason={reason}; status={(status.HasValue ? ((int)status.Value).ToString() : "none")}; origin={origin}; code={errorCode}; action={action}; time_left={timeLeft}.");
    }

    private ExtensionSessionView ViewOf(SessionRecord record, string? reasonCode)
    {
        reasonCode ??= _lastRenewalFailure is "verification_required" or "provider_unavailable" or "network_not_ready"
            ? _lastRenewalFailure : null;
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
            _restoreAttemptedFor = null;
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
        CancellationToken callerToken = default, bool allowChallenge = true, string call = "verification")
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
            {
                var contract = ErrorContract.Parse(response.Body);
                if (contract.GatewayAction(response.StatusCode).Length > 0)
                    ResetRecord(record, call, response.StatusCode, contract);
                else
                    RetainSession(record, call, ResponseReason(response.StatusCode,
                        !string.IsNullOrWhiteSpace(record.SessionId) && response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.PreconditionRequired
                            ? "verification_required" : "session_rejected"), response.StatusCode, contract);
                return SessionResult.Failed(_lastRenewalFailure!, $"session bootstrap failed: HTTP {(int)response.StatusCode}");
            }
            var boot = JsonSerializer.Deserialize<SessionExchange>(response.Body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (!string.IsNullOrWhiteSpace(boot?.SessionId) && !string.IsNullOrWhiteSpace(boot.SessionSecret) &&
                !string.IsNullOrWhiteSpace(boot.ExpiresAt))
            {
                Save(record with { SessionId = boot.SessionId, SessionSecret = boot.SessionSecret, ExpiresAt = boot.ExpiresAt });
                _pendingAuthUrl = null;
                _lastRenewalFailure = null;
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
            if (exception is InvalidOperationException) throw;
            var reason = ExceptionReason(exception);
            RetainSession(Load(), call, reason);
            return SessionResult.Failed(reason, "The session service is temporarily unavailable.");
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
        HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError } => "network_not_ready",
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
                if (!HasUsableSession(record))
                {
                    var bootstrap = await BeginVerificationAsync(limit, call: "signed_fetch");
                    record = Load();
                    if (!HasUsableSession(record)) return HostResponse(bootstrap);
                }
                if (RefreshDue(record))
                {
                    record = await TryRefreshAsync(record, limit);
                    if (!HasUsableSession(record))
                    {
                        var bootstrap = await BeginVerificationAsync(limit, call: "signed_fetch");
                        record = Load();
                        if (!HasUsableSession(record)) return HostResponse(bootstrap);
                    }
                }
                try
                {
                    for (var retry = 0; ; retry++)
                    {
                        var response = await SendSignedAsync(record, method, path, payload, extraHeaders, limit);
                        var contract = ErrorContract.Parse(response.Response.Body);
                        if (contract.ProviderRetry(response.StatusCode) && retry < 2)
                        {
                            await Task.Delay(response.Response.ProviderDelay(contract.RetryAfterSeconds, _timeProvider.GetUtcNow()),
                                _timeProvider, limit);
                            continue;
                        }
                        var reset = contract.GatewayAction(response.StatusCode).Length > 0;
                        if (reset)
                        {
                            ResetRecord(record, "signed_fetch", response.StatusCode, contract);
                            return HostResponse(await BeginVerificationAsync(limit, call: "signed_fetch"));
                        }
                        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.PreconditionRequired ||
                            (int)response.StatusCode >= 500 || response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests)
                            RetainSession(record, "signed_fetch", ResponseReason(response.StatusCode, "verification_required"), response.StatusCode, contract);
                        else if (response.Response.IsSuccessStatusCode) _lastRenewalFailure = null;
                        return response.ToHostResponse();
                    }
                }
                catch (OperationCanceledException exception)
                {
                    RetainSession(record, "signed_fetch", ExceptionReason(exception));
                    throw;
                }
                catch (Exception exception) when (exception is HttpRequestException or IOException)
                {
                    RetainSession(record, "signed_fetch", ExceptionReason(exception));
                    return new { ok = false, statusCode = 0, status = 0, error = "PROVIDER_UNAVAILABLE" };
                }
            }, retryRequests: true);
        }
        catch (OperationCanceledException) when (_networkRequestTimeout.HasValue &&
            ExtensionInvocationBudget.Current?.Token.IsCancellationRequested != true)
        {
            return new { ok = false, statusCode = 0, status = 0, error = "PROVIDER_UNAVAILABLE" };
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
        var now = _timeProvider.GetUtcNow();
        var timestamp = now.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        var bodyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        var window = now.ToUnixTimeSeconds() / _config.TimeWindowSeconds;
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
        var started = _timeProvider.GetTimestamp();
        var response = await SendAsync(requestMethod, target, payload, signedHeaders, cancellationToken);
        if (!response.IsSuccessStatusCode)
            LogResponse(requestMethod, target, response, _timeProvider.GetElapsedTime(started));
        return new(target, response);
    }

    private void LogResponse(string method, Uri target, ResponseData response, TimeSpan elapsed)
    {
        var contract = ErrorContract.Parse(response.Body);
        static string Label(string value, params string[] allowed) => string.IsNullOrEmpty(value) ? "none" :
            allowed.Contains(value, StringComparer.Ordinal) ? value : "unrecognized";
        var path = string.Join('/', target.AbsolutePath.Split('/').Select(segment => segment is
            "" or "v1" or "v2" or "tickets" or "dl" or "tid" or "tid2" or "bootstrap" or "challenge" or
            "session" or "exchange" or "refresh" ? segment : "{redacted}"));
        _logSink?.Invoke("warning", "session.response",
            $"Signed response: method={Label(method, "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS")}; path={path}; status={(int)response.StatusCode}; " +
            $"error={Label(contract.Error, "PROVIDER_UNAVAILABLE", "VERIFY_REQUIRED", "SESSION_INVALID", "REQUEST_AUTH_INVALID")}; " +
            $"code={Label(contract.Code, "PROVIDER_UNAVAILABLE", "VERIFY_REQUIRED", "SESSION_INVALID", "REQUEST_AUTH_INVALID", "PROVIDER_AUTH_REQUIRED", "TICKET_REQUIRED", "TICKET_INVALID", "TICKET_EXPIRED")}; " +
            $"origin={Label(contract.Origin, "provider", "gateway")}; action={Label(contract.Action, "bootstrap_session", "verify")}; " +
            $"retry_mode={Label(contract.RetryMode, "same_operation", "poll_existing", "new_ticket", "none")}; " +
            $"retry_after={Math.Max(response.RetryAfterSeconds, contract.RetryAfterSeconds)}; elapsed_ms={(long)elapsed.TotalMilliseconds}.");
    }

    private async Task<SessionRecord> TryRefreshAsync(SessionRecord record, CancellationToken cancellationToken)
    {
        try
        {
            var body = JsonSerializer.Serialize(new { install_id = record.InstallId });
            var result = await SendSignedAsync(record, "POST", _config.Endpoints.Refresh!, body, null, cancellationToken);
            if (!result.Response.IsSuccessStatusCode)
            {
                var contract = ErrorContract.Parse(result.Response.Body);
                if (contract.GatewayAction(result.StatusCode).Length > 0)
                    return ResetRecord(record, "refresh", result.StatusCode, contract);
                RetainSession(record, "refresh", ResponseReason(result.StatusCode,
                    result.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.PreconditionRequired ? "verification_required" : "renewal_rejected"),
                    result.StatusCode, contract);
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
                expiry <= _timeProvider.GetUtcNow() || !HasUsableSession(next))
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
            RetainSession(record, "refresh", ExceptionReason(exception));
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
        var budget = ExtensionInvocationBudget.Current;
        var requestLimit = budget is { IsDownload: true } ? _signedDownloadRequestLimit : ScriptRequestLimit;
        if (_networkRequestTimeout is { } networkLimit && networkLimit < requestLimit) requestLimit = networkLimit;
        if (budget != null) requestLimit = TimeSpan.FromMilliseconds(Math.Min(budget.RemainingMilliseconds, requestLimit.TotalMilliseconds));
        using var timeout = budget is { IsDownload: true } || _networkRequestTimeout.HasValue
            ? new CancellationTokenSource(requestLimit, _timeProvider) : null;
        using var limit = timeout == null ? null : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        cancellationToken = limit?.Token ?? cancellationToken;
        EnsureAllowed(target);
        using var client = _clients.CreateClient("ExtensionSdkV1");
        client.Timeout = Timeout.InfiniteTimeSpan;
        using var request = new HttpRequestMessage(new HttpMethod(method), target)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };
        if (body.Length > 0)
        {
            request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
            request.Content.Headers.ContentType = new("application/json");
        }
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.TryAddWithoutValidation("User-Agent", $"SpotiFLAC-Mobile/{_config.AppVersion}");
        foreach (var (key, value) in headers)
        {
            if (key.Equals("Host", StringComparison.OrdinalIgnoreCase) || key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
                key.Contains('\r') || key.Contains('\n') || value.Contains('\r') || value.Contains('\n')) continue;
            if (key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase) && request.Content != null)
            {
                request.Content.Headers.Remove(key);
                request.Content.Headers.TryAddWithoutValidation(key, value);
            }
            else
            {
                request.Headers.Remove(key);
                request.Headers.TryAddWithoutValidation(key, value);
            }
        }
        var automaticGzip = method != "HEAD" && !request.Headers.Contains("Range") &&
            (!request.Headers.TryGetValues("Accept-Encoding", out var encodings) || string.IsNullOrEmpty(string.Join("", encodings)));
        if (automaticGzip)
        {
            request.Headers.Remove("Accept-Encoding");
            request.Headers.AcceptEncoding.ParseAdd("gzip");
        }
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.RequestMessage?.RequestUri is not { } finalUri) throw new HttpRequestException("Signed session response URL is unavailable.");
        EnsureAllowed(finalUri);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var decoded = automaticGzip && response.Content.Headers.ContentEncoding.Contains("gzip", StringComparer.OrdinalIgnoreCase)
            ? new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true) : null;
        var source = decoded ?? stream;
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (output.Length + read > MaximumResponseBytes) throw new InvalidDataException("Signed session response exceeds 4 MiB.");
            output.Write(buffer, 0, read);
        }
        var responseHeaders = response.Headers.Concat(response.Content.Headers)
            .Where(header => decoded == null || !header.Key.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase) &&
                !header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(item => item.Key, item => (object)string.Join(", ", item.Value), StringComparer.OrdinalIgnoreCase);
        var retry = response.Headers.RetryAfter?.Delta is { } delta ? Math.Max(0, (int)delta.TotalSeconds) :
            response.Headers.RetryAfter?.Date is { } date ? Math.Max(0, (int)(date - _timeProvider.GetUtcNow()).TotalSeconds) : 0;
        return new(response.StatusCode, Encoding.UTF8.GetString(output.ToArray()), responseHeaders, retry,
            response.Headers.RetryAfter?.Delta, response.Headers.RetryAfter?.Date);
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
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            string protectedContents;
            try { protectedContents = File.ReadAllText(_statePath); }
            catch (FileNotFoundException)
            {
                var record = new SessionRecord(Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(), null, null, null);
                try { Save(record, overwrite: false); }
                catch (IOException) when (File.Exists(_statePath)) { return Load(); }
                return record;
            }
            var json = _protector.Unprotect(protectedContents);
            var loaded = JsonSerializer.Deserialize<SessionRecord>(json);
            if (loaded == null || string.IsNullOrWhiteSpace(loaded.InstallId) ||
                (string.IsNullOrWhiteSpace(loaded.SessionId) != string.IsNullOrWhiteSpace(loaded.SessionSecret)))
                throw new InvalidDataException();
            return loaded;
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            var reason = exception is CryptographicException ? "protection_failed" :
                exception is JsonException or InvalidDataException ? "invalid_record" : "read_failed";
            _logSink?.Invoke("error", "session.load-failed",
                $"Stored session unavailable: call=load; reason={reason}; time_left=unknown. The file was retained; restore storage access or the protection keys before retrying.");
            throw new InvalidOperationException("Stored session is unreadable; the existing file was retained.");
        }
    }

    private void Save(SessionRecord record, bool overwrite = true)
    {
        var temporary = _statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, _protector.Protect(JsonSerializer.Serialize(record)));
            File.Move(temporary, _statePath, overwrite);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private bool HasUsableSession(SessionRecord record) =>
        !string.IsNullOrWhiteSpace(record.SessionId) && !string.IsNullOrWhiteSpace(record.SessionSecret) &&
        (!TryExpiry(record, out var expiry) || expiry > _timeProvider.GetUtcNow());

    private static bool TryExpiry(SessionRecord record, out DateTimeOffset expiry) =>
        DateTimeOffset.TryParse(record.ExpiresAt, out expiry);

    private static byte[] Hmac(byte[] key, string value) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(value));
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record SessionRecord(string InstallId, string? SessionId, string? SessionSecret, string? ExpiresAt);
    private sealed record ResponseData(HttpStatusCode StatusCode, string Body, IReadOnlyDictionary<string, object> Headers,
        int RetryAfterSeconds, TimeSpan? RetryDelta, DateTimeOffset? RetryDate)
    {
        public bool IsSuccessStatusCode => (int)StatusCode is >= 200 and < 300;
        // Adapted from SpotiFLAC-Mobile (MIT), signed_session/protocol.rs provider_delay.
        public TimeSpan ProviderDelay(int contractSeconds, DateTimeOffset now)
        {
            var delay = RetryDelta ?? (RetryDate - now);
            if (delay is not { } value || value <= TimeSpan.Zero)
                value = TimeSpan.FromSeconds(contractSeconds > 0 ? contractSeconds : 1);
            return value > TimeSpan.FromSeconds(120) ? TimeSpan.FromSeconds(120) : value;
        }
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
        // Only these exact gateway contracts revoke a stored sign-in; provider errors and bare statuses do not.
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
                var fields = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in root.EnumerateObject())
                {
                    var key = property.Name.ToLowerInvariant();
                    var value = property.Value;
                    if (value.ValueKind == JsonValueKind.Null) continue;
                    var valid = key switch
                    {
                        "error" or "code" or "origin" or "action" or "retry_mode" => value.ValueKind == JsonValueKind.String,
                        "retryable" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                        "retry_after_seconds" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
                        _ => true
                    };
                    if (!valid) return new();
                    fields[key] = value;
                }
                string Text(string key) => fields.TryGetValue(key, out var value) ? value.GetString()!.Trim() : "";
                return new(Text("error"), Text("code").ToUpperInvariant(), Text("origin").ToLowerInvariant(),
                    Text("action").ToLowerInvariant(), fields.TryGetValue("retryable", out var retryable) && retryable.GetBoolean(),
                    Text("retry_mode").ToLowerInvariant(), fields.TryGetValue("retry_after_seconds", out var seconds)
                        ? (int)Math.Clamp(seconds.GetInt64(), 0, int.MaxValue) : 0);
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
