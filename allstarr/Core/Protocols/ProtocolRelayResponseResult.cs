using Microsoft.AspNetCore.Mvc;

namespace allstarr.Core.Protocols;

public sealed class ProtocolRelayResponseResult(
    HttpResponseMessage upstream,
    bool suppressBody = false) : IActionResult
{
    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection",
        "Keep-Alive",
        "Proxy-Authenticate",
        "Proxy-Authorization",
        "TE",
        "Trailer",
        "Transfer-Encoding",
        "Upgrade"
    };

    public async Task ExecuteResultAsync(ActionContext context)
    {
        using (upstream)
        {
            var response = context.HttpContext.Response;
            response.StatusCode = (int)upstream.StatusCode;
            CopyHeaders(upstream.Headers, response);
            CopyHeaders(upstream.Content.Headers, response);

            if (upstream.Content.Headers.ContentType != null)
            {
                response.ContentType = upstream.Content.Headers.ContentType.ToString();
            }

            if (suppressBody ||
                HttpMethods.IsHead(context.HttpContext.Request.Method) ||
                upstream.StatusCode is System.Net.HttpStatusCode.NoContent or
                    System.Net.HttpStatusCode.NotModified)
            {
                return;
            }

            await upstream.Content.CopyToAsync(
                response.Body,
                context.HttpContext.RequestAborted);
        }
    }

    private static void CopyHeaders(
        System.Net.Http.Headers.HttpHeaders source,
        HttpResponse target)
    {
        foreach (var header in source)
        {
            if (!HopByHopHeaders.Contains(header.Key) &&
                !header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                target.Headers[header.Key] = header.Value.ToArray();
            }
        }
    }
}
