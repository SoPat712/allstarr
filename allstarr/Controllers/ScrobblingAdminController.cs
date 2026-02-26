using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using allstarr.Filters;
using allstarr.Models.Settings;
using allstarr.Services.Admin;

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
    private readonly IConfiguration _configuration;
    private readonly ILogger<ScrobblingAdminController> _logger;
    private readonly HttpClient _httpClient;
    private readonly AdminHelperService _adminHelper;

    public ScrobblingAdminController(
        IOptions<ScrobblingSettings> settings,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        ILogger<ScrobblingAdminController> logger,
        AdminHelperService adminHelper)
    {
        _settings = settings.Value;
        _configuration = configuration;
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient("LastFm");
        _adminHelper = adminHelper;
    }

    /// <summary>
    /// Gets current scrobbling configuration status.
    /// </summary>
    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        var hasApiCredentials = !string.IsNullOrEmpty(_settings.LastFm.ApiKey) &&
                               !string.IsNullOrEmpty(_settings.LastFm.SharedSecret);

        return Ok(new
        {
            Enabled = _settings.Enabled,
            LocalTracksEnabled = _settings.LocalTracksEnabled,
            LastFm = new
            {
                Enabled = _settings.LastFm.Enabled,
                Configured = hasApiCredentials && !string.IsNullOrEmpty(_settings.LastFm.SessionKey),
                HasApiKey = hasApiCredentials,
                HasSessionKey = !string.IsNullOrEmpty(_settings.LastFm.SessionKey),
                Username = _settings.LastFm.Username,
                UsingHardcodedCredentials = hasApiCredentials &&
                    _settings.LastFm.ApiKey == LastFmSettings.DefaultApiKey
            },
            ListenBrainz = new
            {
                Enabled = _settings.ListenBrainz.Enabled,
                Configured = !string.IsNullOrEmpty(_settings.ListenBrainz.UserToken),
                HasUserToken = !string.IsNullOrEmpty(_settings.ListenBrainz.UserToken)
            }
        });
    }

    /// <summary>
    /// Authenticate with Last.fm using credentials from .env file.
    /// Uses hardcoded API credentials from Jellyfin Last.fm plugin for convenience.
    /// </summary>
    [HttpPost("lastfm/authenticate")]
    public async Task<IActionResult> AuthenticateLastFm()
    {
        // Get username and password from settings (loaded from .env)
        var username = _settings.LastFm.Username;
        var password = _settings.LastFm.Password;

        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        {
            return BadRequest(new { error = "Username and password must be set in .env file (SCROBBLING_LASTFM_USERNAME and SCROBBLING_LASTFM_PASSWORD)" });
        }

        // Check if API credentials are available
        if (string.IsNullOrEmpty(_settings.LastFm.ApiKey) || string.IsNullOrEmpty(_settings.LastFm.SharedSecret))
        {
            return BadRequest(new { error = "Last.fm API credentials not configured. This should not happen - please report this bug." });
        }

        try
        {
            // Build parameters for auth.getMobileSession
            var parameters = new Dictionary<string, string>
            {
                ["api_key"] = _settings.LastFm.ApiKey,
                ["method"] = "auth.getMobileSession",
                ["username"] = username,
                ["password"] = password
            };

            // Generate signature
            var signature = GenerateSignature(parameters, _settings.LastFm.SharedSecret);
            parameters["api_sig"] = signature;

            // Send POST request over HTTPS
            var content = new FormUrlEncodedContent(parameters);
            var response = await _httpClient.PostAsync("https://ws.audioscrobbler.com/2.0/", content);
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

            // Save session key to .env file
            try
            {
                var updates = new Dictionary<string, string>
                {
                    ["SCROBBLING_LASTFM_SESSION_KEY"] = sessionKey
                };

                await _adminHelper.UpdateEnvConfigAsync(updates);
                _logger.LogInformation("Session key saved to .env file");
            }
            catch (Exception saveEx)
            {
                _logger.LogError(saveEx, "Failed to save session key to .env file");
                return StatusCode(500, new {
                    error = "Authentication succeeded but failed to save session key",
                    message = "The session key could not be persisted. Check server logs and retry."
                });
            }

            return Ok(new
            {
                Success = true,
                Username = authenticatedUsername,
                Message = "Authentication successful! Session key saved. Please restart the container for changes to take effect."
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error authenticating with Last.fm");
            return StatusCode(500, new { error = "Failed to authenticate with Last.fm" });
        }
    }

    /// <summary>
    /// DEPRECATED: OAuth method - use /authenticate instead for simpler username/password auth.
    /// Step 1: Get Last.fm authentication URL for user to authorize the app.
    /// </summary>
    [HttpGet("lastfm/auth-url")]
    public IActionResult GetLastFmAuthUrl()
    {
        return BadRequest(new {
            error = "OAuth authentication is deprecated. Use POST /lastfm/authenticate with username and password instead.",
            hint = "This is simpler and doesn't require a callback URL."
        });
    }

    /// <summary>
    /// DEPRECATED: OAuth method - use /authenticate instead.
    /// Step 2: Exchange Last.fm auth token for session key.
    /// </summary>
    [HttpPost("lastfm/get-session")]
    public IActionResult GetLastFmSession([FromBody] GetSessionRequest request)
    {
        return BadRequest(new {
            error = "OAuth authentication is deprecated. Use POST /lastfm/authenticate with username and password instead.",
            hint = "This is simpler and doesn't require a callback URL."
        });
    }

    /// <summary>
    /// Test Last.fm connection with current configuration.
    /// </summary>
    [HttpPost("lastfm/test")]
    public async Task<IActionResult> TestLastFmConnection()
    {
        if (!_settings.LastFm.Enabled)
        {
            return BadRequest(new { error = "Last.fm scrobbling is not enabled" });
        }

        if (string.IsNullOrEmpty(_settings.LastFm.ApiKey) ||
            string.IsNullOrEmpty(_settings.LastFm.SharedSecret) ||
            string.IsNullOrEmpty(_settings.LastFm.SessionKey))
        {
            return BadRequest(new { error = "Last.fm is not fully configured (missing API key, shared secret, or session key)" });
        }

        try
        {
            // Try to get user info to test the session key
            var parameters = new Dictionary<string, string>
            {
                ["api_key"] = _settings.LastFm.ApiKey,
                ["method"] = "user.getInfo",
                ["sk"] = _settings.LastFm.SessionKey
            };

            var signature = GenerateSignature(parameters, _settings.LastFm.SharedSecret);
            parameters["api_sig"] = signature;

            var content = new FormUrlEncodedContent(parameters);
            var response = await _httpClient.PostAsync("https://ws.audioscrobbler.com/2.0/", content);
            var responseBody = await response.Content.ReadAsStringAsync();

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
            var updates = new Dictionary<string, string>
            {
                ["SCROBBLING_LOCAL_TRACKS_ENABLED"] = request.Enabled.ToString().ToLower()
            };

            await _adminHelper.UpdateEnvConfigAsync(updates);
            _logger.LogInformation("Local tracks scrobbling setting updated to: {Enabled}", request.Enabled);

            return Ok(new
            {
                Success = true,
                LocalTracksEnabled = request.Enabled,
                Message = "Setting saved! Please restart the container for changes to take effect."
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
            var httpRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.listenbrainz.org/1/validate-token");
            httpRequest.Headers.Add("Authorization", $"Token {request.UserToken}");

            var response = await _httpClient.SendAsync(httpRequest);
            var responseBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                return BadRequest(new { error = "Invalid user token" });
            }

            var jsonDoc = System.Text.Json.JsonDocument.Parse(responseBody);
            var valid = jsonDoc.RootElement.GetProperty("valid").GetBoolean();

            if (!valid)
            {
                return BadRequest(new { error = "Invalid user token" });
            }

            var username = jsonDoc.RootElement.GetProperty("user_name").GetString();

            // Save token to .env file
            try
            {
                var updates = new Dictionary<string, string>
                {
                    ["SCROBBLING_LISTENBRAINZ_USER_TOKEN"] = request.UserToken
                };

                await _adminHelper.UpdateEnvConfigAsync(updates);
                _logger.LogInformation("ListenBrainz token saved to .env file");
            }
            catch (Exception saveEx)
            {
                _logger.LogError(saveEx, "Failed to save token to .env file");
                return StatusCode(500, new {
                    error = "Token validation succeeded but failed to save",
                    username = username,
                    message = "The token could not be persisted. Check server logs and retry."
                });
            }

            return Ok(new
            {
                Success = true,
                Valid = true,
                Username = username,
                Message = "Token validated and saved! Please restart the container for changes to take effect."
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
    public async Task<IActionResult> TestListenBrainzConnection()
    {
        if (!_settings.ListenBrainz.Enabled)
        {
            return BadRequest(new { error = "ListenBrainz scrobbling is not enabled" });
        }

        if (string.IsNullOrEmpty(_settings.ListenBrainz.UserToken))
        {
            return BadRequest(new { error = "ListenBrainz user token is not configured" });
        }

        try
        {
            var httpRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.listenbrainz.org/1/validate-token");
            httpRequest.Headers.Add("Authorization", $"Token {_settings.ListenBrainz.UserToken}");

            var response = await _httpClient.SendAsync(httpRequest);
            var responseBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                return BadRequest(new { error = "Invalid user token" });
            }

            var jsonDoc = System.Text.Json.JsonDocument.Parse(responseBody);
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

    public class GetSessionRequest
    {
        public required string Token { get; set; }
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
