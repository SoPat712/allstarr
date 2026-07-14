using System.Net;
using allstarr.Services.Common;

namespace allstarr.Middleware;

/// <summary>
/// Restricts admin port (5275) access to loopback and configured trusted subnets.
/// </summary>
public class AdminNetworkAllowlistMiddleware
{
    private const int AdminPort = 5275;
    private readonly RequestDelegate _next;
    private readonly ILogger<AdminNetworkAllowlistMiddleware> _logger;
    private readonly List<IPNetwork> _trustedSubnets;
    private readonly IReadOnlySet<IPAddress> _containerGateways;

    public AdminNetworkAllowlistMiddleware(
        RequestDelegate next,
        IConfiguration configuration,
        ILogger<AdminNetworkAllowlistMiddleware> logger)
    {
        _next = next;
        _logger = logger;
        _trustedSubnets = AdminNetworkBindingPolicy.ParseTrustedSubnets(configuration);
        _containerGateways = AdminNetworkBindingPolicy.ResolveContainerGateways(configuration);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Connection.LocalPort != AdminPort)
        {
            await _next(context);
            return;
        }

        var remoteIp = context.Connection.RemoteIpAddress;
        var normalizedRemoteIp = remoteIp == null ? null : AdminNetworkBindingPolicy.NormalizeAddress(remoteIp);
        if (AdminNetworkBindingPolicy.IsRemoteIpAllowed(remoteIp, _trustedSubnets) ||
            (normalizedRemoteIp != null && _containerGateways.Contains(normalizedRemoteIp)))
        {
            await _next(context);
            return;
        }

        _logger.LogWarning("Blocked admin-port request from untrusted IP {RemoteIp} to {Path}",
            remoteIp?.ToString() ?? "(null)", context.Request.Path);

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new
        {
            error = "Access denied",
            message = "Admin UI is restricted to localhost and configured trusted subnets."
        });
    }
}
