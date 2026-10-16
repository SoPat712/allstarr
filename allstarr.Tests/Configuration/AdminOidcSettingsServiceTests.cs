using System.Text.Json;
using allstarr.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using allstarr.Core.Configuration;
using allstarr.Core.Operations;
using allstarr.Core.Secrets;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using allstarr.Services.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Moq;

namespace allstarr.Tests;

public sealed class AdminOidcSettingsServiceTests : IAsyncLifetime
{
    private SqliteTestDatabase database = null!;
    private Factory factory = null!;
    private ConfigurationManager configuration = null!;
    private AdminOidcSettingsService service = null!;
    private readonly Guid userId = Guid.CreateVersion7();
    private AdminAuthSession Actor => new()
    {
        SessionId = "access-fixture",
        UserId = "admin-fixture",
        UserName = "Administrator",
        IsAdministrator = true,
        AllstarrUserId = userId,
        BackendType = "Jellyfin",
        BackendInstanceId = "primary",
        JellyfinAccessToken = "",
        ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
    };

    public async Task InitializeAsync()
    {
        database = await SqliteTestDatabase.CreateAsync();
        factory = new(database.Options);
        configuration = new() { ["Storage:DataDirectory"] = database.StorageOptions.DataDirectory, ["Admin:BasePath"] = "/allstarr" };
        var clock = new SystemPlatformClock();
        var options = new SecretStoreOptions { KeyRingPath = Path.Combine(database.StorageOptions.DataDirectory, "keyring.json") };
        var ring = new FileSecretKeyRingProvider(options);
        await ring.CreateIfMissingAsync(false);
        service = new(factory, new(factory, configuration, clock, new RuntimeSettingsChangeSignal()),
            new(factory, ring, options, clock), configuration, clock);
        await using var db = new AllstarrDbContext(database.Options);
        db.Users.Add(new UserRecord
        {
            Id = userId,
            BackendType = "jellyfin",
            BackendInstanceId = "primary",
            BackendPrincipalId = "admin-fixture",
            DisplayName = "Administrator",
            IsAdmin = true,
            Enabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private async Task<AdminOidcSettingsRequest> Request(string? secret = "private-access-fixture") => new()
    {
        Enabled = true,
        Authority = "https://identity.example.test",
        ClientId = "household-fixture",
        PublicUrl = "https://admin.example.test/allstarr",
        DisplayName = "Household login",
        ClientSecret = secret,
        ExpectedRevision = (await service.GetAsync(Actor, default)).Revision
    };

    [Fact]
    public async Task SaveEncryptsAndRotatesOnlyTheExactClientThenLoadsBeforeStartup()
    {
        await service.SaveAsync(Actor, await Request(), default);
        var snapshot = await service.GetAsync(Actor, default);
        Assert.True(snapshot.Enabled);
        Assert.True(snapshot.HasClientSecret);
        Assert.DoesNotContain("private-access-fixture", JsonSerializer.Serialize(snapshot), StringComparison.Ordinal);
        await service.SaveAsync(Actor, await Request("rotated-access-fixture"), default);
        await using (var db = new AllstarrDbContext(database.Options))
        {
            var reference = Assert.Single(await db.SecretReferences.ToListAsync());
            Assert.Equal(2, reference.ActiveVersion);
            Assert.Equal(2, await db.SecretVersions.CountAsync());
            Assert.StartsWith("admin-oidc:", reference.Purpose, StringComparison.Ordinal);
            Assert.Null(reference.UserId);
            var rows = JsonSerializer.Serialize(await db.RuntimeSettings.ToListAsync());
            Assert.DoesNotContain("access-fixture", rows, StringComparison.Ordinal);
            Assert.DoesNotContain("access-fixture", JsonSerializer.Serialize(await db.AuditEvents.ToListAsync()), StringComparison.Ordinal);
        }
        var restarted = new ConfigurationManager { ["Storage:DataDirectory"] = database.StorageOptions.DataDirectory, ["Admin:BasePath"] = "/allstarr" };
        await StartupConfiguration.PrepareAsync(restarted, Mock.Of<IHostEnvironment>());
        Assert.Equal("true", restarted["Admin:Oidc:Enabled"]);
        Assert.Equal("rotated-access-fixture", restarted["Admin:Oidc:ClientSecret"]);
        Assert.Equal("https://identity.example.test", restarted["Admin:Oidc:Authority"]);
    }

    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task ChangingTheIdentityServiceCannotReuseOrExposeItsOldSecret()
    {
        await service.SaveAsync(Actor, await Request(), default);
        var changed = await Request(null);
        changed.Authority = "https://other-identity.example.test";
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(Actor, changed, default));
        await using var db = new AllstarrDbContext(database.Options);
        Assert.Equal(1, (await db.SecretReferences.SingleAsync()).ActiveVersion);
        Assert.Equal("https://identity.example.test", (await service.GetAsync(Actor, default)).Authority);
        var authority = await db.RuntimeSettings.SingleAsync(item => item.Key == "Admin:Oidc:Authority");
        authority.ValueJson = "\"https://other-identity.example.test\"";
        await db.SaveChangesAsync();
        var restarted = new ConfigurationManager { ["Storage:DataDirectory"] = database.StorageOptions.DataDirectory, ["Admin:BasePath"] = "/allstarr" };
        var storage = await StartupConfiguration.PrepareAsync(restarted, Mock.Of<IHostEnvironment>());
        Assert.Equal(DurableStorageReadiness.Ready, storage.GetSnapshot().Readiness);
        Assert.Equal("false", restarted["Admin:Oidc:Enabled"]);
        Assert.Equal("", restarted["Admin:Oidc:ClientSecret"]);
        Assert.Equal("true", restarted["Setup:OidcNeedsReview"]);
        Assert.Equal("false", restarted["Setup:BackendConfigured"]);
    }

    [Fact]
    public async Task StaleEditsAndDisabledAdministratorsCannotRotateOrRemoveCredentials()
    {
        var stale = await Request("stale-fixture");
        await service.SaveAsync(Actor, await Request(), default);
        await Assert.ThrowsAsync<RuntimeSettingConflictException>(() => service.SaveAsync(Actor, stale, default));
        var revoked = await Request("revoked-fixture");
        await using var db = new AllstarrDbContext(database.Options);
        await db.Users.Where(item => item.Id == userId).ExecuteUpdateAsync(update => update.SetProperty(item => item.IsAdmin, false));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SaveAsync(Actor, revoked, default));
        Assert.Equal(1, (await db.SecretReferences.SingleAsync()).ActiveVersion);
    }

    [Theory]
    [InlineData("http://identity.example.test", "https://admin.example.test/allstarr")]
    [InlineData("https://identity.example.test", "https://admin.example.test/wrong")]
    [InlineData("https://identity.example.test", "https://admin.example.test/allstarr?code=private-fixture")]
    public async Task InvalidPolicyRollsBackEveryCredentialAndSetting(string authority, string publicUrl)
    {
        var request = await Request();
        request.Authority = authority;
        request.PublicUrl = publicUrl;
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(Actor, request, default));
        await using var db = new AllstarrDbContext(database.Options);
        Assert.Empty(await db.RuntimeSettings.ToListAsync());
        Assert.Empty(await db.SecretReferences.ToListAsync());
        Assert.Empty(await db.SecretVersions.ToListAsync());
        Assert.Empty(await db.AuditEvents.ToListAsync());
    }

