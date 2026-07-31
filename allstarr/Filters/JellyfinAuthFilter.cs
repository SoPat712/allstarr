using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using allstarr.Services.Jellyfin;
using allstarr.Services.Common;
using allstarr.Core.Identity;

namespace allstarr.Filters;

/// <summary>
/// Verifies client credentials with Jellyfin before controller actions can synthesize work.
/// </summary>
public class JellyfinAuthFilter : IAsyncActionFilter
{
    public const string BackendPrincipalIdItemKey = "allstarr.backend-principal-id";

    private static readonly string[] AllowedQueryCredentialNames = ["api_key", "access_token", "ApiKey"];

    private readonly JellyfinProxyService _proxyService;
    private readonly BackendIdentityResolver _identityResolver;
    private readonly ILogger<JellyfinAuthFilter> _logger;

    public JellyfinAuthFilter(
        JellyfinProxyService proxyService,
        BackendIdentityResolver identityResolver,
        ILogger<JellyfinAuthFilter> logger)
    {
        _proxyService = proxyService;
        _identityResolver = identityResolver;
        _logger = logger;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var request = context.HttpContext.Request;
        if (IsPublicBootstrapRequest(request))
        {
            await next();
            return;
        }

        if (!HasClientCredentials(request))
        {
            _logger.LogDebug("Rejected Jellyfin request without client credentials for {Path}", request.Path);
            context.Result = new UnauthorizedResult();
            return;
        }

        try
        {
            var endpoint = BuildCurrentUserEndpoint(request);
            var (body, statusCode) = await _proxyService.GetJsonAsync(
                endpoint,
                queryParams: null,
                request.Headers);
            var actorBound = true;
            var unboundNativeFileRelay = false;
            var explicitUserEndpoint = BuildExplicitUserEndpoint(request);
            if (statusCode is StatusCodes.Status400BadRequest or StatusCodes.Status404NotFound)
            {
                if (explicitUserEndpoint != null)
                {
                    body?.Dispose();
                    (body, statusCode) = await _proxyService.GetJsonAsync(
                        explicitUserEndpoint,
                        queryParams: null,
                        request.Headers);
                    actorBound = false;
                }
                else if (IsNativeFileRelayRequest(request))
                {
                    body?.Dispose();
                    (body, statusCode) = await _proxyService.GetJsonAsync(
                        AddQueryCredentials("System/Info", request),
                        queryParams: null,
                        request.Headers);
                    unboundNativeFileRelay = true;
                }
            }

            using (body)
            {
                if (statusCode < StatusCodes.Status200OK || statusCode >= StatusCodes.Status300MultipleChoices)
                {
                    context.Result = CreateVerificationFailure(body, statusCode);
                    return;
                }

                if (unboundNativeFileRelay)
                {
                    await next();
                    return;
                }

                if (!TryGetPrincipal(body, out var principalId, out var displayName, out var isAdministrator))
                {
                    _logger.LogWarning(
                        "Jellyfin Users/Me returned {StatusCode} without a stable principal ID",
                        statusCode);
                    context.Result = new StatusCodeResult(StatusCodes.Status502BadGateway);
                    return;
                }

                context.HttpContext.Items[BackendPrincipalIdItemKey] = principalId;
                if (actorBound)
                {
                    var principal = await _identityResolver.ResolveAsync(
                        new BackendIdentityDescriptor(
                            "Jellyfin",
                            principalId,
                            displayName,
                            isAdministrator),
                        request.HttpContext.RequestAborted);
                    if (principal != null)
                    {
                        context.HttpContext.Items[BackendIdentityResolver.HttpContextPrincipalItemKey] = principal;
                    }
                }
                else
                {
                    _logger.LogDebug(
                        "Verified a Jellyfin API-key request for native relay without binding its declared UserId to an Allstarr actor");
                }
            }
        }
        catch (OperationCanceledException) when (request.HttpContext.RequestAborted.IsCancellationRequested)
        {
            context.Result = new StatusCodeResult(499);
            return;
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning(
                "Timed out verifying Jellyfin client credentials ({ExceptionType})",
                ex.GetType().Name);
            context.Result = new StatusCodeResult(StatusCodes.Status504GatewayTimeout);
            return;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(
                "Could not verify Jellyfin client credentials ({ExceptionType})",
                ex.GetType().Name);
            context.Result = new StatusCodeResult(StatusCodes.Status502BadGateway);
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                "Unexpected failure while verifying Jellyfin client credentials ({ExceptionType})",
                ex.GetType().Name);
            context.Result = new StatusCodeResult(StatusCodes.Status502BadGateway);
            return;
        }

