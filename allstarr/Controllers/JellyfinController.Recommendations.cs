using allstarr.Core.Caching;
using System.Text.Json;
using allstarr.Core.Protocols;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace allstarr.Controllers;

public partial class JellyfinController
{
    private sealed record RecommendationResponse(string Body, string ContentType, int Status);

    private async Task<IActionResult> ReadRecommendationAsync(string itemId, int limit, string? fields, string? userId,
        string kind, Func<Task<IActionResult>> read)
    {
        var original = HttpContext.RequireProtocolExecutionContext();
        var key = CacheKeyBuilder.BuildProtocolMetadataKey("allstarr", JsonSerializer.Serialize(new
        {
            original.Protocol,
            original.BackendInstanceId,
            original.VerifiedBackendPrincipalId,
            original.Actor,
            original.Policy,
            original.Client
        }), "recommendation", JsonSerializer.Serialize(new
        {
            itemId,
            limit,
            fields,
            userId,
            kind,
            Request.Path,
            Query = Request.Query.Where(pair => !pair.Key.Contains("token", StringComparison.OrdinalIgnoreCase) &&
                !pair.Key.Contains("auth", StringComparison.OrdinalIgnoreCase) && !pair.Key.Contains("key", StringComparison.OrdinalIgnoreCase))
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => new { pair.Key, Value = pair.Value.ToString() })
        }));
        if (await _cache.GetAsync<RecommendationResponse>(key) is { } cached)
        {
            using var body = JsonDocument.Parse(cached.Body);
            if (kind != "similar" || !body.RootElement.TryGetProperty("AllstarrBasedOn", out _))
                return new ContentResult { Content = cached.Body, ContentType = cached.ContentType, StatusCode = cached.Status };
        }
        var clock = HttpContext.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3), clock);
        var aborted = HttpContext.RequestAborted;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(aborted, timeout.Token);
        var bounded = new ProtocolExecutionContext(original.Protocol, original.BackendInstanceId,
            original.VerifiedBackendPrincipalId, original.Principal, original.CorrelationId, clock.GetUtcNow().AddMilliseconds(2700),
            linked.Token, original.Client)
        { Policy = original.Policy, IsReadOnly = original.IsReadOnly };
        HttpContext.Items[ProtocolExecutionContextFactory.HttpContextItemKey] = bounded;
        try
        {
            var pending = read();
            _ = pending.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            var result = await pending.WaitAsync(linked.Token);
            var options = HttpContext.RequestServices.GetService<IOptions<JsonOptions>>()?.Value.JsonSerializerOptions;
            var response = result switch
            {
                ContentResult { StatusCode: null or 200, Content: { } body } content => new RecommendationResponse(body, content.ContentType ?? "application/json", 200),
                JsonResult { StatusCode: null or 200 } json => new RecommendationResponse(
                    JsonSerializer.Serialize(json.Value, json.SerializerSettings as JsonSerializerOptions ?? options), json.ContentType ?? "application/json", 200),
                _ => null
            };
            if (response != null && !linked.IsCancellationRequested)
            {
                using var body = JsonDocument.Parse(response.Body);
                if (body.RootElement.TryGetProperty("Items", out var items) && items.GetArrayLength() > 0 &&
                    (kind != "similar" || !body.RootElement.TryGetProperty("AllstarrBasedOn", out _)))
                    await _cache.SetAsync(key, response, TimeSpan.FromHours(6));
            }
            return result;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !aborted.IsCancellationRequested)
        {
            return kind == "instant" ? CreateProtocolResponse(_interactionProtocolAdapter.ShapeInstantMix([])) :
                _responseBuilder.CreateJsonResponse(new { Items = Array.Empty<object>(), TotalRecordCount = 0, StartIndex = 0 });
        }
        finally
        {
            HttpContext.Items[ProtocolExecutionContextFactory.HttpContextItemKey] = original;
        }
    }
}
