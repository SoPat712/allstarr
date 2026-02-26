using System.Text.Json;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using allstarr.Filters;
using allstarr.Models.Settings;
using allstarr.Services.Admin;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin/auth")]
[ServiceFilter(typeof(AdminPortFilter))]
public class AdminAuthController : ControllerBase
{
    private readonly JellyfinSettings _jellyfinSettings;
    private readonly HttpClient _httpClient;
    private readonly AdminAuthSessionService _sessionService;
    private readonly ILogger<AdminAuthController> _logger;

    public AdminAuthController(
        IOptions<JellyfinSettings> jellyfinSettings,
        IHttpClientFactory httpClientFactory,
        AdminAuthSessionService sessionService,
        ILogger<AdminAuthController> logger)
    {
        _jellyfinSettings = jellyfinSettings.Value;
        _httpClient = httpClientFactory.CreateClient();
        _sessionService = sessionService;
        _logger = logger;
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
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

            var session = _sessionService.CreateSession(
                userId: userId,
                userName: userName,
                isAdministrator: isAdministrator,
                jellyfinAccessToken: accessToken,
                jellyfinServerId: serverId);

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
                    isAdministrator = session.IsAdministrator
                },
                expiresAtUtc = session.ExpiresAtUtc
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Admin WebUI Jellyfin login failed");
            return StatusCode(500, new { error = "Failed to authenticate with Jellyfin" });
        }
    }

    [HttpGet("me")]
    public IActionResult GetCurrentSession()
    {
        if (!Request.Cookies.TryGetValue(AdminAuthSessionService.SessionCookieName, out var sessionId) ||
            !_sessionService.TryGetValidSession(sessionId, out var session))
        {
            Response.Cookies.Delete(AdminAuthSessionService.SessionCookieName);
            return Ok(new { authenticated = false });
        }

        return Ok(new
        {
            authenticated = true,
            user = new
            {
                id = session.UserId,
                name = session.UserName,
                isAdministrator = session.IsAdministrator
            },
            expiresAtUtc = session.ExpiresAtUtc
        });
    }

    [HttpPost("logout")]
    public IActionResult Logout()
    {
        if (Request.Cookies.TryGetValue(AdminAuthSessionService.SessionCookieName, out var sessionId))
        {
            _sessionService.RemoveSession(sessionId);
        }

        Response.Cookies.Delete(AdminAuthSessionService.SessionCookieName);
        return Ok(new { success = true });
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

    public class LoginRequest
    {
        public string? Username { get; set; }
        public string? Password { get; set; }
    }

    private sealed class JellyfinAuthenticateRequest
    {
        public string? Username { get; init; }
        public string? Pw { get; init; }
    }
}
