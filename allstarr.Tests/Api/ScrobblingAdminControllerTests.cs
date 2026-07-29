using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using allstarr.Controllers;
using allstarr.Models.Settings;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace allstarr.Tests;

public class ScrobblingAdminControllerTests
{
    [Fact]
    public async Task GetStatus_ReturnsOk()
    {
        var controller = CreateController(
            CreateSettings(username: null, password: null),
            new HttpResponseMessage(HttpStatusCode.OK));

        var result = await controller.GetStatus();
        Assert.IsType<OkObjectResult>(result);
    }

    [Theory]
    [InlineData("", "password123")]
    [InlineData("username", "")]
    [InlineData(null, "password123")]
    [InlineData("username", null)]
    public async Task AuthenticateLastFm_MissingCredentials_ReturnsBadRequest(string? username, string? password)
    {
        var controller = CreateController(
            CreateSettings(username, password),
            new HttpResponseMessage(HttpStatusCode.OK));

        var result = await controller.AuthenticateLastFm();
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, badRequest.StatusCode);
    }

    [Fact]
    public async Task AuthenticateLastFm_WithoutProviderAccount_DoesNotCallProvider()
    {
        var controller = CreateController(
            CreateSettings("testuser", "password123"),
            new HttpResponseMessage(HttpStatusCode.OK));

        var result = await controller.AuthenticateLastFm();
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("provider account", JsonSerializer.Serialize(badRequest.Value), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateListenBrainzToken_DoesNotPersistOrExposeUserToken()
    {
        var userToken = "listenbrainz-secret-token";
        var validResponse = "{\"valid\":true,\"user_name\":\"listener\"}";

        var controller = CreateController(
            CreateSettings("testuser", "password123"),
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(validResponse, Encoding.UTF8, "application/json")
            });

        var result = await controller.ValidateListenBrainzToken(
            new ScrobblingAdminController.ValidateTokenRequest { UserToken = userToken });
        var ok = Assert.IsType<OkObjectResult>(result);

        var payload = JsonSerializer.Serialize(ok.Value);
        Assert.DoesNotContain("userToken", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(userToken, payload, StringComparison.Ordinal);
    }

    private const string TestLastFmApiKey = "0123456789abcdef0123456789abcdef";
    private const string TestLastFmSharedSecret = "fedcba9876543210fedcba9876543210";

    [Fact]
    public async Task AuthenticateLastFm_LegacyApiKey_ReturnsBadRequest()
    {
        var settings = CreateSettings("testuser", "password123");
        settings.LastFm.ApiKey = LastFmSettings.LegacyJellyfinPluginApiKey;
        settings.LastFm.SharedSecret = LastFmSettings.LegacyJellyfinPluginSharedSecret;

        var controller = CreateController(
            settings,
            new HttpResponseMessage(HttpStatusCode.OK));

        var result = await controller.AuthenticateLastFm();
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task AuthenticateLastFm_ManagedAccountWithoutSignedInOwner_ReturnsNotFound()
    {
        var controller = CreateController(
            CreateSettings("testuser", "password123"),
            new HttpResponseMessage(HttpStatusCode.OK));

        var result = await controller.AuthenticateLastFm(new ScrobblingAdminController.LastFmAuthenticationRequest
        {
            AccountId = Guid.CreateVersion7(),
            Username = "testuser",
            Password = "request-only-password"
        });

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        Assert.DoesNotContain("request-only-password", JsonSerializer.Serialize(notFound.Value), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthenticateLastFm_UnmanagedRequest_DoesNotEchoPassword()
    {
        const string password = "request-only-password";
        var controller = CreateController(
            CreateSettings("configured-user", "configured-password"),
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<lfm status='failed'><error code='4'>Authentication Failed</error></lfm>", Encoding.UTF8, "application/xml")
            });

        var result = await controller.AuthenticateLastFm(new ScrobblingAdminController.LastFmAuthenticationRequest
        {
            Username = "entered-user",
            Password = password
        });

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var payload = JsonSerializer.Serialize(badRequest.Value);
        Assert.Contains("provider account", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(password, payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestLastFmConnection_Forbidden_ReturnsActionableCredentialError()
    {
        var settings = CreateSettings("testuser", "password123");
        settings.LastFm.SessionKey = "configured-session";
        var controller = CreateController(
            settings,
            new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("forbidden", Encoding.UTF8, "text/plain")
            });

        var result = await controller.TestLastFmConnection();

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var payload = JsonSerializer.Serialize(badRequest.Value);
        Assert.Contains("Last.fm", payload, StringComparison.Ordinal);
        Assert.Contains("403", payload, StringComparison.Ordinal);
        Assert.Contains("re-authenticate", payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TestListenBrainzConnection_UpstreamFailure_ReturnsActionableGatewayError()
    {
        var settings = CreateSettings("testuser", "password123");
        settings.ListenBrainz.UserToken = "configured-token";
        var controller = CreateController(
            settings,
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("unavailable", Encoding.UTF8, "text/plain")
            });

        var result = await controller.TestListenBrainzConnection();

        var gatewayError = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status502BadGateway, gatewayError.StatusCode);
        var payload = JsonSerializer.Serialize(gatewayError.Value);
        Assert.Contains("ListenBrainz", payload, StringComparison.Ordinal);
        Assert.Contains("503", payload, StringComparison.Ordinal);
        Assert.Contains("Try again later", payload, StringComparison.OrdinalIgnoreCase);
    }

    private static ScrobblingSettings CreateSettings(string? username, string? password)
    {
        return new ScrobblingSettings
        {
            Enabled = true,
            LocalTracksEnabled = false,
            LastFm = new LastFmSettings
            {
                Enabled = true,
                ApiKey = TestLastFmApiKey,
                SharedSecret = TestLastFmSharedSecret,
                SessionKey = string.Empty,
                Username = username,
                Password = password
            },
            ListenBrainz = new ListenBrainzSettings
            {
                Enabled = true,
                UserToken = string.Empty
            }
        };
    }

    private static ScrobblingAdminController CreateController(
        ScrobblingSettings settings,
        HttpResponseMessage httpResponse)
    {
        var mockSettings = new Mock<IOptions<ScrobblingSettings>>();
        mockSettings.Setup(s => s.Value).Returns(settings);

        var logger = new Mock<ILogger<ScrobblingAdminController>>();
        var httpClientFactory = new Mock<IHttpClientFactory>();

        var httpClient = new HttpClient(new StubHttpMessageHandler(httpResponse));
        httpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        return new ScrobblingAdminController(
            mockSettings.Object,
            httpClientFactory.Object,
            logger.Object);
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public StubHttpMessageHandler(HttpResponseMessage response)
        {
            _response = response;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(_response);
        }
    }
}
