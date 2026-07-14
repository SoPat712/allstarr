using Microsoft.AspNetCore.Mvc;

namespace allstarr.Core.Protocols;

public sealed class ProtocolStreamingResponseAdapter
{
    private static readonly string[] ForwardedResponseHeaders =
    [
        "Accept-Ranges",
        "Content-Range",
        "Content-Length",
        "ETag",
        "Last-Modified",
        "Cache-Control"
    ];

    public void ForwardRangeRequestHeaders(
        IHeaderDictionary incomingHeaders,
        HttpRequestMessage request)
    {
        ForwardHeader(incomingHeaders, request, "Range");
        ForwardHeader(incomingHeaders, request, "If-Range");
    }

    public async Task<IActionResult> CreateAsync(
        HttpContext context,
        HttpResponseMessage response,
        CancellationToken cancellationToken,
        bool enableRangeProcessing)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(response);

        if (!response.IsSuccessStatusCode)
        {
            var statusCode = (int)response.StatusCode;
            response.Dispose();
            return new StatusCodeResult(statusCode);
        }

        context.Response.RegisterForDispose(response);
        context.Response.StatusCode = (int)response.StatusCode;
        foreach (var header in ForwardedResponseHeaders)
        {
            if (response.Headers.TryGetValues(header, out var values) ||
                response.Content.Headers.TryGetValues(header, out values))
            {
                context.Response.Headers[header] = values.ToArray();
            }
        }

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return new FileStreamResult(
            stream,
            response.Content.Headers.ContentType?.ToString() ?? "audio/mpeg")
        {
            EnableRangeProcessing = enableRangeProcessing
        };
    }

    public static IActionResult CreateTransportFailure(
        CancellationToken requestAborted,
        Exception exception,
        string message)
    {
        if (requestAborted.IsCancellationRequested && exception is OperationCanceledException)
        {
            return new StatusCodeResult(499);
        }

        return new ObjectResult(new { error = message })
        {
            StatusCode = StatusCodes.Status500InternalServerError
        };
    }

    private static void ForwardHeader(
        IHeaderDictionary incomingHeaders,
        HttpRequestMessage request,
        string name)
    {
        if (incomingHeaders.TryGetValue(name, out var values))
        {
            request.Headers.TryAddWithoutValidation(name, values.ToArray());
        }
    }
}
