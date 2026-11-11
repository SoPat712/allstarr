using allstarr.Core.ClientApi;
using allstarr.Core.Protocols;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;

namespace allstarr.Core.ClientApiV2;

/// <summary>
/// Runs several v2 reads in one round trip. It only dispatches to a fixed list of read routes inside this process: it
/// never makes an HTTP call, never follows an arbitrary path and has no writes. Each read gets its own copy of the
/// request state and its own services, so reads can run side by side without sharing per-request caches.
/// </summary>
public sealed class V2BatchDispatcher(IServiceScopeFactory scopes, ILogger<V2BatchDispatcher> logger)
{
    private const string Prefix = "/allstarr/v2/";
    private const int Parallelism = 4;
    private static readonly string[] ListKeys = ["ids", "include"];
    private static readonly string[] TrackKeys = ["include", "views"];

    public async Task<V2BatchResponse> RunAsync(HttpContext http, ProtocolExecutionContext execution, IReadOnlyList<V2BatchItem> requests)
    {
        using var gate = new SemaphoreSlim(Parallelism);
        var results = await Task.WhenAll(requests.Select(async request =>
        {
            await gate.WaitAsync(execution.CancellationToken);
            try { return (request.Id, Result: await RunOneAsync(http, execution, request.Path)); }
            finally { gate.Release(); }
        }));
        return new(results.ToDictionary(item => item.Id, item => item.Result, StringComparer.Ordinal));
    }

    private async Task<V2BatchResult> RunOneAsync(HttpContext parent, ProtocolExecutionContext execution, string path)
    {
        V2Result result;
        try
        {
            result = await DispatchAsync(parent, execution, path);
        }
        catch (Exception error) when (error is not OperationCanceledException || !execution.CancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(error, "Batch read failed");
            result = new(503, null);
        }
        var status = result.Status < 400 ? result.Status : ClientApiError.NormalizeStatus(result.Status);
        return new(status, status < 400 && result.Body != null ? result.Body : ClientApiError.ForStatus(status));
    }

    private async Task<V2Result> DispatchAsync(HttpContext parent, ProtocolExecutionContext execution, string path)
    {
        if (!path.StartsWith(Prefix, StringComparison.Ordinal)) return new(404, null);
        var queryStart = path.IndexOf('?');
        var route = queryStart < 0 ? path[Prefix.Length..] : path[Prefix.Length..queryStart];
        var query = QueryHelpers.ParseQuery(queryStart < 0 ? null : path[queryStart..]);
        if (route.Contains('#') || query.Values.Any(value => value.Count != 1)) return new(400, null);
        var segments = route.Split('/').Select(Uri.UnescapeDataString).ToArray();
        if (segments.Any(segment => segment.Length == 0 || segment.Contains('/'))) return new(404, null);

        await using var scope = scopes.CreateAsyncScope();
        var http = SubRequest(parent, scope.ServiceProvider);
        var tracks = scope.ServiceProvider.GetRequiredService<V2TrackEndpoints>();
        return segments switch
        {
            ["tracks"] when Allowed(query, ListKeys) => await tracks.ListAsync(http, execution, Value(query, "ids"), Value(query, "include")),
            ["tracks", var id] when Allowed(query, TrackKeys) => await tracks.GetAsync(http, execution, id, Value(query, "include"), Value(query, "views")),
            ["tracks"] or ["tracks", _] => new(400, null),
            _ => new(404, null)
        };
    }

    private static bool Allowed(Dictionary<string, StringValues> query, string[] keys) => query.Keys.All(keys.Contains);

    private static string? Value(Dictionary<string, StringValues> query, string key) => query.TryGetValue(key, out var value) ? value.ToString() : null;

    private static DefaultHttpContext SubRequest(HttpContext parent, IServiceProvider services)
    {
        var http = new DefaultHttpContext
        {
            RequestServices = services,
            RequestAborted = parent.RequestAborted,
            User = parent.User,
            TraceIdentifier = parent.TraceIdentifier
        };
        http.Request.Method = HttpMethods.Get;
        http.Request.Scheme = parent.Request.Scheme;
        http.Request.Host = parent.Request.Host;
        http.Connection.LocalPort = parent.Connection.LocalPort;
        http.Connection.RemoteIpAddress = parent.Connection.RemoteIpAddress;
        foreach (var header in parent.Request.Headers.Where(header => !header.Key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase)))
            http.Request.Headers[header.Key] = header.Value;
        foreach (var item in parent.Items) http.Items[item.Key] = item.Value;
        return http;
    }
}
