using System.ComponentModel;
using allstarr.Core.ClientApiV2;
using allstarr.Core.ClientEvents;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers.ClientApiV2;

[Route("allstarr/v2")]
public sealed class EventsController(V2EventStream events, ClientEventOptions options, IHostApplicationLifetime lifetime) : ClientApiV2ControllerBase
{
    [HttpHead("ping")]
    [EndpointName("ping")]
    [EndpointDescription("Checks reachability when the event stream is disconnected. Requires a Bearer token.")]
    [ProducesResponseType(204)]
    public IActionResult Ping() => NoContent();

    [HttpGet("events")]
    [EndpointName("getV2Events")]
    [EndpointDescription("Streams listener-scoped JSON payloads with increasing id, event and data lines. " +
        "Replay uses Last-Event-ID (preferred) or lastEventId, retaining at most 1,000 events for 10 minutes. " +
        "An unknown or expired cursor starts with resync {}; refetch client state, then continue live. " +
        "Comments ping every 25 seconds. Connections share the v1 limit: 8 per listener, 256 total; excess returns 503. " +
        "Payloads: track.updated {track: V2Track}, lyrics.updated {trackId, revision} (read views=lyrics), " +
        "match.updated {trackId, state}, keep.updated {trackId, state, progress}, mix.updated {mixId, playlistId}, " +
        "releases.updated {count}, libraryAddition.updated {row}, track.renamed {old, new}. " +
        "Renames reach currently connected v2 listeners. Requires a Bearer token.")]
    [ProducesResponseType<string>(200, "text/event-stream")]
    public async Task<IActionResult> Get(
        [FromHeader(Name = "Last-Event-ID"), Description("The last received event id; takes precedence over the query parameter.")] string? lastEventIdHeader,
        [FromQuery, Description("Replay cursor for clients unable to set Last-Event-ID.")] string? lastEventId)
    {
        using var subscription = events.Subscribe(HttpContext, Execution, lastEventIdHeader ?? lastEventId);
        if (subscription == null) return StatusCode(503);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted, lifetime.ApplicationStopping);
        var token = stop.Token;
        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-store, no-transform";
        Response.Headers["X-Accel-Buffering"] = "no";
        HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        Task<V2StreamEvent?>? next = null;
        try
        {
            await WriteAsync(": connected\n\n", token);
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
                if (!next.IsCompleted) continue;
                var value = await next;
                next = null;
                if (value == null) break;
                await WriteAsync($"id: {value.Id}\nevent: {value.Value.Type}\ndata: {value.Value.Json}\n\n", token);
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
