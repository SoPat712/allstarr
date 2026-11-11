using allstarr.Core.ClientApi;
using allstarr.Core.ClientApiV2;

namespace allstarr.Middleware;

/// <summary>
/// Prepares native client API requests: the token travels only in <c>Authorization: Bearer</c>, never in the url where
/// logs and proxies keep it, and artwork is handed to the existing image route so resizing, caching and access checks
/// stay in one place. Runs after the client API checks and before routing.
/// </summary>
public sealed class V2RequestMiddleware(RequestDelegate next)
{
    private static readonly string[] QueryCredentials = ["api_key", "access_token", "ApiKey"];

    public async Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        if (!request.Path.StartsWithSegments("/allstarr/v2"))
        {
            await next(context);
            return;
        }
        if (QueryCredentials.Any(name => request.Query.ContainsKey(name)))
        {
            await ClientApiError.WriteAsync(context, 401);
            return;
        }
        // The media server reads only its own header format; a token-only header keeps the device the token belongs to.
        var authorization = request.Headers.Authorization.ToString();
        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var token = authorization["Bearer ".Length..].Trim();
            if (token.Length is 0 or > 512 || !token.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'))
            {
                await ClientApiError.WriteAsync(context, 401);
                return;
            }
            request.Headers.Authorization = $"MediaBrowser Token=\"{token}\"";
        }
        if (!request.Path.StartsWithSegments(V2ArtworkUrls.PathPrefix.TrimEnd('/'), out var remaining))
        {
            await next(context);
            return;
        }
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method))
        {
            await ClientApiError.WriteAsync(context, 404);
            return;
        }
        var key = remaining.Value?.TrimStart('/') ?? "";
        var target = key.Contains('/') ? null : V2ArtworkUrls.Resolve(key, request.Query["w"]);
        if (target == null)
        {
            await ClientApiError.WriteAsync(context, 404);
            return;
        }
        request.Path = target.Value.Path;
        request.QueryString = target.Value.Query;
        await next(context);
    }
}
