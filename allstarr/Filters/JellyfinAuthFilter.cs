using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using allstarr.Models.Settings;

namespace allstarr.Filters;

/// <summary>
/// Legacy no-op filter retained for compatibility.
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
        _logger.LogTrace("JellyfinAuthFilter: Transparent proxy mode - no authentication check");

        await next();
    }
}