        await next();
    }

    private static bool IsPublicBootstrapRequest(HttpRequest request)
    {
        var path = request.Path.Value?.TrimEnd('/') ?? string.Empty;

        if (HttpMethods.IsPost(request.Method) &&
            (path.Equals("/Users/AuthenticateByName", StringComparison.OrdinalIgnoreCase) ||
             path.Equals("/Users/AuthenticateWithQuickConnect", StringComparison.OrdinalIgnoreCase) ||
             path.Equals("/QuickConnect/Initiate", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if ((HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)) &&
            (path.Equals("/System/Info/Public", StringComparison.OrdinalIgnoreCase) ||
             path.Equals("/System/Ping", StringComparison.OrdinalIgnoreCase) ||
             path.Equals("/GetUtcTime", StringComparison.OrdinalIgnoreCase) ||
             path.Equals("/Users/Public", StringComparison.OrdinalIgnoreCase) ||
             path.Equals("/UserImage", StringComparison.OrdinalIgnoreCase) ||
             path.Equals("/QuickConnect/Enabled", StringComparison.OrdinalIgnoreCase) ||
             path.Equals("/QuickConnect/Connect", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (HttpMethods.IsPost(request.Method) &&
            path.Equals("/System/Ping", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Jellyfin deliberately serves image resources without a token. Several native
        // clients (including Finer) omit credentials from image-loader requests even
        // after an authenticated API session, so mirror the upstream image policy.
        if ((HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)) &&
            IsPublicImagePath(path) &&
            !HasClientCredentials(request))
        {
            return true;
        }

        // The proxied Jellyfin sign-in page must load before a client has a token.
        return (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)) &&
               (string.IsNullOrEmpty(path) ||
                path.Equals("/web", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("/web/", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsPublicImagePath(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 4 &&
            (segments[0].Equals("Items", StringComparison.OrdinalIgnoreCase) ||
             segments[0].Equals("Users", StringComparison.OrdinalIgnoreCase) ||
             segments[0].Equals("Artists", StringComparison.OrdinalIgnoreCase) ||
             segments[0].Equals("Genres", StringComparison.OrdinalIgnoreCase) ||
             segments[0].Equals("MusicGenres", StringComparison.OrdinalIgnoreCase)) &&
               !string.IsNullOrWhiteSpace(segments[1]) &&
               segments[2].Equals("Images", StringComparison.OrdinalIgnoreCase) &&
               !string.IsNullOrWhiteSpace(segments[3]);
    }

    private static bool HasClientCredentials(HttpRequest request)
    {
        if (HasHeaderValue(request, "X-Emby-Authorization") ||
            HasHeaderValue(request, "X-Emby-Token") ||
            HasHeaderValue(request, "Authorization"))
        {
            return true;
        }

        return AllowedQueryCredentialNames.Any(name =>
            request.Query.TryGetValue(name, out var values) &&
            values.Any(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static bool HasHeaderValue(HttpRequest request, string name)
    {
        return request.Headers.TryGetValue(name, out var values) &&
               values.Any(value => !string.IsNullOrWhiteSpace(value));
    }

    private static string BuildCurrentUserEndpoint(HttpRequest request)
    {
        return AddQueryCredentials("Users/Me", request);
    }

    private static string? BuildExplicitUserEndpoint(HttpRequest request)
    {
        var explicitUserId = request.RouteValues.TryGetValue("userId", out var routeUser)
            ? routeUser?.ToString()
            : request.Query.TryGetValue("UserId", out var queryUser)
                ? queryUser.FirstOrDefault()
                : UserIdFromPath(request.Path.Value) ??
                  AuthHeaderHelper.ExtractUserId(request.Headers);
        if (string.IsNullOrWhiteSpace(explicitUserId) || !IsSafeBackendId(explicitUserId))
            return null;

        return AddQueryCredentials($"Users/{Uri.EscapeDataString(explicitUserId)}", request);
    }

    private static string AddQueryCredentials(string endpoint, HttpRequest request)
    {
        var credentials = new List<KeyValuePair<string, string?>>();
        foreach (var name in AllowedQueryCredentialNames)
        {
            if (!request.Query.TryGetValue(name, out var values)) continue;
            credentials.AddRange(values
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => new KeyValuePair<string, string?>(name, value)));
        }

        return credentials.Count == 0 ? endpoint : $"{endpoint}{QueryString.Create(credentials)}";
    }

    private static bool IsSafeBackendId(string value) =>
        value.Length <= 128 && value.All(character => char.IsLetterOrDigit(character) || character is '-' or '_');

    private static bool IsNativeFileRelayRequest(HttpRequest request)
    {
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method)) return false;

        var segments = request.Path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments is { Length: 3 } &&
               segments[0].Equals("Items", StringComparison.OrdinalIgnoreCase) &&
               IsSafeBackendId(segments[1]) &&
               !JellyfinMusicEndpointPolicy.IsSynthesizedMusicItemId(segments[1]) &&
               (segments[2].Equals("File", StringComparison.OrdinalIgnoreCase) ||
                segments[2].Equals("Download", StringComparison.OrdinalIgnoreCase));
    }

    private static string? UserIdFromPath(string? path)
    {
        var segments = path?.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments is { Length: >= 2 } &&
               segments[0].Equals("Users", StringComparison.OrdinalIgnoreCase) &&
               IsSafeBackendId(segments[1])
            ? segments[1]
            : null;
    }

    private static IActionResult CreateVerificationFailure(
        System.Text.Json.JsonDocument? body,
        int statusCode)
    {
        if (body == null)
        {
            return new StatusCodeResult(statusCode);
        }

        return new ContentResult
        {
            Content = body.RootElement.GetRawText(),
            ContentType = "application/json",
            StatusCode = statusCode
        };
    }

    private static bool TryGetPrincipal(
        System.Text.Json.JsonDocument? body,
        out string principalId,
        out string? displayName,
        out bool isAdministrator)
    {
        principalId = string.Empty;
        displayName = null;
        isAdministrator = false;
        if (body == null ||
            (!body.RootElement.TryGetProperty("Id", out var id) &&
             !body.RootElement.TryGetProperty("id", out id)) ||
            id.ValueKind != System.Text.Json.JsonValueKind.String)
        {
            return false;
        }

        principalId = id.GetString() ?? string.Empty;
        if (body.RootElement.TryGetProperty("Name", out var name) ||
            body.RootElement.TryGetProperty("name", out name))
        {
            displayName = name.ValueKind == System.Text.Json.JsonValueKind.String
                ? name.GetString()
                : null;
        }

        if ((body.RootElement.TryGetProperty("Policy", out var policy) ||
             body.RootElement.TryGetProperty("policy", out policy)) &&
            policy.ValueKind == System.Text.Json.JsonValueKind.Object &&
            (policy.TryGetProperty("IsAdministrator", out var admin) ||
             policy.TryGetProperty("isAdministrator", out admin)) &&
            admin.ValueKind is System.Text.Json.JsonValueKind.True or
                System.Text.Json.JsonValueKind.False)
        {
            isAdministrator = admin.GetBoolean();
        }

        return !string.IsNullOrWhiteSpace(principalId);
    }
}
