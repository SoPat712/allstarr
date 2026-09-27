using allstarr.Services.Admin;

namespace allstarr.Middleware;

/// <summary>
/// Applies the configured admin URL prefix on the local admin listener.
/// Reverse proxies may either preserve the prefix or remove it before
/// forwarding; both forms are normalized to the same request path for the
/// rest of the pipeline.
/// </summary>
public sealed class AdminBasePathMiddleware
{
    private const int AdminPort = 5275;

    private readonly RequestDelegate _next;
    private readonly AdminBasePath _basePath;

    public AdminBasePathMiddleware(RequestDelegate next, AdminBasePath basePath)
    {
        _next = next;
        _basePath = basePath ?? throw new ArgumentNullException(nameof(basePath));
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Connection.LocalPort != AdminPort || _basePath.Value.Length == 0)
        {
            await _next(context);
            return;
        }

        var configuredPrefix = _basePath.Value;
        var requestPath = context.Request.Path.Value ?? "/";

        if (string.Equals(requestPath, configuredPrefix, StringComparison.Ordinal))
        {
            // A path base without its trailing slash is ambiguous to relative
            // asset URLs. Keep the query string while making the canonical URL.
            context.Response.StatusCode = StatusCodes.Status308PermanentRedirect;
            context.Response.Headers.Location = configuredPrefix + "/" + context.Request.QueryString;
            return;
        }

        if (requestPath.StartsWith(configuredPrefix + "/", StringComparison.Ordinal))
        {
            context.Request.PathBase = configuredPrefix;
            context.Request.Path = requestPath[configuredPrefix.Length..];
        }
        else
        {
            // The proxy stripped the prefix before forwarding this request.
            // Restore it in PathBase so generated URLs and cookie paths retain
            // the public mount point without changing the route path.
            context.Request.PathBase = configuredPrefix;
        }

        await _next(context);
    }
}
