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

        if (path.Equals("/api/admin/auth", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/admin/auth/", StringComparison.OrdinalIgnoreCase))
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
            var matchSegments = path.TrimEnd('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (matchSegments.Length < 3 || !matchSegments[2].Equals("track-matches", StringComparison.OrdinalIgnoreCase)) return false;
            if (HttpMethods.IsGet(method))
                return matchSegments.Length == 3 || matchSegments.Length == 5 && (
                    matchSegments[3].Equals("targets", StringComparison.OrdinalIgnoreCase) && matchSegments[4] is "local" or "provider" ||
                    matchSegments[3].Equals("spotify", StringComparison.OrdinalIgnoreCase) ||
                    Guid.TryParse(matchSegments[3], out _) && matchSegments[4].Equals("artwork", StringComparison.OrdinalIgnoreCase));
            if (matchSegments.Length < 5 || !Guid.TryParse(matchSegments[3], out _)) return false;
            if (HttpMethods.IsPost(method) && matchSegments.Length == 5 && matchSegments[4] is "resolve" or "rematch") return true;
            return matchSegments.Length >= 6 && matchSegments[4].Equals("manual-authorities", StringComparison.OrdinalIgnoreCase) &&
                Guid.TryParse(matchSegments[5], out _) && (
                    matchSegments.Length == 6 && HttpMethods.IsDelete(method) ||
                    matchSegments.Length == 7 && matchSegments[6].Equals("rematch", StringComparison.OrdinalIgnoreCase) && HttpMethods.IsPost(method));
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
             path.Equals("/api/admin/ui/home", StringComparison.OrdinalIgnoreCase) ||
             path.Equals("/api/admin/ui/activity", StringComparison.OrdinalIgnoreCase) ||
             path.Equals("/api/admin/ui/now-playing", StringComparison.OrdinalIgnoreCase)))
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

        if (IsPlaylistSelfServiceRoute(path, method))
        {
            return true;
        }

        if ((path.Equals("/api/admin/favorite-action-policies", StringComparison.OrdinalIgnoreCase) && HttpMethods.IsGet(method)) ||
            (path.Equals("/api/admin/favorite-action-policies/me", StringComparison.OrdinalIgnoreCase) && HttpMethods.IsPut(method)))
        {
            return true;
        }

        var routeSegments = path.TrimEnd('/').Split('/');
        if (routeSegments.Length >= 4 && routeSegments[3].Equals("jobs", StringComparison.OrdinalIgnoreCase))
            return HttpMethods.IsGet(method) && (routeSegments.Length == 4 ||
                       routeSegments.Length == 5 && Guid.TryParse(routeSegments[4], out _)) ||
                   HttpMethods.IsPost(method) && routeSegments.Length == 6 &&
                   Guid.TryParse(routeSegments[4], out _) && routeSegments[5] == "cancel";

        if (HttpMethods.IsGet(method) && routeSegments.Length == 6 && routeSegments[3] == "downloads" &&
            routeSegments[4] == "artwork" && !string.IsNullOrWhiteSpace(routeSegments[5])) return true;

        return false;
    }

    private static bool IsPlaylistSelfServiceRoute(string path, string method)
    {
        var segments = path.TrimEnd('/').Split('/');
        if (segments.Length < 4) return false;
        var root = segments[3].ToLowerInvariant();
        if (root == "library-index")
            return segments.Length == 5 && (segments[4] == "counts" && HttpMethods.IsGet(method) ||
                                            segments[4] == "enqueue" && HttpMethods.IsPost(method));
        if (root is "playlist-sources" or "media-targets")
        {
            if (!HttpMethods.IsGet(method)) return false;
            return segments.Length == 4 || segments.Length >= 6 && Guid.TryParse(segments[4], out _) &&
                segments[5] == "playlists" && (segments.Length == 6 ||
                    segments.Length == 8 && !string.IsNullOrWhiteSpace(segments[6]) && segments[7] == "artwork");
        }
        if (root != "playlist-links") return false;
        if (segments.Length == 4) return HttpMethods.IsGet(method) || HttpMethods.IsPost(method);
        if (Guid.TryParse(segments[4], out _))
            return segments.Length == 5 && (HttpMethods.IsGet(method) || HttpMethods.IsPut(method) || HttpMethods.IsDelete(method)) ||
                segments.Length == 6 && (HttpMethods.IsPost(method) && segments[5] is "refresh" or "run" or "schedules" ||
                    HttpMethods.IsPatch(method) && segments[5] == "state" || HttpMethods.IsGet(method) && segments[5] == "preview");
        return segments.Length == 6 && (
                segments[4] == "rematch" && (segments[5] == "preview" && HttpMethods.IsGet(method) ||
                                             segments[5] == "apply" && HttpMethods.IsPost(method)) ||
                segments[4] == "schedules" && Guid.TryParse(segments[5], out _) && HttpMethods.IsPut(method)) ||
            segments.Length == 7 && segments[4] == "matches" && (
                Guid.TryParse(segments[5], out _) && segments[6] == "override" && HttpMethods.IsPost(method) ||
                segments[5] == "overrides" && Guid.TryParse(segments[6], out _) && HttpMethods.IsDelete(method));
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
