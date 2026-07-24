using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using allstarr.Filters;
using allstarr.Models.Settings;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using allstarr.Services.AppleMusic;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin/apple-download")]
[Route("api/admin/applemusic")]
[ServiceFilter(typeof(AdminPortFilter))]
public class AppleMusicController : ControllerBase
{
    private const long MaxSetupPackageBytes = 512L * 1024 * 1024;
    private static readonly HashSet<string> SecretFieldNames = new(StringComparer.Ordinal)
    {
        "accesstoken",
        "authorization",
        "cookie",
        "devtoken",
        "mediausertoken",
        "musicusertoken",
        "password",
        "refreshtoken",
        "sessionkey",
        "token"
    };

    private readonly HttpClient _httpClient;
    private readonly ILogger<AppleMusicController> _logger;
    private readonly IAppleDownloadEndpointDiscovery? _discovery;
    private readonly string _setupUploadDirectory;

    public AppleMusicController(
        IHttpClientFactory httpClientFactory,
        IOptions<AppleDownloadSettings> settings,
        ILogger<AppleMusicController> logger,
        IAppleDownloadEndpointDiscovery? discovery = null)
    {
        _httpClient = httpClientFactory.CreateClient("AppleMusic");
        _logger = logger;
        _discovery = discovery;
        _setupUploadDirectory = string.IsNullOrWhiteSpace(settings.Value.SetupUploadDirectory)
            ? "/app/apple-upload"
            : settings.Value.SetupUploadDirectory;

        if (allstarr.Services.Common.OutboundRequestGuard.TryCreateConfiguredServiceUri(
                settings.Value.BaseUrl, out var baseUri, out _))
        {
            _httpClient.BaseAddress = baseUri;
        }
        _httpClient.Timeout = TimeSpan.FromMinutes(10); // Setup and login can take several minutes.
    }

    [HttpGet("status")]
    public async Task<IActionResult> GetStatus(CancellationToken cancellationToken = default)
    {
        AppleDownloadEndpointSnapshot? discovery = null;
        if (_discovery != null)
        {
            discovery = await _discovery.DiscoverAsync(cancellationToken);
            if (discovery.State is AppleDownloadEndpointState.NeedsConfiguration or
                AppleDownloadEndpointState.Unreachable or
                AppleDownloadEndpointState.Incompatible)
            {
                return JsonContent(DiscoveryStatus(discovery), StatusCodes.Status200OK);
            }
        }
        if (_httpClient.BaseAddress == null)
        {
            return JsonContent(new JsonObject
            {
                ["state"] = "needs_config",
                ["ready"] = false,
                ["reason_code"] = "invalid_or_missing_endpoint"
            }, StatusCodes.Status200OK);
        }

        try
        {
            using var healthResponse = await _httpClient.GetAsync("api/health", cancellationToken);
            var healthJson = await healthResponse.Content.ReadAsStringAsync(cancellationToken);
            if (!healthResponse.IsSuccessStatusCode)
            {
                return UpstreamFailure(
                    healthResponse.StatusCode,
                    "health_unavailable",
                    "Apple Music sidecar health check failed.");
            }

            if (TryParseObject(healthJson) is not { } health)
            {
                return InvalidSidecarContract("health");
            }

            var staged = GetBoolean(health, "staged") == true;
            var daemonRunning = GetBoolean(health, "daemon_running") == true;
            var wrapperHealthy = GetBoolean(health, "wrapper_healthy") == true;
            var healthLoggedIn = GetBoolean(health, "logged_in") == true;

            JsonObject? account = null;
            string? wrapperVersion = null;
            if (daemonRunning)
            {
                using var meResponse = await _httpClient.GetAsync("api/me", cancellationToken);
                var meJson = await meResponse.Content.ReadAsStringAsync(cancellationToken);
                if (!meResponse.IsSuccessStatusCode)
                {
                    return UpstreamFailure(
                        meResponse.StatusCode,
                        meResponse.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                            ? "account_unauthorized"
                            : "account_unavailable",
                        meResponse.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                            ? "Apple Music wrapper account requires authentication."
                            : "Apple Music wrapper account status is unavailable.");
                }

                if (TryParseObject(meJson) is not { } wrapperStatus)
                {
                    return InvalidSidecarContract("account status");
                }

                account = NormalizeAccountStatus(wrapperStatus, healthLoggedIn);
                wrapperVersion = GetString(wrapperStatus, "version");
            }

            account ??= NormalizeAccountStatus(new JsonObject(), daemonRunning && healthLoggedIn);
            var loginState = GetString(account, "state") ?? "logged_out";
            var loggedIn = string.Equals(loginState, "authenticated", StringComparison.Ordinal);
            var ready = staged && daemonRunning && wrapperHealthy && loggedIn;

            var status = new JsonObject
            {
                ["state"] = BuildOverallState(staged, daemonRunning, wrapperHealthy, loginState, ready),
                ["staged"] = staged,
                ["daemon_running"] = daemonRunning,
                ["wrapper_healthy"] = wrapperHealthy,
                ["logged_in"] = loggedIn,
                ["login_state"] = loginState,
                ["ready"] = ready,
                ["account"] = account
            };
            if (!string.IsNullOrWhiteSpace(wrapperVersion))
            {
                status["wrapper_version"] = wrapperVersion;
            }
            if (discovery != null)
            {
                status["api_version"] = discovery.ApiVersion;
                status["capabilities"] = JsonSerializer.SerializeToNode(discovery.Capabilities.Select(item => new
                {
                    id = item.Id,
                    state = item.State.ToString().ToLowerInvariant(),
                    reason_code = item.ReasonCode
                }));
            }

            return JsonContent(status, StatusCodes.Status200OK);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return SidecarException("read status", ex);
        }
    }

