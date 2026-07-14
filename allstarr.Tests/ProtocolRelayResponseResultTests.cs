using System.Net;
using allstarr.Core.Protocols;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Tests;

public sealed class ProtocolRelayResponseResultTests
{
    [Fact]
    public async Task ExecuteResultAsync_PreservesStatusHeadersContentTypeAndRawBody()
    {
        var upstream = new HttpResponseMessage(HttpStatusCode.PartialContent)
        {
            Content = new ByteArrayContent([0, 1, 2, 255])
        };
        upstream.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"fixture\"");
        upstream.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        upstream.Content.Headers.ContentRange =
            new System.Net.Http.Headers.ContentRangeHeaderValue(0, 3, 9);
        var http = new DefaultHttpContext();
        http.Response.Body = new MemoryStream();

        await new ProtocolRelayResponseResult(upstream).ExecuteResultAsync(
            new ActionContext { HttpContext = http });

        Assert.Equal(StatusCodes.Status206PartialContent, http.Response.StatusCode);
        Assert.Equal("\"fixture\"", http.Response.Headers.ETag);
        Assert.Equal("bytes 0-3/9", http.Response.Headers.ContentRange);
        Assert.Equal("application/octet-stream", http.Response.ContentType);
        Assert.Equal([0, 1, 2, 255], ((MemoryStream)http.Response.Body).ToArray());
    }

    [Fact]
    public async Task ExecuteResultAsync_HeadPreservesLengthWithoutWritingBodyOrHopByHopHeaders()
    {
        var upstream = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1, 2, 3])
        };
        upstream.Content.Headers.ContentLength = 3;
        upstream.Headers.Connection.Add("keep-alive");
        var http = new DefaultHttpContext();
        http.Request.Method = HttpMethods.Head;
        http.Response.Body = new MemoryStream();

        await new ProtocolRelayResponseResult(upstream).ExecuteResultAsync(
            new ActionContext { HttpContext = http });

        Assert.Equal("3", http.Response.Headers["Content-Length"].ToString());
        Assert.False(http.Response.Headers.ContainsKey("Connection"));
        Assert.Empty(((MemoryStream)http.Response.Body).ToArray());
    }
}
