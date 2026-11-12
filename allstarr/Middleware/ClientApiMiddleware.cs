using allstarr.Core.Configuration;
using allstarr.Core.ClientApi;
using allstarr.Models.Settings;

namespace allstarr.Middleware;

public sealed class ClientApiMiddleware(RequestDelegate next, BackendSelectionAuthority backend,
    ClientApiAvailability availability, ILogger<ClientApiMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/allstarr"))
        {
            await next(context);
            return;
        }

        if (context.Connection.LocalPort != 8080 || backend.Type != BackendType.Jellyfin)
        {
            await ClientApiError.WriteAsync(context, 404);
            return;
        }

        try
        {
            if (!await availability.IsEnabledAsync(context))
            {
                await ClientApiError.WriteAsync(context, 404);
                return;
            }
            await next(context);
            if (context.Response.StatusCode >= 400 && !context.Response.HasStarted)
                await ClientApiError.WriteAsync(context, context.Response.StatusCode);
        }
        catch (Exception exception) when (!context.Response.HasStarted)
        {
            logger.LogError(exception, "Client request failed");
            context.Response.Clear();
            await ClientApiError.WriteAsync(context, 503);
        }
    }
}
