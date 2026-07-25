using System.Text.Json;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using allstarr.Filters;
using allstarr.Models.Settings;
using allstarr.Services.Admin;
using allstarr.Core.Identity;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin/auth")]
[ServiceFilter(typeof(AdminPortFilter))]
public class AdminAuthController : ControllerBase
{
    private readonly JellyfinSettings _jellyfinSettings;
    private readonly SubsonicSettings _subsonicSettings;
    private readonly BackendType _backendType;
    private readonly HttpClient _httpClient;
    private readonly AdminAuthSessionService _sessionService;
    private readonly ILogger<AdminAuthController> _logger;
    private readonly BackendIdentityResolver? _identityResolver;
    private readonly ProviderAccountManagementMode _providerAccountManagementMode;

    public AdminAuthController(
        IOptions<JellyfinSettings> jellyfinSettings,
        IOptions<SubsonicSettings> subsonicSettings,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        AdminAuthSessionService sessionService,
        ILogger<AdminAuthController> logger,
        BackendIdentityResolver? identityResolver = null,
        ProviderAccountManagementOptions? providerAccountManagementOptions = null)
    {
        _jellyfinSettings = jellyfinSettings.Value;
        _subsonicSettings = subsonicSettings.Value;
        _backendType = Enum.TryParse<BackendType>(
            configuration["Backend:Type"],
            ignoreCase: true,
            out var configuredBackend)
            ? configuredBackend
            : BackendType.Jellyfin;
        _httpClient = httpClientFactory.CreateClient();
        _sessionService = sessionService;
        _logger = logger;
        _identityResolver = identityResolver;
        _providerAccountManagementMode = (providerAccountManagementOptions ?? new())
            .ParseManagementMode();
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        if (_backendType == BackendType.Subsonic)
        {
            return await LoginWithSubsonicAsync(request);
        }

        if (string.IsNullOrWhiteSpace(_jellyfinSettings.Url))
        {
            return StatusCode(500, new { error = "Jellyfin URL is not configured" });
        }

        var username = request.Username?.Trim();
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest(new { error = "Username and password are required" });
        }

        var jellyfinAuthUrl = $"{_jellyfinSettings.Url.TrimEnd('/')}/Users/AuthenticateByName";
        var deviceId = Guid.NewGuid().ToString("N");
        var authHeader =
            $"MediaBrowser Client=\"AllstarrAdmin\", Device=\"WebUI\", DeviceId=\"{deviceId}\", Version=\"1.0.0\"";

        try
        {
            var loginJson = JsonSerializer.Serialize(new JellyfinAuthenticateRequest
            {
                Username = username,
                Pw = request.Password
            }, new JsonSerializerOptions
            {
                PropertyNamingPolicy = null
            });

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, jellyfinAuthUrl)
            {
                Content = new StringContent(loginJson, Encoding.UTF8, "application/json")
            };
            httpRequest.Headers.TryAddWithoutValidation("X-Emby-Authorization", authHeader);

