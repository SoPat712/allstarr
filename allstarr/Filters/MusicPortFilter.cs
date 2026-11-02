using allstarr.Core.ClientApi;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace allstarr.Filters;

public sealed class MusicPortFilter : IResourceFilter
{
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        if (context.HttpContext.Connection.LocalPort != 8080)
            context.Result = new JsonResult(ClientApiError.ForStatus(404), ClientApiContract.JsonOptions) { StatusCode = 404 };
    }

    public void OnResourceExecuted(ResourceExecutedContext context) { }
}
