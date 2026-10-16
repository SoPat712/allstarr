using System.Text.Json;
using allstarr.Controllers;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using allstarr.Core.Configuration;
using allstarr.Core.Operations;
using allstarr.Core.Secrets;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using allstarr.Models.Settings;
using allstarr.Services.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Moq;

namespace allstarr.Tests;

public sealed class BackendSetupServiceTests : IAsyncLifetime
{
    private SqliteTestDatabase database = null!;
    private Factory factory = null!;
    private ConfigurationManager configuration = null!;
    private DurableRuntimeSettingsService settings = null!;
    private EncryptedSecretStore secrets = null!;
    private DurableStorageState storage = null!;
    private readonly Mock<IBackendConnectionVerifier> verifier = new(MockBehavior.Strict);
    private readonly SystemPlatformClock clock = new();

    public async Task InitializeAsync()
    {
        database = await SqliteTestDatabase.CreateAsync();
        factory = new(database.Options);
        configuration = new() { ["Storage:DataDirectory"] = database.StorageOptions.DataDirectory, ["Setup:BackendConfigured"] = "false" };
        settings = new(factory, configuration, clock, new RuntimeSettingsChangeSignal());
        var options = new SecretStoreOptions { KeyRingPath = Path.Combine(database.StorageOptions.DataDirectory, "keyring.json") };
        var ring = new FileSecretKeyRingProvider(options);
        await ring.CreateIfMissingAsync(false);
        secrets = new(factory, ring, options, clock);
        storage = new(database.StorageOptions);
        storage.Set(DurableStorageReadiness.Ready, "fixture");
    }

