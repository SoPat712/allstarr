using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace allstarr.Filters;

/// <summary>
/// Filter that restricts access to admin endpoints to only the admin port (5275).
/// This prevents the admin API from being accessed through the main proxy port.
/// </summary>
public class AdminPortFilter : IActionFilter
{
    private const int AdminPort = 5275;
    private readonly ILogger<AdminPortFilter> _logger;

    public AdminPortFilter(ILogger<AdminPortFilter> logger)
    {
        _logger = logger;
    }

    public void OnActionExecuting(ActionExecutingContext context)
    {
        var requestPort = context.HttpContext.Connection.LocalPort;

        _logger.LogDebug("AdminPortFilter: Request to {Path} on port {Port} (admin port is {AdminPort})",
            context.HttpContext.Request.Path, requestPort, AdminPort);

        if (requestPort != AdminPort)
        {
            _logger.LogWarning("Admin endpoint {Path} accessed on wrong port {Port}, rejecting",
                context.HttpContext.Request.Path, requestPort);
            context.Result = new NotFoundResult();
        }
    }

    public void OnActionExecuted(ActionExecutedContext context)
    {
        // No action needed after execution
    }
}
