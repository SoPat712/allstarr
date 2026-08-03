using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Providers.Spotify;
using allstarr.Core.Storage;
using allstarr.Core.Secrets;
using allstarr.Core.Settings;
using allstarr.Filters;
using allstarr.Services.Admin;
using allstarr.Models.Settings;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Controllers;

/// <summary>
/// Admin controller for scrobbling configuration and authentication.
/// Note: Does not require API key auth - users authenticate with Last.fm directly.
/// </summary>
[ApiController]
[Route("api/admin/scrobbling")]
[ServiceFilter(typeof(AdminPortFilter))]
public class ScrobblingAdminController : ControllerBase
{
    private readonly ScrobblingSettings _settings;
    private readonly ILogger<ScrobblingAdminController> _logger;
    private readonly HttpClient _httpClient;
    private readonly IDbContextFactory<AllstarrDbContext>? _contextFactory;
    private readonly IProviderAccountSecretAccessor? _accountSecrets;
    private readonly EncryptedSecretStore? _secretStore;

    public ScrobblingAdminController(
        IOptions<ScrobblingSettings> settings,
        IHttpClientFactory httpClientFactory,
        ILogger<ScrobblingAdminController> logger,
        IDbContextFactory<AllstarrDbContext>? contextFactory = null,
        IProviderAccountSecretAccessor? accountSecrets = null,
        EncryptedSecretStore? secretStore = null)
    {
        _settings = settings.Value;
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient("LastFm");
        _contextFactory = contextFactory;
        _accountSecrets = accountSecrets;
        _secretStore = secretStore;
    }

    /// <summary>
    /// Gets current scrobbling configuration status.
    /// </summary>
    [HttpGet("status")]
    public async Task<IActionResult> GetStatus(CancellationToken cancellationToken = default)
    {
        var lastFmAccount = await ReadCurrentAccountSecretsAsync("lastfm", cancellationToken);
        var listenBrainzAccount = await ReadCurrentAccountSecretsAsync("listenbrainz", cancellationToken);
        var lastFmApiKey = Secret(lastFmAccount, "apikey") ?? _settings.LastFm.ApiKey;
        var lastFmSharedSecret = Secret(lastFmAccount, "sharedsecret") ?? _settings.LastFm.SharedSecret;
        var lastFmSessionKey = Secret(lastFmAccount, "sessionkey") ?? _settings.LastFm.SessionKey;
        var listenBrainzToken = Secret(listenBrainzAccount, "token", "usertoken") ?? _settings.ListenBrainz.UserToken;
        var lastFmEnabled = lastFmAccount != null || _settings.LastFm.Enabled;
        var listenBrainzEnabled = listenBrainzAccount != null || _settings.ListenBrainz.Enabled;
        var hasApiCredentials = !string.IsNullOrEmpty(lastFmApiKey) && !string.IsNullOrEmpty(lastFmSharedSecret);

        return Ok(new
        {
            Enabled = _settings.Enabled,
            LocalTracksEnabled = _settings.LocalTracksEnabled,
            SyntheticLocalPlayedSignalEnabled = _settings.SyntheticLocalPlayedSignalEnabled,
            LastFm = new
            {
                Enabled = lastFmEnabled,
                Configured = hasApiCredentials && !string.IsNullOrEmpty(lastFmSessionKey),
                HasApiKey = hasApiCredentials,
                HasSessionKey = !string.IsNullOrEmpty(lastFmSessionKey),
                Username = Secret(lastFmAccount, "username") ?? _settings.LastFm.Username,
                Source = lastFmAccount != null ? "user_account" : "runtime_settings"
            },
            ListenBrainz = new
            {
                Enabled = listenBrainzEnabled,
                Configured = !string.IsNullOrEmpty(listenBrainzToken),
                HasUserToken = !string.IsNullOrEmpty(listenBrainzToken),
                Source = listenBrainzAccount != null ? "user_account" : "runtime_settings"
            }
        });
    }

