using allstarr.Core.Identity;
using allstarr.Core.Protocols;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace allstarr.Filters;

public sealed class ClientApiListenerFilter : IActionFilter
{
    public void OnActionExecuting(ActionExecutingContext context)
    {
        var http = context.HttpContext;
        if (http.GetProtocolExecutionContext() is not { Principal: { } principal } execution)
        {
            context.Result = new StatusCodeResult(StatusCodes.Status403Forbidden);
            return;
        }

        var listener = principal with { IsAdministrator = false };
        http.Items[BackendIdentityResolver.HttpContextPrincipalItemKey] = listener;
        http.Items[ProtocolExecutionContextFactory.HttpContextItemKey] = new ProtocolExecutionContext(
            execution.Protocol, execution.BackendInstanceId, execution.VerifiedBackendPrincipalId,
            listener, execution.CorrelationId, execution.Deadline, execution.CancellationToken, execution.Client)
        { Policy = execution.Policy };
    }

    public void OnActionExecuted(ActionExecutedContext context) { }
}
