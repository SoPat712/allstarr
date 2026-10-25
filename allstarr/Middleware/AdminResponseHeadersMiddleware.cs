namespace allstarr.Middleware;

public sealed class AdminResponseHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        if (context.Connection.LocalPort == 5275)
        {
            context.Response.OnStarting(static state =>
            {
                var response = (HttpResponse)state;
                response.Headers["X-Content-Type-Options"] = "nosniff";
                response.Headers["X-Frame-Options"] = "DENY";
                response.Headers.Append("Content-Security-Policy", "frame-ancestors 'none'");
                return Task.CompletedTask;
            }, context.Response);
        }

        return next(context);
    }
}
