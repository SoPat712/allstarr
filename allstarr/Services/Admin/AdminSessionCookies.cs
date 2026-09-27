namespace allstarr.Services.Admin;

internal static class AdminSessionCookies
{
    public static string Path(HttpRequest request) => request.PathBase.HasValue ? request.PathBase.Value! : "/";

    public static void Write(HttpContext context, AdminAuthSession session) =>
        Write(context, session.SessionId, session.ExpiresAtUtc);

    public static void Write(HttpContext context, string sessionId, DateTime expiresAtUtc) =>
        context.Response.Cookies.Append(AdminAuthSessionService.SessionCookieName, sessionId, new CookieOptions
        {
            HttpOnly = true,
            Secure = context.Request.IsHttps,
            SameSite = SameSiteMode.Strict,
            Path = Path(context.Request),
            IsEssential = true,
            Expires = expiresAtUtc
        });

    public static void Delete(HttpContext context)
    {
        var path = Path(context.Request);
        context.Response.Cookies.Delete(AdminAuthSessionService.SessionCookieName, new CookieOptions { Path = path });
        context.Response.Cookies.Delete(AdminAuthSessionService.LegacySessionCookieName, new CookieOptions { Path = path });
        context.Response.Cookies.Delete(AdminAuthSessionService.LegacySessionCookieName,
            new CookieOptions { Path = context.Request.PathBase + "/api/admin/auth" });
    }
}
