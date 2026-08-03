using System.Net;
using System.Text.Json;
using allstarr.Filters;
using allstarr.Services.Subsonic;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;

namespace allstarr.Tests;

public sealed class SubsonicExceptionFilterTests
{
    public static TheoryData<Exception, bool, int, int> SafeMappings => new()
    {
        { new FileNotFoundException("raw id"), false, 70, 404 },
        { new KeyNotFoundException("raw provider"), false, 70, 404 },
        { new HttpRequestException("raw", null, HttpStatusCode.NotFound), false, 70, 404 },
        { new UnauthorizedAccessException("raw token"), false, 50, 403 },
        { new HttpRequestException("raw", null, HttpStatusCode.Forbidden), false, 50, 403 },
        { new OperationCanceledException("raw"), true, 0, 499 },
        { new OperationCanceledException("raw"), false, 0, 504 },
        { new TimeoutException("raw"), false, 0, 504 },
        { new HttpRequestException("raw", null, HttpStatusCode.TooManyRequests), false, 0, 429 },
        { new NotSupportedException("raw"), false, 0, 503 },
        { new InvalidOperationException("raw"), false, 0, 503 },
        { new HttpRequestException("raw secret"), false, 0, 502 },
        { new IOException("raw path"), false, 0, 502 },
        { new ArgumentException("raw input"), false, 0, 400 },
        { new Exception("raw internals"), false, 0, 500 }
    };

    [Theory]
    [MemberData(nameof(SafeMappings))]
    public void Map_CoversKnownFailuresWithoutReturningRawMessages(
        Exception exception,
        bool requestAborted,
        int expectedCode,
        int expectedStatus)
    {
        var mapped = SubsonicExceptionFilter.Map(exception, requestAborted);

        Assert.Equal(expectedCode, mapped.Code);
        Assert.Equal(expectedStatus, mapped.StatusCode);
        Assert.DoesNotContain("raw", mapped.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OnException_UsesVerifiedJsonFormatAndSafeEnvelope()
    {
        var http = new DefaultHttpContext();
        http.Items[SubsonicAuthFilter.RequestParametersItemKey] =
            SubsonicRequestParameters.FromDictionary(new Dictionary<string, string> { ["f"] = "json" });
        var context = new ExceptionContext(
            new ActionContext(http, new RouteData(), new ActionDescriptor()),
            [])
        {
            Exception = new UnauthorizedAccessException("raw credential")
        };
        var filter = new SubsonicExceptionFilter(
            new SubsonicResponseBuilder(),
            NullLogger<SubsonicExceptionFilter>.Instance);

        filter.OnException(context);

        var result = Assert.IsType<JsonResult>(context.Result);
        Assert.Equal(403, result.StatusCode);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
        var error = json.RootElement.GetProperty("subsonic-response").GetProperty("error");
        Assert.Equal(50, error.GetProperty("code").GetInt32());
        Assert.DoesNotContain("raw credential", error.GetProperty("message").GetString());
        Assert.True(context.ExceptionHandled);
    }
}