    private BackendSetupService Service() => new(factory, configuration, storage, settings, secrets, verifier.Object, clock);
    private static BackendSetupRequest Request(bool scans = true) => new()
    {
        Backend = "Jellyfin",
        Url = "http://backend-fixture:8096/",
        Username = "admin-fixture",
        Password = "private-password-fixture",
        UseForLibraryScans = scans
    };
    private void Accept() => verifier.Setup(item => item.VerifyAsync(BackendType.Jellyfin, "http://backend-fixture:8096", "admin-fixture",
        "private-password-fixture", It.IsAny<CancellationToken>())).ReturnsAsync(new AdminOidcBackendUser("principal-fixture", "Administrator", true, "private-token-fixture"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Save_VerifiesThenPersistsOnceAndRestartsWithTheSameEncryptedConnection(bool scans)
    {
        Accept();
        await Service().SaveAsync(Request(scans), default);
        Assert.True(Service().IsConfigured);
        Assert.Equal("true", configuration["Setup:BackendRestartRequired"]);
        await using var db = new AllstarrDbContext(database.Options);
        var rows = await db.RuntimeSettings.ToListAsync();
        Assert.Equal(scans ? 5 : 3, rows.Count);
        Assert.All(rows, row => Assert.Equal(1, row.Revision));
        var serialized = JsonSerializer.Serialize(rows);
        Assert.DoesNotContain("private-password-fixture", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("private-token-fixture", serialized, StringComparison.Ordinal);
        Assert.Equal(scans ? 1 : 0, await db.SecretReferences.CountAsync());
        Assert.Empty(await db.ProviderAccounts.ToListAsync());
        Assert.Equal("backend.configure", (await db.AuditEvents.SingleAsync()).Action);
        var replay = await Assert.ThrowsAsync<BackendSetupException>(() => Service().SaveAsync(Request(scans), default));
        Assert.Equal("backend_already_configured", replay.Code);
        var restarted = new ConfigurationManager { ["Storage:DataDirectory"] = database.StorageOptions.DataDirectory };
        await StartupConfiguration.PrepareAsync(restarted, Mock.Of<IHostEnvironment>());
        Assert.Equal("http://backend-fixture:8096", restarted["Jellyfin:Url"]);
        Assert.Equal(scans ? "private-token-fixture" : "", restarted["Jellyfin:ApiKey"]);
        verifier.Verify(item => item.VerifyAsync(It.IsAny<BackendType>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Lane", "ReleaseCritical")]
    public async Task Save_RejectsUnverifiedAndNonAdministratorIdentitiesWithoutAnyPersistence(bool returnsListener)
    {
        verifier.Setup(item => item.VerifyAsync(It.IsAny<BackendType>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(returnsListener ? new AdminOidcBackendUser("listener", "Listener", false, "private-token-fixture") : null);
        var error = await Assert.ThrowsAsync<BackendSetupException>(() => Service().SaveAsync(Request(), default));
        Assert.Equal("administrator_required", error.Code);
        await using var db = new AllstarrDbContext(database.Options);
        Assert.Empty(await db.RuntimeSettings.ToListAsync());
        Assert.Empty(await db.SecretReferences.ToListAsync());
        Assert.Empty(await db.AuditEvents.ToListAsync());
        Assert.False(Service().IsConfigured);
    }

    [Fact]
    public async Task Save_VerificationDoesNotHoldAWriterAndConcurrentSetupCannotOverwriteTheWinner()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        verifier.Setup(item => item.VerifyAsync(It.IsAny<BackendType>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async () => { entered.TrySetResult(); await resume.Task; return new AdminOidcBackendUser("principal", "Administrator", true, "token"); });
        var saving = Service().SaveAsync(Request(), default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await Task.Run(() => settings.ApplyBatchAsync([new("Cache:LyricsDays", "31")], "concurrent-fixture")).WaitAsync(TimeSpan.FromSeconds(3));
            await settings.ApplyBatchAsync([new("Backend:Type", "Subsonic"), new("Backend:Url", "http://winner-fixture:4533")], "winner-fixture");
        }
        finally { resume.TrySetResult(); }
        var error = await Assert.ThrowsAsync<BackendSetupException>(() => saving);
        Assert.Equal("backend_already_configured", error.Code);
        await using var db = new AllstarrDbContext(database.Options);
        Assert.Equal("\"http://winner-fixture:4533\"", (await db.RuntimeSettings.SingleAsync(item => item.Key == "Backend:Url")).ValueJson);
        Assert.Empty(await db.SecretReferences.ToListAsync());
        var audit = await db.AuditEvents.ToListAsync();
        Assert.Equal(2, audit.Count);
        Assert.All(audit, item => Assert.Equal("runtime-settings.batch-apply", item.Action));
    }

    [Fact]
    public async Task Save_MissingKeyRingRollsBackTheConnectionAndItsCredentialTogether()
    {
        Accept();
        File.Delete(Path.Combine(database.StorageOptions.DataDirectory, "keyring.json"));
        await Assert.ThrowsAsync<FileNotFoundException>(() => Service().SaveAsync(Request(), default));
        await using var db = new AllstarrDbContext(database.Options);
        Assert.Empty(await db.RuntimeSettings.ToListAsync());
        Assert.Empty(await db.SecretReferences.ToListAsync());
        Assert.Empty(await db.AuditEvents.ToListAsync());
        Assert.False(Service().IsConfigured);
    }

    [Theory]
    [InlineData("file:///tmp/server")]
    [InlineData("http://user:password@backend")]
    [InlineData("http://backend/?token=private")]
    public async Task Save_RejectsUnsafeAddressesBeforeVerification(string address)
    {
        var request = Request(); request.Url = address;
        var error = await Assert.ThrowsAsync<BackendSetupException>(() => Service().SaveAsync(request, default));
        Assert.Equal("invalid_backend", error.Code);
        verifier.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Controller_RequiresCsrfAndRestartsOnlyAfterTheSavedResponseCompletes()
    {
        Accept();
        var antiforgery = new Mock<IAntiforgery>(MockBehavior.Strict);
        antiforgery.Setup(item => item.IsRequestValidAsync(It.IsAny<HttpContext>())).ReturnsAsync(false);
        var lifetime = new Mock<IHostApplicationLifetime>(MockBehavior.Strict);
        lifetime.Setup(item => item.StopApplication());
        var completion = new CompletionFeature();
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpResponseFeature>(completion);
        var controller = new BackendSetupController(Service(), storage, antiforgery.Object, lifetime.Object)
        { ControllerContext = new() { HttpContext = context } };
        var blocked = Assert.IsType<BadRequestObjectResult>(await controller.Save(Request(), default));
        Assert.Contains("setup_csrf_required", JsonSerializer.Serialize(blocked.Value), StringComparison.Ordinal);
        verifier.VerifyNoOtherCalls();
        Assert.Empty(completion.Callbacks);
        antiforgery.Setup(item => item.IsRequestValidAsync(It.IsAny<HttpContext>())).ReturnsAsync(true);
        var request = Request();
        var saved = Assert.IsType<OkObjectResult>(await controller.Save(request, default));
        var response = JsonSerializer.Serialize(saved.Value);
        Assert.DoesNotContain("private-password-fixture", response, StringComparison.Ordinal);
        Assert.DoesNotContain("private-token-fixture", response, StringComparison.Ordinal);
        Assert.Equal("", request.Password);
        Assert.Equal("no-store", controller.Response.Headers.CacheControl);
        lifetime.Verify(item => item.StopApplication(), Times.Never);
        await Assert.Single(completion.Callbacks)();
        lifetime.Verify(item => item.StopApplication(), Times.Once);
        var replay = Assert.IsType<ObjectResult>(await controller.Save(Request(), default));
        Assert.Equal(409, replay.StatusCode);
        Assert.Single(completion.Callbacks);
    }

    [Fact]
    public void Controller_StatusOffersTheSharedFirstStepWithoutExposingConnectionOrCredentials()
    {
        var antiforgery = new Mock<IAntiforgery>();
        antiforgery.Setup(item => item.GetAndStoreTokens(It.IsAny<HttpContext>()))
            .Returns(new AntiforgeryTokenSet("csrf-fixture", "cookie-fixture", "field", "X-Allstarr-CSRF"));
        var controller = new BackendSetupController(Service(), storage, antiforgery.Object, Mock.Of<IHostApplicationLifetime>())
        { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };
        var result = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(controller.Status()).Value,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.False(result.GetProperty("configured").GetBoolean());
        Assert.True(result.GetProperty("storageReady").GetBoolean());
        Assert.Equal(OnboardingStateService.BackendIdentityStep, result.GetProperty("step").GetProperty("id").GetString());
        Assert.Equal("csrf-fixture", result.GetProperty("csrfToken").GetString());
        Assert.False(result.TryGetProperty("url", out _));
    }

    private sealed class CompletionFeature : HttpResponseFeature
    {
        public List<Func<Task>> Callbacks { get; } = [];
        public override void OnCompleted(Func<object, Task> callback, object state) => Callbacks.Add(() => callback(state));
    }

    private sealed class Factory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
    }
    public async Task DisposeAsync() { database.ClearHostPool(); await database.DisposeAsync(); }
}
