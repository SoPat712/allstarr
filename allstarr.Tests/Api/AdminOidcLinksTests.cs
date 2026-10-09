using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Identity;
using allstarr.Core.Operations;
using allstarr.Core.Secrets;
using allstarr.Core.Storage;
using allstarr.Models.Settings;
using allstarr.Services.Admin;
using allstarr.Controllers;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace allstarr.Tests;

public sealed class AdminOidcLinksTests
{
    [Theory]
    [InlineData("jellyfin")]
    [InlineData("subsonic")]
    public async Task LinkRequiresExactIdentity_RevalidatesBackend_AndRevokesSessions(string backendName)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        IDbContextFactory<AllstarrDbContext> factory = new Factory(database.Options);
        var options = new IdentityOptions();
        var state = new DurableStorageState(new() { DataDirectory = database.StorageOptions.DataDirectory, DatabaseFileName = database.StorageOptions.DatabaseFileName });
        state.Set(DurableStorageReadiness.Ready);
        var identities = new BackendIdentityResolver(factory, state, options, new SystemPlatformClock());
        var alice = (await identities.ResolveAsync(new(backendName, "alice", "Alice")))!;
        var bob = (await identities.ResolveAsync(new(backendName, "bob", "Bob")))!;
        var root = Directory.CreateTempSubdirectory("allstarr-oidc-tests-");
        try
        {
            var secretOptions = new SecretStoreOptions { KeyRingPath = Path.Combine(root.FullName, "keys.json") };
            await File.WriteAllTextAsync(secretOptions.KeyRingPath, JsonSerializer.Serialize(new
            {
                activeKeyId = "fixture",
                keys = new Dictionary<string, string> { ["fixture"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) }
            }));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(secretOptions.KeyRingPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var secrets = new EncryptedSecretStore(factory, new(secretOptions), secretOptions, new SystemPlatformClock());
            var oidc = new AdminOidcOptions { Enabled = true };
            var handler = new NativeHandler(backendName);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Backend:Type"] = backendName }).Build();
            var jellyfin = new JellyfinSettings { Url = "http://native.test" };
            var subsonic = new SubsonicSettings { Url = "http://native.test" };
            var native = new AdminOidcBackendAuthentication(configuration, Options.Create(jellyfin), Options.Create(subsonic), new Clients(handler));
            var sessions = new AdminAuthSessionService(new EfAdminAuthSessionStore(factory), new EphemeralDataProtectionProvider(),
                NullLogger<AdminAuthSessionService>.Instance, factory, oidc, native, options);
            var links = new AdminOidcLinks(factory, secrets, options, native, identities, sessions);
            var credential = new AdminOidcCredential(backendName, "http://native.test", "alice", "private-fixture-credential");
            var key = new string('a', 64);
            await links.LinkAsync(key, alice, credential, default);
            await using (var db = await factory.CreateDbContextAsync())
            {
                Assert.Single(await db.AdminOidcLinks.ToListAsync());
                var version = Assert.Single(await db.SecretVersions.ToListAsync());
                Assert.DoesNotContain("private-fixture-credential", Encoding.UTF8.GetString(version.Ciphertext));
            }
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => links.LinkAsync(key, bob, credential with { UserId = "bob" }, default));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => links.LinkAsync(new string('b', 64), alice, credential, default));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => links.LinkAsync(key, alice, credential with { Endpoint = "http://other.test" }, default));

            handler.Admin = true;
            var session = await links.SignInAsync(key, default);
            Assert.NotNull(session);
            Assert.Equal(backendName == "jellyfin" ? "Jellyfin" : "Subsonic", session.BackendType);
            Assert.True(session.IsAdministrator);
            Assert.Equal(alice.UserId, session.AllstarrUserId);
            Assert.NotNull(await sessions.GetValidSessionAsync(session.SessionId));
            if (backendName == "subsonic") Assert.Null(await secrets.GetSubsonicPlaylistGrantAsync(alice));
            handler.Admin = false;
            Assert.False((await links.SignInAsync(key, default))!.IsAdministrator);
            handler.Accept = false;
            Assert.Null(await links.SignInAsync(key, default));
            handler.Accept = true;
            handler.UserId = "bob";
            Assert.Null(await links.SignInAsync(key, default));
            handler.UserId = "alice";
            var calls = handler.Calls;
            jellyfin.Url = subsonic.Url = "http://replacement.test";
            Assert.Null(await links.SignInAsync(key, default));
            Assert.Equal(calls, handler.Calls);
            Assert.Null(await sessions.GetValidSessionAsync(session.SessionId));
            jellyfin.Url = subsonic.Url = "http://native.test";

            var bobSession = await sessions.CreateSessionAsync("bob", "Bob", false, "", null,
                backendType: backendName, allstarrUserId: bob.UserId);
            Assert.False(await links.IsLinkedAsync(bobSession, default));
            await links.UnlinkAsync(bobSession, default);
            Assert.False(await links.IsLinkedAsync(session, default));
            session = (await links.SignInAsync(key, default))!;
            Assert.NotNull(session);
            Assert.True(await links.IsLinkedAsync(session, default));
            var csrf = new Mock<IAntiforgery>();
            csrf.Setup(item => item.IsRequestValidAsync(It.IsAny<HttpContext>())).ReturnsAsync(false);
            var requestContext = new DefaultHttpContext();
            requestContext.Request.Scheme = "https";
            requestContext.Request.Headers.Cookie = AdminAuthSessionService.SessionCookieName + "=" + session.SessionId;
            var oidcController = new AdminOidcController(oidc, links, sessions, csrf.Object, NullLogger<AdminOidcController>.Instance)
            {
                ControllerContext = new() { HttpContext = requestContext }
            };
            Assert.IsType<BadRequestObjectResult>(await oidcController.Unlink(default));
            var authController = new AdminAuthController(Options.Create(jellyfin), Options.Create(subsonic), configuration,
                new Clients(handler), sessions, NullLogger<AdminAuthController>.Instance, null!, identities,
                oidcOptions: oidc, oidcLinks: links, antiforgery: csrf.Object)
            {
                ControllerContext = new() { HttpContext = requestContext }
            };
            Assert.IsType<BadRequestObjectResult>(await authController.Login(new() { Username = "alice", Password = "fixture", LinkOidc = true }));
            Assert.True(await links.IsLinkedAsync(session, default));
            await links.UnlinkAsync(session, default);
            Assert.Null(await links.SignInAsync(key, default));
            Assert.Null(await sessions.GetValidSessionAsync(session.SessionId));
            await using var context = await factory.CreateDbContextAsync();
            Assert.Empty(await context.AdminOidcLinks.ToListAsync());
            Assert.NotNull((await context.SecretReferences.SingleAsync()).RevokedAt);
        }
        finally { root.Delete(true); }
    }

    private sealed class Factory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
    }

    private sealed class Clients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false);
    }

    private sealed class NativeHandler(string backend) : HttpMessageHandler
    {
        public bool Accept { get; set; } = true;
        public bool Admin { get; set; }
        public string UserId { get; set; } = "alice";
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            string json;
            if (backend == "jellyfin")
            {
                Assert.Equal("/Users/Me", request.RequestUri!.AbsolutePath);
                Assert.Contains("private-fixture-credential", request.Headers.GetValues("Authorization").Single());
                Assert.False(request.Headers.Contains("X-Emby-Token"));
                json = JsonSerializer.Serialize(new { Id = UserId, Name = "Alice", Policy = new { IsAdministrator = Admin, IsDisabled = !Accept } });
            }
            else
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("", request.RequestUri!.Query);
                Assert.Contains("p=private-fixture-credential", await request.Content!.ReadAsStringAsync(cancellationToken));
                json = JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["subsonic-response"] = new { status = Accept ? "ok" : "failed", user = new { username = UserId, adminRole = Admin } }
                });
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
