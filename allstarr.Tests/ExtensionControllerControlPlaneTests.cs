using System.Net;
using System.Text;
using allstarr.Controllers;
using allstarr.Core.Extensions;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using allstarr.Models.Settings;
using allstarr.Services.Admin;
using allstarr.Services.Common;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace allstarr.Tests;

public sealed class ExtensionControllerControlPlaneTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "allstarr-extension-controller", Guid.NewGuid().ToString("N"));
    private ExtensionControlPlaneService _service = null!;
    private ExtensionManager _manager = null!;

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
        var clientFactory = new Mock<IHttpClientFactory>();
        clientFactory.Setup(item => item.CreateClient("ExtensionSdkV1"))
            .Returns(() => new HttpClient(new RegistryResponseHandler()));
        var environment = new Mock<IWebHostEnvironment>();
        environment.SetupGet(item => item.EnvironmentName).Returns(Environments.Development);
        environment.SetupGet(item => item.ContentRootPath).Returns(_root);
        var adminHelper = new AdminHelperService(
            NullLogger<AdminHelperService>.Instance,
            Options.Create(new JellyfinSettings()),
            environment.Object);
        _manager = new ExtensionManager(
            clientFactory.Object,
            NullLogger<ExtensionManager>.Instance,
            configuration,
            adminHelper,
            _service);
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
    public async Task RegistryRemovalRequiresAdministratorAndHonorsRevision()
    {
        var registry = await _service.AddRegistryAsync(new("Disposable", "https://extensions.example.test/disposable.json"));
        var anonymous = Controller();
        Assert.IsType<UnauthorizedObjectResult>(await anonymous.RemoveRegistry(registry.Id, registry.Revision, default));

        var administrator = Controller(Session(administrator: true));
        var removed = Assert.IsType<OkObjectResult>(await administrator.RemoveRegistry(
            registry.Id, registry.Revision, default));
        Assert.Contains("removed", System.Text.Json.JsonSerializer.Serialize(removed.Value), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await _service.ListRegistriesAsync());
    }

    [Fact]
    public void Controller_HasSingleDependencyInjectionConstructor()
    {
        Assert.Single(typeof(ExtensionController).GetConstructors());
    }

    [Fact]
    public async Task ReviewEndpoint_RequiresPlatformUserLinkedAdministratorSession()
    {
        var administrator = Controller(Session(administrator: true, allstarrUserId: null));
        var result = await administrator.ReviewPermissions(Guid.CreateVersion7(), new PermissionReviewRequest(), default);
        Assert.IsType<ConflictObjectResult>(result);
    }

    [Fact]
    public async Task AddRegistry_RejectsRepositoryPagesWithActionableApiFeedback()
    {
        var administrator = Controller(Session(administrator: true));

        var result = Assert.IsType<BadRequestObjectResult>(await administrator.AddRegistry(
            new RegistryRequest
            {
                Name = "SpotiFLAC",
                RegistryUrl = "https://github.com/spotiflacapp/SpotiFLAC-Extension"
            },
            default));
        var serialized = System.Text.Json.JsonSerializer.Serialize(result.Value);

        Assert.Contains("GitHub project page", serialized, StringComparison.Ordinal);
        Assert.Contains("raw registry.json", serialized, StringComparison.Ordinal);
        Assert.Empty(await _service.ListRegistriesAsync());
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
        return new ExtensionController(_manager, _service, null, NullLogger<ExtensionController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
    }

    private static AdminAuthSession Session(bool administrator, Guid? allstarrUserId = null) => new()
    {
        SessionId = "fixture",
        UserId = "backend-user",
        UserName = "Fixture",
        IsAdministrator = administrator,
        AllstarrUserId = allstarrUserId,
        JellyfinAccessToken = "fixture",
        ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
        LastSeenUtc = DateTime.UtcNow
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

    private sealed class RegistryResponseHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            const string registry = """
            {
              "extensions": [
                {
                  "id": "fixture-extension",
                  "downloadUrl": "https://extensions.example.test/fixture.zip",
                  "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                }
              ]
            }
            """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(registry, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class DbFactory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
        public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