            using var response = await _httpClient.SendAsync(httpRequest);

            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or
                    System.Net.HttpStatusCode.Forbidden)
                {
                    return Unauthorized(new { error = "Invalid Jellyfin credentials" });
                }

                if (response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
                {
                    return StatusCode(503, new { error = "Jellyfin is temporarily unavailable" });
                }

                return StatusCode((int)response.StatusCode, new
                {
                    error = "Failed to authenticate with Jellyfin"
                });
            }

            using var authDoc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            var root = authDoc.RootElement;

            var accessToken = root.TryGetProperty("AccessToken", out var tokenProp) ? tokenProp.GetString() : null;
            var serverId = root.TryGetProperty("ServerId", out var serverIdProp) ? serverIdProp.GetString() : null;
            if (string.IsNullOrWhiteSpace(accessToken) ||
                !root.TryGetProperty("User", out var userProp))
            {
                return StatusCode(502, new { error = "Jellyfin returned an invalid authentication response" });
            }

            var userId = userProp.TryGetProperty("Id", out var userIdProp) ? userIdProp.GetString() : null;
            var userName = userProp.TryGetProperty("Name", out var userNameProp) ? userNameProp.GetString() : username;
            var isAdministrator = userProp.TryGetProperty("Policy", out var policyProp) &&
                                  policyProp.TryGetProperty("IsAdministrator", out var adminProp) &&
                                  adminProp.ValueKind == JsonValueKind.True;

            if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(userName))
            {
                return StatusCode(502, new { error = "Jellyfin user details are missing in auth response" });
            }

            var principal = _identityResolver == null
                ? null
                : await _identityResolver.ResolveAsync(
                    new BackendIdentityDescriptor(
                        "Jellyfin",
                        userId,
                        userName,
                        isAdministrator),
                    HttpContext.RequestAborted);
            var session = _sessionService.CreateSession(
                userId: userId,
                userName: userName,
                isAdministrator: isAdministrator,
                jellyfinAccessToken: accessToken,
                jellyfinServerId: serverId,
                isPersistent: request.RememberMe,
                tenantId: principal?.TenantId,
                allstarrUserId: principal?.UserId);

            SetSessionCookie(session.SessionId, session.ExpiresAtUtc);

            _logger.LogInformation("Admin WebUI login successful for Jellyfin user {UserName} ({UserId})",
                session.UserName, session.UserId);

            return Ok(new
            {
                authenticated = true,
                user = new
                {
                    id = session.UserId,
                    name = session.UserName,
                    isAdministrator = session.IsAdministrator,
                    tenantId = session.TenantId,
                    allstarrUserId = session.AllstarrUserId,
                    avatarUrl = $"/api/admin/auth/me/avatar?user={Uri.EscapeDataString(session.UserId)}"
                },
                rememberMe = session.IsPersistent,
                backend = BackendType.Jellyfin.ToString(),
                providerAccountManagementMode = _providerAccountManagementMode.ToString(),
                expiresAtUtc = session.ExpiresAtUtc
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(
                "Admin WebUI Jellyfin login failed ({ExceptionType})",
                ex.GetType().Name);
            return StatusCode(500, new { error = "Failed to authenticate with Jellyfin" });
        }
    }

    [HttpGet("me")]
    public IActionResult GetCurrentSession()
    {
        if (!_sessionService.TryGetValidSession(Request, out var session))
        {
            DeleteSessionCookies();
            return Ok(new
            {
                authenticated = false,
                backend = _backendType.ToString(),
                providerAccountManagementMode = _providerAccountManagementMode.ToString()
            });
        }

        // Re-issue the canonical root-scoped cookie while validating the session.
        // Older Allstarr builds could leave a more narrowly scoped cookie behind,
        // causing /auth/me to succeed while sibling admin APIs received a stale ID.
        SetSessionCookie(session.SessionId, session.ExpiresAtUtc);

        return Ok(new
        {
            authenticated = true,
            user = new
            {
                id = session.UserId,
                name = session.UserName,
                isAdministrator = session.IsAdministrator,
                tenantId = session.TenantId,
                allstarrUserId = session.AllstarrUserId,
                avatarUrl = session.BackendType.Equals(BackendType.Jellyfin.ToString(), StringComparison.OrdinalIgnoreCase)
                    ? $"/api/admin/auth/me/avatar?user={Uri.EscapeDataString(session.UserId)}"
                    : null
            },
            rememberMe = session.IsPersistent,
            backend = session.BackendType,
            providerAccountManagementMode = _providerAccountManagementMode.ToString(),
            expiresAtUtc = session.ExpiresAtUtc
        });
    }

    [HttpGet("me/avatar")]
    public async Task<IActionResult> GetCurrentUserAvatar(CancellationToken cancellationToken)
    {
        if (!_sessionService.TryGetValidSession(Request, out var session) ||
            !session.BackendType.Equals(BackendType.Jellyfin.ToString(), StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(session.JellyfinAccessToken) ||
            string.IsNullOrWhiteSpace(_jellyfinSettings.Url))
        {
            return NotFound();
        }

        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{_jellyfinSettings.Url.TrimEnd('/')}/Users/{Uri.EscapeDataString(session.UserId)}/Images/Primary?width=96&quality=90");
        request.Headers.TryAddWithoutValidation("X-Emby-Token", session.JellyfinAccessToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (!response.IsSuccessStatusCode || contentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true)
        {
            return NotFound();
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (bytes.Length is <= 0 or > 5 * 1024 * 1024)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "private, no-store";
        Response.Headers.Vary = "Cookie";
        return File(bytes, contentType);
    }

    [HttpPost("logout")]
    public IActionResult Logout()
    {
        foreach (var sessionId in _sessionService.ReadSessionIds(Request))
        {
            _sessionService.RemoveSession(sessionId);
        }

        DeleteSessionCookies();
        return Ok(new { success = true });
    }

    private void DeleteSessionCookies()
    {
        Response.Cookies.Delete(AdminAuthSessionService.SessionCookieName, new CookieOptions { Path = "/" });
        Response.Cookies.Delete(AdminAuthSessionService.LegacySessionCookieName, new CookieOptions { Path = "/" });
        Response.Cookies.Delete(AdminAuthSessionService.LegacySessionCookieName, new CookieOptions { Path = "/api/admin/auth" });
    }

    private void SetSessionCookie(string sessionId, DateTime expiresAtUtc)
    {
        var secure = Request.IsHttps ||
                     string.Equals(Request.Headers["X-Forwarded-Proto"], "https",
                         StringComparison.OrdinalIgnoreCase);

        Response.Cookies.Append(AdminAuthSessionService.SessionCookieName, sessionId, new CookieOptions
        {
            HttpOnly = true,
            Secure = secure,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            IsEssential = true,
            Expires = expiresAtUtc
        });
    }

    private async Task<IActionResult> LoginWithSubsonicAsync(LoginRequest request)
    {
        if (string.IsNullOrWhiteSpace(_subsonicSettings.Url))
        {
            return StatusCode(500, new { error = "Subsonic URL is not configured" });
        }

        var username = request.Username?.Trim();
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest(new { error = "Username and password are required" });
        }

        try
        {
            var endpoint = $"{_subsonicSettings.Url.TrimEnd('/')}/rest/getUser.view";
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["u"] = username,
                    ["p"] = request.Password,
                    ["username"] = username,
                    ["v"] = "1.16.1",
                    ["c"] = "allstarr-admin",
                    ["f"] = "json"
                })
            };
            using var response = await _httpClient.SendAsync(httpRequest, HttpContext.RequestAborted);

            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or
                    System.Net.HttpStatusCode.Forbidden)
                {
                    return Unauthorized(new { error = "Invalid Subsonic credentials" });
                }

                return StatusCode((int)response.StatusCode, new
                {
                    error = "Failed to authenticate with Subsonic"
                });
            }

            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(HttpContext.RequestAborted),
                cancellationToken: HttpContext.RequestAborted);
            if (!TryReadSubsonicIdentity(document.RootElement, username, out var identity))
            {
                return Unauthorized(new { error = "Invalid Subsonic credentials" });
            }

            var principal = _identityResolver == null
                ? null
                : await _identityResolver.ResolveAsync(
                    new BackendIdentityDescriptor(
                        "Subsonic",
                        identity.UserName,
                        identity.UserName,
                        identity.IsAdministrator),
                    HttpContext.RequestAborted);
            var session = _sessionService.CreateSession(
                userId: identity.UserName,
                userName: identity.UserName,
                isAdministrator: identity.IsAdministrator,
                jellyfinAccessToken: string.Empty,
                jellyfinServerId: null,
                isPersistent: request.RememberMe,
                backendType: BackendType.Subsonic.ToString(),
                tenantId: principal?.TenantId,
                allstarrUserId: principal?.UserId);

            SetSessionCookie(session.SessionId, session.ExpiresAtUtc);
            _logger.LogInformation(
                "Admin WebUI login successful for Subsonic user {UserName}",
                session.UserName);

            return Ok(new
            {
                authenticated = true,
                user = new
                {
                    id = session.UserId,
                    name = session.UserName,
                    isAdministrator = session.IsAdministrator,
                    tenantId = session.TenantId,
                    allstarrUserId = session.AllstarrUserId
                },
                rememberMe = session.IsPersistent,
                backend = BackendType.Subsonic.ToString(),
                providerAccountManagementMode = _providerAccountManagementMode.ToString(),
                expiresAtUtc = session.ExpiresAtUtc
            });
        }
        catch (JsonException)
        {
            return StatusCode(502, new { error = "Subsonic returned an invalid authentication response" });
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            return new StatusCodeResult(499);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Subsonic admin authentication failed ({ExceptionType})",
                ex.GetType().Name);
            return StatusCode(502, new { error = "Failed to authenticate with Subsonic" });
        }
    }

    private static bool TryReadSubsonicIdentity(
        JsonElement root,
        string requestedUserName,
        out SubsonicIdentity identity)
    {
        identity = default;
        if (!root.TryGetProperty("subsonic-response", out var envelope) ||
            !envelope.TryGetProperty("status", out var status) ||
            !string.Equals(status.GetString(), "ok", StringComparison.OrdinalIgnoreCase) ||
            !envelope.TryGetProperty("user", out var user))
        {
            return false;
        }

        var returnedUserName = user.TryGetProperty("username", out var username)
            ? username.GetString()
            : requestedUserName;
        if (string.IsNullOrWhiteSpace(returnedUserName) ||
            !returnedUserName.Equals(requestedUserName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var isAdministrator = user.TryGetProperty("adminRole", out var adminRole) &&
                              adminRole.ValueKind == JsonValueKind.True;
        identity = new SubsonicIdentity(returnedUserName, isAdministrator);
        return true;
    }

    private readonly record struct SubsonicIdentity(string UserName, bool IsAdministrator);

    public class LoginRequest
    {
        public string? Username { get; set; }
        public string? Password { get; set; }
        public bool RememberMe { get; set; }
    }

    private sealed class JellyfinAuthenticateRequest
    {
        public string? Username { get; init; }
        public string? Pw { get; init; }
    }
}
