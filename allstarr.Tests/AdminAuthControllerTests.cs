using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using Moq;
using allstarr.Controllers;
using allstarr.Models.Settings;
using allstarr.Services.Admin;
using allstarr.Core.Identity;

namespace allstarr.Tests;

public class AdminAuthControllerTests
{
    [Fact]
    public async Task Login_WithValidNonAdminJellyfinUser_CreatesSessionAndCookie()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;

        var handler = new DelegateHttpMessageHandler(async (request, _) =>
        {
            capturedRequest = request;
            capturedBody = request.Content is null ? null : await request.Content.ReadAsStringAsync();

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                                            {
                                              "AccessToken":"token-123",
                                              "ServerId":"server-1",
                                              "User":{
                                                "Id":"user-1",
                                                "Name":"josh",
                                                "Policy":{"IsAdministrator":false}
                                              }
                                            }
                                            """)
            };
        });

        var sessionService = AdminAuthSessionTestSupport.Create();
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-Forwarded-Proto"] = "https";

        var controller = CreateController(handler, sessionService, httpContext);
        var result = await controller.Login(new AdminAuthController.LoginRequest
        {
            Username = " josh ",
            Password = "secret-pass"
        });

        var ok = Assert.IsType<OkObjectResult>(result);
        var payloadJson = JsonSerializer.Serialize(ok.Value);
        using var payload = JsonDocument.Parse(payloadJson);

        Assert.True(payload.RootElement.GetProperty("authenticated").GetBoolean());
        Assert.Equal("user-1", payload.RootElement.GetProperty("user").GetProperty("id").GetString());
        Assert.Equal("josh", payload.RootElement.GetProperty("user").GetProperty("name").GetString());
        Assert.False(payload.RootElement.GetProperty("user").GetProperty("isAdministrator").GetBoolean());

        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Post, capturedRequest!.Method);
        Assert.Equal("http://jellyfin.local/Users/AuthenticateByName", capturedRequest.RequestUri?.ToString());
        Assert.Contains("X-Emby-Authorization", capturedRequest.Headers.Select(h => h.Key));

        Assert.NotNull(capturedBody);
        Assert.Contains("\"Username\":\"josh\"", capturedBody!);
        Assert.Contains("\"Pw\":\"secret-pass\"", capturedBody!);

        var setCookies = httpContext.Response.Headers.SetCookie;
        Assert.Single(setCookies);
        var setCookieHeader = setCookies[0] ?? string.Empty;
        Assert.Contains($"{AdminAuthSessionService.SessionCookieName}=", setCookieHeader);
        Assert.Contains("httponly", setCookieHeader.ToLowerInvariant());
        Assert.Contains("secure", setCookieHeader.ToLowerInvariant());
        Assert.Contains("samesite=strict", setCookieHeader.ToLowerInvariant());

        var sessionId = ExtractCookieValue(setCookieHeader);
        var session = await sessionService.GetValidSessionAsync(sessionId);
        Assert.NotNull(session);
        Assert.Equal("user-1", session.UserId);
        Assert.Equal("josh", session.UserName);
        Assert.False(session.IsAdministrator);
    }

    [Fact]
    public async Task Login_WithInvalidCredentials_ReturnsUnauthorized()
    {
        var handler = new DelegateHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));

        var sessionService = AdminAuthSessionTestSupport.Create();
        var httpContext = new DefaultHttpContext();
        var controller = CreateController(handler, sessionService, httpContext);

        var result = await controller.Login(new AdminAuthController.LoginRequest
        {
            Username = "josh",
            Password = "wrong"
        });

        var unauthorized = Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Equal(StatusCodes.Status401Unauthorized, unauthorized.StatusCode);
        Assert.False(httpContext.Response.Headers.ContainsKey("Set-Cookie"));
    }

    [Fact]
    public async Task Login_WithValidSubsonicAdmin_UsesFormPostAndStoresNoPassword()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;
        var handler = new DelegateHttpMessageHandler(async (request, _) =>
        {
            capturedRequest = request;
            capturedBody = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {
                      "subsonic-response": {
                        "status": "ok",
                        "version": "1.16.1",
                        "user": { "username": "alice", "adminRole": true }
                      }
                    }
                    """)
            };
        });
        var sessionService = AdminAuthSessionTestSupport.Create();
        var httpContext = new DefaultHttpContext();
        var controller = CreateController(
            handler,
            sessionService,
            httpContext,
            BackendType.Subsonic);

        var result = await controller.Login(new AdminAuthController.LoginRequest
        {
            Username = "alice",
            Password = "secret-pass"
        });

        var ok = Assert.IsType<OkObjectResult>(result);
        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
        Assert.Equal("Subsonic", payload.RootElement.GetProperty("backend").GetString());
        Assert.True(payload.RootElement.GetProperty("user").GetProperty("isAdministrator").GetBoolean());
        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Post, capturedRequest!.Method);
        Assert.Equal("http://subsonic.local/rest/getUser.view", capturedRequest.RequestUri?.ToString());
        Assert.DoesNotContain("secret-pass", capturedRequest.RequestUri?.ToString());
        Assert.Contains("u=alice", capturedBody);
        Assert.Contains("p=secret-pass", capturedBody);

        var sessionId = ExtractCookieValue(httpContext.Response.Headers.SetCookie[0]!);
        var session = await sessionService.GetValidSessionAsync(sessionId);
        Assert.NotNull(session);
        Assert.Equal("Subsonic", session.BackendType);
        Assert.Equal(string.Empty, session.JellyfinAccessToken);
    }

    [Fact]
    public async Task Login_WithSubsonicProtocolAuthFailure_ReturnsUnauthorized()
    {
        var handler = new DelegateHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"subsonic-response":{"status":"failed","error":{"code":40,"message":"Wrong username or password"}}}
                    """)
            }));
        var controller = CreateController(
            handler,
            AdminAuthSessionTestSupport.Create(),
            new DefaultHttpContext(),
            BackendType.Subsonic);

        var result = await controller.Login(new AdminAuthController.LoginRequest
        {
            Username = "alice",
            Password = "wrong"
        });

        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public async Task GetCurrentSession_WithUnknownCookie_ReturnsUnauthenticated()
    {
        var handler = new DelegateHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

        var sessionService = AdminAuthSessionTestSupport.Create();
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers.Cookie = $"{AdminAuthSessionService.SessionCookieName}=missing-session";

        var controller = CreateController(handler, sessionService, httpContext);
        var result = await controller.GetCurrentSession();

        var ok = Assert.IsType<OkObjectResult>(result);
        var payloadJson = JsonSerializer.Serialize(ok.Value);
        using var payload = JsonDocument.Parse(payloadJson);

        Assert.False(payload.RootElement.GetProperty("authenticated").GetBoolean());
        var setCookies = httpContext.Response.Headers.SetCookie;
        Assert.Equal(3, setCookies.Count);
        Assert.Contains(setCookies, value => value!.Contains($"{AdminAuthSessionService.SessionCookieName}=") &&
                                             value.Contains("expires=", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(ProviderAccountManagementMode.AdminManaged)]
    [InlineData(ProviderAccountManagementMode.UserManaged)]
    [InlineData(ProviderAccountManagementMode.Hybrid)]
    public async Task GetCurrentSession_ExposesOnlyTheNonSecretAccountManagementMode(
        ProviderAccountManagementMode managementMode)
    {
        var handler = new DelegateHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var controller = CreateController(
            handler,
            AdminAuthSessionTestSupport.Create(),
            new DefaultHttpContext(),
            managementMode: managementMode);

        var result = Assert.IsType<OkObjectResult>(await controller.GetCurrentSession());
        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));

        Assert.False(payload.RootElement.GetProperty("authenticated").GetBoolean());
        Assert.Equal(
            managementMode.ToString(),
            payload.RootElement.GetProperty("providerAccountManagementMode").GetString());
        Assert.False(payload.RootElement.TryGetProperty("secret", out _));
        Assert.False(payload.RootElement.TryGetProperty("accounts", out _));
    }

    [Fact]
    public async Task GetCurrentSession_WithValidCookie_ReturnsSessionUser()
    {
        var handler = new DelegateHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

        var sessionService = AdminAuthSessionTestSupport.Create();
        var session = await sessionService.CreateSessionAsync(
            userId: "user-42",
            userName: "alice",
            isAdministrator: true,
            jellyfinAccessToken: "token",
            jellyfinServerId: "server");

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers.Cookie = $"{AdminAuthSessionService.SessionCookieName}={session.SessionId}";

        var controller = CreateController(handler, sessionService, httpContext);
        var result = await controller.GetCurrentSession();

        var ok = Assert.IsType<OkObjectResult>(result);
        var payloadJson = JsonSerializer.Serialize(ok.Value);
        using var payload = JsonDocument.Parse(payloadJson);

        Assert.True(payload.RootElement.GetProperty("authenticated").GetBoolean());
        Assert.Equal("user-42", payload.RootElement.GetProperty("user").GetProperty("id").GetString());
        Assert.Equal("alice", payload.RootElement.GetProperty("user").GetProperty("name").GetString());
        Assert.True(payload.RootElement.GetProperty("user").GetProperty("isAdministrator").GetBoolean());
        Assert.Equal(
            "Hybrid",
            payload.RootElement.GetProperty("providerAccountManagementMode").GetString());

        var refreshedCookie = Assert.Single(httpContext.Response.Headers.SetCookie);
        Assert.Contains($"{AdminAuthSessionService.SessionCookieName}={session.SessionId}", refreshedCookie);
        Assert.Contains("path=/", refreshedCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetCurrentSession_WithLegacyCookie_MigratesToV3Cookie()
    {
        var handler = new DelegateHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var sessionService = AdminAuthSessionTestSupport.Create();
        var session = await sessionService.CreateSessionAsync(
            userId: "legacy-user",
            userName: "legacy",
            isAdministrator: true,
            jellyfinAccessToken: "token",
            jellyfinServerId: "server");
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers.Cookie = $"{AdminAuthSessionService.LegacySessionCookieName}={session.SessionId}";

        var result = Assert.IsType<OkObjectResult>(
            await CreateController(handler, sessionService, httpContext).GetCurrentSession());
        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));

        Assert.True(payload.RootElement.GetProperty("authenticated").GetBoolean());
        var migratedCookie = Assert.Single(httpContext.Response.Headers.SetCookie);
        Assert.Contains($"{AdminAuthSessionService.SessionCookieName}={session.SessionId}", migratedCookie);
        Assert.Contains("path=/", migratedCookie, StringComparison.OrdinalIgnoreCase);
    }

    private static AdminAuthController CreateController(
        HttpMessageHandler handler,
        AdminAuthSessionService sessionService,
        HttpContext httpContext,
        BackendType backendType = BackendType.Jellyfin,
        ProviderAccountManagementMode managementMode = ProviderAccountManagementMode.Hybrid)
    {
        var jellyfinOptions = Options.Create(new JellyfinSettings
        {
            Url = "http://jellyfin.local"
        });

        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(x => x.CreateClient(It.IsAny<string>())).Returns(new HttpClient(handler));

        var logger = new Mock<ILogger<AdminAuthController>>();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Backend:Type"] = backendType.ToString()
            })
            .Build();
        var controller = new AdminAuthController(
            jellyfinOptions,
            Options.Create(new SubsonicSettings { Url = "http://subsonic.local" }),
            configuration,
            httpClientFactory.Object,
            sessionService,
            logger.Object,
            identityResolver: null,
            providerAccountManagementOptions: new ProviderAccountManagementOptions
            {
                ManagementMode = managementMode.ToString()
            })
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = httpContext
            }
        };

        return controller;
    }

    private static string ExtractCookieValue(string setCookieHeader)
    {
        var cookiePart = setCookieHeader.Split(';', 2)[0];
        var parts = cookiePart.Split('=', 2);
        return parts.Length == 2 ? parts[1] : string.Empty;
    }

    private sealed class DelegateHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public DelegateHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return _handler(request, cancellationToken);
        }
    }
}
