using allstarr.Core.ClientEvents;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers.ClientApi;

[Route("allstarr/v1/events")]
public sealed class EventsController(ClientEventHub hub, ClientEventOptions options, IHostApplicationLifetime lifetime) : ClientApiControllerBase
{
    [HttpGet]
    [EndpointName("getEvents")]
    [ProducesResponseType<string>(200, "text/event-stream")]
    public async Task<IActionResult> Get()
    {
        using var subscription = hub.Subscribe(Execution.RequireActor().EffectiveUserId!.Value, Execution.Client.DeviceId);
        if (subscription == null) return StatusCode(503);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted, lifetime.ApplicationStopping);
        var token = stop.Token;
        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-store, no-transform";
        Response.Headers["X-Accel-Buffering"] = "no";
        HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        Task<ClientEvent?>? next = null;
        try
        {
            await WriteAsync("event: ready\ndata: {}\n\n", token);
            using var timer = new PeriodicTimer(options.HeartbeatInterval);
            var heartbeat = timer.WaitForNextTickAsync(token).AsTask();
            while (!token.IsCancellationRequested)
            {
                next ??= subscription.ReadAsync(token);
                await Task.WhenAny(next, heartbeat);
                if (heartbeat.IsCompleted)
                {
                    if (!await heartbeat) break;
                    await WriteAsync(": ping\n\n", token);
                    heartbeat = timer.WaitForNextTickAsync(token).AsTask();
                }
                if (next.IsCompleted)
                {
                    var value = await next;
                    next = null;
                    if (value == null) break;
                    await WriteAsync($"event: {value.Type}\ndata: {value.Json}\n\n", token);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (IOException) { }
        finally
        {
            await stop.CancelAsync();
            if (next != null)
                try { await next; } catch (OperationCanceledException) { }
        }
        return new EmptyResult();
    }

    private async Task WriteAsync(string frame, CancellationToken token)
    {
        await Response.WriteAsync(frame, token);
        await Response.Body.FlushAsync(token);
    }
}