    /// <summary>
    /// Authenticate a managed Last.fm provider account.
    /// </summary>
    [HttpPost("lastfm/authenticate")]
    public async Task<IActionResult> AuthenticateLastFm(
        [FromBody] LastFmAuthenticationRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        var managed = request?.AccountId is { } accountId
            ? await ReadOwnedLastFmAccountAsync(accountId, cancellationToken)
            : null;
        if (request?.AccountId != null && managed == null)
        {
            return NotFound(new { error = "The selected Last.fm account was not found for the signed-in user." });
        }
        if (managed == null)
        {
            return BadRequest(new
            {
                error = "Select a Last.fm provider account so the session can be stored securely."
            });
        }

        var username = request?.Username ?? Secret(managed?.Secrets, "username") ?? _settings.LastFm.Username;
        var password = request?.Password ?? _settings.LastFm.Password;
        var apiKey = Secret(managed?.Secrets, "apikey") ?? _settings.LastFm.ApiKey;
        var sharedSecret = Secret(managed?.Secrets, "sharedsecret") ?? _settings.LastFm.SharedSecret;

        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        {
            return BadRequest(new { error = "Username and password are required for the selected Last.fm provider account." });
        }

        if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(sharedSecret))
        {
            return BadRequest(new
            {
                error = "Last.fm API credentials are required. Create an application at https://www.last.fm/api/account/create " +
                        "and save its credentials on the provider account."
            });
        }

