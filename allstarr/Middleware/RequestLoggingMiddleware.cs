using System.Diagnostics;
using System.Text;

namespace allstarr.Middleware;

/// <summary>
/// Middleware that logs all incoming HTTP requests when debug logging is enabled.
/// Useful for debugging client issues and seeing what requests are being made.
/// </summary>
public class RequestLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RequestLoggingMiddleware> _logger;
    private readonly IConfiguration _configuration;
    
    public RequestLoggingMiddleware(
        RequestDelegate next, 
        ILogger<RequestLoggingMiddleware> logger,
        IConfiguration configuration)
    {
        _next = next;
        _logger = logger;
        _configuration = configuration;
        
        // Log initialization status
        var initialValue = _configuration.GetValue<bool>("Debug:LogAllRequests");
        _logger.LogWarning("🔍 RequestLoggingMiddleware initialized - LogAllRequests={LogAllRequests}", initialValue);
        
        if (initialValue)
        {
            _logger.LogWarning("🔍 Request logging ENABLED - all HTTP requests will be logged");
        }
        else
        {
            _logger.LogInformation("Request logging disabled (set DEBUG_LOG_ALL_REQUESTS=true to enable)");
        }
    }
    
    public async Task InvokeAsync(HttpContext context)
    {
        // Check configuration on every request to allow dynamic toggling
        var logAllRequests = _configuration.GetValue<bool>("Debug:LogAllRequests");
        
        if (!logAllRequests)
        {
            await _next(context);
            return;
        }
        
        var stopwatch = Stopwatch.StartNew();
        var request = context.Request;
        
        // Log request details
        var requestLog = new StringBuilder();
        requestLog.AppendLine($"📥 HTTP {request.Method} {request.Path}{request.QueryString}");
        requestLog.AppendLine($"   Host: {request.Host}");
        requestLog.AppendLine($"   Content-Type: {request.ContentType ?? "(none)"}");
        requestLog.AppendLine($"   Content-Length: {request.ContentLength?.ToString() ?? "(none)"}");
        
        // Log important headers
        if (request.Headers.ContainsKey("User-Agent"))
        {
            requestLog.AppendLine($"   User-Agent: {request.Headers["User-Agent"]}");
        }
        if (request.Headers.ContainsKey("X-Emby-Authorization"))
        {
            requestLog.AppendLine($"   X-Emby-Authorization: {MaskAuthHeader(request.Headers["X-Emby-Authorization"]!)}");
        }
        if (request.Headers.ContainsKey("Authorization"))
        {
            requestLog.AppendLine($"   Authorization: {MaskAuthHeader(request.Headers["Authorization"]!)}");
        }
        if (request.Headers.ContainsKey("X-Emby-Token"))
        {
            requestLog.AppendLine($"   X-Emby-Token: ***");
        }
        if (request.Headers.ContainsKey("X-Emby-Device-Id"))
        {
            requestLog.AppendLine($"   X-Emby-Device-Id: {request.Headers["X-Emby-Device-Id"]}");
        }
        if (request.Headers.ContainsKey("X-Emby-Client"))
        {
            requestLog.AppendLine($"   X-Emby-Client: {request.Headers["X-Emby-Client"]}");
        }
        
        _logger.LogInformation(requestLog.ToString().TrimEnd());
        
        // Capture response status
        var originalBodyStream = context.Response.Body;
        
        try
        {
            await _next(context);
            
            stopwatch.Stop();
            
            // Log response
            _logger.LogInformation(
                "📤 HTTP {Method} {Path} → {StatusCode} ({ElapsedMs}ms)",
                request.Method,
                request.Path,
                context.Response.StatusCode,
                stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, 
                "❌ HTTP {Method} {Path} → EXCEPTION ({ElapsedMs}ms)",
                request.Method,
                request.Path,
                stopwatch.ElapsedMilliseconds);
            throw;
        }
    }
    
    private static string MaskAuthHeader(string authHeader)
    {
        // Mask tokens in auth headers for security
        if (string.IsNullOrEmpty(authHeader))
            return "(empty)";
        
        // For MediaBrowser format: MediaBrowser Client="...", Token="..."
        if (authHeader.Contains("Token=", StringComparison.OrdinalIgnoreCase))
        {
            var parts = authHeader.Split(',');
            var masked = new List<string>();
            foreach (var part in parts)
            {
                if (part.Contains("Token=", StringComparison.OrdinalIgnoreCase))
                {
                    masked.Add("Token=\"***\"");
                }
                else
                {
                    masked.Add(part.Trim());
                }
            }
            return string.Join(", ", masked);
        }
        
        // For Bearer tokens
        if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return "Bearer ***";
        }
        
        // For other formats, just mask everything after first 10 chars
        if (authHeader.Length > 10)
        {
            return authHeader.Substring(0, 10) + "***";
        }
        
        return "***";
    }
}
