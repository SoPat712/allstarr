using allstarr.Core.Protocols;
using allstarr.Core.ClientApi;
using allstarr.Core.Settings;
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
    private readonly IEffectiveProviderPolicyResolver _policies;
    private readonly ClientApiAvailability? _clientApi;

    public ProtocolExecutionContextFilter(
        ProtocolExecutionContextFactory factory,
        ILogger<ProtocolExecutionContextFilter> logger,
        IEffectiveProviderPolicyResolver policies,
        ClientApiAvailability? clientApi = null)
    {
        _factory = factory;
        _logger = logger;
        _policies = policies;
        _clientApi = clientApi;
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
                    AuthHeaderHelper.ExtractDeviceId(context.HttpContext.Request)));
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
                    parameters?.GetValueOrDefault("c")));
        }

        if (context.HttpContext.GetProtocolExecutionContext() is { Principal: { } principal } execution)
        {
            items[ProtocolExecutionContextFactory.HttpContextItemKey] = execution with
            {
                Policy = await _policies.ResolveForUserAsync(principal.UserId,
                    context.HttpContext.RequestAborted)
            };
        }
        if (context.HttpContext.Connection.LocalPort != 5275 &&
            context.HttpContext.Request.Headers["X-Allstarr-Title-Markers"].ToString() == "none" &&
            context.HttpContext.GetProtocolExecutionContext() is { } requestExecution &&
            _clientApi != null && await _clientApi.IsEnabledAsync(context.HttpContext))
        {
            var policy = requestExecution.Policy ?? await _policies.ResolveAsync(context.HttpContext.RequestAborted);
            items[ProtocolExecutionContextFactory.HttpContextItemKey] = requestExecution with
            {
                Policy = policy with
                {
                    Preferences = policy.Preferences with { ShowExternalLabel = false, ShowExplicitLabel = false }
                }
            };
        }
        await next();
    }
}
