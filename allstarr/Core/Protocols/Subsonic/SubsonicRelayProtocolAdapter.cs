using allstarr.Services.Subsonic;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Core.Protocols.Subsonic;

public sealed class SubsonicRelayProtocolAdapter
{
    private static readonly HashSet<string> ExcludedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection",
        "Content-Length",
        "Content-Type",
        "Keep-Alive",
        "Proxy-Authenticate",
        "Proxy-Authorization",
        "TE",
        "Trailer",
        "Transfer-Encoding",
        "Upgrade"
    };

    public IActionResult CreateResult(
        SubsonicProxyResponse response,
        string fallbackContentType) => new RelayResult(
            response.Body,
            response.ContentType ?? fallbackContentType,
            (int)response.StatusCode,
            response.Headers);

    private sealed class RelayResult(
        byte[] body,
        string contentType,
        int statusCode,
        IReadOnlyDictionary<string, string[]> headers) : IActionResult
    {
        public async Task ExecuteResultAsync(ActionContext context)
        {
            var response = context.HttpContext.Response;
            response.StatusCode = statusCode;
            response.ContentType = contentType;
            foreach (var (name, values) in headers)
            {
                if (!ExcludedHeaders.Contains(name))
                {
                    response.Headers[name] = values;
                }
            }

            if (body.Length == 0 || HttpMethods.IsHead(context.HttpContext.Request.Method))
            {
                return;
            }

            response.ContentLength = body.Length;
            await response.Body.WriteAsync(body, context.HttpContext.RequestAborted);
        }
    }
}
