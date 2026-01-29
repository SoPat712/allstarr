using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using allstarr.Models.Settings;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace allstarr.Filters;

/// <summary>
/// Authentication filter for Jellyfin API endpoints.
/// Validates client credentials against configured username and API key.
/// Clients can authenticate via:
/// - Authorization header: MediaBrowser Token="apikey"
/// - X-Emby-Token header
/// - Query parameter: api_key
/// - JSON body (for login endpoints): Username/Pw fields
/// </summary>
public partial class JellyfinAuthFilter : IAsyncActionFilter
{
    private readonly JellyfinSettings _settings;
    private readonly ILogger<JellyfinAuthFilter> _logger;

    public JellyfinAuthFilter(
        IOptions<JellyfinSettings> settings,
        ILogger<JellyfinAuthFilter> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        // Skip auth if no credentials configured (open mode)
        if (string.IsNullOrEmpty(_settings.ClientUsername) || string.IsNullOrEmpty(_settings.ApiKey))
        {
            _logger.LogDebug("Auth skipped - no client credentials configured");
            await next();
            return;
        }

        var request = context.HttpContext.Request;
        
        // Try to extract credentials from various sources
        var (username, token) = await ExtractCredentialsAsync(request);

        // Validate credentials
        if (!ValidateCredentials(username, token))
        {
            _logger.LogWarning("Authentication failed for user '{Username}' from {IP}", 
                username ?? "unknown", 
                context.HttpContext.Connection.RemoteIpAddress);
            
            context.Result = new UnauthorizedObjectResult(new
            {
                error = "Invalid credentials",
                message = "Authentication required. Provide valid username and API key."
            });
            return;
        }

        _logger.LogDebug("Authentication successful for user '{Username}'", username);
        await next();
    }

    private async Task<(string? username, string? token)> ExtractCredentialsAsync(HttpRequest request)
    {
        string? username = null;
        string? token = null;

        // 1. Check Authorization header (MediaBrowser format)
        if (request.Headers.TryGetValue("Authorization", out var authHeader))
        {
            var authValue = authHeader.ToString();
            
            // Parse MediaBrowser auth header: MediaBrowser Client="...", Token="..."
            if (authValue.StartsWith("MediaBrowser", StringComparison.OrdinalIgnoreCase))
            {
                token = ExtractTokenFromMediaBrowser(authValue);
                username = ExtractUserIdFromMediaBrowser(authValue);
            }
            // Basic auth: Basic base64(username:password)
            else if (authValue.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            {
                (username, token) = ParseBasicAuth(authValue);
            }
        }

        // 2. Check X-Emby-Token header
        if (string.IsNullOrEmpty(token) && request.Headers.TryGetValue("X-Emby-Token", out var embyToken))
        {
            token = embyToken.ToString();
        }

        // 3. Check X-MediaBrowser-Token header
        if (string.IsNullOrEmpty(token) && request.Headers.TryGetValue("X-MediaBrowser-Token", out var mbToken))
        {
            token = mbToken.ToString();
        }

        // 4. Check X-Emby-Authorization header (alternative format)
        if (string.IsNullOrEmpty(token) && request.Headers.TryGetValue("X-Emby-Authorization", out var embyAuth))
        {
            token = ExtractTokenFromMediaBrowser(embyAuth.ToString());
            if (string.IsNullOrEmpty(username))
            {
                username = ExtractUserIdFromMediaBrowser(embyAuth.ToString());
            }
        }

        // 5. Check query parameters
        if (string.IsNullOrEmpty(token))
        {
            token = request.Query["api_key"].FirstOrDefault() 
                 ?? request.Query["ApiKey"].FirstOrDefault()
                 ?? request.Query["X-Emby-Token"].FirstOrDefault();
        }

        if (string.IsNullOrEmpty(username))
        {
            username = request.Query["userId"].FirstOrDefault()
                    ?? request.Query["UserId"].FirstOrDefault()
                    ?? request.Query["u"].FirstOrDefault();
        }

        // 6. Check JSON body for login endpoints (Jellyfin: Username/Pw, Navidrome: username/password)
        if ((string.IsNullOrEmpty(username) || string.IsNullOrEmpty(token)) && 
            request.ContentType?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true &&
            request.ContentLength > 0)
        {
            var (bodyUsername, bodyPassword) = await ExtractCredentialsFromBodyAsync(request);
            if (string.IsNullOrEmpty(username)) username = bodyUsername;
            if (string.IsNullOrEmpty(token)) token = bodyPassword;
        }

        return (username, token);
    }

    private async Task<(string? username, string? password)> ExtractCredentialsFromBodyAsync(HttpRequest request)
    {
        try
        {
            request.EnableBuffering();
            request.Body.Position = 0;
            
            using var reader = new StreamReader(request.Body, leaveOpen: true);
            var body = await reader.ReadToEndAsync();
            request.Body.Position = 0;

            if (string.IsNullOrEmpty(body)) return (null, null);

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            // Try Jellyfin format: Username, Pw
            string? username = null;
            string? password = null;

            if (root.TryGetProperty("Username", out var usernameProp))
                username = usernameProp.GetString();
            else if (root.TryGetProperty("username", out var usernameLowerProp))
                username = usernameLowerProp.GetString();

            if (root.TryGetProperty("Pw", out var pwProp))
                password = pwProp.GetString();
            else if (root.TryGetProperty("pw", out var pwLowerProp))
                password = pwLowerProp.GetString();
            else if (root.TryGetProperty("Password", out var passwordProp))
                password = passwordProp.GetString();
            else if (root.TryGetProperty("password", out var passwordLowerProp))
                password = passwordLowerProp.GetString();

            return (username, password);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to parse credentials from request body");
            return (null, null);
        }
    }

    private string? ExtractTokenFromMediaBrowser(string header)
    {
        var match = TokenRegex().Match(header);
        return match.Success ? match.Groups[1].Value : null;
    }

    private string? ExtractUserIdFromMediaBrowser(string header)
    {
        var match = UserIdRegex().Match(header);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static (string? username, string? password) ParseBasicAuth(string authHeader)
    {
        try
        {
            var base64 = authHeader["Basic ".Length..].Trim();
            var bytes = Convert.FromBase64String(base64);
            var credentials = System.Text.Encoding.UTF8.GetString(bytes);
            var parts = credentials.Split(':', 2);
            
            return parts.Length == 2 ? (parts[0], parts[1]) : (null, null);
        }
        catch
        {
            return (null, null);
        }
    }

    private bool ValidateCredentials(string? username, string? token)
    {
        // Must have token (API key used as password)
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        // Token must match API key
        if (!string.Equals(token, _settings.ApiKey, StringComparison.Ordinal))
        {
            return false;
        }

        // If username provided, it must match configured client username
        if (!string.IsNullOrEmpty(username) && 
            !string.Equals(username, _settings.ClientUsername, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    [GeneratedRegex(@"Token=""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"UserId=""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex UserIdRegex();
}