    [HttpPost("setup")]
    [RequestSizeLimit(MaxSetupPackageBytes)]
    public async Task<IActionResult> Setup(
        [FromForm] IFormFile? file,
        CancellationToken cancellationToken = default)
    {
        if (file == null || file.Length == 0)
        {
            return BadRequest(new { error = "package_required", message = "Choose an Apple Music APK or APKM package first." });
        }

        if (file.Length > MaxSetupPackageBytes)
        {
            return BadRequest(new { error = "package_too_large", message = "Apple Music packages must be 512 MB or smaller." });
        }

        var extension = Path.GetExtension(file.FileName);
        if (!extension.Equals(".apk", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".apkm", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { error = "unsupported_package", message = "Upload an .apk or .apkm file." });
        }

        try
        {
            Directory.CreateDirectory(_setupUploadDirectory);
            var safeName = Path.GetFileName(file.FileName);
            var stagedName = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}-{safeName}";
            var destination = Path.Combine(_setupUploadDirectory, stagedName);
            await using var stream = System.IO.File.Create(destination);
            await file.CopyToAsync(stream, cancellationToken);

            _logger.LogInformation("Apple provider package staged for host preparation: {Extension} ({Bytes} bytes)", extension, file.Length);
            return Accepted(new
            {
                state = "staged",
                fileName = safeName,
                sizeBytes = file.Length,
                stagedAt = DateTimeOffset.UtcNow,
                architecture = "auto",
                message = "Package uploaded. Run ./allstarr.sh install-apple on the Docker host to verify, build, and start the Apple gateway."
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Apple provider package could not be staged");
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "package_stage_failed", message = "The Apple package could not be staged on the host." });
        }
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login(
        [FromBody] JsonElement credentials,
        CancellationToken cancellationToken = default)
    {
        if (_httpClient.BaseAddress == null) return MissingEndpoint();
        try
        {
            using var response = await _httpClient.PostAsJsonAsync(
                "api/login",
                credentials,
                cancellationToken);
            return await ForwardSanitizedResponseAsync(response, "login", cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return SidecarException("start login", ex);
        }
    }

    [HttpPost("login/2fa")]
    public async Task<IActionResult> Login2fa(
        [FromBody] JsonElement code,
        CancellationToken cancellationToken = default)
    {
        if (_httpClient.BaseAddress == null) return MissingEndpoint();
        try
        {
            using var response = await _httpClient.PostAsJsonAsync(
                "api/login/2fa",
                code,
                cancellationToken);
            return await ForwardSanitizedResponseAsync(response, "2FA", cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return SidecarException("submit 2FA", ex);
        }
    }

    private async Task<IActionResult> ForwardSanitizedResponseAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (TryParseNode(body) is not { } parsed || parsed is JsonValue)
        {
            var status = response.IsSuccessStatusCode
                ? StatusCodes.Status502BadGateway
                : (int)response.StatusCode;
            return JsonContent(new JsonObject
            {
                ["error"] = "invalid_sidecar_response",
                ["message"] = $"Apple Music sidecar returned an invalid {operation} response."
            }, status);
        }

        var sanitized = SanitizeNode(parsed) ?? new JsonObject();
        return JsonContent(sanitized, (int)response.StatusCode);
    }

    private static IActionResult MissingEndpoint() => JsonContent(new JsonObject
    {
        ["error"] = "invalid_or_missing_endpoint",
        ["message"] = "Configure the external Apple download provider URL first."
    }, StatusCodes.Status409Conflict);

    private static JsonObject DiscoveryStatus(AppleDownloadEndpointSnapshot snapshot) => new()
    {
        ["state"] = snapshot.State switch
        {
            AppleDownloadEndpointState.NeedsConfiguration => "needs_config",
            AppleDownloadEndpointState.NeedsAuthentication => "needs_login",
            AppleDownloadEndpointState.Available => "ready",
            AppleDownloadEndpointState.Incompatible => "unsupported",
            _ => "degraded"
        },
        ["ready"] = snapshot.State == AppleDownloadEndpointState.Available,
        ["logged_in"] = snapshot.Authenticated,
        ["staged"] = false,
        ["daemon_running"] = false,
        ["wrapper_healthy"] = false,
        ["api_version"] = snapshot.ApiVersion,
        ["reason_code"] = snapshot.ReasonCode,
        ["capabilities"] = JsonSerializer.SerializeToNode(snapshot.Capabilities.Select(item => new
        {
            id = item.Id,
            state = item.State.ToString().ToLowerInvariant(),
            reason_code = item.ReasonCode
        }))
    };

    private IActionResult SidecarException(string operation, Exception exception)
    {
        var (status, error, message) = exception switch
        {
            TaskCanceledException => (
                StatusCodes.Status504GatewayTimeout,
                "sidecar_timeout",
                $"Apple Music sidecar timed out while attempting to {operation}."),
            HttpRequestException => (
                StatusCodes.Status503ServiceUnavailable,
                "sidecar_unreachable",
                $"Apple Music sidecar is unreachable; unable to {operation}."),
            _ => (
                StatusCodes.Status502BadGateway,
                "sidecar_failure",
                $"Apple Music sidecar could not {operation}.")
        };

        // Exception text can contain internal URLs or wrapper details; log only its type.
        _logger.LogWarning(
            "Apple Music sidecar operation failed: {Operation} ({ExceptionType})",
            operation,
            exception.GetType().Name);
        return JsonContent(new JsonObject
        {
            ["error"] = error,
            ["message"] = message
        }, status);
    }

    private static IActionResult UpstreamFailure(
        HttpStatusCode upstreamStatus,
        string error,
        string message) => JsonContent(new JsonObject
        {
            ["error"] = error,
            ["message"] = message
        }, (int)upstreamStatus);

    private static IActionResult InvalidSidecarContract(string operation) =>
        JsonContent(new JsonObject
        {
            ["error"] = "invalid_sidecar_response",
            ["message"] = $"Apple Music sidecar returned an invalid {operation} response."
        }, StatusCodes.Status502BadGateway);

    private static ContentResult JsonContent(JsonNode payload, int statusCode) => new()
    {
        Content = payload.ToJsonString(),
        ContentType = "application/json",
        StatusCode = statusCode
    };

    private static JsonNode? TryParseNode(string json)
    {
        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonObject? TryParseObject(string json) => TryParseNode(json) as JsonObject;

    private static JsonObject NormalizeAccountStatus(JsonObject wrapperStatus, bool healthLoggedIn)
    {
        var source = wrapperStatus["auth"] as JsonObject ?? wrapperStatus;
        var explicitLoggedIn = GetBoolean(source, "logged_in");
        var state = NormalizeLoginState(
            GetString(source, "state"),
            explicitLoggedIn ?? healthLoggedIn);

        var account = new JsonObject
        {
            ["state"] = state,
            ["logged_in"] = string.Equals(state, "authenticated", StringComparison.Ordinal)
        };

        CopyString(source, account, "username");
        CopyString(source, account, "apple_id");
        CopyString(source, account, "storefront");
        return account;
    }

    private static string NormalizeLoginState(string? state, bool loggedIn)
    {
        var normalized = string.IsNullOrWhiteSpace(state)
            ? string.Empty
            : state.Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');

        return normalized switch
        {
            "authenticated" or "logged_in" or "ready" => "authenticated",
            "awaiting_2fa" or "awaiting2fa" or "needs_2fa" or "2fa_required" or "two_factor_required" => "awaiting_2fa",
            "logged_out" or "unauthenticated" => "logged_out",
            "" => loggedIn ? "authenticated" : "logged_out",
            _ => normalized
        };
    }

    private static string BuildOverallState(
        bool staged,
        bool daemonRunning,
        bool wrapperHealthy,
        string loginState,
        bool ready)
    {
        if (!staged) return "needs_setup";
        if (!daemonRunning) return "daemon_offline";
        if (!wrapperHealthy) return "wrapper_unhealthy";
        if (string.Equals(loginState, "awaiting_2fa", StringComparison.Ordinal)) return "awaiting_2fa";
        if (ready) return "ready";
        return "needs_login";
    }

    private static bool? GetBoolean(JsonObject source, string key)
    {
        try
        {
            return source[key]?.GetValue<bool>();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static string? GetString(JsonObject source, string key)
    {
        try
        {
            return source[key]?.GetValue<string>();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static void CopyString(JsonObject source, JsonObject target, string key)
    {
        var value = GetString(source, key);
        if (!string.IsNullOrWhiteSpace(value))
        {
            target[key] = value;
        }
    }

    private static JsonNode? SanitizeNode(JsonNode? node)
    {
        if (node is JsonObject sourceObject)
        {
            var result = new JsonObject();
            foreach (var property in sourceObject)
            {
                if (IsSecretField(property.Key))
                {
                    continue;
                }

                result[property.Key] = SanitizeNode(property.Value);
            }
            return result;
        }

        if (node is JsonArray sourceArray)
        {
            var result = new JsonArray();
            foreach (var item in sourceArray)
            {
                result.Add(SanitizeNode(item));
            }
            return result;
        }

        return node?.DeepClone();
    }

    private static bool IsSecretField(string key)
    {
        var normalized = new string(key
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
        return SecretFieldNames.Contains(normalized);
    }
}
