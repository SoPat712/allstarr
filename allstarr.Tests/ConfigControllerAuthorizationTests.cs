using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using allstarr.Controllers;
using allstarr.Models.Admin;
using allstarr.Models.Settings;
using allstarr.Services.Admin;
using allstarr.Services.Common;
using allstarr.Services.Spotify;

namespace allstarr.Tests;

public class ConfigControllerAuthorizationTests
{
    [Fact]
    public async Task UpdateConfig_WithoutAdminSession_ReturnsForbidden()
    {
        var controller = CreateController(CreateHttpContextWithSession(isAdmin: false));
        var result = await controller.UpdateConfig(new ConfigUpdateRequest
        {
            Updates = new Dictionary<string, string> { ["TEST_KEY"] = "value" }
        });

        AssertForbidden(result);
    }

    [Fact]
    public async Task RestartContainer_WithoutAdminSession_ReturnsForbidden()
    {
        var controller = CreateController(CreateHttpContextWithSession(isAdmin: false));
        var result = await controller.RestartContainer();

        AssertForbidden(result);
    }

    [Fact]
    public void ExportEnv_WithoutAdminSession_ReturnsForbidden()
    {
        var controller = CreateController(CreateHttpContextWithSession(isAdmin: false));
        var result = controller.ExportEnv();

        AssertForbidden(result);
    }

    [Fact]
    public async Task ImportEnv_WithoutAdminSession_ReturnsForbidden()
    {
        var controller = CreateController(CreateHttpContextWithSession(isAdmin: false));
        var file = new FormFile(Stream.Null, 0, 0, "file", "config.env");
        var result = await controller.ImportEnv(file);

        AssertForbidden(result);
    }

    [Fact]
    public async Task UpdateConfig_WithAdminSession_ContinuesToValidation()
    {
        var controller = CreateController(CreateHttpContextWithSession(isAdmin: true));
        var result = await controller.UpdateConfig(new ConfigUpdateRequest());

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, badRequest.StatusCode);
    }

    [Fact]
    public void ExportEnv_WithAdminSession_WhenFeatureDisabled_ReturnsNotFound()
    {
        var controller = CreateController(CreateHttpContextWithSession(isAdmin: true));
        var result = controller.ExportEnv();

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        Assert.Equal(StatusCodes.Status404NotFound, notFound.StatusCode);
    }

    private static HttpContext CreateHttpContextWithSession(bool isAdmin)
    {
        var context = new DefaultHttpContext();
        context.Connection.LocalPort = 5275;
        context.Items[AdminAuthSessionService.HttpContextSessionItemKey] = new AdminAuthSession
        {
            SessionId = "session-id",
            UserId = "user-id",
            UserName = "user",
            IsAdministrator = isAdmin,
            JellyfinAccessToken = "token",
            JellyfinServerId = "server-id",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
            LastSeenUtc = DateTime.UtcNow
        };

        return context;
    }

    private static ConfigController CreateController(
        HttpContext httpContext,
        Dictionary<string, string?>? configValues = null)
    {
        var logger = new Mock<ILogger<ConfigController>>();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues ?? new Dictionary<string, string?>())
            .Build();

        var webHostEnvironment = new Mock<IWebHostEnvironment>();
        webHostEnvironment.SetupGet(e => e.EnvironmentName).Returns(Environments.Development);
        webHostEnvironment.SetupGet(e => e.ContentRootPath).Returns(Directory.GetCurrentDirectory());
        var helperLogger = new Mock<ILogger<AdminHelperService>>();
        var helperService = new AdminHelperService(
            helperLogger.Object,
            Options.Create(new JellyfinSettings()),
            webHostEnvironment.Object);

        var redisLogger = new Mock<ILogger<RedisCacheService>>();
        var redisCache = new RedisCacheService(
            Options.Create(new RedisSettings
            {
                Enabled = false,
                ConnectionString = "localhost:6379"
            }),
            redisLogger.Object);
        var spotifyCookieLogger = new Mock<ILogger<SpotifySessionCookieService>>();
        var spotifySessionCookieService = new SpotifySessionCookieService(
            Options.Create(new SpotifyApiSettings()),
            helperService,
            spotifyCookieLogger.Object);

        var controller = new ConfigController(
            logger.Object,
            configuration,
            Options.Create(new SpotifyApiSettings()),
            Options.Create(new JellyfinSettings()),
            Options.Create(new SubsonicSettings()),
            Options.Create(new DeezerSettings()),
            Options.Create(new QobuzSettings()),
            Options.Create(new SquidWTFSettings()),
            Options.Create(new AppleMusicSettings()),
            Options.Create(new MusicBrainzSettings()),
            Options.Create(new SpotifyImportSettings()),
            Options.Create(new ScrobblingSettings()),
            helperService,
            spotifySessionCookieService,
            redisCache)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = httpContext
            }
        };

        return controller;
    }

    private static void AssertForbidden(IActionResult result)
    {
        var forbidden = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);

        var payload = JsonSerializer.Serialize(forbidden.Value);
        using var document = JsonDocument.Parse(payload);
        Assert.Equal("Administrator permissions required", document.RootElement.GetProperty("error").GetString());
    }
}
