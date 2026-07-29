using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using allstarr.Middleware;
using allstarr.Models.Settings;

namespace allstarr.Tests;

public class WebSocketProxyMiddlewareTests
{
    [Fact]
    public void BuildMaskedQuery_RedactsSensitiveParams()
    {
        var qs = "?api_key=secret&deviceId=abc&token=othertoken";
        var masked = allstarr.Middleware.WebSocketProxyMiddleware.BuildMaskedQuery(qs);

        Assert.Contains("api_key=<redacted>", masked);
        Assert.Contains("deviceId=abc", masked);
        Assert.Contains("token=<redacted>", masked);
        Assert.DoesNotContain("secret", masked);
        Assert.DoesNotContain("othertoken", masked);
    }

    [Fact]
    public void BuildMaskedQuery_EmptyOrNull_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, allstarr.Middleware.WebSocketProxyMiddleware.BuildMaskedQuery(null));
        Assert.Equal(string.Empty, allstarr.Middleware.WebSocketProxyMiddleware.BuildMaskedQuery(string.Empty));
    }
}
