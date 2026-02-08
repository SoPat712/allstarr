using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

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
    
    public AdminStaticFilesMiddleware(
        RequestDelegate next,
        IWebHostEnvironment env)
    {
        _next = next;
        _env = env;
    }
    
    public async Task InvokeAsync(HttpContext context)
    {
        var port = context.Connection.LocalPort;
        
        if (port == AdminPort)
        {
            var path = context.Request.Path.Value ?? "/";
            
            // Serve index.html for root path
            if (path == "/" || path == "/index.html")
            {
                var indexPath = Path.Combine(_env.WebRootPath, "index.html");
                if (File.Exists(indexPath))
                {
                    context.Response.ContentType = "text/html";
                    await context.Response.SendFileAsync(indexPath);
                    return;
                }
            }
            
            // Try to serve static file from wwwroot
            var filePath = Path.Combine(_env.WebRootPath, path.TrimStart('/'));
            if (File.Exists(filePath))
            {
                var contentType = GetContentType(filePath);
                context.Response.ContentType = contentType;
                await context.Response.SendFileAsync(filePath);
                return;
            }
        }
        
        // Not admin port or file not found - continue pipeline
        await _next(context);
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
