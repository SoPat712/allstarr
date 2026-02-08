using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace allstarr.Filters;

/// <summary>
/// Filter that restricts access to admin endpoints to only the admin port (5275).
/// This prevents the admin API from being accessed through the main proxy port.
/// </summary>
public class AdminPortFilter : IActionFilter
{
    private const int AdminPort = 5275;
    
    public void OnActionExecuting(ActionExecutingContext context)
    {
        var requestPort = context.HttpContext.Connection.LocalPort;
        
        if (requestPort != AdminPort)
        {
            context.Result = new NotFoundResult();
        }
    }
    
    public void OnActionExecuted(ActionExecutedContext context)
    {
        // No action needed after execution
    }
}
