using System.Text.Json;
using allstarr.Core.Storage;

namespace allstarr.Middleware;

/// <summary>
/// Keeps a deployment on its selected durable store when that store is unavailable.
/// </summary>
public sealed class DurableMutationGuardMiddleware
{
    private static readonly HashSet<string> SubsonicMutationMethods = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "addchatmessage",
        "changePassword",
        "createbookmark",
        "createinternetRadioStation",
        "createplaylist",
        "createshare",
        "createuser",
        "deletebookmark",
        "deleteinternetRadioStation",
        "deleteplaylist",
        "deletepodcastepisode",
        "deleteshare",
        "deleteuser",
        "downloadpodcastepisode",
        "jukeboxcontrol",
        "refreshpodcasts",
        "saveplayqueue",
        "scrobble",
        "setrating",
        "star",
        "startscan",
        "unstar",
        "updateinternetRadioStation",
        "updateplaylist",
        "updateshare",
        "updateuser"
    };

    private readonly RequestDelegate _next;

    public DurableMutationGuardMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(
        HttpContext context,
        DurableStorageOptions options,
        DurableStorageState storageState,
        IDurableStorageRuntimeProbe storageProbe)
    {
        if (!options.EnforceMutationGuard ||
            IsReadOnly(context.Request) ||
            IsOperationalOrRecoveryPath(context.Request.Path))
        {
            await _next(context);
            return;
        }

        await storageProbe.CheckAsync(context.RequestAborted);
        var snapshot = storageState.GetSnapshot();
        if (snapshot.Readiness == DurableStorageReadiness.Ready)
        {
            await _next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.ContentType = "application/problem+json";
        context.Response.Headers.RetryAfter = "10";
        await context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            type = "https://allstarr.local/problems/durable-storage-unavailable",
            title = "Durable storage is not ready",
            status = StatusCodes.Status503ServiceUnavailable,
            code = snapshot.ErrorCode ?? "durable_storage_not_ready",
            storageProvider = snapshot.Provider.ToString(),
            detail = "State-changing work is paused until the selected durable database is ready."
        }));
    }

    private static bool IsReadOnly(HttpRequest request)
    {
        if (HttpMethods.IsHead(request.Method) || HttpMethods.IsOptions(request.Method))
        {
            return true;
        }

        return HttpMethods.IsGet(request.Method) && !IsSubsonicMutationPath(request.Path);
    }

    private static bool IsSubsonicMutationPath(PathString path)
    {
        var segments = path.Value?
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments is not { Length: 2 } ||
            !segments[0].Equals("rest", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var method = segments[1].EndsWith(".view", StringComparison.OrdinalIgnoreCase)
            ? segments[1][..^5]
            : segments[1];
        return SubsonicMutationMethods.Contains(method);
    }

    private static bool IsOperationalOrRecoveryPath(PathString path) =>
        path.StartsWithSegments("/health") ||
        path.StartsWithSegments("/api/admin/auth");
}
