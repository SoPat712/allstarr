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
    public void GetStatus_ReturnsOk()
    {
        var controller = CreateController(
            CreateSettings(username: null, password: null),
            new HttpResponseMessage(HttpStatusCode.OK));

        var result = controller.GetStatus();
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
    public async Task AuthenticateLastFm_WhenSessionSaveFails_DoesNotExposeSessionKey()
    {
        var sessionKey = "super-secret-session-key";
        var successXml = $"<lfm status='ok'><session><name>testuser</name><key>{sessionKey}</key></session></lfm>";

        var controller = CreateController(
            CreateSettings("testuser", "password123"),
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(successXml, Encoding.UTF8, "application/xml")
            },
            adminHelper: null);

        var result = await controller.AuthenticateLastFm();
        var serverError = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status500InternalServerError, serverError.StatusCode);

        var payload = JsonSerializer.Serialize(serverError.Value);
        Assert.DoesNotContain("sessionKey", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(sessionKey, payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthenticateLastFm_SuccessResponse_DoesNotIncludeSessionKey()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "allstarr-tests", Guid.NewGuid().ToString("N"), "app");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var successXml = "<lfm status='ok'><session><name>testuser</name><key>secret-session-key</key></session></lfm>";

            var adminHelper = CreateAdminHelperService(tempRoot);
            var controller = CreateController(
                CreateSettings("testuser", "password123"),
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(successXml, Encoding.UTF8, "application/xml")
                },
                adminHelper);

            var result = await controller.AuthenticateLastFm();
            var ok = Assert.IsType<OkObjectResult>(result);
            Assert.Equal(StatusCodes.Status200OK, ok.StatusCode);

            var payload = JsonSerializer.Serialize(ok.Value);
            using var document = JsonDocument.Parse(payload);
            Assert.False(document.RootElement.TryGetProperty("SessionKey", out _));
            Assert.True(document.RootElement.GetProperty("Success").GetBoolean());
        }
        finally
        {
            var testRoot = Path.GetDirectoryName(tempRoot);
            if (!string.IsNullOrEmpty(testRoot) && Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ValidateListenBrainzToken_WhenSaveFails_DoesNotExposeUserToken()
    {
        var userToken = "listenbrainz-secret-token";
        var validResponse = "{\"valid\":true,\"user_name\":\"listener\"}";

        var controller = CreateController(
            CreateSettings("testuser", "password123"),
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(validResponse, Encoding.UTF8, "application/json")
            },
            adminHelper: null);

        var result = await controller.ValidateListenBrainzToken(
            new ScrobblingAdminController.ValidateTokenRequest { UserToken = userToken });
        var serverError = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status500InternalServerError, serverError.StatusCode);

        var payload = JsonSerializer.Serialize(serverError.Value);
        Assert.DoesNotContain("userToken", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(userToken, payload, StringComparison.Ordinal);
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
                ApiKey = "cb3bdcd415fcb40cd572b137b2b255f5",
                SharedSecret = "3a08f9fad6ddc4c35b0dce0062cecb5e",
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

    private static AdminHelperService CreateAdminHelperService(string contentRootPath)
    {
        var helperLogger = new Mock<ILogger<AdminHelperService>>();
        var webHostEnvironment = new Mock<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
        webHostEnvironment.SetupGet(e => e.EnvironmentName).Returns(Environments.Development);
        webHostEnvironment.SetupGet(e => e.ContentRootPath).Returns(contentRootPath);

        return new AdminHelperService(
            helperLogger.Object,
            Options.Create(new JellyfinSettings()),
            webHostEnvironment.Object);
    }

    private static ScrobblingAdminController CreateController(
        ScrobblingSettings settings,
        HttpResponseMessage httpResponse,
        AdminHelperService? adminHelper = null)
    {
        var mockSettings = new Mock<IOptions<ScrobblingSettings>>();
        mockSettings.Setup(s => s.Value).Returns(settings);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        var logger = new Mock<ILogger<ScrobblingAdminController>>();
        var httpClientFactory = new Mock<IHttpClientFactory>();

        var httpClient = new HttpClient(new StubHttpMessageHandler(httpResponse));
        httpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        return new ScrobblingAdminController(
            mockSettings.Object,
            configuration,
            httpClientFactory.Object,
            logger.Object,
            adminHelper!);
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
