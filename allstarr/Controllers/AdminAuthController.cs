using System.Text.Json;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using allstarr.Filters;
using allstarr.Models.Settings;
using allstarr.Services.Admin;
using allstarr.Services.Common;
using allstarr.Core.Identity;
using allstarr.Core.Secrets;
using allstarr.Core.Configuration;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin/auth")]
[ServiceFilter(typeof(AdminPortFilter))]
public sealed class AdminAuthController : ControllerBase
{
    private readonly JellyfinSettings _jellyfinSettings;
    private readonly SubsonicSettings _subsonicSettings;
    private readonly BackendType _backendType;
    private readonly HttpClient _httpClient;
    private readonly AdminAuthSessionService _sessionService;
    private readonly ILogger<AdminAuthController> _logger;
    private readonly BackendIdentityResolver? _identityResolver;
    private readonly ProviderAccountOptions _providerAccountOptions;
    private readonly IMediaAssetResolver _mediaAssets;
    private readonly ReleaseComposition _releaseComposition;
    private readonly AdminOidcOptions? _oidcOptions;
    private readonly AdminOidcLinks? _oidcLinks;
    private readonly IAntiforgery? _antiforgery;
    private readonly EncryptedSecretStore? _secrets;
    private string? _pendingOidcKey;

    public AdminAuthController(
        IOptions<JellyfinSettings> jellyfinSettings,
        IOptions<SubsonicSettings> subsonicSettings,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        AdminAuthSessionService sessionService,
        ILogger<AdminAuthController> logger,
        IMediaAssetResolver mediaAssets,
        BackendIdentityResolver? identityResolver = null,
        ProviderAccountOptions? providerAccountManagementOptions = null,
        ReleaseComposition? releaseComposition = null,
        AdminOidcOptions? oidcOptions = null,
        AdminOidcLinks? oidcLinks = null,
        IAntiforgery? antiforgery = null,
        EncryptedSecretStore? secrets = null)
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
        _mediaAssets = mediaAssets;
        _identityResolver = identityResolver;
        _providerAccountOptions = providerAccountManagementOptions ?? new();
        _releaseComposition = releaseComposition ?? ReleaseComposition.Core;
        _oidcOptions = oidcOptions;
        _oidcLinks = oidcLinks;
        _antiforgery = antiforgery;
        _secrets = secrets;
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        if (request.LinkOidc)
        {
            if (!Request.IsHttps) return BadRequest(new { error = "SSO linking requires HTTPS." });
            if (_oidcOptions?.Enabled != true || _oidcLinks == null || _antiforgery == null)
                return BadRequest(new { error = "SSO is not enabled." });
            if (!await _antiforgery.IsRequestValidAsync(HttpContext))
                return BadRequest(new { error = "Reload the page before linking SSO." });
            var pending = await HttpContext.AuthenticateAsync(AdminOidcOptions.PendingScheme);
            _pendingOidcKey = pending.Principal == null ? null : AdminOidcLinks.IdentityKey(pending.Principal, _oidcOptions.ClientId);
            if (_pendingOidcKey == null) return Unauthorized(new { error = "Sign in through SSO again before linking." });
        }
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
            httpRequest.Headers.TryAddWithoutValidation("Authorization", authHeader);

            using var response = await _httpClient.SendAsync(httpRequest, HttpContext.RequestAborted);

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

