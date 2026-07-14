using System.Text.Json;
using allstarr.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Tests;

public sealed class ProxyResponseResultFactoryTests
{
    [Theory]
    [InlineData(StatusCodes.Status401Unauthorized)]
    [InlineData(StatusCodes.Status403Forbidden)]
    [InlineData(StatusCodes.Status404NotFound)]
    [InlineData(StatusCodes.Status503ServiceUnavailable)]
    public void Create_JsonBodyPreservesUpstreamFailureStatus(int statusCode)
    {
        using var document = JsonDocument.Parse("""{"error":"upstream failure"}""");

        var result = ProxyResponseResultFactory.Create(document, statusCode);

        var json = Assert.IsType<JsonResult>(result);
        Assert.Equal(statusCode, json.StatusCode);
        Assert.Contains("upstream failure", JsonSerializer.Serialize(json.Value), StringComparison.Ordinal);
    }

    [Fact]
    public void Create_EmptyUnauthorizedResponseUsesUnauthorizedResult()
    {
        var result = ProxyResponseResultFactory.Create(null, StatusCodes.Status401Unauthorized);

        Assert.IsType<UnauthorizedResult>(result);
    }

    [Fact]
    public void Create_SuccessFallbackPreservesUpstreamStatus()
    {
        var result = ProxyResponseResultFactory.Create(
            null,
            StatusCodes.Status201Created,
            new { created = true });

        var json = Assert.IsType<JsonResult>(result);
        Assert.Equal(StatusCodes.Status201Created, json.StatusCode);
    }
}
