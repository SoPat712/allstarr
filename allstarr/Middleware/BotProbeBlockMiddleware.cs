using allstarr.Services.Common;

namespace allstarr.Middleware;

/// <summary>
/// Short-circuits common internet scanner paths before they reach the Jellyfin proxy.
/// </summary>
public class BotProbeBlockMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<BotProbeBlockMiddleware> _logger;

    public BotProbeBlockMiddleware(
        RequestDelegate next,
        ILogger<BotProbeBlockMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var requestPath = context.Request.Path.Value;
        if (!BotProbeDetector.IsHighConfidenceProbePath(requestPath))
        {
            await _next(context);
            return;
        }

        _logger.LogDebug("Short-circuited likely bot probe from {RemoteIp}: {Method} {Path}",
            context.Connection.RemoteIpAddress?.ToString() ?? "(null)",
            context.Request.Method,
            requestPath);

        context.Response.StatusCode = StatusCodes.Status404NotFound;
    }
}
