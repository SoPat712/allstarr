using System.Net;
using allstarr.Services.Subsonic;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace allstarr.Filters;

/// <summary>
/// Converts unhandled synthesized-route failures into safe Subsonic responses.
/// </summary>
public sealed class SubsonicExceptionFilter(
    SubsonicResponseBuilder responseBuilder,
    ILogger<SubsonicExceptionFilter> logger) : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        var error = Map(
            context.Exception,
            context.HttpContext.RequestAborted.IsCancellationRequested);
        logger.LogError(
            "Subsonic request failed safely ({ExceptionType}, code {ErrorCode})",
            context.Exception.GetType().Name,
            error.Code);

        var result = responseBuilder.CreateError(Format(context.HttpContext), error.Code, error.Message);
        switch (result)
        {
            case JsonResult json:
                json.StatusCode = error.StatusCode;
                break;
            case ContentResult content:
                content.StatusCode = error.StatusCode;
                break;
        }

        context.Result = result;
        context.ExceptionHandled = true;
    }

    internal static (int Code, string Message, int StatusCode) Map(
        Exception exception,
        bool requestAborted = false) => exception switch
        {
            FileNotFoundException or KeyNotFoundException or
                HttpRequestException { StatusCode: HttpStatusCode.NotFound } =>
                (70, "Requested data was not found.", StatusCodes.Status404NotFound),
            UnauthorizedAccessException or
                HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } =>
                (50, "User is not authorized for this operation.", StatusCodes.Status403Forbidden),
            OperationCanceledException when requestAborted =>
                (0, "Request was canceled.", 499),
            TimeoutException or OperationCanceledException =>
                (0, "Request timed out.", StatusCodes.Status504GatewayTimeout),
            HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } =>
                (0, "The requested service is temporarily unavailable.", StatusCodes.Status429TooManyRequests),
            NotSupportedException or InvalidOperationException =>
                (0, "The requested operation is unavailable.", StatusCodes.Status503ServiceUnavailable),
            HttpRequestException or IOException =>
                (0, "The requested service is temporarily unavailable.", StatusCodes.Status502BadGateway),
            ArgumentException =>
                (0, "Invalid request.", StatusCodes.Status400BadRequest),
            _ =>
                (0, "Request failed.", StatusCodes.Status500InternalServerError)
        };

    private static string Format(HttpContext context)
    {
        var requested = context.Items.TryGetValue(SubsonicAuthFilter.RequestParametersItemKey, out var value) &&
                        value is SubsonicRequestParameters parameters
            ? parameters.GetValueOrDefault("f", "xml")
            : context.Request.Query["f"].FirstOrDefault();
        return requested?.Equals("json", StringComparison.OrdinalIgnoreCase) == true ? "json" : "xml";
    }
}
