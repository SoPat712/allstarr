using allstarr.Controllers;
using allstarr.Core.Extensions;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace allstarr.Tests;

public sealed class ExtensionControllerControlPlaneTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "allstarr-extension-controller", Guid.NewGuid().ToString("N"));
    private ExtensionControlPlaneService _service = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        var options = new DbContextOptionsBuilder<AllstarrDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_root, "state.db")}").Options;
        var factory = new DbFactory(options);
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.MigrateAsync();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Extensions:Directory"] = Path.Combine(_root, "extensions")
        }).Build();
        _service = new ExtensionControlPlaneService(factory, new Clock(), configuration);
    }

    [Fact]
    public async Task RegistryEndpoints_RequireAdministratorAndReturnSafeRecords()
    {
        var anonymous = Controller();
        Assert.IsType<UnauthorizedObjectResult>(await anonymous.ListRegistries(default));

        var ordinaryUser = Controller(Session(administrator: false));
        var forbidden = Assert.IsType<ObjectResult>(await ordinaryUser.ListRegistries(default));
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);

        var administrator = Controller(Session(administrator: true));
        Assert.IsType<OkObjectResult>(await administrator.AddRegistry(
            new RegistryRequest { Name = "Official", RegistryUrl = "https://extensions.example.test/index.json" }, default));
        var result = Assert.IsType<OkObjectResult>(await administrator.ListRegistries(default));
        var serialized = System.Text.Json.JsonSerializer.Serialize(result.Value);
        Assert.Contains("extensions.example.test", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("packagePath", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReviewEndpoint_RequiresPlatformUserLinkedAdministratorSession()
    {
        var administrator = Controller(Session(administrator: true, allstarrUserId: null));
        var result = await administrator.ReviewPermissions(Guid.CreateVersion7(), new PermissionReviewRequest(), default);
        Assert.IsType<ConflictObjectResult>(result);
    }

    [Fact]
    public async Task LogEndpoint_RejectsUnboundedRequests()
    {
        var administrator = Controller(Session(administrator: true));
        var result = await administrator.ListLogs(null, null, 501, default);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task LegacyAndStagingEndpoints_DoNotBypassAdministratorAuthentication()
    {
        var controller = Controller();
        Assert.IsType<UnauthorizedObjectResult>(controller.GetRepositories());
        Assert.IsType<UnauthorizedObjectResult>(await controller.GetStoreExtensions(default));
        Assert.IsType<UnauthorizedObjectResult>(controller.GetInstalledExtensions());
        Assert.IsType<UnauthorizedObjectResult>(await controller.InstallExtension(new InstallRequest(), default));
        Assert.IsType<UnauthorizedObjectResult>(controller.UninstallExtension("fixture-extension"));
        Assert.IsType<UnauthorizedObjectResult>(controller.DisableExtension("fixture-extension"));
        Assert.IsType<UnauthorizedObjectResult>(await controller.EnableExtension("fixture-extension"));
        Assert.IsType<UnauthorizedObjectResult>(await controller.UninstallPackage(
            Guid.CreateVersion7(), new UninstallPackageRequest { RetainProviderAccounts = true }, default));
    }

    private ExtensionController Controller(AdminAuthSession? session = null)
    {
        var context = new DefaultHttpContext();
        if (session != null)
            context.Items[AdminAuthSessionService.HttpContextSessionItemKey] = session;
        return new ExtensionController(null!, _service, NullLogger<ExtensionController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
    }

    private static AdminAuthSession Session(bool administrator, Guid? allstarrUserId = null) => new()
    {
        SessionId = "fixture", UserId = "backend-user", UserName = "Fixture",
        IsAdministrator = administrator, AllstarrUserId = allstarrUserId,
        JellyfinAccessToken = "fixture", ExpiresAtUtc = DateTime.UtcNow.AddHours(1), LastSeenUtc = DateTime.UtcNow
    };

    public Task DisposeAsync()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        return Task.CompletedTask;
    }

    private sealed class Clock : IPlatformClock
    {
        public DateTimeOffset UtcNow => new(2026, 7, 12, 8, 0, 0, TimeSpan.Zero);
    }

    private sealed class DbFactory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
        public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