            using var authDoc = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(HttpContext.RequestAborted),
                cancellationToken: HttpContext.RequestAborted);
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

            return await CompleteLoginAsync(
                BackendType.Jellyfin,
                userId,
                userName,
                isAdministrator,
                accessToken,
                serverId,
                request);
        }
        catch (JsonException)
        {
            return StatusCode(502, new { error = "Jellyfin returned an invalid authentication response" });
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            return new StatusCodeResult(499);
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
    public async Task<IActionResult> GetCurrentSession()
    {
        var session = await _sessionService.GetValidSessionAsync(Request, HttpContext.RequestAborted);
        if (session is null)
        {
            DeleteSessionCookies();
            return Ok(new
            {
                authenticated = false,
                backend = _backendType.ToString(),
                listenersCanConnectOwnAccounts = _providerAccountOptions.ListenersCanConnectOwnAccounts,
                features = ReleaseFeatures()
            });
        }

        // Renew the cookie at the configured admin path, shared by all admin APIs.
        SetSessionCookie(session.SessionId, session.ExpiresAtUtc);

        return Ok(AuthenticatedSessionResponse(session));
    }

    [HttpGet("me/avatar")]
    public async Task<IActionResult> GetCurrentUserAvatar(CancellationToken cancellationToken)
    {
        var session = await _sessionService.GetValidSessionAsync(Request, cancellationToken);
        if (session is null ||
            !session.BackendType.Equals(BackendType.Jellyfin.ToString(), StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(session.JellyfinAccessToken) ||
            string.IsNullOrWhiteSpace(_jellyfinSettings.Url))
        {
            return NotFound();
        }

        return await GetUserAvatarAsync(session, session.UserId, cancellationToken);
    }

    [HttpGet("/api/admin/ui/users/{userId}/avatar")]
    public async Task<IActionResult> GetUserAvatar(string userId, CancellationToken cancellationToken)
    {
        var session = await _sessionService.GetValidSessionAsync(Request, cancellationToken);
        if (session is not { IsAdministrator: true } ||
            !session.BackendType.Equals(BackendType.Jellyfin.ToString(), StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(session.JellyfinAccessToken) ||
            string.IsNullOrWhiteSpace(_jellyfinSettings.Url) ||
            string.IsNullOrWhiteSpace(userId) ||
            userId.Length > 128 ||
            userId.Any(character => !char.IsLetterOrDigit(character) && character is not '-' and not '_'))
        {
            return NotFound();
        }

        return await GetUserAvatarAsync(session, userId, cancellationToken);
    }

    private async Task<IActionResult> GetUserAvatarAsync(
        AdminAuthSession session,
        string userId,
        CancellationToken cancellationToken)
    {
        var asset = await _mediaAssets.ResolveAsync(
            new MediaAssetIdentity(
                session.AllstarrUserId,
                null,
                "jellyfin",
                "user-avatar",
                userId,
                session.JellyfinServerId,
                Width: 96),
            async token =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get,
                    $"{_jellyfinSettings.Url!.TrimEnd('/')}/Users/{Uri.EscapeDataString(userId)}/Images/Primary?width=96&quality=90");
                request.Headers.TryAddWithoutValidation("Authorization", AuthHeaderHelper.CreateAuthHeader(session.JellyfinAccessToken, "AllstarrAdmin", "WebUI", "allstarr-admin-webui", AppVersion.Version));
                using var response = await _httpClient.SendAsync(request, token);
                var contentType = response.Content.Headers.ContentType?.MediaType;
                if (!response.IsSuccessStatusCode ||
                    contentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true)
                    return null;
                var bytes = await response.Content.ReadAsByteArrayAsync(token);
                return new MediaAssetSource(
                    bytes,
                    contentType,
                    response.Headers.ETag?.Tag,
                    response.Content.Headers.LastModified);
            },
            5 * 1024 * 1024,
            cancellationToken);
        if (asset == null) return NotFound();

        Response.Headers.CacheControl = "private, max-age=300";
        Response.Headers.Vary = "Cookie";
        return File(asset.Bytes, asset.ContentType);
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        foreach (var sessionId in _sessionService.ReadSessionIds(Request))
        {
            await _sessionService.RemoveSessionAsync(sessionId, HttpContext.RequestAborted);
        }

        DeleteSessionCookies();
        if (_oidcOptions?.Enabled == true) await HttpContext.SignOutAsync(AdminOidcOptions.PendingScheme);
        return Ok(new { success = true });
    }

    private void DeleteSessionCookies() => AdminSessionCookies.Delete(HttpContext);

    private void SetSessionCookie(string sessionId, DateTime expiresAtUtc)
        => AdminSessionCookies.Write(HttpContext, sessionId, expiresAtUtc);

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
            using var httpRequest = SubsonicAuthenticationRequest(username, request.Password);
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
            if (!AdminBackendIdentity.TryReadSubsonic(document.RootElement, username, out var identity, allowMissingUserName: true))
            {
                return Unauthorized(new { error = "Invalid Subsonic credentials" });
            }

            return await CompleteLoginAsync(
                BackendType.Subsonic,
                identity.UserId,
                identity.Name,
                identity.IsAdministrator,
                string.Empty,
                null,
                request);
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

    private HttpRequestMessage SubsonicAuthenticationRequest(string username, string password) =>
        new(HttpMethod.Post, $"{_subsonicSettings.Url!.TrimEnd('/')}/rest/getUser.view")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["u"] = username,
                ["p"] = password,
                ["username"] = username,
                ["v"] = "1.16.1",
                ["c"] = "allstarr-admin",
                ["f"] = "json"
            })
        };

    [HttpGet("playlist-consent")]
    public async Task<IActionResult> GetPlaylistConsent(CancellationToken cancellationToken)
    {
        var session = await _sessionService.GetValidSessionAsync(Request, cancellationToken);
        if (session == null) return Unauthorized();
        if (_backendType != BackendType.Subsonic)
            return Ok(new { supported = false, granted = false, updatedAt = (DateTimeOffset?)null });
        var principal = await ConsentPrincipalAsync(session, cancellationToken);
        if (principal == null || _secrets == null) return Unauthorized();
        return Ok(ConsentStatus(await _secrets.GetSubsonicPlaylistGrantAsync(principal, cancellationToken)));
    }

    [HttpPost("playlist-consent")]
    public async Task<IActionResult> GrantPlaylistConsent([FromBody] PlaylistConsentRequest request,
        CancellationToken cancellationToken)
    {
        var session = await _sessionService.GetValidSessionAsync(Request, cancellationToken);
        if (session == null) return Unauthorized();
        if (_backendType != BackendType.Subsonic || _secrets == null)
            return BadRequest(new { error = "Playlist consent is available for Subsonic listeners." });
        if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length > 2000)
            return BadRequest(new { error = "Enter your backend password." });
        try
        {
            using var authentication = SubsonicAuthenticationRequest(session.UserId, request.Password);
            using var response = await _httpClient.SendAsync(authentication, cancellationToken);
            if (!response.IsSuccessStatusCode) return Unauthorized(new { error = "Backend reauthentication failed." });
            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            if (!AdminBackendIdentity.TryReadSubsonic(document.RootElement, session.UserId, out var reauthenticated))
                return Unauthorized(new { error = "Backend reauthentication failed." });
            var principal = await ConsentPrincipalAsync(session, cancellationToken, reauthenticated);
            if (principal == null) return Unauthorized();
            return Ok(ConsentStatus(await _secrets.StoreSubsonicPlaylistGrantAsync(
                principal, request.Password, cancellationToken)));
        }
        catch (Exception exception) when (exception is JsonException or HttpRequestException)
        {
            return StatusCode(502, new { error = "Backend reauthentication is unavailable. Try again." });
        }
        catch (UnauthorizedAccessException) { return Unauthorized(); }
    }

    [HttpDelete("playlist-consent")]
    public async Task<IActionResult> RevokePlaylistConsent(CancellationToken cancellationToken)
    {
        var session = await _sessionService.GetValidSessionAsync(Request, cancellationToken);
        if (session == null) return Unauthorized();
        if (_backendType != BackendType.Subsonic || _secrets == null) return BadRequest();
        var principal = await ConsentPrincipalAsync(session, cancellationToken);
        if (principal == null) return Unauthorized();
        await _secrets.RevokeSubsonicPlaylistGrantAsync(principal, cancellationToken);
        return Ok(ConsentStatus(null));
    }

    private async Task<AllstarrPrincipal?> ConsentPrincipalAsync(
        AdminAuthSession session,
        CancellationToken cancellationToken,
        AdminOidcBackendUser? verifiedUser = null)
    {
        if (_identityResolver == null || !session.BackendType.Equals("Subsonic", StringComparison.OrdinalIgnoreCase) ||
            !session.AllstarrUserId.HasValue) return null;
        try
        {
            var principal = await _identityResolver.ResolveAsync(
                new BackendIdentityDescriptor(
                    "subsonic",
                    session.UserId,
                    verifiedUser?.Name ?? session.UserName,
                    verifiedUser?.IsAdministrator ?? session.IsAdministrator),
                cancellationToken);
            return principal?.UserId == session.AllstarrUserId &&
                   principal.IsAdministrator == session.IsAdministrator
                ? principal
                : null;
        }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static object ConsentStatus(BackendCredentialGrant? grant) =>
        new { supported = true, granted = grant != null, updatedAt = grant?.UpdatedAt };

    public sealed class PlaylistConsentRequest
    {
        public string Password { get; set; } = string.Empty;
    }

    private async Task<IActionResult> CompleteLoginAsync(
        BackendType backend,
        string userId,
        string userName,
        bool isAdministrator,
        string accessToken,
        string? serverId,
        LoginRequest request)
    {
        var backendName = backend.ToString();
        var principal = _identityResolver == null
            ? null
            : await _identityResolver.ResolveAsync(
                new BackendIdentityDescriptor(backendName, userId, userName, isAdministrator),
                HttpContext.RequestAborted);
        if (principal == null)
            return StatusCode(503, new { error = "Native account identity is unavailable. Try again." });
        if (_pendingOidcKey != null)
        {
            try
            {
                await _oidcLinks!.LinkAsync(_pendingOidcKey, principal,
                    new(backendName.ToLowerInvariant(),
                        (backend == BackendType.Jellyfin ? _jellyfinSettings.Url : _subsonicSettings.Url)!.TrimEnd('/'),
                        userId, backend == BackendType.Jellyfin ? accessToken : request.Password!), HttpContext.RequestAborted);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or Microsoft.EntityFrameworkCore.DbUpdateException)
            {
                return Conflict(new { error = "This SSO identity or media account is already linked. Disconnect its existing link first." });
            }
            await HttpContext.SignOutAsync(AdminOidcOptions.PendingScheme);
        }
        if (backend == BackendType.Subsonic && request.ManagePlaylists)
        {
            if (_secrets == null)
                return StatusCode(503, new { error = "Playlist consent could not be saved. Try again." });
            await _secrets.StoreSubsonicPlaylistGrantAsync(principal, request.Password!, HttpContext.RequestAborted);
        }
        var session = await _sessionService.CreateSessionAsync(
            userId,
            userName,
            isAdministrator,
            accessToken,
            serverId,
            request.RememberMe,
            backendName,
            principal.UserId,
            HttpContext.RequestAborted,
            subsonicReadAuthentication: backend == BackendType.Subsonic
                ? allstarr.Services.Subsonic.SubsonicSessionAuthentication.Create(userId, request.Password!)
                : null);

        SetSessionCookie(session.SessionId, session.ExpiresAtUtc);
        _logger.LogInformation(
            "Admin WebUI login successful for {Backend} user {UserName} ({UserId})",
            backendName,
            session.UserName,
            session.UserId);
        return Ok(AuthenticatedSessionResponse(session));
    }

    private object AuthenticatedSessionResponse(AdminAuthSession session) => new
    {
        authenticated = true,
        user = new
        {
            id = session.UserId,
            name = session.UserName,
            isAdministrator = session.IsAdministrator,
            allstarrUserId = session.AllstarrUserId,
            avatarUrl = session.BackendType.Equals(
                BackendType.Jellyfin.ToString(), StringComparison.OrdinalIgnoreCase)
                ? $"/api/admin/auth/me/avatar?user={Uri.EscapeDataString(session.UserId)}"
                : null
        },
        rememberMe = session.IsPersistent,
        backend = session.BackendType,
        listenersCanConnectOwnAccounts = _providerAccountOptions.ListenersCanConnectOwnAccounts,
        features = ReleaseFeatures(),
        expiresAtUtc = session.ExpiresAtUtc
    };

    private object ReleaseFeatures() => new
    {
        intelligence = _releaseComposition.IntelligenceEnabled
    };

    public sealed class LoginRequest
    {
        public string? Username { get; set; }
        public string? Password { get; set; }
        public bool RememberMe { get; set; }
        public bool LinkOidc { get; set; }
        public bool ManagePlaylists { get; set; }
    }

    private sealed class JellyfinAuthenticateRequest
    {
        public string? Username { get; init; }
        public string? Pw { get; init; }
    }
}
