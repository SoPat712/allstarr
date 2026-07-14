using allstarr.Core.Protocols;
using allstarr.Services.Common;
using allstarr.Services.Subsonic;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace allstarr.Filters;

/// <summary>
/// Projects protocol-native authentication into a secret-free core request context.
/// Public bootstrap and backend ping routes have no verified principal and pass through unchanged.
/// </summary>
public sealed class ProtocolExecutionContextFilter : IAsyncActionFilter
{
    private readonly ProtocolExecutionContextFactory _factory;
    private readonly ILogger<ProtocolExecutionContextFilter> _logger;

    public ProtocolExecutionContextFilter(
        ProtocolExecutionContextFactory factory,
        ILogger<ProtocolExecutionContextFilter> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task OnActionExecutionAsync(
        ActionExecutingContext context,
        ActionExecutionDelegate next)
    {
        var items = context.HttpContext.Items;
        var jellyfinPrincipal = items.TryGetValue(
                JellyfinAuthFilter.BackendPrincipalIdItemKey,
                out var jellyfinValue)
            ? jellyfinValue as string
            : null;
        var subsonicPrincipal = items.TryGetValue(
                SubsonicAuthFilter.BackendPrincipalNameItemKey,
                out var subsonicValue)
            ? subsonicValue as string
            : null;
        if (jellyfinPrincipal != null && subsonicPrincipal != null)
        {
            _logger.LogError("A request contained conflicting verified protocol principals");
            context.Result = new StatusCodeResult(StatusCodes.Status500InternalServerError);
            return;
        }

        if (jellyfinPrincipal != null)
        {
            items[ProtocolExecutionContextFactory.HttpContextItemKey] = _factory.Create(
                context.HttpContext,
                ProtocolKind.Jellyfin,
                jellyfinPrincipal,
                client: new ProtocolClientDescriptor(
                    AuthHeaderHelper.ExtractClientName(context.HttpContext.Request.Headers),
                    AuthHeaderHelper.ExtractDeviceId(context.HttpContext.Request.Headers)));
        }
        else if (subsonicPrincipal != null)
        {
            var parameters = items.TryGetValue(
                    SubsonicAuthFilter.RequestParametersItemKey,
                    out var parameterValue)
                ? parameterValue as SubsonicRequestParameters
                : null;
            items[ProtocolExecutionContextFactory.HttpContextItemKey] = _factory.Create(
                context.HttpContext,
                ProtocolKind.Subsonic,
                subsonicPrincipal,
                client: new ProtocolClientDescriptor(
                    parameters?.GetValueOrDefault("c")),
                libraryScopeId: parameters?.GetValueOrDefault("musicFolderId") is { Length: > 0 } libraryId
                    ? libraryId
                    : null);
        }

        await next();
    }
}
