namespace allstarr.Controllers;

internal static class AdminProcessRestart
{
    public static void Schedule(HttpContext context, IHostApplicationLifetime? lifetime = null)
    {
        if (context.RequestServices?.GetService<IConfiguration>() is { } configuration)
            configuration["Setup:RestartPending"] = "true";
        lifetime ??= context.RequestServices?.GetService<IHostApplicationLifetime>();
        if (lifetime is not null)
            context.Response.OnCompleted(() => { lifetime.StopApplication(); return Task.CompletedTask; });
    }
}
