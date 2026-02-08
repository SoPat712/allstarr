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
        if (string.IsNullOrEmpty(apiKey) || !string.Equals(apiKey, _settings.ApiKey, StringComparison.Ordinal))
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

        _logger.LogDebug("API key authentication successful for {Path}", request.Path);
        await next();
    }
}
