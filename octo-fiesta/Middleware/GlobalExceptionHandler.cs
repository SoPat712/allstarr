using Microsoft.AspNetCore.Diagnostics;

namespace octo_fiesta.Middleware;

/// <summary>
/// Global exception handler that catches unhandled exceptions and returns appropriate Subsonic API error responses
/// </summary>
public class GlobalExceptionHandler : IExceptionHandler
{
    private readonly ILogger<GlobalExceptionHandler> _logger;

    public GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger)
    {
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        _logger.LogError(exception, "Unhandled exception occurred: {Message}", exception.Message);

        var (statusCode, subsonicErrorCode, errorMessage) = MapExceptionToResponse(exception);

        httpContext.Response.StatusCode = statusCode;
        httpContext.Response.ContentType = "application/json";

        var response = CreateSubsonicErrorResponse(subsonicErrorCode, errorMessage);
        await httpContext.Response.WriteAsJsonAsync(response, cancellationToken);

        return true;
    }

    /// <summary>
    /// Maps exception types to HTTP status codes and Subsonic error codes
    /// </summary>
    private (int statusCode, int subsonicErrorCode, string message) MapExceptionToResponse(Exception exception)
    {
        return exception switch
        {
            // Not Found errors (404)
            FileNotFoundException => (404, 70, "Resource not found"),
            DirectoryNotFoundException => (404, 70, "Directory not found"),
            
            // Authentication errors (401)
            UnauthorizedAccessException => (401, 40, "Wrong username or password"),
            
            // Bad Request errors (400)
            ArgumentNullException => (400, 10, "Required parameter is missing"),
            ArgumentException => (400, 10, "Invalid request"),
            FormatException => (400, 10, "Invalid format"),
            InvalidOperationException => (400, 10, "Operation not valid"),
            
            // External service errors (502)
            HttpRequestException => (502, 0, "External service unavailable"),
            TimeoutException => (504, 0, "Request timeout"),
            
            // Generic server error (500)
            _ => (500, 0, "An internal server error occurred")
        };
    }

    /// <summary>
    /// Creates a Subsonic-compatible error response
    /// Subsonic error codes:
    /// 0 = Generic error
    /// 10 = Required parameter missing
    /// 20 = Incompatible Subsonic REST protocol version
    /// 30 = Incompatible Subsonic REST protocol version (server)
    /// 40 = Wrong username or password
    /// 50 = User not authorized
    /// 60 = Trial period for the Subsonic server is over
    /// 70 = Requested data was not found
    /// </summary>
    private object CreateSubsonicErrorResponse(int code, string message)
    {
        return new Dictionary<string, object>
        {
            ["subsonic-response"] = new
            {
                status = "failed",
                version = "1.16.1",
                error = new { code, message }
            }
        };
    }
}
