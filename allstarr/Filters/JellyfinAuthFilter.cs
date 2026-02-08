using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using allstarr.Models.Settings;

namespace allstarr.Filters;

/// <summary>
/// REMOVED: Authentication filter for Jellyfin API endpoints.
/// 
/// This filter has been removed because Allstarr acts as a TRANSPARENT PROXY.
/// Clients authenticate directly with Jellyfin through the proxy, not with the proxy itself.
/// 
/// Authentication flow:
/// 1. Client sends credentials to /Users/AuthenticateByName
/// 2. Proxy forwards request to Jellyfin (no validation)
/// 3. Jellyfin validates credentials and returns AccessToken
/// 4. Client uses AccessToken in subsequent requests
/// 5. Proxy forwards token to Jellyfin for validation
/// 
/// The proxy NEVER validates credentials or tokens - that's Jellyfin's job.
/// The proxy only forwards authentication headers transparently.
/// 
/// If you need to restrict access to the proxy itself, use network-level controls
/// (firewall, VPN, reverse proxy with auth) instead of application-level auth.
/// </summary>
public class JellyfinAuthFilter : IAsyncActionFilter
{
    private readonly ILogger<JellyfinAuthFilter> _logger;

    public JellyfinAuthFilter(ILogger<JellyfinAuthFilter> logger)
    {
        _logger = logger;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        // This filter is now a no-op - all authentication is handled by Jellyfin
        // Keeping the class for backwards compatibility but it does nothing
        
        _logger.LogTrace("JellyfinAuthFilter: Transparent proxy mode - no authentication check");
        
        await next();
    }
}
