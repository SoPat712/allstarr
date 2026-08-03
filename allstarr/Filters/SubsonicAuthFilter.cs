using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using allstarr.Services.Subsonic;
using allstarr.Core.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace allstarr.Filters;

/// <summary>
/// Verifies Subsonic credentials with the backend before synthesized actions execute.
/// </summary>
public sealed class SubsonicAuthFilter : IAsyncResourceFilter
{
    public const string BackendPrincipalNameItemKey = "allstarr.backend-principal-name";
    public const string RequestParametersItemKey = "allstarr.subsonic-request-parameters";

    private static readonly IReadOnlySet<string> VerificationParameterNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "u", "p", "t", "s", "apiKey", "v", "c", "f"
        };

    private readonly SubsonicRequestParser _requestParser;
    private readonly SubsonicProxyService _proxyService;
    private readonly SubsonicResponseBuilder _responseBuilder;
    private readonly BackendIdentityResolver _identityResolver;
    private readonly ILogger<SubsonicAuthFilter> _logger;

    public SubsonicAuthFilter(
        SubsonicRequestParser requestParser,
        SubsonicProxyService proxyService,
        SubsonicResponseBuilder responseBuilder,
        BackendIdentityResolver identityResolver,
        ILogger<SubsonicAuthFilter> logger)
    {
        _requestParser = requestParser;
        _proxyService = proxyService;
        _responseBuilder = responseBuilder;
        _identityResolver = identityResolver;
        _logger = logger;
    }

    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var request = context.HttpContext.Request;
        if (IsBackendValidatedPing(request) || IsPublicExtensionDiscovery(request))
        {
            await next();
            return;
        }

        var parameters = await _requestParser.ExtractAllParametersAsync(request);
        context.HttpContext.Items[RequestParametersItemKey] = parameters;
        var format = parameters.GetValueOrDefault("f", "xml");

        if (!TryResolveMechanism(parameters, out var mechanism, out var principalName, out var errorCode, out var error))
        {
            context.Result = CreateProtocolError(format, errorCode, error, StatusCodes.Status401Unauthorized);
            return;
        }

        try
        {
            var verificationParameters = parameters.Select(VerificationParameterNames);
            var response = await _proxyService.RelayRawAsync(
                "rest/ping.view",
                verificationParameters,
                request.HttpContext.RequestAborted);

            if (!response.IsSuccessStatusCode || GetProtocolStatus(response) != ProtocolStatus.Success)
            {
                context.Result = CreateRawResult(response, format);
                return;
            }

            if (mechanism == AuthenticationMechanism.ApiKey)
            {
                var tokenInfo = await _proxyService.RelayRawAsync(
                    "rest/tokenInfo.view",
                    verificationParameters,
                    request.HttpContext.RequestAborted);
                if (!tokenInfo.IsSuccessStatusCode || GetProtocolStatus(tokenInfo) != ProtocolStatus.Success)
                {
                    context.Result = CreateRawResult(tokenInfo, format);
                    return;
                }

                principalName = GetTokenInfoUsername(tokenInfo);
                if (string.IsNullOrWhiteSpace(principalName))
                {
                    context.Result = CreateProtocolError(
                        format,
                        0,
                        "Backend did not identify the API-key principal",
                        StatusCodes.Status502BadGateway);
                    return;
                }
            }

            context.HttpContext.Items[BackendPrincipalNameItemKey] = principalName!;
            var principal = await _identityResolver.ResolveAsync(
                new BackendIdentityDescriptor("Subsonic", principalName!, principalName),
                request.HttpContext.RequestAborted);
            if (principal != null)
            {
                context.HttpContext.Items[BackendIdentityResolver.HttpContextPrincipalItemKey] = principal;
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
                "Timed out verifying Subsonic client credentials ({ExceptionType})",
                ex.GetType().Name);
            context.Result = CreateProtocolError(
                format,
                0,
                "Backend authentication timed out",
                StatusCodes.Status504GatewayTimeout);
            return;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(
                "Could not verify Subsonic client credentials ({ExceptionType})",
                ex.GetType().Name);
            context.Result = CreateProtocolError(
                format,
                0,
                "Backend authentication is unavailable",
                StatusCodes.Status502BadGateway);
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                "Unexpected failure while verifying Subsonic client credentials ({ExceptionType})",
                ex.GetType().Name);
            context.Result = CreateProtocolError(
                format,
                0,
                "Backend authentication failed",
                StatusCodes.Status502BadGateway);
            return;
        }

        await next();
    }

    private static bool IsBackendValidatedPing(HttpRequest request)
    {
        var path = request.Path.Value?.TrimEnd('/') ?? string.Empty;
        return path.Equals("/rest/ping", StringComparison.OrdinalIgnoreCase) ||
               path.Equals("/rest/ping.view", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPublicExtensionDiscovery(HttpRequest request)
    {
        var path = request.Path.Value?.TrimEnd('/') ?? string.Empty;
        return path.Equals("/rest/getOpenSubsonicExtensions", StringComparison.OrdinalIgnoreCase) ||
               path.Equals("/rest/getOpenSubsonicExtensions.view", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryResolveMechanism(
        SubsonicRequestParameters parameters,
        out AuthenticationMechanism mechanism,
        out string? principalName,
        out int errorCode,
        out string error)
    {
        mechanism = AuthenticationMechanism.None;
        principalName = null;
        errorCode = 40;
        error = "Missing authentication parameters";

        var usernames = parameters.GetAllValues("u")
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var hasPassword = parameters.HasNonEmptyValue("p");
        var hasToken = parameters.HasNonEmptyValue("t");
        var hasSalt = parameters.HasNonEmptyValue("s");
        var hasApiKey = parameters.HasNonEmptyValue("apiKey");

        if (hasApiKey)
        {
            if (usernames.Count > 0 || hasPassword || hasToken || hasSalt)
            {
                errorCode = 43;
                error = "Multiple conflicting authentication mechanisms provided";
                return false;
            }

            mechanism = AuthenticationMechanism.ApiKey;
            return true;
        }

        if (usernames.Count != 1)
        {
            if (usernames.Count > 1)
            {
                errorCode = 43;
                error = "Multiple conflicting authentication principals provided";
            }

            return false;
        }

        principalName = usernames[0];
        if (hasPassword && !hasToken && !hasSalt)
        {
            mechanism = AuthenticationMechanism.Password;
            return true;
        }

        if (!hasPassword && hasToken && hasSalt)
        {
            mechanism = AuthenticationMechanism.Token;
            return true;
        }

        if (hasPassword || hasToken || hasSalt)
        {
            errorCode = 43;
            error = "Multiple conflicting or incomplete authentication mechanisms provided";
        }

        return false;
    }

    private IActionResult CreateProtocolError(
        string format,
        int code,
        string message,
        int statusCode)
    {
        var result = _responseBuilder.CreateError(format, code, message);
        switch (result)
        {
            case JsonResult json:
                json.StatusCode = statusCode;
                break;
            case ContentResult content:
                content.StatusCode = statusCode;
                break;
        }

        return result;
    }

    private static IActionResult CreateRawResult(SubsonicProxyResponse response, string format)
    {
        var statusCode = (int)response.StatusCode;
        if (response.Body.Length == 0)
        {
            return new StatusCodeResult(statusCode);
        }

        return new ContentResult
        {
            Content = Encoding.UTF8.GetString(response.Body),
            ContentType = response.ContentType ?? $"application/{format}",
            StatusCode = statusCode
        };
    }

    private static ProtocolStatus GetProtocolStatus(SubsonicProxyResponse response)
    {
        try
        {
            var content = Encoding.UTF8.GetString(response.Body);
            if (response.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true ||
                content.TrimStart().StartsWith('{'))
            {
                using var document = JsonDocument.Parse(content);
                if (document.RootElement.TryGetProperty("subsonic-response", out var root) &&
                    root.TryGetProperty("status", out var status))
                {
                    return ParseStatus(status.GetString());
                }

                return ProtocolStatus.Invalid;
            }

            var documentXml = XDocument.Parse(content);
            return ParseStatus(documentXml.Root?.Attribute("status")?.Value);
        }
        catch
        {
            return ProtocolStatus.Invalid;
        }
    }

    private static string? GetTokenInfoUsername(SubsonicProxyResponse response)
    {
        try
        {
            var content = Encoding.UTF8.GetString(response.Body);
            if (response.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true ||
                content.TrimStart().StartsWith('{'))
            {
                using var document = JsonDocument.Parse(content);
                return document.RootElement
                    .GetProperty("subsonic-response")
                    .GetProperty("tokenInfo")
                    .GetProperty("username")
                    .GetString();
            }

            var documentXml = XDocument.Parse(content);
            var ns = documentXml.Root?.GetDefaultNamespace() ?? XNamespace.None;
            return documentXml.Descendants(ns + "tokenInfo").FirstOrDefault()?.Attribute("username")?.Value;
        }
        catch
        {
            return null;
        }
    }

    private static ProtocolStatus ParseStatus(string? status) => status?.ToLowerInvariant() switch
    {
        "ok" => ProtocolStatus.Success,
        "failed" => ProtocolStatus.Failed,
        _ => ProtocolStatus.Invalid
    };

    private enum AuthenticationMechanism
    {
        None,
        Password,
        Token,
        ApiKey
    }

    private enum ProtocolStatus
    {
        Invalid,
        Success,
        Failed
    }
}
