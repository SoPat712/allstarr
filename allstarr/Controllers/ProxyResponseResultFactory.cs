using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

public static class ProxyResponseResultFactory
{
    public static IActionResult Create(
        JsonDocument? result,
        int statusCode,
        object? fallbackValue = null)
    {
        if (result != null)
        {
            return new JsonResult(JsonSerializer.Deserialize<object>(result.RootElement.GetRawText()))
            {
                StatusCode = statusCode
            };
        }

        if (statusCode == StatusCodes.Status401Unauthorized)
        {
            return new UnauthorizedResult();
        }

        if (statusCode == StatusCodes.Status403Forbidden)
        {
            return new ForbidResult();
        }

        if (statusCode == StatusCodes.Status404NotFound)
        {
            return new NotFoundResult();
        }

        if (statusCode >= StatusCodes.Status400BadRequest)
        {
            return new StatusCodeResult(statusCode);
        }

        if (fallbackValue != null)
        {
            return new JsonResult(fallbackValue) { StatusCode = statusCode };
        }

        return statusCode == StatusCodes.Status204NoContent
            ? new NoContentResult()
            : new StatusCodeResult(statusCode);
    }
}