        try
        {
            // Build parameters for auth.getMobileSession
            var parameters = new Dictionary<string, string>
            {
                ["api_key"] = apiKey,
                ["method"] = "auth.getMobileSession",
                ["username"] = username,
                ["password"] = password
            };

            // Generate signature
            var signature = GenerateSignature(parameters, sharedSecret);
            parameters["api_sig"] = signature;

            // Send POST request over HTTPS
            var content = new FormUrlEncodedContent(parameters);
            using var response = await _httpClient.PostAsync("https://ws.audioscrobbler.com/2.0/", content);
            var responseBody = await response.Content.ReadAsStringAsync();

            _logger.LogInformation("Last.fm authentication response status: {StatusCode}", response.StatusCode);

            // Parse response
            var doc = XDocument.Parse(responseBody);
            var root = doc.Root;

            if (root?.Attribute("status")?.Value == "failed")
            {
                var errorElement = root.Element("error");
                var errorCode = errorElement?.Attribute("code")?.Value;
                var errorMessage = errorElement?.Value ?? "Unknown error";

                if (errorCode == "4")
                {
                    return BadRequest(new { error = "Invalid username or password" });
                }

                return BadRequest(new { error = $"Last.fm error: {errorMessage}" });
            }

            // Extract session info
            var sessionElement = root?.Element("session");
            var sessionKey = sessionElement?.Element("key")?.Value;
            var authenticatedUsername = sessionElement?.Element("name")?.Value;

            if (string.IsNullOrEmpty(sessionKey))
            {
                return BadRequest(new { error = "Failed to get session key from Last.fm response" });
            }

            _logger.LogInformation("Successfully authenticated Last.fm user: {Username}", authenticatedUsername);

            try
            {
                await SaveManagedLastFmSessionAsync(
                    managed!,
                    apiKey,
                    sharedSecret,
                    authenticatedUsername ?? username,
                    sessionKey,
                    cancellationToken);
                _logger.LogInformation("Last.fm session saved to encrypted provider account {AccountId}", managed!.Account.Id);
            }
            catch (Exception saveEx)
            {
                _logger.LogError(saveEx, "Failed to save the Last.fm session to the provider account");
                return StatusCode(500, new
                {
                    error = "Authentication succeeded but failed to save session key",
                    message = "The session key could not be persisted. Check server logs and retry."
                });
            }

            return Ok(new
            {
                Success = true,
                Username = authenticatedUsername,
                Message = "Last.fm connected. The session was stored securely and is ready now."
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error authenticating with Last.fm");
            return StatusCode(500, new { error = "Failed to authenticate with Last.fm" });
        }
    }

    public sealed class LastFmAuthenticationRequest
    {
        public Guid? AccountId { get; set; }
        public string? Username { get; set; }
        public string? Password { get; set; }
    }

    /// <summary>
    /// Test Last.fm connection with current configuration.
    /// </summary>
    [HttpPost("lastfm/test")]
    public async Task<IActionResult> TestLastFmConnection(CancellationToken cancellationToken = default)
    {
        var account = await ReadCurrentAccountSecretsAsync("lastfm", cancellationToken);
        var apiKey = Secret(account, "apikey") ?? _settings.LastFm.ApiKey;
        var sharedSecret = Secret(account, "sharedsecret") ?? _settings.LastFm.SharedSecret;
        var sessionKey = Secret(account, "sessionkey") ?? _settings.LastFm.SessionKey;
        if (account == null && !_settings.LastFm.Enabled)
        {
            return BadRequest(new { error = "Last.fm scrobbling is not enabled" });
        }

        if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(sharedSecret) || string.IsNullOrEmpty(sessionKey))
        {
            return BadRequest(new { error = "Last.fm is not fully configured (missing API key, shared secret, or session key)" });
        }

        try
        {
            // Try to get user info to test the session key
            var parameters = new Dictionary<string, string>
            {
                ["api_key"] = apiKey,
                ["method"] = "user.getInfo",
                ["sk"] = sessionKey
            };

            var signature = GenerateSignature(parameters, sharedSecret);
            parameters["api_sig"] = signature;

            var content = new FormUrlEncodedContent(parameters);
            using var response = await _httpClient.PostAsync("https://ws.audioscrobbler.com/2.0/", content, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return BuildProviderConnectionError(
                    "Last.fm",
                    response.StatusCode,
                    "Check the API key and session, then re-authenticate.");
            }

            var doc = XDocument.Parse(responseBody);
            var root = doc.Root;

            if (root?.Attribute("status")?.Value == "failed")
            {
                var errorElement = root.Element("error");
                var errorCode = errorElement?.Attribute("code")?.Value;
                var errorMessage = errorElement?.Value ?? "Unknown error";

                if (errorCode == "9")
                {
                    return BadRequest(new { error = "Session key is invalid. Please re-authenticate." });
                }

                return BadRequest(new { error = $"Last.fm error: {errorMessage}" });
            }

            var userElement = root?.Element("user");
            var username = userElement?.Element("name")?.Value;
            var playcount = userElement?.Element("playcount")?.Value;

            return Ok(new
            {
                Success = true,
                Message = "Last.fm connection successful!",
                Username = username,
                Playcount = playcount
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error testing Last.fm connection");
            return StatusCode(500, new { error = "Failed to test Last.fm connection" });
        }
    }

    /// <summary>
    /// Update local tracks scrobbling setting.
    /// </summary>
    [HttpPost("local-tracks/update")]
    public async Task<IActionResult> UpdateLocalTracksEnabled([FromBody] UpdateLocalTracksRequest request)
    {
        try
        {
            if (!HttpContext.Items.TryGetValue(AdminAuthSessionService.HttpContextSessionItemKey, out var value) ||
                value is not AdminAuthSession session || session.TenantId is not { } tenantId)
            {
                return Unauthorized(new { error = "An authenticated tenant session is required." });
            }

            var settings = HttpContext.RequestServices.GetRequiredService<IDurableRuntimeSettings>();
            var current = await settings.GetAsync(
                tenantId,
                "Scrobbling:LocalTracksEnabled",
                HttpContext.RequestAborted);
            await settings.ApplyBatchAsync(
                tenantId,
                [new RuntimeSettingWrite(
                    "Scrobbling:LocalTracksEnabled",
                    request.Enabled.ToString(),
                    current.Origin == RuntimeSettingOrigin.Durable ? current.Revision : null)],
                "admin-ui",
                session.AllstarrUserId,
                HttpContext.RequestAborted);
            _logger.LogInformation("Local tracks scrobbling setting updated to: {Enabled}", request.Enabled);

            return Ok(new
            {
                Success = true,
                LocalTracksEnabled = request.Enabled,
                Message = "Setting saved."
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update local tracks scrobbling setting");
            return StatusCode(500, new { error = "Failed to update local tracks scrobbling setting" });
        }
    }

    /// <summary>
    /// Validate ListenBrainz user token.
    /// </summary>
    [HttpPost("listenbrainz/validate")]
    public async Task<IActionResult> ValidateListenBrainzToken([FromBody] ValidateTokenRequest request)
    {
        if (string.IsNullOrEmpty(request.UserToken))
        {
            return BadRequest(new { error = "User token is required" });
        }

        try
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.listenbrainz.org/1/validate-token");
            httpRequest.Headers.Add("Authorization", $"Token {request.UserToken}");

            using var response = await _httpClient.SendAsync(httpRequest);
            var responseBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                return BuildProviderConnectionError(
                    "ListenBrainz",
                    response.StatusCode,
                    "Check the user token and save a replacement if needed.");
            }

            using var jsonDoc = System.Text.Json.JsonDocument.Parse(responseBody);
            var valid = jsonDoc.RootElement.GetProperty("valid").GetBoolean();

            if (!valid)
            {
                return BadRequest(new { error = "Invalid user token" });
            }

            var username = jsonDoc.RootElement.GetProperty("user_name").GetString();

            return Ok(new
            {
                Success = true,
                Valid = true,
                Username = username,
                Message = "Token validated. Save it through a ListenBrainz provider account."
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating ListenBrainz token");
            return StatusCode(500, new { error = "Failed to validate ListenBrainz token" });
        }
    }

    /// <summary>
    /// Test ListenBrainz connection with current configuration.
    /// </summary>
    [HttpPost("listenbrainz/test")]
    public async Task<IActionResult> TestListenBrainzConnection(CancellationToken cancellationToken = default)
    {
        var account = await ReadCurrentAccountSecretsAsync("listenbrainz", cancellationToken);
        var token = Secret(account, "token", "usertoken") ?? _settings.ListenBrainz.UserToken;
        if (account == null && !_settings.ListenBrainz.Enabled)
        {
            return BadRequest(new { error = "ListenBrainz scrobbling is not enabled" });
        }

        if (string.IsNullOrEmpty(token))
        {
            return BadRequest(new { error = "ListenBrainz user token is not configured" });
        }

        try
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.listenbrainz.org/1/validate-token");
            httpRequest.Headers.Add("Authorization", $"Token {token}");

            using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return BuildProviderConnectionError(
                    "ListenBrainz",
                    response.StatusCode,
                    "Check the user token and save a replacement if needed.");
            }

            using var jsonDoc = System.Text.Json.JsonDocument.Parse(responseBody);
            var valid = jsonDoc.RootElement.GetProperty("valid").GetBoolean();

            if (!valid)
            {
                return BadRequest(new { error = "Invalid user token" });
            }

            var username = jsonDoc.RootElement.GetProperty("user_name").GetString();

            return Ok(new
            {
                Success = true,
                Message = "ListenBrainz connection successful!",
                Username = username
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error testing ListenBrainz connection");
            return StatusCode(500, new { error = "Failed to test ListenBrainz connection" });
        }
    }

    private async Task<IReadOnlyDictionary<string, string>?> ReadCurrentAccountSecretsAsync(
        string providerId,
        CancellationToken cancellationToken)
    {
        if (_contextFactory == null || _accountSecrets == null ||
            !HttpContext.Items.TryGetValue(AdminAuthSessionService.HttpContextSessionItemKey, out var value) ||
            value is not AdminAuthSession session || session.TenantId is not { } tenant ||
            session.AllstarrUserId is not { } user)
        {
            return null;
        }

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var account = await db.ProviderAccounts.AsNoTracking()
            .Where(item => item.ProviderId == providerId && item.Enabled && item.SecretReferenceId != null &&
                item.Scope == ProviderAccountScope.User && item.TenantId == tenant && item.OwnerUserId == user)
            .OrderBy(item => item.UpdatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (account == null)
        {
            return null;
        }

        var context = new ProviderAccountContext(
            account.Id,
            account.ProviderId,
            account.Scope,
            account.Revision,
            account.Enabled,
            account.TenantId,
            account.OwnerUserId,
            account.LibraryScopeId,
            "scrobbling-admin",
            account.SecretReferenceId);
        return await _accountSecrets.UseAsync(context, bytes =>
        {
            using var document = JsonDocument.Parse(bytes);
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                var key = new string(property.Name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
                if (!string.IsNullOrWhiteSpace(key) && property.Value.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(property.Value.GetString()))
                {
                    values[key] = property.Value.GetString()!;
                }
            }
            return Task.FromResult<IReadOnlyDictionary<string, string>>(values);
        }, cancellationToken);
    }

    private sealed record ManagedLastFmAccount(
        ProviderAccountRecord Account,
        IReadOnlyDictionary<string, string> Secrets);

    private async Task<ManagedLastFmAccount?> ReadOwnedLastFmAccountAsync(
        Guid accountId,
        CancellationToken cancellationToken)
    {
        if (_contextFactory == null || _accountSecrets == null ||
            !HttpContext.Items.TryGetValue(AdminAuthSessionService.HttpContextSessionItemKey, out var value) ||
            value is not AdminAuthSession session || session.TenantId is not { } tenant ||
            session.AllstarrUserId is not { } user)
        {
            return null;
        }

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var account = await db.ProviderAccounts.AsNoTracking().SingleOrDefaultAsync(item =>
            item.Id == accountId && item.ProviderId == "lastfm" &&
            item.Scope == ProviderAccountScope.User && item.TenantId == tenant &&
            item.OwnerUserId == user && item.SecretReferenceId != null,
            cancellationToken);
        if (account == null) return null;

        var accountContext = new ProviderAccountContext(
            account.Id,
            account.ProviderId,
            account.Scope,
            account.Revision,
            account.Enabled,
            account.TenantId,
            account.OwnerUserId,
            account.LibraryScopeId,
            "lastfm-authentication",
            account.SecretReferenceId);
        var secrets = await _accountSecrets.UseAsync(accountContext, bytes =>
        {
            using var document = JsonDocument.Parse(bytes);
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                var key = new string(property.Name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
                if (property.Value.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(property.Value.GetString()))
                {
                    values[key] = property.Value.GetString()!;
                }
            }
            return Task.FromResult<IReadOnlyDictionary<string, string>>(values);
        }, cancellationToken);
        return new ManagedLastFmAccount(account, secrets);
    }

    private async Task SaveManagedLastFmSessionAsync(
        ManagedLastFmAccount managed,
        string apiKey,
        string sharedSecret,
        string username,
        string sessionKey,
        CancellationToken cancellationToken)
    {
        if (_secretStore == null)
            throw new InvalidOperationException("Encrypted provider account storage is unavailable.");

        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            apiKey,
            sharedSecret,
            username,
            sessionKey
        });
        try
        {
            await _secretStore.StoreAsync(
                managed.Account.TenantId,
                $"provider-account:lastfm:{managed.Account.Id:N}",
                payload,
                managed.Account.SecretReferenceId,
                cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }

        await using var db = await _contextFactory!.CreateDbContextAsync(cancellationToken);
        var account = await db.ProviderAccounts.SingleAsync(item => item.Id == managed.Account.Id, cancellationToken);
        account.UpdatedAt = DateTimeOffset.UtcNow;
        account.Revision++;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static string? Secret(IReadOnlyDictionary<string, string>? values, params string[] names)
    {
        if (values == null) return null;
        foreach (var name in names)
        {
            if (values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)) return value;
        }
        return null;
    }

    private IActionResult BuildProviderConnectionError(
        string provider,
        HttpStatusCode upstreamStatus,
        string credentialHint)
    {
        var status = (int)upstreamStatus;
        if (upstreamStatus is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return BadRequest(new
            {
                error = $"{provider} rejected the configured credentials (HTTP {status}). {credentialHint}"
            });
        }

        if (upstreamStatus == HttpStatusCode.TooManyRequests)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                error = $"{provider} rate-limited the connection test (HTTP {status}). Try again later."
            });
        }

        return StatusCode(StatusCodes.Status502BadGateway, new
        {
            error = $"{provider} is unavailable or returned an unexpected response (HTTP {status}). Try again later."
        });
    }

    private string GenerateSignature(Dictionary<string, string> parameters, string sharedSecret)
    {
        var sorted = parameters.OrderBy(kvp => kvp.Key);
        var signatureString = new StringBuilder();

        foreach (var kvp in sorted)
        {
            signatureString.Append(kvp.Key);
            signatureString.Append(kvp.Value);
        }

        signatureString.Append(sharedSecret);

        var bytes = Encoding.UTF8.GetBytes(signatureString.ToString());
        var hash = MD5.HashData(bytes);

        // Convert to UPPERCASE hex string (Last.fm requires uppercase)
        var sb = new StringBuilder();
        foreach (byte b in hash)
        {
            sb.Append(b.ToString("X2"));
        }
        return sb.ToString();
    }

    public class ValidateTokenRequest
    {
        public required string UserToken { get; set; }
    }

    public class UpdateLocalTracksRequest
    {
        public required bool Enabled { get; set; }
    }
}
