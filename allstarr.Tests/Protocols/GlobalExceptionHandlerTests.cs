using System.Text.Json;
using allstarr.Core.Protocols;
using allstarr.Middleware;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.Logging.Abstractions;

namespace allstarr.Tests;

public sealed class GlobalExceptionHandlerTests
{
    [Fact]
    public async Task SavedSubsonicEndpoint_UsesSubsonicErrorsWithoutVerifiedExecution()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/getPlaylists";
        context.Response.Body = new MemoryStream();
        var error = new UnauthorizedAccessException();
        context.Features.Set<IExceptionHandlerFeature>(new ExceptionHandlerFeature
        {
            Error = error,
            Endpoint = new Endpoint(_ => Task.CompletedTask,
                new EndpointMetadataCollection(new ControllerActionDescriptor { ControllerName = "Subsonic" }), "fixture")
        });
        var handler = new GlobalExceptionHandler(NullLogger<GlobalExceptionHandler>.Instance);
        Assert.True(await handler.TryHandleAsync(context, error, default));
        Assert.Equal(401, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        using var body = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal(40, body.RootElement.GetProperty("subsonic-response").GetProperty("error").GetProperty("code").GetInt32());
    }

    [Theory]
    [InlineData(ProtocolKind.Jellyfin, "/Items", 0, 403, false)]
    [InlineData(ProtocolKind.Subsonic, "/getPlaylists", 0, 401, true)]
    [InlineData(ProtocolKind.Unknown, "/rest/getPlaylists.view", 0, 401, true)]
    [InlineData(ProtocolKind.Unknown, "/api/admin/config", 5275, 401, true)]
    public async Task PermissionErrors_RespectProtocolAndAdminBoundary(ProtocolKind protocol, string path, int port, int status, bool subsonic)
    {
        var context = new DefaultHttpContext();
        context.Connection.LocalPort = port;
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        if (protocol != ProtocolKind.Unknown)
            context.Items[ProtocolExecutionContextFactory.HttpContextItemKey] = new ProtocolExecutionContext(
                protocol, "primary", "listener", null, "fixture", DateTimeOffset.UtcNow.AddMinutes(1), default);
        var handler = new GlobalExceptionHandler(NullLogger<GlobalExceptionHandler>.Instance);
        Assert.True(await handler.TryHandleAsync(context, new UnauthorizedAccessException("Private permission detail"), default));
        Assert.Equal(status, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        using var body = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal(subsonic, body.RootElement.TryGetProperty("subsonic-response", out _));
        if (!subsonic)
        {
            Assert.StartsWith("application/problem+json", context.Response.ContentType);
            Assert.Equal(status, body.RootElement.GetProperty("status").GetInt32());
            Assert.Equal("Forbidden", body.RootElement.GetProperty("title").GetString());
            Assert.False(body.RootElement.TryGetProperty("detail", out _));
        }
    }
}
