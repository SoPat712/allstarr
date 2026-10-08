using System.Text.Json;
using System.Text.RegularExpressions;
using allstarr.Services.Admin;

namespace allstarr.Middleware;

/// <summary>
/// Enforces backend-authenticated local sessions for admin API endpoints on port 5275.
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

        var session = await _sessionService.GetValidSessionAsync(context.Request, context.RequestAborted);
        if (session is null)
        {
            AdminSessionCookies.Delete(context);
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

        if (path.StartsWith("/api/admin/track-matches", StringComparison.OrdinalIgnoreCase))
        {
            var segments = path.TrimEnd('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 3 || !segments[2].Equals("track-matches", StringComparison.OrdinalIgnoreCase)) return false;
            if (HttpMethods.IsGet(method))
                return segments.Length == 3 || segments.Length == 5 && (
                    segments[3].Equals("targets", StringComparison.OrdinalIgnoreCase) && segments[4] is "local" or "provider" ||
                    segments[3].Equals("spotify", StringComparison.OrdinalIgnoreCase) ||
                    Guid.TryParse(segments[3], out _) && segments[4].Equals("artwork", StringComparison.OrdinalIgnoreCase));
            if (segments.Length < 5 || !Guid.TryParse(segments[3], out _)) return false;
            if (HttpMethods.IsPost(method) && segments.Length == 5 && segments[4] is "resolve" or "rematch") return true;
            return segments.Length >= 6 && segments[4].Equals("manual-authorities", StringComparison.OrdinalIgnoreCase) &&
                Guid.TryParse(segments[5], out _) && (
                    segments.Length == 6 && HttpMethods.IsDelete(method) ||
                    segments.Length == 7 && segments[6].Equals("rematch", StringComparison.OrdinalIgnoreCase) && HttpMethods.IsPost(method));
        }

        if (path.TrimEnd('/').Equals("/api/admin/preferences", StringComparison.OrdinalIgnoreCase))
            return HttpMethods.IsGet(method) || HttpMethods.IsPut(method) || HttpMethods.IsDelete(method);

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
            (path.Equals("/api/admin/ui/schema", StringComparison.OrdinalIgnoreCase) ||
             path.Equals("/api/admin/ui/home", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (HttpMethods.IsGet(method) &&
            path.Equals("/api/admin/updates/stream", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (IsProviderAccountSelfServiceRoute(path, method))
        {
            return true;
        }

        if (HttpMethods.IsPost(method) &&
            path.TrimEnd('/').Equals("/api/admin/scrobbling/lastfm/authenticate", StringComparison.OrdinalIgnoreCase))
            return true;

        if (IsPlaylistSelfServiceRoute(path))
        {
            return true;
        }

        if ((path.Equals("/api/admin/favorite-action-policies", StringComparison.OrdinalIgnoreCase) && HttpMethods.IsGet(method)) ||
            (path.Equals("/api/admin/favorite-action-policies/me", StringComparison.OrdinalIgnoreCase) && HttpMethods.IsPut(method)))
        {
            return true;
        }

        if (path.Equals("/api/admin/intelligence", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/admin/intelligence/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (path.Equals("/api/admin/jobs", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/admin/jobs/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static bool IsPlaylistSelfServiceRoute(string path)
    {
        var normalizedPath = path.Length > 1 ? path.TrimEnd('/') : path;
        return new[]
        {
            "/api/admin/library-index",
            "/api/admin/playlist-links",
            "/api/admin/playlist-sources",
            "/api/admin/media-targets"
        }.Any(root => normalizedPath.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                      normalizedPath.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsProviderAccountSelfServiceRoute(string path, string method)
    {
        const string root = "/api/admin/provider-accounts";
        var normalizedPath = path.Length > 1 ? path.TrimEnd('/') : path;
        if (normalizedPath.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            return HttpMethods.IsGet(method) || HttpMethods.IsPost(method);
        }

        if (!normalizedPath.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var segments = normalizedPath[(root.Length + 1)..]
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 1 && Guid.TryParse(segments[0], out _))
        {
            return HttpMethods.IsDelete(method) || HttpMethods.IsPatch(method);
        }

        return segments.Length == 2 &&
               Guid.TryParse(segments[0], out _) &&
               (segments[1].Equals("secret", StringComparison.OrdinalIgnoreCase) ||
                segments[1].Equals("audience", StringComparison.OrdinalIgnoreCase)) &&
               HttpMethods.IsPut(method);
    }

    private async Task WriteUnauthorizedResponse(HttpContext context)
    {
        _logger.LogInformation(
            "AdminAuthenticationMiddleware rejected unauthenticated request to {Path}; sessionCookiePresent={SessionCookiePresent}",
            context.Request.Path,
            context.Request.Headers.Cookie.Any(value =>
                value?.Contains(AdminAuthSessionService.SessionCookieName, StringComparison.Ordinal) == true ||
                value?.Contains(AdminAuthSessionService.LegacySessionCookieName, StringComparison.Ordinal) == true));

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            error = "Authentication required",
            message = "Please sign in with your configured media-server account."
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
            message = "This action is restricted to media-server administrators."
        }));
    }
}
