using System.Text.Json;
using System.Text.RegularExpressions;
using allstarr.Services.Admin;

namespace allstarr.Middleware;

/// <summary>
/// Enforces Jellyfin-authenticated local sessions for admin API endpoints on port 5275.
/// </summary>
public class AdminAuthenticationMiddleware
{
    private const int AdminPort = 5275;
    private static readonly Regex PlaylistLinkRoute = new(
        @"^/api/admin/jellyfin/playlists/[^/]+/(link|unlink)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly RequestDelegate _next;
    private readonly AdminAuthSessionService _sessionService;
    private readonly ILogger<AdminAuthenticationMiddleware> _logger;

    public AdminAuthenticationMiddleware(
        RequestDelegate next,
        AdminAuthSessionService sessionService,
        ILogger<AdminAuthenticationMiddleware> logger)
    {
        _next = next;
        _sessionService = sessionService;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        if (!path.StartsWith("/api/admin", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        // Keep 404 behavior from AdminPortFilter for non-admin-port requests.
        if (context.Connection.LocalPort != AdminPort)
        {
            await _next(context);
            return;
        }

        if (path.StartsWith("/api/admin/auth", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        if (!context.Request.Cookies.TryGetValue(AdminAuthSessionService.SessionCookieName, out var sessionId) ||
            !_sessionService.TryGetValidSession(sessionId, out var session))
        {
            context.Response.Cookies.Delete(AdminAuthSessionService.SessionCookieName);
            await WriteUnauthorizedResponse(context);
            return;
        }

        context.Items[AdminAuthSessionService.HttpContextSessionItemKey] = session;

        if (!session.IsAdministrator && !IsAllowedForNonAdministrator(context.Request))
        {
            await WriteForbiddenResponse(context);
            return;
        }

        await _next(context);
    }

    private static bool IsAllowedForNonAdministrator(HttpRequest request)
    {
        var path = request.Path.Value ?? string.Empty;
        var method = request.Method;

        if (HttpMethods.IsGet(method) &&
            path.Equals("/api/admin/jellyfin/playlists", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (HttpMethods.IsPost(method) || HttpMethods.IsDelete(method))
        {
            if (PlaylistLinkRoute.IsMatch(path))
            {
                return true;
            }
        }

        if (HttpMethods.IsGet(method) &&
            path.Equals("/api/admin/spotify/user-playlists", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private async Task WriteUnauthorizedResponse(HttpContext context)
    {
        _logger.LogDebug("AdminAuthenticationMiddleware rejected unauthenticated request to {Path}",
            context.Request.Path);

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            error = "Authentication required",
            message = "Please sign in with your Jellyfin account."
        }));
    }

    private async Task WriteForbiddenResponse(HttpContext context)
    {
        _logger.LogDebug("AdminAuthenticationMiddleware rejected unauthorized request to {Path}",
            context.Request.Path);

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            error = "Administrator permissions required",
            message = "This action is restricted to Jellyfin administrators."
        }));
    }
}
