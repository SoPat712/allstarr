using System.Net;
using System.Text.Json;
using allstarr.Controllers;
using allstarr.Core.Identity;
using allstarr.Core.Playlists;
using allstarr.Core.Playlists.Targets;
using allstarr.Core.Operations;
using allstarr.Core.Secrets;
using allstarr.Core.Storage;
using allstarr.Models.Settings;
using allstarr.Services.Admin;
using allstarr.Services.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace allstarr.Tests;

public sealed class AdminPlaylistConsentTests : IAsyncLifetime
{
    private SqliteTestDatabase _database = null!;
    private IDbContextFactory<AllstarrDbContext> _factory = null!;
    private BackendIdentityResolver _identities = null!;
    private EncryptedSecretStore _secrets = null!;
    private AdminAuthSessionService _sessions = null!;
    private readonly Backend _backend = new();

    public async Task InitializeAsync()
    {
        _database = await SqliteTestDatabase.CreateAsync();
        _factory = new Factory(_database.Options);
        _sessions = new(new MemoryAdminAuthSessionStore(), new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider(),
            NullLogger<AdminAuthSessionService>.Instance, contextFactory: _factory, identityOptions: new());
        var state = new DurableStorageState(_database.StorageOptions);
        state.Set(DurableStorageReadiness.Ready);
        _identities = new(_factory, state, new(), new SystemPlatformClock());
        var options = new SecretStoreOptions { KeyRingPath = Path.Combine(_database.StorageOptions.DataDirectory, "consent-keys.json") };
        var keyRing = new FileSecretKeyRingProvider(options);
        await keyRing.CreateIfMissingAsync(false);
        _secrets = new(_factory, keyRing, options, new SystemPlatformClock());
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task NativeLoginStoresWriteCredentialOnlyAfterAuthenticationAndExplicitConsent(bool consent, bool accepted)
    {
        _backend.Accept = accepted;
        var controller = Controller();
        var result = await controller.Login(new() { Username = "alice", Password = "fixture-password", ManagePlaylists = consent });
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(consent && accepted ? 1 : 0, await db.SecretReferences.CountAsync());
        if (!accepted)
        {
            Assert.IsType<UnauthorizedObjectResult>(result);
            Assert.Equal(0, controller.Response.Headers.SetCookie.Count);
            return;
        }
        Assert.IsType<OkObjectResult>(result);
        Assert.DoesNotContain("fixture-password", JsonSerializer.Serialize(((OkObjectResult)result).Value), StringComparison.Ordinal);
        var principal = (await _identities.ResolveAsync(new("subsonic", "alice")))!;
        Assert.Equal(consent, await _secrets.GetSubsonicPlaylistGrantAsync(principal) != null);
        if (consent)
        {
            using var lease = await _secrets.OpenSubsonicPlaylistCredentialAsync("primary", "alice", null);
            Assert.Contains("fixture-password", lease.ReadUtf8(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ConsentReauthenticationUsesCurrentListener_AndRevocationLeavesSessionAndOtherGrant()
    {
        var alice = (await _identities.ResolveAsync(new("subsonic", "alice")))!;
        var bob = (await _identities.ResolveAsync(new("subsonic", "bob")))!;
        await _secrets.StoreSubsonicPlaylistGrantAsync(bob, "bob-fixture");
        var session = await _sessions.CreateSessionAsync("alice", "Alice", false, "", null,
            backendType: "Subsonic", allstarrUserId: alice.UserId);
        var controller = Controller(session.SessionId);
        var initial = Assert.IsType<OkObjectResult>(await controller.GetPlaylistConsent(default));
        Assert.False(JsonSerializer.SerializeToElement(initial.Value).GetProperty("granted").GetBoolean());
        _backend.ReturnedUser = "bob";
        Assert.IsType<UnauthorizedObjectResult>(await controller.GrantPlaylistConsent(new() { Password = "wrong-owner" }, default));
        Assert.Null(await _secrets.GetSubsonicPlaylistGrantAsync(alice));
        _backend.ReturnedUser = null;
        Assert.IsType<OkObjectResult>(await controller.GrantPlaylistConsent(new() { Password = "alice-fixture" }, default));
        Assert.Equal("alice", _backend.RequestedUser);
        Assert.IsType<OkObjectResult>(await controller.RevokePlaylistConsent(default));
        Assert.Null(await _secrets.GetSubsonicPlaylistGrantAsync(alice));
        Assert.NotNull(await _secrets.GetSubsonicPlaylistGrantAsync(bob));
        Assert.NotNull(await _sessions.GetValidSessionAsync(session.SessionId));
    }

    [Fact]
    public async Task BackendPlaylistBrowseNeedsOnlyCurrentViewerSession_AndRejectsForeignIdentity()
    {
        var principal = (await _identities.ResolveAsync(new("subsonic", "alice")))!;
        var session = await _sessions.CreateSessionAsync("alice", "Alice", false, "", null,
            backendType: "Subsonic", allstarrUserId: principal.UserId);
        await using var db = await _factory.CreateDbContextAsync();
        var identity = await db.Users.SingleAsync(item => item.Id == principal.UserId);
        var target = new Mock<IBackendPlaylistTarget>(MockBehavior.Strict);
        target.Setup(item => item.ListPageAsync(It.Is<BackendPlaylistTargetContext>(value =>
                value.VerifiedPrincipalId == "alice" && value.CredentialReference == null),
                null, 0, 31, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BackendPlaylistTargetResult<IReadOnlyList<BackendPlaylistSummary>>(
                BackendPlaylistTargetStatus.Success, [new("public-playlist", "Public", Writable: false)]));
        var resolver = new Mock<IBackendPlaylistTargetResolver>(MockBehavior.Strict);
        resolver.Setup(item => item.Resolve("subsonic")).Returns(target.Object);
        var controller = new PlaylistLinksController(_factory, null!, null!, null!, null!, null!, null!, null!, null!,
            resolver.Object, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!)
        { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };
        controller.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = session;
        Assert.IsType<OkObjectResult>(await controller.BrowseTargetPlaylists(identity.Id, null, null));
        var grant = await _secrets.StoreSubsonicPlaylistGrantAsync(principal, "fixture-password");
        await _secrets.RevokeSubsonicPlaylistGrantAsync(principal);
        Assert.IsType<OkObjectResult>(await controller.BrowseTargetPlaylists(identity.Id, null, null));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _secrets.OpenSubsonicPlaylistCredentialAsync(
            principal.BackendInstanceId, "alice", grant.ReferenceId));
        var other = (await _identities.ResolveAsync(new("subsonic", "bob")))!;
        var foreignIdentity = await db.Users.SingleAsync(item => item.Id == other.UserId);
        Assert.IsType<NotFoundResult>(await controller.BrowseTargetPlaylists(foreignIdentity.Id, null, null));
        target.VerifyAll();
    }

    [Fact]
    public async Task DisabledListenerCannotReadGrantReauthorizeOrRevokeThroughAnOldSession()
    {
        var principal = (await _identities.ResolveAsync(new("subsonic", "alice")))!;
        await _secrets.StoreSubsonicPlaylistGrantAsync(principal, "fixture-password");
        var session = await _sessions.CreateSessionAsync("alice", "Alice", false, "", null,
            backendType: "Subsonic", allstarrUserId: principal.UserId);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            (await db.Users.SingleAsync(item => item.Id == principal.UserId)).Enabled = false;
            await db.SaveChangesAsync();
        }
        var controller = Controller(session.SessionId);
        Assert.IsType<UnauthorizedResult>(await controller.GetPlaylistConsent(default));
        Assert.IsType<UnauthorizedResult>(await controller.GrantPlaylistConsent(new() { Password = "fixture-password" }, default));
        Assert.IsType<UnauthorizedResult>(await controller.RevokePlaylistConsent(default));
    }

    [Fact]
    public async Task ConsentEndpointsRequireSession_AndRequestDoesNotPrintPassword()
    {
        var request = new AdminAuthController.PlaylistConsentRequest { Password = "private-fixture" };
        Assert.DoesNotContain("private-fixture", request.ToString(), StringComparison.Ordinal);
        var controller = Controller();
        Assert.IsType<UnauthorizedResult>(await controller.GetPlaylistConsent(default));
        Assert.IsType<UnauthorizedResult>(await controller.GrantPlaylistConsent(request, default));
        Assert.IsType<UnauthorizedResult>(await controller.RevokePlaylistConsent(default));
        Assert.False(new AdminAuthController.LoginRequest().ManagePlaylists);
    }

    private AdminAuthController Controller(string? sessionId = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Backend:Type"] = "Subsonic" }).Build();
        var controller = new AdminAuthController(Options.Create(new JellyfinSettings()),
            Options.Create(new SubsonicSettings { Url = "http://backend.test" }), configuration, new Clients(_backend),
            _sessions, NullLogger<AdminAuthController>.Instance,
            new MediaAssetResolver(new TestMemoryApplicationCache(), NullLogger<MediaAssetResolver>.Instance),
            identityResolver: _identities, secrets: _secrets)
        { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };
        if (sessionId != null) controller.Request.Headers.Cookie = AdminAuthSessionService.SessionCookieName + "=" + sessionId;
        return controller;
    }

    public async Task DisposeAsync() => await _database.DisposeAsync();

    private sealed class Factory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
    }

    private sealed class Clients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Backend : HttpMessageHandler
    {
        public bool Accept { get; set; } = true;
        public string? ReturnedUser { get; set; }
        public string? RequestedUser { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var form = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(cancellationToken));
            RequestedUser = form["u"].ToString();
            Assert.Equal(RequestedUser, form["username"].ToString());
            return new(Accept ? HttpStatusCode.OK : HttpStatusCode.Unauthorized)
            {
                Content = new StringContent(JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["subsonic-response"] = new { status = "ok", user = new { username = ReturnedUser ?? RequestedUser, adminRole = false } }
                }))
            };
        }
    }
}
