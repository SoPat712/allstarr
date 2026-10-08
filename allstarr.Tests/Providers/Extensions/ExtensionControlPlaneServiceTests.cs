using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using allstarr.Core.Capabilities;
using allstarr.Core.Extensions;
using allstarr.Core.Downloads;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace allstarr.Tests;

public sealed class ExtensionControlPlaneServiceTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "allstarr-extension-control", Guid.NewGuid().ToString("N"));
    private readonly Guid _reviewer = Guid.CreateVersion7();
    private SqliteTestDatabase _database = null!;
    private DbFactory _factory = null!;
    private ExtensionControlPlaneService _service = null!;
    private IConfiguration _configuration = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _database = await SqliteTestDatabase.CreateAsync();
        _factory = new(_database.Options);
        await using var db = await _factory.CreateDbContextAsync();
        var tenant = Guid.CreateVersion7();
        db.Tenants.Add(new TenantRecord { Id = tenant, Slug = "extensions", Name = "Extensions", CreatedAt = DateTimeOffset.UtcNow });
        db.Users.Add(new PlatformUserRecord { Id = _reviewer, TenantId = tenant, DisplayName = "Reviewer", Status = PlatformUserStatus.Active, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        _configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Extensions:Directory"] = Path.Combine(_root, "extensions") }).Build();
        _service = new(_factory, new Clock(), _configuration);
    }

    [Fact]
    public async Task StageReviewActivateAndUpdate_AreDurableAndExplicit()
    {
        var registry = await _service.AddRegistryAsync(new("Fixture", "https://registry.example.test/index.json"));
        var v1 = Package("1.0.0", "a");
        var first = await _service.StageAsync(v1, registry.Id);
        Assert.Equal(ExtensionPackageState.ReviewRequired, first.State);

        first = await _service.ReviewAsync(first.Id, _reviewer, first.Revision,
        [
            new("network", "https://api.example.test/", true),
            new("secret", "accountToken", true)
        ]);
        Assert.Equal(ExtensionPackageState.Staged, first.State);
        first = await _service.ActivateAsync(first.Id, first.Revision);
        Assert.Equal(ExtensionPackageState.Active, first.State);

        var second = await _service.StageAsync(Package("1.1.0", "b"), registry.Id);
        second = await _service.ReviewAsync(second.Id, _reviewer, second.Revision,
        [
            new("network", "https://api.example.test/", true),
            new("secret", "accountToken", true)
        ]);
        second = await _service.ActivateAsync(second.Id, second.Revision);
        Assert.Equal(first.Id, second.PreviousPackageId);

        first = (await _service.ListPackagesAsync()).Single(item => item.Id == first.Id);
        Assert.Equal(ExtensionPackageState.RolledBack, first.State);
        Assert.Equal(ExtensionPackageState.Active, second.State);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(second.Id, (await db.ExtensionPackages.SingleAsync(item => item.State == ExtensionPackageState.Active)).Id);
        Assert.Equal(2, await db.ExtensionLogs.CountAsync(item => item.EventCode == "package.activated"));
    }

    [Fact]
    public async Task RequiredPermissionDenialPreventsActivationAndLogsAreRedacted()
    {
        var package = await _service.StageAsync(Package("2.0.0", "denied"));
        package = await _service.ReviewAsync(package.Id, _reviewer, package.Revision,
        [
            new("network", "https://api.example.test/", true),
            new("secret", "accountToken", false)
        ]);
        Assert.Equal(ExtensionPackageState.Failed, package.State);
        Assert.False(Directory.Exists(package.PackagePath));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.ActivateAsync(package.Id, package.Revision));

        await _service.WriteLogAsync(package.Id, "warning", "fixture.failed", "token=should-not-survive password:also-secret", "test");
        await using var db = await _factory.CreateDbContextAsync();
        var log = await db.ExtensionLogs.SingleAsync(item => item.EventCode == "fixture.failed");
        Assert.DoesNotContain("should-not-survive", log.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("also-secret", log.Message, StringComparison.Ordinal);
        Assert.Contains("[redacted]", log.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisabledPermissionedPackageRequiresFreshReviewBeforeReactivation()
    {
        var package = await _service.StageAsync(Package("1.0.0", "reenable"));
        package = await _service.ReviewAsync(package.Id, _reviewer, package.Revision,
        [
            new("network", "https://api.example.test/", true),
            new("secret", "accountToken", true)
        ]);
        package = await _service.ActivateAsync(package.Id, package.Revision);

        await _service.DisableAsync(package.Id, package.Revision);
        package = (await _service.ListPackagesAsync()).Single(item => item.Id == package.Id);
        Assert.Equal(ExtensionPackageState.Disabled, package.State);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.ActivateAsync(package.Id, package.Revision));
        package = await _service.ResetPermissionsForReviewAsync(package.Id, package.Revision);
        Assert.Equal(ExtensionPackageState.ReviewRequired, package.State);
        Assert.All(await _service.ListPermissionReviewsAsync(package.Id),
            item => Assert.Equal(ExtensionPermissionDecision.Pending, item.Decision));
        package = await _service.ReviewAsync(package.Id, _reviewer, package.Revision,
        [
            new("network", "https://api.example.test/", true),
            new("secret", "accountToken", true)
        ]);
        package = await _service.ActivateAsync(package.Id, package.Revision);
        Assert.Equal(ExtensionPackageState.Active, package.State);
    }

    [Fact]
    public async Task ActivePermissionedPackageCanStopForFreshReview()
    {
        var package = await _service.StageAsync(Package("1.0.0", "active-review"));
        package = await _service.ReviewAsync(package.Id, _reviewer, package.Revision,
        [
            new("network", "https://api.example.test/", true),
            new("secret", "accountToken", true)
        ]);
        package = await _service.ActivateAsync(package.Id, package.Revision);

        package = await _service.ResetPermissionsForReviewAsync(package.Id, package.Revision);

        Assert.Equal(ExtensionPackageState.ReviewRequired, package.State);
        Assert.NotNull(package.DisabledAt);
        Assert.All(await _service.ListPermissionReviewsAsync(package.Id),
            item => Assert.Equal(ExtensionPermissionDecision.Pending, item.Decision));
    }

    [Fact]
    public async Task AbandoningCancelledActivationRemovesContentAndLeavesHistoricalRecord()
    {
        var package = await _service.StageAsync(Package("1.0.0", "cancelled"));

        package = await _service.AbandonStagingAsync(package.Id, package.Revision);

        Assert.Equal(ExtensionPackageState.Uninstalled, package.State);
        Assert.Equal("staging_cancelled", package.FailureCode);
        Assert.False(Directory.Exists(package.PackagePath));
    }

    [Fact]
    public async Task RegistryRequiresExplicitHttpsConfiguration()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _service.AddRegistryAsync(new("Bad", "http://registry.example.test/index.json")));
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Empty(await db.ExtensionRegistries.ToListAsync());
    }

    [Fact]
    public async Task RegistryRemovalRequiresEveryDependentPackageToBeUninstalled()
    {
        var registry = await _service.AddRegistryAsync(new("Fixture", "https://registry.example.test/index.json"));
        var package = await _service.StageAsync(Package("1.0.0", "registry-removal"), registry.Id);

        var blocked = await Assert.ThrowsAsync<ExtensionRegistryInUseException>(() =>
            _service.RemoveRegistryAsync(registry.Id, registry.Revision));
        var dependency = Assert.Single(blocked.Dependencies);
        Assert.Equal(package.Id, dependency.PackageId);
        Assert.Equal("Fixture", dependency.DisplayName);

        package = await _service.ReviewAsync(package.Id, _reviewer, package.Revision,
        [
            new("network", "https://api.example.test/", true),
            new("secret", "accountToken", true)
        ]);
        package = await _service.ActivateAsync(package.Id, package.Revision);
        await _service.DisableAsync(package.Id, package.Revision);
        package = (await _service.ListPackagesAsync()).Single(item => item.Id == package.Id);
        package = await _service.UninstallAsync(package.Id, package.Revision);

        await _service.RemoveRegistryAsync(registry.Id, registry.Revision);
        Assert.Empty(await _service.ListRegistriesAsync());
        Assert.Null((await _service.ListPackagesAsync()).Single(item => item.Id == package.Id).RegistryId);
    }

    [Fact]
    public async Task ActivateRejectsPackageContentsChangedAfterStaging()
    {
        var package = await _service.StageAsync(Package("3.0.0", "tamper"));
        package = await _service.ReviewAsync(package.Id, _reviewer, package.Revision,
            [
                new("network", "https://api.example.test/", true),
                new("secret", "accountToken", true)
            ]);
        File.AppendAllText(Path.Combine(package.PackagePath, "index.js"), "// changed");
        await Assert.ThrowsAsync<ExtensionSdkValidationException>(() =>
            _service.ActivateAsync(package.Id, package.Revision));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RuntimeUpdatePreservesActiveVersionWhenPreviousPackageIsUninstalled(bool concurrentUninstall)
    {
        var package = await _service.StageAsync(Package("4.0.0", "runtime"));
        package = await _service.ReviewAsync(package.Id, _reviewer, package.Revision,
        [
            new("network", "https://api.example.test/", true),
            new("secret", "accountToken", true)
        ]);
        var registry = new ProviderRegistry([]);
        var registrationGate = new PausedProviderRegistration(registry);
        var clients = new Mock<IHttpClientFactory>();
        clients.Setup(item => item.CreateClient(It.IsAny<string>())).Returns(new HttpClient());
        var coordinator = new ExtensionRuntimeCoordinator(_factory, _service, registrationGate, registry, clients.Object,
            Mock.Of<allstarr.Core.Providers.Spotify.IProviderAccountSecretAccessor>(),
            new ProviderDownloadArtifactResolver(Mock.Of<IProviderDownloadArtifactStore>(),
                new ProviderDownloadWorkspaceOptions { RootPath = Path.Combine(_root, "download-workspaces") }),
            new ProviderDownloadWorkspaceOptions { RootPath = Path.Combine(_root, "download-workspaces") },
            _configuration,
            NullLogger<ExtensionRuntimeCoordinator>.Instance);

        package = await coordinator.ActivateAsync(package.Id, package.Revision);

        Assert.True(registry.TryGet(package.ExtensionId, out var descriptor));
        Assert.Equal(ProviderOrigin.Extension, descriptor!.Origin);
        Assert.Equal(ProviderAccountRequirement.Required, descriptor.Capabilities.Single().AccountRequirement);
        Assert.Contains(ProviderAccountScope.Personal, descriptor.Capabilities.Single().AllowedAccountScopes);
        Assert.True(registry.TryGetCapability<IProviderMetadataCapability>(package.ExtensionId,
            ProviderCapabilityKind.Metadata, out _));
        var previous = package;
        package = await _service.StageAsync(Package("4.1.0", "runtime-update"));
        package = await _service.ReviewAsync(package.Id, _reviewer, package.Revision,
        [
            new("network", "https://api.example.test/", true),
            new("secret", "accountToken", true)
        ]);
        IProviderMetadataCapability? activeCapability;
        if (concurrentUninstall)
        {
            registrationGate.PauseNextRegistration = true;
            var activation = Task.Run(() => coordinator.ActivateAsync(package.Id, package.Revision));
            Task<ExtensionPackageRecord>? uninstall = null;
            try
            {
                await registrationGate.Registered.Task.WaitAsync(TimeSpan.FromSeconds(20));
                Assert.True(registry.TryGetCapability(package.ExtensionId,
                    ProviderCapabilityKind.Metadata, out activeCapability));
                previous = (await _service.ListPackagesAsync()).Single(item => item.Id == previous.Id);
                uninstall = Task.Run(() => coordinator.UninstallAsync(previous.Id, previous.Revision));
                for (var attempt = 0; attempt < 100; attempt++)
                {
                    var stored = (await _service.ListPackagesAsync()).Single(item => item.Id == previous.Id);
                    if (stored.State == ExtensionPackageState.Uninstalled) break;
                    await Task.Delay(10);
                }
                Assert.Equal(ExtensionPackageState.Uninstalled,
                    (await _service.ListPackagesAsync()).Single(item => item.Id == previous.Id).State);
                await Task.WhenAny(uninstall, Task.Delay(TimeSpan.FromSeconds(1)));
            }
            finally
            {
                registrationGate.Resume.TrySetResult();
                package = await activation.WaitAsync(TimeSpan.FromSeconds(20));
                if (uninstall != null) await uninstall.WaitAsync(TimeSpan.FromSeconds(20));
            }
        }
        else
        {
            package = await coordinator.ActivateAsync(package.Id, package.Revision);
            Assert.True(registry.TryGetCapability(package.ExtensionId,
                ProviderCapabilityKind.Metadata, out activeCapability));
            previous = (await _service.ListPackagesAsync()).Single(item => item.Id == previous.Id);
            await coordinator.UninstallAsync(previous.Id, previous.Revision);
        }
        Assert.False(Directory.Exists(previous.PackagePath));
        Assert.True(Directory.Exists(package.PackagePath));
        Assert.True(registry.TryGetCapability<IProviderMetadataCapability>(package.ExtensionId,
            ProviderCapabilityKind.Metadata, out var retainedCapability));
        Assert.Same(activeCapability, retainedCapability);

        await coordinator.DisableAsync(package.Id, package.Revision);
        Assert.False(registry.TryGet(package.ExtensionId, out _));
        package = (await _service.ListPackagesAsync(package.ExtensionId)).Single(item => item.Id == package.Id);
        package = await coordinator.UninstallAsync(package.Id, package.Revision);
        Assert.Equal(ExtensionPackageState.Uninstalled, package.State);
        Assert.False(Directory.Exists(package.PackagePath));
        var reinstalled = await _service.StageAsync(Package("4.0.0", "runtime"));
        Assert.NotEqual(package.Id, reinstalled.Id);
        Assert.Equal(ExtensionPackageState.ReviewRequired, reinstalled.State);
    }

    private sealed class PausedProviderRegistration(ProviderRegistry registry) : IDynamicProviderRegistry
    {
        public bool PauseNextRegistration { get; set; }
        public TaskCompletionSource Registered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void RegisterOrReplaceExtension(ProviderRegistration registration)
        {
            registry.RegisterOrReplaceExtension(registration);
            if (!PauseNextRegistration) return;
            PauseNextRegistration = false;
            Registered.TrySetResult();
            Resume.Task.WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
        }

        public bool RemoveExtension(string providerId) => registry.RemoveExtension(providerId);
    }

    private VerifiedExtensionPackage Package(string version, string suffix)
    {
        var packageRoot = Path.Combine(_root, "extensions", ".staging", suffix);
        Directory.CreateDirectory(packageRoot);
        var manifest = new ExtensionSdkManifest("fixture-provider", "Fixture", version, "1", "index.js",
            [new(ProviderCapabilityKind.Metadata, ["searchTracks", "getTrack"], [ProviderAccountScope.Personal])],
            [new(ExtensionPermissionKind.Network, "https://api.example.test/", true), new(ExtensionPermissionKind.Secret, "accountToken", true)]);
        File.WriteAllText(Path.Combine(packageRoot, "manifest.json"), JsonSerializer.Serialize(new
        {
            id = manifest.Id,
            displayName = manifest.DisplayName,
            version = manifest.Version,
            sdkVersion = manifest.SdkVersion,
            entryPoint = manifest.EntryPoint,
            capabilities = new[] { new { kind = "metadata", hooks = new[] { "searchTracks", "getTrack" }, accountScopes = new[] { "user" } } },
            permissions = new[] { new { kind = "network", value = "https://api.example.test/", required = true }, new { kind = "secret", value = "accountToken", required = true } }
        }));
        File.WriteAllText(Path.Combine(packageRoot, "index.js"),
            "registerExtension({ searchTracks: function() { return []; }, getTrack: function() { return null; } });");
        var archiveHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(suffix))).ToLowerInvariant();
        return new(manifest, archiveHash, 100, 100, 2, packageRoot,
            ExtensionSdkV1.ComputePackageContentSha256(packageRoot));
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "allstarr.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }

    public async Task DisposeAsync()
    {
        await _database.DisposeAsync();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class Clock : IPlatformClock
    {
        public DateTimeOffset UtcNow => new(2026, 7, 12, 7, 0, 0, TimeSpan.Zero);
    }

    private sealed class DbFactory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
        public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}
