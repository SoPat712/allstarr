namespace allstarr.Middleware;

/// <summary>
/// Middleware that only serves static files on the admin port (5275).
/// This keeps the admin UI isolated from the main proxy port.
/// </summary>
public class AdminStaticFilesMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IWebHostEnvironment _env;
    private const int AdminPort = 5275;
    private readonly string _webRootPath;
    private readonly string _webRootPathWithSeparator;

    public AdminStaticFilesMiddleware(
        RequestDelegate next,
        IWebHostEnvironment env)
    {
        _next = next;
        _env = env;
        var webRoot = string.IsNullOrWhiteSpace(_env.WebRootPath)
            ? Path.Combine(_env.ContentRootPath, "wwwroot")
            : _env.WebRootPath;
        _webRootPath = Path.GetFullPath(webRoot);
        _webRootPathWithSeparator = _webRootPath.EndsWith(Path.DirectorySeparatorChar)
            ? _webRootPath
            : _webRootPath + Path.DirectorySeparatorChar;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var port = context.Connection.LocalPort;

        if (port == AdminPort)
        {
            var path = context.Request.Path.Value ?? "/";

            if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
            {
                await _next(context);
                return;
            }

            if (path == "/" || path == "/index.html")
            {
                var indexPath = Path.Combine(_webRootPath, "index.html");
                if (File.Exists(indexPath))
                {
                    SetRevalidationHeaders(context.Response);
                    context.Response.ContentType = "text/html";
                    await context.Response.SendFileAsync(indexPath);
                    return;
                }
            }

            // Canonicalize and enforce root boundary to block traversal attempts.
            var candidatePath = ResolveStaticFilePath(path);
            if (candidatePath == null)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            if (File.Exists(candidatePath))
            {
                if (path.StartsWith("/_app/immutable/", StringComparison.Ordinal) &&
                    !string.Equals(Path.GetExtension(candidatePath), ".css", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
                }
                else
                {
                    SetRevalidationHeaders(context.Response);
                }
                var contentType = GetContentType(candidatePath);
                context.Response.ContentType = contentType;
                await context.Response.SendFileAsync(candidatePath);
                return;
            }
        }

        // Not admin port or file not found - continue pipeline
        await _next(context);
    }

    private static void SetRevalidationHeaders(HttpResponse response)
    {
        // Entry HTML and shared static media must revalidate across container updates.
        response.Headers.CacheControl = "no-store";
        response.Headers.Pragma = "no-cache";
        response.Headers.Expires = "0";
    }

    private string? ResolveStaticFilePath(string requestPath)
    {
        var relativePath = requestPath.TrimStart('/');
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        try
        {
            var normalizedRelativePath = relativePath.Replace('/', Path.DirectorySeparatorChar);
            var candidatePath = Path.GetFullPath(Path.Combine(_webRootPath, normalizedRelativePath));

            if (string.Equals(candidatePath, _webRootPath, GetPathComparison()))
            {
                return null;
            }

            if (!candidatePath.StartsWith(_webRootPathWithSeparator, GetPathComparison()))
            {
                return null;
            }

            return candidatePath;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static StringComparison GetPathComparison()
    {
        return OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
    }

    private static string GetContentType(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".html" => "text/html",
            ".css" => "text/css",
            ".js" => "application/javascript",
            ".json" => "application/json",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".svg" => "image/svg+xml",
            ".ico" => "image/x-icon",
            _ => "application/octet-stream"
        };
    }
}
