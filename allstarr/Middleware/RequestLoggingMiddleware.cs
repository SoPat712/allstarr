using System.Diagnostics;
using System.Text;
using allstarr.Core.Operations;

namespace allstarr.Middleware;

/// <summary>
/// Middleware that logs all incoming HTTP requests when debug logging is enabled.
/// Useful for debugging client issues and seeing what requests are being made.
/// </summary>
public class RequestLoggingMiddleware
{
    private static int _inflight;
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
        _logger.LogInformation("RequestLoggingMiddleware initialized - LogAllRequests={LogAllRequests}", initialValue);

        if (initialValue)
        {
            _logger.LogInformation(
                "🔍 Request logging ENABLED - sensitive request values are always redacted");
        }
        else
        {
            _logger.LogInformation("Request logging disabled (set DEBUG_LOG_ALL_REQUESTS=true to enable)");
        }
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var inflight = Interlocked.Increment(ref _inflight);
        try
        {
            var enabled = _configuration.GetValue("Diagnostics:ServerTimingEnabled", true);
            var pre = PreMilliseconds();
            using var timing = enabled ? RequestTiming.Begin(pre, inflight) : null;
            var started = Stopwatch.GetTimestamp();
            long? answered = null;
            RequestTiming.Result? result = null;
            context.Response.OnStarting(() =>
            {
                answered = Stopwatch.GetTimestamp();
                if (timing != null)
                {
                    result ??= timing.Snapshot(RouteLabel(context));
                    context.Response.Headers["Server-Timing"] = result.Header;
                }
                return Task.CompletedTask;
            });
            if (pre > 250 && _logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("Request reached middleware after {PreMs:F1} ms: pending {PendingWorkItemCount}, threads {ThreadCount}, inflight {Inflight}, route {Route}",
                    pre, ThreadPool.PendingWorkItemCount, ThreadPool.ThreadCount, inflight, RouteLabel(context));
            try { await InvokeLoggedAsync(context); }
            finally
            {
                NoteSlowOrFailed(context, started, answered);
                if (timing != null && _logger.IsEnabled(LogLevel.Debug))
                {
                    result ??= timing.Snapshot(RouteLabel(context));
                    _logger.LogDebug("Request timing {Method} {Route} answered {StatusCode}: total {TotalMs:F1} ms, jellyfin {JellyfinMs:F1} ms ({JellyfinCalls} calls), db {DbMs:F1} ms, provider {ProviderMs:F1} ms, cache {Cache}, pre {PreMs:F1} ms, inflight {Inflight}, stages {Stages}",
                        context.Request.Method, result.Route, context.Response.StatusCode, result.TotalMs, result.JellyfinMs,
                        result.JellyfinCalls, result.DbMs, result.ProviderMs, result.Cache, result.PreMs, result.Inflight, result.StageSummary);
                }
            }
        }
        finally { Interlocked.Decrement(ref _inflight); }
    }

    private static double PreMilliseconds()
    {
        for (var activity = Activity.Current; activity != null; activity = activity.Parent)
        {
            if (activity.OperationName != "Microsoft.AspNetCore.Hosting.HttpRequestIn" ||
                (activity.Source.Name != "" && (activity.Source.Name != "Microsoft.AspNetCore" || activity.Kind != ActivityKind.Server)) ||
                activity.StartTimeUtc == default) continue;
            return Math.Max(0, (DateTime.UtcNow - activity.StartTimeUtc).TotalMilliseconds);
        }
        return 0;
    }

    private static string RouteLabel(HttpContext context)
    {
        var path = context.Request.Path;
        if (path.StartsWithSegments("/api") || context.Connection.LocalPort == 5275) return "admin";
        if (path.StartsWithSegments("/rest")) return "subsonic";
        if (!path.StartsWithSegments("/allstarr/v1", out var remaining)) return "jellyfin";
        if (remaining.StartsWithSegments("/me/sound")) return "sound";
        if (remaining.StartsWithSegments("/me/stats")) return "stats";
        if (remaining.StartsWithSegments("/me/taste")) return "taste";
        if (remaining.StartsWithSegments("/mixes")) return "mixes";
        if (remaining.StartsWithSegments("/lyrics")) return "lyrics";
        return "client";
    }

    private async Task InvokeLoggedAsync(HttpContext context)
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
        var queryStringForLog = BuildMaskedQueryString(request.QueryString.Value);

        // Log request details
        var requestLog = new StringBuilder();
        var requestUrlForLog = $"{request.Scheme}://{request.Host}{SafeOperationalText.Sanitize(request.Path.Value)}{queryStringForLog}";
        requestLog.AppendLine($"📥 HTTP {request.Method} {requestUrlForLog}");
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
            var value = request.Headers["X-Emby-Authorization"].ToString();
            requestLog.AppendLine($"   X-Emby-Authorization: {MaskAuthHeader(value)}");
        }
        if (request.Headers.ContainsKey("Authorization"))
        {
            var value = request.Headers["Authorization"].ToString();
            requestLog.AppendLine($"   Authorization: {MaskAuthHeader(value)}");
        }
        if (request.Headers.ContainsKey("X-Emby-Token"))
        {
            var value = request.Headers["X-Emby-Token"].ToString();
            requestLog.AppendLine("   X-Emby-Token: ***");
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

        try
        {
            await _next(context);

            stopwatch.Stop();

            // Log response
            _logger.LogInformation(
                "📤 HTTP {Method} {Url} → {StatusCode} ({ElapsedMs}ms)",
                request.Method,
                requestUrlForLog,
                context.Response.StatusCode,
                stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(
                "❌ HTTP {Method} {Url} → {ExceptionType} ({ElapsedMs}ms)",
                request.Method,
                requestUrlForLog,
                ex.GetType().Name,
                stopwatch.ElapsedMilliseconds);
            throw;
        }
    }

    private static readonly TimeSpan SlowAnswer = TimeSpan.FromSeconds(3);

    // Requests that answer slowly, fail, or that the client gives up on before any answer are always noted (path only,
    // never query values), so a client's connection trouble can be checked against what the server saw. Time counts to
    // the first byte, so a song streaming for minutes is not slow.
    private void NoteSlowOrFailed(HttpContext context, long started, long? answered)
    {
        if (string.Equals(context.Request.Headers.Upgrade, "websocket", StringComparison.OrdinalIgnoreCase)) return;
        var waited = (int)Stopwatch.GetElapsedTime(started, answered ?? Stopwatch.GetTimestamp()).TotalMilliseconds;
        if (answered == null && context.RequestAborted.IsCancellationRequested)
            _logger.LogWarning("Client gave up on {Method} {Path} after {WaitedMs} ms", context.Request.Method,
                SafeOperationalText.Sanitize(context.Request.Path.Value), waited);
        else if (context.Response.StatusCode >= 500 || waited >= SlowAnswer.TotalMilliseconds)
            _logger.LogWarning("Request {Method} {Path} answered {StatusCode} after {WaitedMs} ms", context.Request.Method,
                SafeOperationalText.Sanitize(context.Request.Path.Value), context.Response.StatusCode, waited);
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

    private static string BuildMaskedQueryString(string? queryString)
    {
        if (string.IsNullOrWhiteSpace(queryString))
        {
            return string.Empty;
        }

        var redactedParts = queryString.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2)[0])
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => $"{key}=<redacted>")
            .ToArray();

        return redactedParts.Length == 0
            ? string.Empty
            : "?" + string.Join("&", redactedParts);
    }
}
