using System.Net;
using System.Text;
using allstarr.Middleware;
using allstarr.Models.Settings;
using allstarr.Services.Admin;
using allstarr.Services.Subsonic;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace allstarr.Tests;

public sealed class AdminBackendSessionValidationTests
{
    [Fact]
    public async Task JellyfinSessionValidationRequiresCurrentAdministratorRole()
    {
        var handler = new FixtureHandler(
            _ => Json(HttpStatusCode.OK,
                """{"Id":"alice","Name":"Alice","Policy":{"IsDisabled":false,"IsAdministrator":false}}"""));
        var validator = CreateValidator("Jellyfin", handler);

        var result = await validator.ValidateSessionAsync(JellyfinSession(true), default);

        Assert.Equal(AdminBackendSessionValidationStatus.Invalid, result.Status);
        Assert.Equal("/Users/Me", handler.Requests.Single().RequestUri!.AbsolutePath);
        Assert.Contains("fixture-token", handler.Requests.Single().Headers.GetValues("Authorization").Single(),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.Redirect)]
    public async Task JellyfinTemporaryOrRedirectResponsesRemainUnavailable(HttpStatusCode status)
    {
        var handler = new FixtureHandler(_ => Json(status, "not-forwarded"));
        var validator = CreateValidator("Jellyfin", handler);

        var result = await validator.ValidateSessionAsync(JellyfinSession(true), default);

        Assert.Equal(AdminBackendSessionValidationStatus.Unavailable, result.Status);
    }

