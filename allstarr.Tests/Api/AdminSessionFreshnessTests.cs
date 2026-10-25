using System.Net;
using System.Text;
using allstarr.Core.Identity;
using allstarr.Core.Storage;
using allstarr.Middleware;
using allstarr.Models.Settings;
using allstarr.Services.Admin;
using allstarr.Services.Subsonic;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace allstarr.Tests;

public sealed class AdminSessionFreshnessTests
{
    [Theory]
    [InlineData("Jellyfin")]
    [InlineData("Subsonic")]
    public async Task BackendDemotionBlocksThePrivilegedApiWithoutAnotherLogin(string backendType)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new AdminAuthTestDbContextFactory(database.Options);
        var userId = Guid.CreateVersion7();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Users.Add(new UserRecord
            {
                Id = userId,
                BackendType = backendType.ToLowerInvariant(),
                BackendInstanceId = "backend",
                BackendPrincipalId = "backend-user",
                DisplayName = "Listener",
                IsAdmin = true,
                Enabled = true
            });
            await db.SaveChangesAsync();
        }

        const string endpoint = "http://backend.invalid";
        using var handler = new BackendHandler(backendType);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Backend:Type"] = backendType }).Build();
        var backend = new AdminOidcBackendAuthentication(configuration,
            Options.Create(new JellyfinSettings { Url = endpoint }),
            Options.Create(new SubsonicSettings { Url = endpoint }), new Clients(handler));
        var material = backendType == "Jellyfin" ? "fixture-session-token" : "fixture-password";
        var demoted = await backend.ValidateAsync(new(backendType.ToLowerInvariant(), endpoint,
            "backend-user", material), CancellationToken.None);
        Assert.NotNull(demoted);
        Assert.False(demoted.IsAdministrator);
        handler.Calls = 0;

        var service = new AdminAuthSessionService(new EfAdminAuthSessionStore(factory),
            new EphemeralDataProtectionProvider(), NullLogger<AdminAuthSessionService>.Instance,
            contextFactory: factory, oidcBackend: backend,
            identityOptions: new IdentityOptions { BackendInstanceId = "backend" });
        var session = await service.CreateSessionAsync("backend-user", "Listener", true,
            backendType == "Jellyfin" ? material : "", null,
            backendType: backendType, allstarrUserId: userId,
            subsonicReadAuthentication: backendType == "Subsonic"
                ? SubsonicSessionAuthentication.Create("backend-user", material) : null);

        var invoked = false;
        var middleware = new AdminAuthenticationMiddleware(context =>
        {
            invoked = true;
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return Task.CompletedTask;
        }, service, NullLogger<AdminAuthenticationMiddleware>.Instance);
        var context = new DefaultHttpContext();
        context.Connection.LocalPort = 5275;
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/api/admin/config";
        context.Request.Headers.Cookie = AdminAuthSessionService.SessionCookieName + "=" + session.SessionId;
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        Assert.False(invoked);
        Assert.Contains(context.Response.StatusCode,
            new[] { StatusCodes.Status401Unauthorized, StatusCodes.Status403Forbidden });
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task NativeEndpointTransitionDuringValidationFailsClosedAndRetainsSession()
    {
        await using var fixture = await AdminAuthSessionTestSupport.CreateLinkedAsync(
            backendInstanceId: "backend");
        var settings = new JellyfinSettings { Url = "http://backend.invalid" };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Backend:Type"] = "Jellyfin" }).Build();
        using var handler = new HeldBackendHandler();
        var backend = new AdminOidcBackendAuthentication(
            configuration,
            Options.Create(settings),
            Options.Create(new SubsonicSettings { Url = settings.Url }),
            new Clients(handler));
        var session = await fixture.CreateSessionAsync(
            "backend-user", "Listener", true, backendType: "Jellyfin");
        var service = fixture.CreateService(oidcBackend: backend, backendSessionValidator: backend);

        var validation = service.GetValidSessionAsync(session.SessionId);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        settings.Url = "http://replacement.invalid";
        handler.Release.TrySetResult(true);

        await Assert.ThrowsAsync<AdminBackendSessionUnavailableException>(
            () => validation.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Contains(session.SessionId, fixture.Store.Records.Keys);
    }

    [Fact]
    public async Task NativeEndpointTransitionDuringFinalRecordProofKeepsTheCookieRetryable()
    {
        await using var fixture = await AdminAuthSessionTestSupport.CreateLinkedAsync(
            backendInstanceId: "backend");
        var settings = new JellyfinSettings { Url = "http://backend.invalid" };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Backend:Type"] = "Jellyfin" }).Build();
        using var handler = new BackendHandler("Jellyfin");
        var backend = new AdminOidcBackendAuthentication(configuration, Options.Create(settings),
            Options.Create(new SubsonicSettings { Url = settings.Url }), new Clients(handler));
        var session = await fixture.CreateSessionAsync("backend-user", "Listener", true);
        var store = new TransitionStore(fixture.Store, () => settings.Url = "http://replacement.invalid");
        var service = fixture.CreateService(store, oidcBackend: backend);

        await Assert.ThrowsAsync<AdminBackendSessionUnavailableException>(
            () => service.GetValidSessionAsync(session.SessionId));

        Assert.Contains(session.SessionId, fixture.Store.Records.Keys);
    }

    [Theory]
    [InlineData("busy")]
    [InlineData("malformed")]
    [InlineData("transport")]
    [InlineData("timeout")]
    public async Task BackendOutageReturns503AndTheSameCookieRecovers(string failure)
    {
        await using var fixture = await AdminAuthSessionTestSupport.CreateLinkedAsync(
            backendInstanceId: "backend");
        using var handler = new RecoveringBackendHandler(failure);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Backend:Type"] = "Jellyfin",
                [AdminBasePath.ConfigurationKey] = "/allstarr"
            }).Build();
        var backend = new AdminOidcBackendAuthentication(configuration,
            Options.Create(new JellyfinSettings { Url = "http://backend.invalid" }),
            Options.Create(new SubsonicSettings { Url = "http://backend.invalid" }), new Clients(handler));
        var session = await fixture.CreateSessionAsync("backend-user", "Listener", true);
        var record = fixture.Store.Records[session.SessionId];
        var originalPayload = record.ProtectedPayload;
        var originalSeen = DateTimeOffset.UtcNow.AddMinutes(-6);
        record.LastSeenAt = originalSeen;
        var service = fixture.CreateService(oidcBackend: backend, backendSessionValidator: backend);
        var actions = 0;
        using var host = await new HostBuilder().ConfigureWebHost(web => web.UseTestServer()
            .ConfigureServices(services =>
            {
                services.AddSingleton(service);
                services.AddSingleton(new AdminBasePath(configuration));
                services.AddExceptionHandler<GlobalExceptionHandler>();
                services.AddProblemDetails();
            })
            .Configure(app =>
            {
                app.Use((context, next) =>
                {
                    context.Connection.LocalPort = 5275;
                    return next(context);
                });
                app.UseExceptionHandler();
                app.UseMiddleware<AdminBasePathMiddleware>();
                app.UseMiddleware<AdminAuthenticationMiddleware>();
                app.Run(context =>
                {
                    Interlocked.Increment(ref actions);
                    return context.Response.WriteAsync("allowed");
                });
            })).StartAsync();
        using var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("Cookie", AdminAuthSessionService.SessionCookieName + "=" + session.SessionId);

        using var unavailable = await client.GetAsync("/allstarr/api/admin/config");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        Assert.False(unavailable.Headers.Contains("Set-Cookie"));
        var body = await unavailable.Content.ReadAsStringAsync();
        Assert.Contains("temporarily unavailable", body, StringComparison.Ordinal);
        Assert.DoesNotContain("private-fixture-response", body, StringComparison.Ordinal);
        Assert.Equal(0, actions);
        Assert.Equal(originalPayload, fixture.Store.Records[session.SessionId].ProtectedPayload);
        Assert.Equal(originalSeen, fixture.Store.Records[session.SessionId].LastSeenAt);

        handler.Recovered = true;
        using var recovered = await client.GetAsync("/allstarr/api/admin/config");

        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal("allowed", await recovered.Content.ReadAsStringAsync());
        Assert.Equal(1, actions);
        Assert.Equal(originalPayload, fixture.Store.Records[session.SessionId].ProtectedPayload);
        Assert.True(fixture.Store.Records[session.SessionId].LastSeenAt > originalSeen);
    }

    private sealed class RecoveringBackendHandler(string failure) : HttpMessageHandler
    {
        public bool Recovered;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (!Recovered && failure == "transport") throw new HttpRequestException("private-fixture-response");
            if (!Recovered && failure == "timeout") throw new TaskCanceledException("private-fixture-response");
            return Task.FromResult(new HttpResponseMessage(!Recovered && failure == "busy"
                ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            {
                Content = new StringContent(!Recovered && failure == "malformed"
                    ? "private-fixture-response"
                    : """{"Id":"backend-user","Name":"Listener","Policy":{"IsDisabled":false,"IsAdministrator":true}}""",
                    Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class TransitionStore(IAdminAuthSessionStore inner, Action transition) : IAdminAuthSessionStore
    {
        private int reads;
        public async Task<AdminAuthSessionRecord?> FindAsync(string id, CancellationToken token)
        {
            var record = await inner.FindAsync(id, token);
            if (Interlocked.Increment(ref reads) == 3) transition();
            return record;
        }
        public Task AddAsync(AdminAuthSessionRecord record, CancellationToken token) => inner.AddAsync(record, token);
        public Task TouchAsync(string id, DateTimeOffset seen, CancellationToken token) => inner.TouchAsync(id, seen, token);
        public Task RemoveAsync(string id, CancellationToken token) => inner.RemoveAsync(id, token);
        public Task RemoveExpiredAsync(DateTimeOffset now, CancellationToken token) => inner.RemoveExpiredAsync(now, token);
    }

    private sealed class BackendHandler(string backendType) : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            var json = backendType == "Jellyfin"
                ? """{"Id":"backend-user","Name":"Listener","Policy":{"IsDisabled":false,"IsAdministrator":false}}"""
                : """{"subsonic-response":{"status":"ok","user":{"username":"backend-user","adminRole":false}}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class Clients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class HeldBackendHandler : HttpMessageHandler
    {
        public TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult(true);
            await Release.Task.WaitAsync(cancellationToken);
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"Id\":\"backend-user\",\"Name\":\"Listener\",\"Policy\":{\"IsDisabled\":false,\"IsAdministrator\":true}}",
                    Encoding.UTF8,
                    "application/json")
            };
        }
    }
}
