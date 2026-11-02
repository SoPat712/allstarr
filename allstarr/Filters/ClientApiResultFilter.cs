using allstarr.Core.ClientApi;
using allstarr.Core.Protocols;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace allstarr.Filters;

public sealed class ClientApiResultFilter : IAlwaysRunResultFilter
{
    public void OnResultExecuting(ResultExecutingContext context)
    {
        var status = (context.Result as IStatusCodeActionResult)?.StatusCode ?? context.HttpContext.Response.StatusCode;
        if (status >= 400)
        {
            // Users/Me cannot resolve an API key without an explicitly declared user.
            if (status is 400 or 404 && context.HttpContext.GetProtocolExecutionContext() == null)
                status = StatusCodes.Status403Forbidden;
            status = ClientApiError.NormalizeStatus(status);
            var error = (context.Result as ObjectResult)?.Value as ClientApiError ??
                (context.Result as JsonResult)?.Value as ClientApiError ?? ClientApiError.ForStatus(status);
            context.Result = new JsonResult(error, ClientApiContract.JsonOptions) { StatusCode = status };
        }
        else if (context.Result is ObjectResult result)
        {
            context.Result = new JsonResult(result.Value, ClientApiContract.JsonOptions) { StatusCode = status };
        }
        else if (context.Result is JsonResult json)
        {
            json.SerializerSettings = ClientApiContract.JsonOptions;
        }
    }

    public void OnResultExecuted(ResultExecutedContext context) { }
}
