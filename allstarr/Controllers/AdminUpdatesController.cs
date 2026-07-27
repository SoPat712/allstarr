using System.Text.Json;
using allstarr.Filters;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin/updates")]
[ServiceFilter(typeof(AdminPortFilter))]
public sealed class AdminUpdatesController(AdminUpdateFeed feed) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [HttpGet("stream")]
    public async Task Stream(CancellationToken cancellationToken)
    {
        if (!HttpContext.Items.TryGetValue(AdminAuthSessionService.HttpContextSessionItemKey, out var value) ||
            value is not AdminAuthSession { TenantId: { } tenantId } session ||
            (!session.IsAdministrator && !session.AllstarrUserId.HasValue))
        {
            Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var lastEventId = Request.Headers["Last-Event-ID"].FirstOrDefault();
        var recovered = !string.IsNullOrWhiteSpace(lastEventId);
        var cursor = AdminUpdateCursor.Now();
        if (recovered && !AdminUpdateCursor.TryParse(lastEventId, out cursor))
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            await Response.WriteAsJsonAsync(new { error = "Invalid Last-Event-ID." }, cancellationToken);
            return;
        }

        var untilExpiry = session.ExpiresAtUtc - DateTime.UtcNow;
        if (untilExpiry <= TimeSpan.Zero)
        {
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache, no-transform";
        Response.Headers.Connection = "keep-alive";
        Response.Headers["X-Accel-Buffering"] = "no";

        using var expiry = new CancellationTokenSource(untilExpiry);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            HttpContext.RequestAborted,
            expiry.Token);
        var token = linked.Token;
        var lastWrite = DateTimeOffset.UtcNow;

        await WriteEventAsync(
            "stream-status",
            null,
            new { state = "live", recovered },
            token);

        try
        {
            while (!token.IsCancellationRequested)
            {
                var updates = await feed.ReadAsync(
                    new AdminUpdateScope(tenantId, session.AllstarrUserId, session.IsAdministrator),
                    cursor,
                    100,
                    token);

                foreach (var update in updates)
                {
                    await WriteEventAsync("update", update.EventId, update, token);
                    AdminUpdateCursor.TryParse(update.EventId, out cursor);
                    lastWrite = DateTimeOffset.UtcNow;
                }

                if (DateTimeOffset.UtcNow - lastWrite >= TimeSpan.FromSeconds(15))
                {
                    await Response.WriteAsync(": keepalive\n\n", token);
                    await Response.Body.FlushAsync(token);
                    lastWrite = DateTimeOffset.UtcNow;
                }

                if (updates.Count < 100)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), token);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private async Task WriteEventAsync(
        string eventName,
        string? eventId,
        object payload,
        CancellationToken cancellationToken)
    {
        if (eventId is not null)
        {
            await Response.WriteAsync($"id: {eventId}\n", cancellationToken);
        }

        await Response.WriteAsync($"event: {eventName}\n", cancellationToken);
        await Response.WriteAsync("retry: 2000\n", cancellationToken);
        await Response.WriteAsync($"data: {JsonSerializer.Serialize(payload, JsonOptions)}\n\n", cancellationToken);
        await Response.Body.FlushAsync(cancellationToken);
    }
}
