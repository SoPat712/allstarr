using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using allstarr.Core.Capabilities;
using allstarr.Core.Extensions;
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
    private DbFactory _factory = null!;
    private ExtensionControlPlaneService _service = null!;
    private IConfiguration _configuration = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        var options = new DbContextOptionsBuilder<AllstarrDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_root, "state.db")}").Options;
        _factory = new(options);
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.MigrateAsync();
        var tenant = Guid.CreateVersion7();
        db.Tenants.Add(new TenantRecord { Id = tenant, Slug = "extensions", Name = "Extensions", CreatedAt = DateTimeOffset.UtcNow });
        db.Users.Add(new PlatformUserRecord { Id = _reviewer, TenantId = tenant, DisplayName = "Reviewer", Status = PlatformUserStatus.Active, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        _configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Extensions:Directory"] = Path.Combine(_root, "extensions") }).Build();
        _service = new(_factory, new Clock(), _configuration);
    }

    [Fact]
    public async Task StageReviewActivateUpdateAndRollback_AreDurableAndExplicit()
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

        second = await _service.RollbackAsync(second.Id, second.Revision);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(ExtensionPackageState.Active, second.State);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Single(await db.ExtensionPackages.Where(item => item.State == ExtensionPackageState.Active).ToListAsync());
        Assert.Contains(await db.ExtensionLogs.ToListAsync(), item => item.EventCode == "package.rolled-back");
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
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.ActivateAsync(package.Id, package.Revision));

        await _service.WriteLogAsync(package.Id, "warning", "fixture.failed", "token=should-not-survive password:also-secret", "test");
        await using var db = await _factory.CreateDbContextAsync();
        var log = await db.ExtensionLogs.SingleAsync(item => item.EventCode == "fixture.failed");
        Assert.DoesNotContain("should-not-survive", log.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("also-secret", log.Message, StringComparison.Ordinal);
        Assert.Contains("[redacted]", log.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RegistryRequiresExplicitHttpsConfiguration()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _service.AddRegistryAsync(new("Bad", "http://registry.example.test/index.json")));
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Empty(await db.ExtensionRegistries.ToListAsync());
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

    [Fact]
    public async Task RuntimeActivationRegistersReviewedPackageAndDisableRemovesIt()
    {
        var package = await _service.StageAsync(Package("4.0.0", "runtime"));
        package = await _service.ReviewAsync(package.Id, _reviewer, package.Revision,
        [
            new("network", "https://api.example.test/", true),
            new("secret", "accountToken", true)
        ]);
        var registry = new ProviderRegistry([]);
        var clients = new Mock<IHttpClientFactory>();
        clients.Setup(item => item.CreateClient(It.IsAny<string>())).Returns(new HttpClient());
        var coordinator = new ExtensionRuntimeCoordinator(_factory, _service, registry, registry, clients.Object,
            Mock.Of<allstarr.Core.Providers.Spotify.IProviderAccountSecretAccessor>(),
            new FirstPartyExtensionPolicy(_configuration), _configuration,
            NullLogger<ExtensionRuntimeCoordinator>.Instance);

        package = await coordinator.ActivateAsync(package.Id, package.Revision);

        Assert.True(registry.TryGet(package.ExtensionId, out var descriptor));
        Assert.Equal(ProviderOrigin.Extension, descriptor!.Origin);
        Assert.Equal(ProviderAccountRequirement.Required, descriptor.Capabilities.Single().AccountRequirement);
        Assert.Contains(ProviderAccountScope.User, descriptor.Capabilities.Single().AllowedAccountScopes);
        Assert.True(registry.TryGetCapability<IProviderMetadataCapability>(package.ExtensionId,
            ProviderCapabilityKind.Metadata, out _));
        await coordinator.DisableAsync(package.Id, package.Revision);
        Assert.False(registry.TryGet(package.ExtensionId, out _));
        package = (await _service.ListPackagesAsync(package.ExtensionId)).Single(item => item.Id == package.Id);
        package = await coordinator.UninstallAsync(package.Id, package.Revision, retainProviderAccounts: true);
        Assert.Equal(ExtensionPackageState.Uninstalled, package.State);
        Assert.False(Directory.Exists(package.PackagePath));
        var reinstalled = await _service.StageAsync(Package("4.0.0", "runtime"));
        Assert.NotEqual(package.Id, reinstalled.Id);
        Assert.Equal(ExtensionPackageState.ReviewRequired, reinstalled.State);
    }

    [Fact]
    public async Task FirstPartyBootstrap_VerifiesAndStagesWithoutAutoApprovingPermissions()
    {
        var bundle = Path.Combine(_root, "bundle");
        Directory.CreateDirectory(bundle);
        var source = Path.Combine(RepositoryRoot(), "first-party", "providers", "deezer");
        var archive = FirstPartyExtensionPackages.Build(source, Path.Combine(bundle, "deezer-1.0.0.zip"));
        var lockPath = Path.Combine(bundle, "bundle.lock.json");
        File.WriteAllText(lockPath, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            sdkVersion = "1",
            packages = new[] { new
            {
                id = "deezer", version = "1.0.0", activation = "ready",
                archiveFile = Path.GetFileName(archive.Path), archiveSha256 = archive.Sha256,
                contentSha256 = archive.ContentSha256
            } }
        }));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Extensions:Directory"] = Path.Combine(_root, "extensions"),
            ["Extensions:FirstPartyBundleLockPath"] = lockPath,
            ["Extensions:BootstrapFirstPartyBundle"] = "true"
        }).Build();
        var controlPlane = new ExtensionControlPlaneService(_factory, new Clock(), configuration);
        var bootstrapper = new FirstPartyExtensionBootstrapper(new FirstPartyExtensionPolicy(configuration),
            controlPlane, configuration, NullLogger<FirstPartyExtensionBootstrapper>.Instance);

        await bootstrapper.StartAsync(default);
        await bootstrapper.StartAsync(default);

        var package = Assert.Single(await controlPlane.ListPackagesAsync("deezer"));
        Assert.Equal(ExtensionPackageState.ReviewRequired, package.State);
        var reviews = await controlPlane.ListPermissionReviewsAsync(package.Id);
        Assert.NotEmpty(reviews);
        Assert.All(reviews, item => Assert.Equal(ExtensionPermissionDecision.Pending, item.Decision));
    }

    [Fact]
    public async Task StartupRestore_UsesLockedFirstPartyReplacementAndDisableRestoresBuiltIn()
    {
        var package = await _service.StageAsync(Package("5.0.0", "first-party"));
        package = await _service.ReviewAsync(package.Id, _reviewer, package.Revision,
        [
            new("network", "https://api.example.test/", true),
            new("secret", "accountToken", true)
        ]);
        package = await _service.ActivateAsync(package.Id, package.Revision);
        var lockPath = Path.Combine(_root, "runtime-bundle.lock.json");
        File.WriteAllText(lockPath, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            sdkVersion = "1",
            packages = new[] { new
            {
                id = package.ExtensionId, version = package.Version, activation = "ready",
                archiveFile = "fixture-provider.zip", archiveSha256 = package.Sha256,
                contentSha256 = package.ContentSha256
            } }
        }));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Extensions:Directory"] = Path.Combine(_root, "extensions"),
            ["Extensions:FirstPartyBundleLockPath"] = lockPath
        }).Build();
        var builtIn = new ProviderRegistration(new ProviderDescriptor("fixture-provider", "Built-in fixture",
            "fallback", ProviderOrigin.BuiltIn, "1", "1",
            [new ProviderCapabilityDescriptor(ProviderCapabilityKind.Metadata,
                ProviderCapabilitySupportState.ConfiguredOnly, ProviderAccountRequirement.None, "1")],
            new ProviderPermissionDescriptor()));
        var registry = new ProviderRegistry([builtIn]);
        var clients = new Mock<IHttpClientFactory>();
        clients.Setup(item => item.CreateClient(It.IsAny<string>())).Returns(new HttpClient());
        var coordinator = new ExtensionRuntimeCoordinator(_factory, _service, registry, registry, clients.Object,
            Mock.Of<allstarr.Core.Providers.Spotify.IProviderAccountSecretAccessor>(),
            new FirstPartyExtensionPolicy(configuration), configuration,
            NullLogger<ExtensionRuntimeCoordinator>.Instance);

        await coordinator.StartAsync(default);
        Assert.Equal(ProviderOrigin.Extension, registry.GetRequired("fixture-provider").Origin);
        await coordinator.DisableAsync(package.Id, package.Revision);
        Assert.Equal(ProviderOrigin.BuiltIn, registry.GetRequired("fixture-provider").Origin);
    }

    private VerifiedExtensionPackage Package(string version, string suffix)
    {
        var packageRoot = Path.Combine(_root, "extensions", ".staging", suffix);
        Directory.CreateDirectory(packageRoot);
        var manifest = new ExtensionSdkManifest("fixture-provider", "Fixture", version, "1", "index.js",
            [new(ProviderCapabilityKind.Metadata, ["searchTracks", "getTrack"], [ProviderAccountScope.User])],
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

    public Task DisposeAsync()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        return Task.CompletedTask;
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
