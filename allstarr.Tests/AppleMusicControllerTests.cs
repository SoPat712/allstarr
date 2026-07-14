using System.Net;
using System.Text;
using System.Text.Json;
using allstarr.Controllers;
using allstarr.Models.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace allstarr.Tests;

public sealed class AppleMusicControllerTests
{
    [Fact]
    public async Task GetStatus_NormalizesReadySidecarAndAuthenticatedAccount()
    {
        var controller = CreateController(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/health" => Json(HttpStatusCode.OK,
                """{"status":"ok","staged":true,"daemon_running":true,"wrapper_healthy":true,"logged_in":true}"""),
            "/api/me" => Json(HttpStatusCode.OK,
                """{"version":"0.0.1","auth":{"state":"logged_in","storefront":"us"}}"""),
            _ => Json(HttpStatusCode.NotFound, "{}")
        });

        var content = AssertContent(await controller.GetStatus(), StatusCodes.Status200OK);
        using var document = JsonDocument.Parse(content.Content!);
        var root = document.RootElement;

        Assert.Equal("ready", root.GetProperty("state").GetString());
        Assert.True(root.GetProperty("staged").GetBoolean());
        Assert.True(root.GetProperty("daemon_running").GetBoolean());
        Assert.True(root.GetProperty("wrapper_healthy").GetBoolean());
        Assert.True(root.GetProperty("logged_in").GetBoolean());
        Assert.True(root.GetProperty("ready").GetBoolean());
        Assert.Equal("authenticated", root.GetProperty("login_state").GetString());
        Assert.Equal("0.0.1", root.GetProperty("wrapper_version").GetString());
    }

    [Fact]
    public async Task GetStatus_UsesWrapperAccountStateInsteadOfSessionFilesAlone()
    {
        var controller = CreateController(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/health" => Json(HttpStatusCode.OK,
                """{"status":"ok","staged":true,"daemon_running":true,"wrapper_healthy":true,"logged_in":true}"""),
            "/api/me" => Json(HttpStatusCode.OK,
                """{"version":"0.0.1","runtime":{"playback_ready":false},"auth":{"state":"logged_out","music_user_token":"must-not-leak","devToken":"must-not-leak"}}"""),
            _ => Json(HttpStatusCode.NotFound, "{}")
        });

        var content = AssertContent(await controller.GetStatus(), StatusCodes.Status200OK);
        using var document = JsonDocument.Parse(content.Content!);
        Assert.Equal("needs_login", document.RootElement.GetProperty("state").GetString());
        Assert.Equal("logged_out", document.RootElement.GetProperty("login_state").GetString());
        Assert.False(document.RootElement.GetProperty("logged_in").GetBoolean());
        Assert.False(document.RootElement.GetProperty("ready").GetBoolean());
        Assert.DoesNotContain("must-not-leak", content.Content, StringComparison.Ordinal);
        Assert.False(document.RootElement.GetProperty("account").TryGetProperty("music_user_token", out _));
    }

    [Fact]
    public async Task GetStatus_PreservesAwaitingTwoFactorStateForTheWebUi()
    {
        var controller = CreateController(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/health" => Json(HttpStatusCode.OK,
                """{"status":"ok","staged":true,"daemon_running":true,"wrapper_healthy":true,"logged_in":false}"""),
            "/api/me" => Json(HttpStatusCode.OK,
                """{"version":"0.0.1","runtime":{"playback_ready":false},"auth":{"state":"two-factor-required","username":"listener@example.test"}}"""),
            _ => Json(HttpStatusCode.NotFound, "{}")
        });

        var content = AssertContent(await controller.GetStatus(), StatusCodes.Status200OK);
        using var document = JsonDocument.Parse(content.Content!);
        Assert.Equal("awaiting_2fa", document.RootElement.GetProperty("state").GetString());
        Assert.Equal("awaiting_2fa", document.RootElement.GetProperty("login_state").GetString());
        Assert.False(document.RootElement.GetProperty("logged_in").GetBoolean());
    }

    [Fact]
    public async Task GetStatus_DaemonOffline_DoesNotRequestAccountStatus()
    {
        var requestedPaths = new List<string>();
        var controller = CreateController(request =>
        {
            requestedPaths.Add(request.RequestUri!.AbsolutePath);
            return Json(HttpStatusCode.OK,
                """{"status":"ok","staged":true,"daemon_running":false,"wrapper_healthy":false,"logged_in":true}""");
        });

        var content = AssertContent(await controller.GetStatus(), StatusCodes.Status200OK);
        using var document = JsonDocument.Parse(content.Content!);
        Assert.Equal("daemon_offline", document.RootElement.GetProperty("state").GetString());
        Assert.False(document.RootElement.GetProperty("logged_in").GetBoolean());
        Assert.Equal(["/api/health"], requestedPaths);
    }

    [Fact]
    public async Task GetStatus_HealthFailure_PreservesStatusWithoutRelayingRawBody()
    {
        var controller = CreateController(_ => Text(
            HttpStatusCode.ServiceUnavailable,
            "wrapper stack with secret-token"));

        var content = AssertContent(
            await controller.GetStatus(),
            StatusCodes.Status503ServiceUnavailable);

        Assert.Contains("health_unavailable", content.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("wrapper stack", content.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token", content.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetStatus_AccountUnauthorized_PreservesStatusAndRedactsPayload()
    {
        var controller = CreateController(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/health" => Json(HttpStatusCode.OK,
                """{"staged":true,"daemon_running":true,"wrapper_healthy":true,"logged_in":false}"""),
            "/api/me" => Json(HttpStatusCode.Unauthorized,
                """{"detail":"raw wrapper error","music_user_token":"must-not-leak"}"""),
            _ => Json(HttpStatusCode.NotFound, "{}")
        });

        var content = AssertContent(
            await controller.GetStatus(),
            StatusCodes.Status401Unauthorized);

        Assert.Contains("account_unauthorized", content.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("raw wrapper error", content.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("must-not-leak", content.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetStatus_InvalidSuccessfulPayload_ReturnsContractFailure()
    {
        var controller = CreateController(_ => Text(HttpStatusCode.OK, "not-json secret-token"));

        var content = AssertContent(await controller.GetStatus(), StatusCodes.Status502BadGateway);

        Assert.Contains("invalid_sidecar_response", content.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("not-json", content.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token", content.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_AcceptedResponseKeepsTwoFactorPayloadAndStatusCode()
    {
        var controller = CreateController(_ => Json(
            HttpStatusCode.Accepted,
            """{"state":"awaiting_2fa","message":"Enter the verification code."}"""));
        using var credentials = JsonDocument.Parse("""{"username":"listener","password":"secret"}""");

        var content = AssertContent(
            await controller.Login(credentials.RootElement),
            StatusCodes.Status202Accepted);

        Assert.Contains("awaiting_2fa", content.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_UpstreamFailurePreservesStatusAndRecursivelyRedactsSecrets()
    {
        var controller = CreateController(_ => Json(
            HttpStatusCode.Forbidden,
            """{"error":"invalid_credentials","musicUserToken":"secret-one","auth":{"access_token":"secret-two"}}"""));
        using var credentials = JsonDocument.Parse("""{"username":"listener","password":"secret"}""");

        var content = AssertContent(
            await controller.Login(credentials.RootElement),
            StatusCodes.Status403Forbidden);

        Assert.Contains("invalid_credentials", content.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-one", content.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-two", content.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("musicUserToken", content.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("access_token", content.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_NonJsonFailurePreservesStatusWithoutRelayingRawText()
    {
        var controller = CreateController(_ => Text(
            HttpStatusCode.BadGateway,
            "raw wrapper stack secret-token"));
        using var credentials = JsonDocument.Parse("""{"username":"listener","password":"secret"}""");

        var content = AssertContent(
            await controller.Login(credentials.RootElement),
            StatusCodes.Status502BadGateway);

        Assert.Contains("invalid_sidecar_response", content.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("raw wrapper stack", content.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token", content.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_ConnectionExceptionReturnsActionableRedactedFailure()
    {
        var controller = CreateController(_ =>
            throw new HttpRequestException("http://wrapper.internal/?token=must-not-leak"));
        using var credentials = JsonDocument.Parse("""{"username":"listener","password":"secret"}""");

        var content = AssertContent(
            await controller.Login(credentials.RootElement),
            StatusCodes.Status503ServiceUnavailable);

        Assert.Contains("sidecar_unreachable", content.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("wrapper.internal", content.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("must-not-leak", content.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login2fa_AcceptedResponsePreservesPendingStateAndStatusCode()
    {
        var controller = CreateController(_ => Json(
            HttpStatusCode.Accepted,
            """{"state":"awaiting_2fa","message":"Try another code."}"""));
        using var code = JsonDocument.Parse("""{"code":"123456"}""");

        var content = AssertContent(
            await controller.Login2fa(code.RootElement),
            StatusCodes.Status202Accepted);

        Assert.Contains("awaiting_2fa", content.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login2fa_NonSuccessStatusIsPreservedAndSanitized()
    {
        var controller = CreateController(_ => Json(
            HttpStatusCode.Unauthorized,
            """{"error":"invalid_code","sessionKey":"must-not-leak"}"""));
        using var code = JsonDocument.Parse("""{"code":"000000"}""");

        var content = AssertContent(
            await controller.Login2fa(code.RootElement),
            StatusCodes.Status401Unauthorized);

        Assert.Contains("invalid_code", content.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("must-not-leak", content.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("sessionKey", content.Content, StringComparison.Ordinal);
    }

    private static ContentResult AssertContent(IActionResult result, int expectedStatus)
    {
        var content = Assert.IsType<ContentResult>(result);
        Assert.Equal(expectedStatus, content.StatusCode);
        Assert.Equal("application/json", content.ContentType);
        Assert.NotNull(content.Content);
        return content;
    }

    private static AppleMusicController CreateController(
        Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var client = new HttpClient(new StubHttpMessageHandler(responder));
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(value => value.CreateClient("AppleMusic")).Returns(client);

        return new AppleMusicController(
            factory.Object,
            Options.Create(new AppleDownloadSettings { BaseUrl = "http://apple-sidecar.test" }),
            NullLogger<AppleMusicController>.Instance);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string content) =>
        new(status)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json")
        };

    private static HttpResponseMessage Text(HttpStatusCode status, string content) =>
        new(status)
        {
            Content = new StringContent(content, Encoding.UTF8, "text/plain")
        };

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(responder(request));
    }
}