    [Fact]
    public async Task FailedStagingRollsBackANewEncryptedSecretVersion()
    {
        var request = await Request();
        request.Enabled = false;
        request.Authority = "http://identity.example.test";
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(Actor, request, default));
        await using var db = new AllstarrDbContext(database.Options);
        Assert.Empty(await db.RuntimeSettings.ToListAsync());
        Assert.Empty(await db.SecretReferences.ToListAsync());
        Assert.Empty(await db.SecretVersions.ToListAsync());
    }

    [Fact]
    public async Task ExplicitRemovalDisablesSignOnAndRevokesOnlyItsOwnReference()
    {
        await service.SaveAsync(Actor, await Request(), default);
        var removal = await Request(null);
        removal.Enabled = false;
        removal.RemoveClientSecret = true;
        await service.SaveAsync(Actor, removal, default);
        Assert.False((await service.GetAsync(Actor, default)).HasClientSecret);
        await using var db = new AllstarrDbContext(database.Options);
        Assert.NotNull((await db.SecretReferences.SingleAsync()).RevokedAt);
        Assert.Single(await db.SecretVersions.ToListAsync());
    }

    [Fact]
    public async Task ControllerRequiresFreshAntiforgeryAndCompletesTheResponseBeforeRestarting()
    {
        var context = new DefaultHttpContext();
        var completion = new CompletionFeature();
        context.Features.Set<IHttpResponseFeature>(completion);
        context.Items[AdminAuthSessionService.HttpContextSessionItemKey] = Actor;
        var antiforgery = new Mock<IAntiforgery>();
        antiforgery.Setup(item => item.GetAndStoreTokens(It.IsAny<HttpContext>()))
            .Returns(new AntiforgeryTokenSet("csrf-fixture", "cookie-fixture", "field", "X-Allstarr-CSRF"));
        var lifetime = new Mock<IHostApplicationLifetime>();
        var controller = new AdminAccessController(service, null!, lifetime.Object, antiforgery.Object)
        { ControllerContext = new() { HttpContext = context } };
        var rejected = await Request();
        Assert.IsType<BadRequestObjectResult>(await controller.SaveOidc(rejected, default));
        Assert.Null(rejected.ClientSecret);
        Assert.Empty(completion.Callbacks);
        var get = Assert.IsType<OkObjectResult>(await controller.GetOidc(default));
        using var response = JsonDocument.Parse(JsonSerializer.Serialize(get.Value));
        Assert.Equal("csrf-fixture", response.RootElement.GetProperty("csrfToken").GetString());
        antiforgery.Setup(item => item.IsRequestValidAsync(It.IsAny<HttpContext>())).ReturnsAsync(true);
        var request = await Request();
        var saved = Assert.IsType<OkObjectResult>(await controller.SaveOidc(request, default));
        Assert.DoesNotContain("private-access-fixture", JsonSerializer.Serialize(saved.Value), StringComparison.Ordinal);
        Assert.Null(request.ClientSecret);
        Assert.Equal("no-store", controller.Response.Headers.CacheControl);
        lifetime.Verify(item => item.StopApplication(), Times.Never);
        await Assert.Single(completion.Callbacks)();
        lifetime.Verify(item => item.StopApplication(), Times.Once);
    }

    private sealed class CompletionFeature : HttpResponseFeature
    {
        public List<Func<Task>> Callbacks { get; } = [];
        public override void OnCompleted(Func<object, Task> callback, object state) => Callbacks.Add(() => callback(state));
    }

    private sealed class Factory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    { public AllstarrDbContext CreateDbContext() => new(options); }
    public async Task DisposeAsync() { database.ClearHostPool(); await database.DisposeAsync(); }
}
