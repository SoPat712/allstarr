using System.Net;
using System.Net.Http.Headers;
using allstarr.Core.Protocols;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Tests;

public sealed class ProtocolStreamingResponseAdapterTests
{
    private readonly ProtocolStreamingResponseAdapter _adapter = new();

    [Fact]
    public void ForwardRangeRequestHeaders_PreservesRangeAndValidator()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Range = "bytes=8-15";
        context.Request.Headers.IfRange = "\"fixture-etag\"";
        using var request = new HttpRequestMessage(HttpMethod.Head, "http://backend.test/audio");

        _adapter.ForwardRangeRequestHeaders(context.Request.Headers, request);

        Assert.Equal("bytes=8-15", request.Headers.GetValues("Range").Single());
        Assert.Equal("\"fixture-etag\"", request.Headers.GetValues("If-Range").Single());
        Assert.Equal(HttpMethod.Head, request.Method);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateAsync_PreservesPartialStatusHeadersAndContentType(
        bool enableRangeProcessing)
    {
        var context = new DefaultHttpContext();
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
        {
            Content = new ByteArrayContent([1, 2, 3, 4])
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/flac");
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(8, 11, 32);
        response.Headers.AcceptRanges.Add("bytes");
        response.Headers.ETag = new EntityTagHeaderValue("\"fixture-etag\"");

        var result = await _adapter.CreateAsync(
            context,
            response,
            CancellationToken.None,
            enableRangeProcessing);

        var file = Assert.IsType<FileStreamResult>(result);
        Assert.Equal(StatusCodes.Status206PartialContent, context.Response.StatusCode);
        Assert.Equal("audio/flac", file.ContentType);
        Assert.Equal(enableRangeProcessing, file.EnableRangeProcessing);
        Assert.Equal("bytes 8-11/32", context.Response.Headers.ContentRange);
        Assert.Equal("bytes", context.Response.Headers.AcceptRanges);
        Assert.Equal("\"fixture-etag\"", context.Response.Headers.ETag);
    }

    [Fact]
    public void CreateTransportFailure_MapsClientCancellationWithoutAResponseBody()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();

        var result = ProtocolStreamingResponseAdapter.CreateTransportFailure(
            source.Token,
            new OperationCanceledException(source.Token),
            "must not be exposed");

        var status = Assert.IsType<StatusCodeResult>(result);
        Assert.Equal(499, status.StatusCode);
    }
}