    [Fact]
    public async Task SubsonicValidationUsesStoredTokenAndSaltWithoutPassword()
    {
        var authentication = SubsonicSessionAuthentication.Create("alice", "fixture-password");
        var session = new AdminAuthSession
        {
            SessionId = "fixture",
            UserId = "alice",
            UserName = "Alice",
            IsAdministrator = true,
            BackendType = "Subsonic",
            BackendInstanceId = "backend",
            AllstarrUserId = Guid.NewGuid(),
            JellyfinAccessToken = string.Empty,
            SubsonicReadAuthentication = authentication,
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        };
        var handler = new FixtureHandler(_ => Json(HttpStatusCode.OK,
            "{\"subsonic-response\":{\"status\":\"ok\",\"user\":{\"username\":\"alice\",\"adminRole\":true}}}"));
        var validator = CreateValidator("Subsonic", handler);

        var result = await validator.ValidateSessionAsync(session, default);

        Assert.Equal(AdminBackendSessionValidationStatus.Valid, result.Status);
        var body = Assert.Single(handler.RequestBodies);
        Assert.Contains("u=alice", body, StringComparison.Ordinal);
        Assert.Contains("t=" + authentication["t"], body, StringComparison.Ordinal);
        Assert.Contains("s=" + authentication["s"], body, StringComparison.Ordinal);
        Assert.Contains("username=alice", body, StringComparison.Ordinal);
        Assert.Contains("f=json", body, StringComparison.Ordinal);
        Assert.DoesNotContain("p=", body, StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-password", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task SubsonicNonSuccessStatusCannotAuthorizeAnOkEnvelope(HttpStatusCode status)
    {
        var session = SubsonicSession();
        var handler = new FixtureHandler(_ => Json(status,
            "{\"subsonic-response\":{\"status\":\"ok\",\"user\":{\"username\":\"alice\",\"adminRole\":true}}}"));
        var validator = CreateValidator("Subsonic", handler);

        var result = await validator.ValidateSessionAsync(session, default);

        Assert.Equal(AdminBackendSessionValidationStatus.Unavailable, result.Status);
    }

    [Theory]
    [InlineData(41)]
    [InlineData(42)]
    [InlineData(43)]
    public async Task SubsonicCompatibilityFailuresRemainUnavailable(int code)
    {
        var session = SubsonicSession();
        var handler = new FixtureHandler(_ => Json(HttpStatusCode.OK,
            $"{{\"subsonic-response\":{{\"status\":\"failed\",\"error\":{{\"code\":{code}}}}}}}"));
        var validator = CreateValidator("Subsonic", handler);

        var result = await validator.ValidateSessionAsync(session, default);

        Assert.Equal(AdminBackendSessionValidationStatus.Unavailable, result.Status);
    }

    [Theory]
    [InlineData(40)]
    [InlineData(44)]
    [InlineData(50)]
    public async Task SubsonicAuthenticationFailureCodeIsDefinitivelyInvalid(int code)
    {
        var session = new AdminAuthSession
        {
            SessionId = "fixture",
            UserId = "alice",
            UserName = "Alice",
            IsAdministrator = true,
            BackendType = "Subsonic",
            BackendInstanceId = "backend",
            AllstarrUserId = Guid.NewGuid(),
            JellyfinAccessToken = string.Empty,
            SubsonicReadAuthentication = SubsonicSessionAuthentication.Create("alice", "fixture-password"),
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        };
        var handler = new FixtureHandler(_ => Json(HttpStatusCode.OK,
            $"{{\"subsonic-response\":{{\"status\":\"failed\",\"error\":{{\"code\":{code}}}}}}}"));
        var validator = CreateValidator("Subsonic", handler);

        var result = await validator.ValidateSessionAsync(session, default);

        Assert.Equal(AdminBackendSessionValidationStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task IncompleteBackendIdentityIsUnavailable()
    {
        var handler = new FixtureHandler(
            _ => Json(HttpStatusCode.OK,
                "{\"Id\":\"alice\",\"Name\":\"Alice\",\"Policy\":{\"IsDisabled\":false}}"));
        var validator = CreateValidator("Jellyfin", handler);

        var result = await validator.ValidateSessionAsync(JellyfinSession(true), default);

        Assert.Equal(AdminBackendSessionValidationStatus.Unavailable, result.Status);
    }

    [Fact]
    public async Task AdminErrorBoundaryReturnsGeneric503ForNormalizedPrefixedRequest()
    {
        var context = new DefaultHttpContext();
        context.Connection.LocalPort = 5275;
        context.Request.PathBase = "/admin";
        context.Request.Path = "/api/admin/config";
        context.Response.Body = new MemoryStream();
        var handler = new GlobalExceptionHandler(NullLogger<GlobalExceptionHandler>.Instance);

        Assert.True(await handler.TryHandleAsync(
            context,
            new AdminBackendSessionUnavailableException(),
            CancellationToken.None));

        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.Contains("Backend temporarily unavailable", body, StringComparison.Ordinal);
        Assert.DoesNotContain("backend.invalid", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BackendSnapshotNeverSendsATokenToTheOtherBackend()
    {
        var configuration = new Mock<IConfiguration>();
        var reads = 0;
        configuration.Setup(item => item["Backend:Type"])
            .Returns(() => Interlocked.Increment(ref reads) == 1 ? "Jellyfin" : "Subsonic");
        var handler = new FixtureHandler(_ => Json(HttpStatusCode.OK,
            """{"Id":"alice","Name":"Alice","Policy":{"IsDisabled":false,"IsAdministrator":true}}"""));
        var validator = new AdminOidcBackendAuthentication(configuration.Object,
            Options.Create(new JellyfinSettings { Url = "http://jellyfin.invalid" }),
            Options.Create(new SubsonicSettings { Url = "http://subsonic.invalid" }), new FixtureClients(handler));

        var result = await validator.ValidateSessionAsync(JellyfinSession(true), default);

        Assert.Equal(AdminBackendSessionValidationStatus.Unavailable, result.Status);
        Assert.Equal("jellyfin.invalid", Assert.Single(handler.Requests).RequestUri!.Host);
    }

    [Fact]
    public async Task SubsonicApiKeyValidationDoesNotSendUsernameAuthentication()
    {
        var session = SubsonicSession();
        session.SubsonicReadAuthentication!.Clear();
        session.SubsonicReadAuthentication["apiKey"] = "fixture-api-key";
        session.SubsonicReadAuthentication["v"] = "1.16.1";
        session.SubsonicReadAuthentication["c"] = "fixture";
        var handler = new FixtureHandler(_ => Json(HttpStatusCode.OK,
            """{"subsonic-response":{"status":"ok","user":{"username":"alice","adminRole":true}}}"""));
        var validator = CreateValidator("Subsonic", handler);

        var result = await validator.ValidateSessionAsync(session, default);

        Assert.Equal(AdminBackendSessionValidationStatus.Valid, result.Status);
        var form = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(Assert.Single(handler.RequestBodies));
        Assert.Equal("fixture-api-key", form["apiKey"].ToString());
        Assert.Equal("alice", form["username"].ToString());
        foreach (var key in new[] { "u", "p", "t", "s" }) Assert.False(form.ContainsKey(key));
    }

    [Fact]
    public async Task SubsonicConflictingSessionAuthenticationIsNotForwarded()
    {
        var session = SubsonicSession();
        session.SubsonicReadAuthentication!["apiKey"] = "fixture-api-key";
        var handler = new FixtureHandler(_ => Json(HttpStatusCode.OK,
            """{"subsonic-response":{"status":"ok","user":{"username":"alice","adminRole":true}}}"""));
        var validator = CreateValidator("Subsonic", handler);

        var result = await validator.ValidateSessionAsync(session, default);

        Assert.Equal(AdminBackendSessionValidationStatus.Unavailable, result.Status);
        Assert.Empty(handler.Requests);
    }

    private static AdminOidcBackendAuthentication CreateValidator(
        string backend,
        FixtureHandler handler)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Backend:Type"] = backend }).Build();
        var jellyfin = new JellyfinSettings { Url = "http://backend.invalid" };
        var subsonic = new SubsonicSettings { Url = "http://backend.invalid" };
        return new AdminOidcBackendAuthentication(
            configuration, Options.Create(jellyfin), Options.Create(subsonic), new FixtureClients(handler));
    }

    private static AdminAuthSession JellyfinSession(bool administrator) => new()
    {
        SessionId = "fixture",
        UserId = "alice",
        UserName = "Alice",
        IsAdministrator = administrator,
        BackendType = "Jellyfin",
        BackendInstanceId = "backend",
        JellyfinAccessToken = "fixture-token",
        ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
    };

    private static AdminAuthSession SubsonicSession() => new()
    {
        SessionId = "fixture",
        UserId = "alice",
        UserName = "Alice",
        IsAdministrator = true,
        BackendType = "Subsonic",
        BackendInstanceId = "backend",
        AllstarrUserId = Guid.NewGuid(),
        JellyfinAccessToken = string.Empty,
        SubsonicReadAuthentication = SubsonicSessionAuthentication.Create("alice", "fixture-password"),
        ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
    };

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class FixtureClients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FixtureHandler(
        Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (request.Content != null)
                RequestBodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            return response(request);
        }
    }
}
