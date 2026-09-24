using System.Net;
using System.Text;
using allstarr.Controllers;
using allstarr.Models.Settings;
using allstarr.Services.Common;
using allstarr.Services.Jellyfin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace allstarr.Tests;

public class JellyfinAuthenticationProxyTests
{
    [Fact]
    public async Task AuthenticateByName_PreservesJellyfinErrorStatusAndClientIdentity()
    {
        string? forwardedAuthorization = null;
        var handler = new StubHandler(request =>
        {
            forwardedAuthorization = request.Headers.GetValues("Authorization").Single();
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("{\"error\":\"client identity required\"}")
            };
        });
        var clientFactory = new Mock<IHttpClientFactory>();
        clientFactory.Setup(factory => factory.CreateClient(It.IsAny<string>()))
            .Returns(new HttpClient(handler));
        var settings = Options.Create(new JellyfinSettings { Url = "http://jellyfin.local" });
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"Username\":\"josh\",\"Pw\":\"test\"}"));
        context.Request.Headers.Authorization = "MediaBrowser Client=\"Yuzic\", Device=\"Phone\", DeviceId=\"device-1\", Version=\"2.0\"";
        var cache = new RedisCacheService(
            Options.Create(new RedisSettings { Enabled = false }),
            NullLogger<RedisCacheService>.Instance);
        var proxy = new JellyfinProxyService(
            clientFactory.Object,
            settings,
            new HttpContextAccessor { HttpContext = context },
            NullLogger<JellyfinProxyService>.Instance,
            cache);
        var controller = new JellyfinController(
            settings,
            Options.Create(new SpotifyImportSettings()),
            Options.Create(new SpotifyApiSettings()),
            Options.Create(new ScrobblingSettings()),
            null!, null!, null!, null!, null!,
            proxy, null!, null!, cache,
            new ConfigurationBuilder().Build(),
            NullLogger<JellyfinController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };

        var result = await controller.AuthenticateByName();

        var content = Assert.IsType<ContentResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, content.StatusCode);
        Assert.Equal("{\"error\":\"client identity required\"}", content.Content);
        Assert.Equal(context.Request.Headers.Authorization.ToString(), forwardedAuthorization);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(send(request));
    }
}
