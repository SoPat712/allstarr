using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using allstarr.Models.Settings;

namespace allstarr.Filters;

/// <summary>
/// Simple API key authentication filter for admin endpoints.
/// Validates against Jellyfin API key via query parameter or header.
/// </summary>
public class ApiKeyAuthFilter : IAsyncActionFilter
{
    private readonly JellyfinSettings _settings;
    private readonly ILogger<ApiKeyAuthFilter> _logger;

    public ApiKeyAuthFilter(
        IOptions<JellyfinSettings> settings,
        ILogger<ApiKeyAuthFilter> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var request = context.HttpContext.Request;
        
        // Extract API key from query parameter or header
        var apiKey = request.Query["api_key"].FirstOrDefault()
                  ?? request.Headers["X-Api-Key"].FirstOrDefault()
                  ?? request.Headers["X-Emby-Token"].FirstOrDefault();

        // Validate API key
        if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(_settings.ApiKey) || !FixedTimeEquals(apiKey, _settings.ApiKey))
        {
            _logger.LogWarning("Unauthorized access attempt to {Path} from {IP}", 
                request.Path, 
                context.HttpContext.Connection.RemoteIpAddress);
            
            context.Result = new UnauthorizedObjectResult(new
            {
                error = "Unauthorized",
                message = "Valid API key required. Provide via ?api_key=YOUR_KEY or X-Api-Key header."
            });
            return;
        }

        _logger.LogInformation("API key authentication successful for {Path}", request.Path);
        await next();
    }

    // Use a robust constant-time comparison by comparing fixed-length hashes of the inputs.
    // This avoids leaking lengths and uses the platform's fixed-time compare helper.
    private static bool FixedTimeEquals(string a, string b)
    {
        if (a == null || b == null) return false;

        // Compute SHA-256 hashes and compare them in constant time
        using var sha = System.Security.Cryptography.SHA256.Create();
        var aHash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(a));
        var bHash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(b));

        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(aHash, bHash);
    }
}
