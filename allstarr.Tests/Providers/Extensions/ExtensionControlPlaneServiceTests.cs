using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using allstarr.Core.Capabilities;
using allstarr.Core.Extensions;
using allstarr.Core.Downloads;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ExtensionControlPlaneServiceTests : IAsyncLifetime
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
        db.Users.Add(new UserRecord { Id = _reviewer, DisplayName = "Reviewer", Enabled = true, IsAdmin = true, BackendType = "jellyfin", BackendInstanceId = "fixture", BackendPrincipalId = "reviewer", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
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
        Assert.Equal(ExtensionPackageState.Uninstalled, first.State);
        Assert.False(Directory.Exists(first.PackagePath));
        Assert.True(Directory.Exists(second.PackagePath));
        Assert.Equal(2, (await _service.ListPermissionReviewsAsync(first.Id)).Count);
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

    [Theory]
    [InlineData("status {\"installId\":\"install-123\",\"sessionId\":\"session-456\"}")]
    [InlineData("{\"session_secret\": \"session-456\", \"access_token\":\"install-123\"}")]
    [InlineData("Authorization: Bearer install-123 then session_id=session-456")]
    [InlineData("cookie='install-123'; password=\"session-456\"")]
    public void SecretPattern_RedactsQuotedKeysBearerValuesAndSessionIdentifiers(string message)
    {
        var redacted = ExtensionControlPlaneService.SecretPattern().Replace(message, "$1=[redacted]");

        Assert.DoesNotContain("install-123", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("session-456", redacted, StringComparison.Ordinal);
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
    [InlineData(null)]
    [InlineData("COPYING")]
    [InlineData("NOTICE")]
    public async Task ActivationReverifiesRootLegalFilesAndRejectsChangedNotices(string? changedFile)
    {
        var content = Package("3.0.0", "legal");
        var legalFiles = new Dictionary<string, byte[]>
        {
            ["LICENSE"] = Encoding.UTF8.GetBytes("SPDX-License-Identifier: LGPL-3.0-only\nSee COPYING for the license text.\n"),
            ["COPYING"] = Encoding.UTF8.GetBytes("GNU GENERAL PUBLIC LICENSE\r\nVersion 3, 29 June 2007\r\nCopyright © 2007 Free Software Foundation, Inc.\r\n"),
            ["NOTICE"] = Encoding.UTF8.GetBytes("Fixture Extension\nCopyright 2026 Example\n\nSPDX-License-Identifier: LGPL-3.0-only\nSee LICENSE and COPYING for the complete terms.\n\n")
        };
        foreach (var (name, bytes) in legalFiles)
            File.WriteAllBytes(Path.Combine(content.PackageRoot, name), bytes);
        File.WriteAllText(Path.Combine(content.PackageRoot, "icon.png"), "image");
        content = content with
        {
            FileCount = 6,
            ExpandedBytes = Directory.GetFiles(content.PackageRoot).Sum(path => new FileInfo(path).Length),
            ContentSha256 = ExtensionSdkV1.ComputePackageContentSha256(content.PackageRoot)
        };
        var package = await _service.StageAsync(content);
        package = await _service.ReviewAsync(package.Id, _reviewer, package.Revision,
        [
            new("network", "https://api.example.test/", true),
            new("secret", "accountToken", true)
        ]);
        foreach (var (name, bytes) in legalFiles)
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(package.PackagePath, name)));
        Assert.Equal(content.ContentSha256, package.ContentSha256);
        Assert.Equal(content.ContentSha256, ExtensionSdkV1.ComputePackageContentSha256(package.PackagePath));

        if (changedFile != null)
        {
            File.AppendAllText(Path.Combine(package.PackagePath, changedFile), "Changed license notice.\n");
            var error = await Assert.ThrowsAsync<ExtensionSdkValidationException>(() => _service.ActivateAsync(package.Id, package.Revision));
            Assert.Equal("Extension package contents changed after checksum verification; stage the package again.", error.Message);
        }
        else
        {
            package = await _service.ActivateAsync(package.Id, package.Revision);
            Assert.Equal(ExtensionPackageState.Active, package.State);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RuntimeUpdatePreservesActiveVersionWhenRetiredPackageUninstallIsRefused(bool concurrentUninstall)
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
        Assert.Contains(ProviderAccountScope.Shared, descriptor.Capabilities.Single().AllowedAccountScopes);
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
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                while ((await _service.ListPackagesAsync(cancellationToken: timeout.Token)).Single(item => item.Id == previous.Id).State != ExtensionPackageState.Uninstalled)
                    await Task.Delay(50, timeout.Token);
                Assert.False(uninstall.IsCompleted);
                Assert.Equal(ExtensionPackageState.Uninstalled,
                    (await _service.ListPackagesAsync()).Single(item => item.Id == previous.Id).State);
            }
            finally
            {
                registrationGate.Resume.TrySetResult();
                package = await activation.WaitAsync(TimeSpan.FromSeconds(20));
                if (uninstall != null)
                    await Assert.ThrowsAsync<InvalidOperationException>(() => uninstall.WaitAsync(TimeSpan.FromSeconds(20)));
            }
            Assert.Equal(ExtensionPackageState.Uninstalled,
                (await _service.ListPackagesAsync()).Single(item => item.Id == previous.Id).State);
        }
        else
        {
            package = await coordinator.ActivateAsync(package.Id, package.Revision);
            Assert.True(registry.TryGetCapability(package.ExtensionId,
                ProviderCapabilityKind.Metadata, out activeCapability));
            previous = (await _service.ListPackagesAsync()).Single(item => item.Id == previous.Id);
            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.UninstallAsync(previous.Id, previous.Revision));
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

    [Fact]
    public async Task ExistingPersonalAccount_AdoptsOldSessionWithoutChangingItsAudience()
    {
        var package = await ReviewedPackage("5.0.0", "existing-signed", signedSession: true);
        var existing = await AddAccountAsync(package.ExtensionId);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var account = await db.ProviderAccounts.SingleAsync(item => item.Id == existing.Id);
            account.OwnerUserId = _reviewer;
            await db.SaveChangesAsync();
        }
        var protection = new EphemeralDataProtectionProvider();
        var handler = new SignedSessionHandler();
        var clients = new Mock<IHttpClientFactory>();
        clients.Setup(item => item.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler, false));
        var runtime = Path.Combine(_root, "extensions", ".runtime", package.ExtensionId);
        var source = Path.Combine(runtime, "sessions", "local");
        var oldSandbox = new allstarr.Services.Common.ExtensionSandbox(package.PackagePath, package.ManifestJson,
            await File.ReadAllTextAsync(Path.Combine(package.PackagePath, "index.js")), clients.Object, NullLogger.Instance,
            new(new HashSet<string>(["https://api.example.test/"]), new HashSet<string>(), new HashSet<string>()),
            runtime, protection.CreateProtector("allstarr.extensions.signed-session.v1"), source);
        await oldSandbox.CompleteSignedSessionGrantAsync("grant-1");
        var oldPath = Assert.Single(Directory.GetFiles(source, "signed-session-*.protected"));
        var bytes = await File.ReadAllBytesAsync(oldPath);
        var coordinator = Coordinator(handler, protection);
        package = await coordinator.ActivateAsync(package.Id, package.Revision);
        var view = await coordinator.SignedSessionStatusAsync(package.Id);
        Assert.Equal(existing.Id, view.AccountId);
        Assert.Equal(ExtensionSessionStates.SignedIn, view.State);
        Assert.False(File.Exists(oldPath));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Assert.Single(Directory.GetFiles(source,
            "signed-session-*.protected", SearchOption.AllDirectories))));
        await using var check = await _factory.CreateDbContextAsync();
        var adopted = await check.ProviderAccounts.SingleAsync();
        Assert.Equal(_reviewer, adopted.OwnerUserId);
        using var settings = JsonDocument.Parse(adopted.SettingsJson);
        Assert.Equal("lossless", settings.RootElement.GetProperty("quality").GetString());
        Assert.Equal("local", settings.RootElement.GetProperty("__extensionSessionSource").GetString());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ArchivedConnection_CreatesAndBindsANewSharedDefault(bool legacy, bool startup)
    {
        var package = await ReviewedPackage("5.0.0", "archived", signedSession: true);
        var archived = await AddAccountAsync(package.ExtensionId);
        var settings = legacy
            ? """{"__removed":true,"__extensionSessionRemoved":true}"""
            : """{"__removed":true,"__extensionSessionRemoved":true,"__extensionSessionSource":"local"}""";
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var account = await db.ProviderAccounts.SingleAsync(item => item.Id == archived.Id);
            account.Enabled = false;
            account.OwnerUserId = _reviewer;
            account.SettingsJson = settings;
            await db.SaveChangesAsync();
        }
        var coordinator = Coordinator(new SignedSessionHandler());
        if (startup)
        {
            package = await _service.ActivateAsync(package.Id, package.Revision);
            await coordinator.StartAsync(default);
        }
        else
        {
            package = await _service.ActivateAsync(package.Id, package.Revision);
            await coordinator.DisableAsync(package.Id, package.Revision);
            package = (await _service.ListPackagesAsync()).Single(item => item.Id == package.Id);
            await coordinator.UninstallAsync(package.Id, package.Revision);
            package = await ReviewedPackage("5.1.0", "reinstalled", signedSession: true);
            package = await coordinator.ActivateAsync(package.Id, package.Revision);
        }

        var signedIn = await coordinator.CompleteSignedSessionGrantAsync(package.Id, package.Revision, "grant-1");

        await using var check = await _factory.CreateDbContextAsync();
        var fresh = Assert.Single(await check.ProviderAccounts.ToListAsync(), item => item.Id != archived.Id);
        Assert.True(fresh.Enabled);
        Assert.Equal(ProviderAccountScope.Shared, fresh.Scope);
        Assert.Null(fresh.SecretReferenceId);
        Assert.Equal(1, fresh.Revision);
        using var freshSettings = JsonDocument.Parse(fresh.SettingsJson);
        Assert.Equal("local", freshSettings.RootElement.GetProperty("__extensionSessionSource").GetString());
        Assert.False(freshSettings.RootElement.TryGetProperty("__removed", out _));
        Assert.Equal(fresh.Id, signedIn.AccountId);
        Assert.Equal(ExtensionSessionStates.SignedIn, signedIn.State);
        Assert.Equal(fresh.Id, (await coordinator.SignedSessionStatusAsync(package.Id)).AccountId);
        var retained = await check.ProviderAccounts.SingleAsync(item => item.Id == archived.Id);
        Assert.False(retained.Enabled);
        Assert.Equal(_reviewer, retained.OwnerUserId);
        Assert.Equal(settings, retained.SettingsJson);
        Assert.Equal(archived.Revision, retained.Revision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovedExplicitConnection_IsRejectedByEverySessionAction(bool enabled)
    {
        var package = await ReviewedPackage("5.0.0", "removed-explicit", signedSession: true);
        var handler = new SignedSessionHandler();
        var coordinator = Coordinator(handler);
        package = await coordinator.ActivateAsync(package.Id, package.Revision);
        Guid accountId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var account = await db.ProviderAccounts.SingleAsync();
            accountId = account.Id;
            account.Enabled = enabled;
            account.SettingsJson = """{"__removed":true,"__extensionSessionSource":"local"}""";
            await db.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<KeyNotFoundException>(() => coordinator.SignedSessionStatusAsync(package.Id, accountId: accountId));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => coordinator.StartSignedSessionVerificationAsync(package.Id, package.Revision, accountId: accountId));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => coordinator.CompleteSignedSessionGrantAsync(package.Id, package.Revision, "grant-1", accountId: accountId));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => coordinator.ClearSignedSessionAsync(package.Id, package.Revision, accountId: accountId));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task SignedSessionAccount_AudienceRoundTripKeepsProtectedStateAndOtherAccountsStaySeparate()
    {
        var package = await ReviewedPackage("5.0.0", "signed-account", signedSession: true);
        var protection = new EphemeralDataProtectionProvider();
        var coordinator = Coordinator(new SignedSessionHandler(), protection);
        package = await coordinator.ActivateAsync(package.Id, package.Revision);
        var signedIn = await coordinator.CompleteSignedSessionGrantAsync(package.Id, package.Revision, "grant-1");
        var id = Assert.IsType<Guid>(signedIn.AccountId);
        await using var db = await _factory.CreateDbContextAsync();
        var account = await db.ProviderAccounts.SingleAsync(item => item.Id == id);
        Assert.Null(account.OwnerUserId);
        Assert.Null(account.SecretReferenceId);
        Assert.Equal("local", JsonDocument.Parse(account.SettingsJson).RootElement.GetProperty("__extensionSessionSource").GetString());
        var statePath = Assert.Single(Directory.GetFiles(Path.Combine(_root, "extensions", ".runtime", package.ExtensionId),
            "signed-session-*.protected", SearchOption.AllDirectories));
        var protectedState = await File.ReadAllBytesAsync(statePath);
        account.OwnerUserId = _reviewer;
        account.Revision++;
        await db.SaveChangesAsync();
        Assert.Equal(id, (await coordinator.SignedSessionStatusAsync(package.Id)).AccountId);
        Assert.Equal(ExtensionSessionStates.SignedIn, (await coordinator.SignedSessionStatusAsync(package.Id)).State);
        account.OwnerUserId = null;
        account.Revision++;
        await db.SaveChangesAsync();
        Assert.Equal(protectedState, await File.ReadAllBytesAsync(statePath));
        var separate = await AddAccountAsync(package.ExtensionId);
        var second = await coordinator.SignedSessionStatusAsync(package.Id, accountId: separate.Id);
        Assert.Equal(separate.Id, second.AccountId);
        Assert.Equal(ExtensionSessionStates.SignedOut, second.State);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => coordinator.SignedSessionStatusAsync(package.Id, accountId: Guid.NewGuid()));
        var descriptorRegistry = new ProviderRegistry([]);
        var options = new ProviderDownloadWorkspaceOptions { RootPath = Path.Combine(_root, "other-downloads") };
        var restored = new ExtensionRuntimeCoordinator(_factory, _service, descriptorRegistry, descriptorRegistry,
            Mock.Of<IHttpClientFactory>(), Mock.Of<allstarr.Core.Providers.Spotify.IProviderAccountSecretAccessor>(),
            new ProviderDownloadArtifactResolver(Mock.Of<IProviderDownloadArtifactStore>(), options), options,
            _configuration, NullLogger<ExtensionRuntimeCoordinator>.Instance, protection);
        await restored.StartAsync(default);
        var restoredView = await restored.SignedSessionStatusAsync(package.Id);
        Assert.Equal(id, restoredView.AccountId);
        Assert.Equal(ExtensionSessionStates.SignedIn, restoredView.State);
        Assert.Equal(protectedState, await File.ReadAllBytesAsync(statePath));
        Assert.True(descriptorRegistry.TryGet(package.ExtensionId, out var descriptor));
        Assert.All(descriptor!.Capabilities, capability =>
        {
            Assert.Equal(ProviderAccountRequirement.Required, capability.AccountRequirement);
            Assert.Contains(ProviderAccountScope.Shared, capability.AllowedAccountScopes);
            Assert.Contains(ProviderAccountScope.Personal, capability.AllowedAccountScopes);
        });
    }

    [Fact]
    public async Task RestartWithoutProtectionKeysLogsStorageFailureAndRetainsSessionFile()
    {
        var package = await ReviewedPackage("5.0.0", "missing-protection", signedSession: true);
        var coordinator = Coordinator(new SignedSessionHandler());
        package = await coordinator.ActivateAsync(package.Id, package.Revision);
        await coordinator.CompleteSignedSessionGrantAsync(package.Id, package.Revision, "grant-1");
        var statePath = Assert.Single(Directory.GetFiles(Path.Combine(_root, "extensions", ".runtime", package.ExtensionId),
            "signed-session-*.protected", SearchOption.AllDirectories));
        var before = await File.ReadAllBytesAsync(statePath);
        var restarted = Coordinator(new SignedSessionHandler());
        await restarted.StartAsync(default);

        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.SignedSessionStatusAsync(package.Id));

        Assert.Equal(before, await File.ReadAllBytesAsync(statePath));
        await using var db = await _factory.CreateDbContextAsync();
        var log = await db.ExtensionLogs.SingleAsync(item => item.ExtensionPackageId == package.Id &&
            item.EventCode == "session.load-failed");
        Assert.Contains("reason=protection_failed", log.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("session-1", log.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-1", log.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SignedSessionActions_RequireTheCurrentActiveRevisionAndAreRecorded()
    {
        var package = await _service.StageAsync(Package("5.0.0", "signed", signedSession: true));
        package = await _service.ReviewAsync(package.Id, _reviewer, package.Revision,
        [
            new("network", "https://api.example.test/", true),
            new("secret", "accountToken", true)
        ]);
        var registry = new ProviderRegistry([]);
        var clients = new Mock<IHttpClientFactory>();
        clients.Setup(item => item.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(new SignedSessionHandler(), disposeHandler: false));
        var coordinator = new ExtensionRuntimeCoordinator(_factory, _service, new PausedProviderRegistration(registry),
            registry, clients.Object, Mock.Of<allstarr.Core.Providers.Spotify.IProviderAccountSecretAccessor>(),
            new ProviderDownloadArtifactResolver(Mock.Of<IProviderDownloadArtifactStore>(),
                new ProviderDownloadWorkspaceOptions { RootPath = Path.Combine(_root, "download-workspaces") }),
            new ProviderDownloadWorkspaceOptions { RootPath = Path.Combine(_root, "download-workspaces") },
            _configuration, NullLogger<ExtensionRuntimeCoordinator>.Instance);
        var runtimeState = Path.Combine(_root, "extensions", ".runtime", package.ExtensionId);
        package = await coordinator.ActivateAsync(package.Id, package.Revision);

        Assert.Equal(ExtensionSessionStates.SignedOut, (await coordinator.SignedSessionStatusAsync(package.Id)).State);
        Assert.Single(Directory.GetFiles(Path.Combine(runtimeState, "sessions", "local"), "signed-session-*.protected", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(runtimeState, "signed-session-*.protected"));
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
            coordinator.StartSignedSessionVerificationAsync(package.Id, package.Revision - 1));
        var pending = await coordinator.StartSignedSessionVerificationAsync(package.Id, package.Revision);
        Assert.Equal(ExtensionSessionStates.VerificationPending, pending.State);
        var signedIn = await coordinator.CompleteSignedSessionGrantAsync(package.Id, package.Revision, "grant-1");
        Assert.Equal(ExtensionSessionStates.SignedIn, signedIn.State);
        var failed = await coordinator.CompleteSignedSessionGrantAsync(
            package.Id, package.Revision, "spotiflac://session-grant/?state=other&grant=grant-2");
        Assert.Equal("grant_wrong_extension", failed.ReasonCode);
        Assert.Equal(ExtensionSessionStates.SignedIn, failed.State);
        Assert.Equal(ExtensionSessionStates.SignedOut,
            (await coordinator.ClearSignedSessionAsync(package.Id, package.Revision)).State);

        await coordinator.DisableAsync(package.Id, package.Revision);
        package = (await _service.ListPackagesAsync()).Single(item => item.Id == package.Id);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
            coordinator.StartSignedSessionVerificationAsync(package.Id, package.Revision));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => coordinator.SignedSessionStatusAsync(package.Id));
        package = await coordinator.UninstallAsync(package.Id, package.Revision);
        Assert.False(Directory.Exists(runtimeState));

        await using var db = await _factory.CreateDbContextAsync();
        var events = await db.ExtensionLogs.AsNoTracking()
            .Where(item => item.ExtensionPackageId == package.Id && item.EventCode!.StartsWith("session."))
            .OrderBy(item => item.CreatedAt).ThenBy(item => item.Id)
            .Select(item => new { item.EventCode, item.Message })
            .ToListAsync();
        Assert.Equal(["session.started", "session.granted", "session.failed", "session.reset", "session.cleared"],
            events.Select(item => item.EventCode));
        var reset = Assert.Single(events, item => item.EventCode == "session.reset");
        Assert.Contains("call=user_clear", reset.Message, StringComparison.Ordinal);
        Assert.Contains("reason=user_requested", reset.Message, StringComparison.Ordinal);
        Assert.Contains("time_left=true", reset.Message, StringComparison.Ordinal);
        Assert.All(events, item =>
        {
            Assert.DoesNotContain("session-1", item.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("challenge-1", item.Message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task RemovedConnection_IsExcludedFromSessionRenewal()
    {
        var package = await _service.StageAsync(Package("6.0.0", "removed-renewal", signedSession: true, refreshSession: true));
        package = await _service.ReviewAsync(package.Id, _reviewer, package.Revision,
            [new("network", "https://api.example.test/", true), new("secret", "accountToken", true)]);
        var handler = new SignedSessionHandler { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30).ToString("O") };
        var coordinator = Coordinator(handler);
        package = await coordinator.ActivateAsync(package.Id, package.Revision);
        await coordinator.CompleteSignedSessionGrantAsync(package.Id, package.Revision, "grant-1");
        await using (var db = await _factory.CreateDbContextAsync())
        {
            (await db.ProviderAccounts.SingleAsync()).SettingsJson = """{"__removed":true,"__extensionSessionSource":"local"}""";
            await db.SaveChangesAsync();
        }
        var calls = handler.Calls;

        await coordinator.RefreshSignedSessionsAsync();

        Assert.Equal(calls, handler.Calls);
        Assert.Equal(0, handler.RefreshCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SessionRenewal_LogsFailuresAndSkipsRemovedRuntime(bool uninstall)
    {
        var package = await _service.StageAsync(Package("6.0.0", "renewal", signedSession: true, refreshSession: true));
        package = await _service.ReviewAsync(package.Id, _reviewer, package.Revision,
            [new("network", "https://api.example.test/", true), new("secret", "accountToken", true)]);
        var registry = new ProviderRegistry([]);
        var handler = new SignedSessionHandler
        {
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30).ToString("O"),
            RejectRefresh = true
        };
        var clients = new Mock<IHttpClientFactory>();
        clients.Setup(item => item.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler, false));
        var options = new ProviderDownloadWorkspaceOptions { RootPath = Path.Combine(_root, "downloads") };
        var coordinator = new ExtensionRuntimeCoordinator(_factory, _service, registry, registry, clients.Object,
            Mock.Of<allstarr.Core.Providers.Spotify.IProviderAccountSecretAccessor>(),
            new ProviderDownloadArtifactResolver(Mock.Of<IProviderDownloadArtifactStore>(), options), options,
            _configuration, NullLogger<ExtensionRuntimeCoordinator>.Instance);
        package = await coordinator.ActivateAsync(package.Id, package.Revision);
        await coordinator.RefreshSignedSessionsAsync(); // Signed out: never verify.
        Assert.Equal(0, handler.Calls);
        await coordinator.CompleteSignedSessionGrantAsync(package.Id, package.Revision, "grant-1");
        await coordinator.RefreshSignedSessionsAsync();
        Assert.Equal(ExtensionSessionStates.SignedIn, (await coordinator.SignedSessionStatusAsync(package.Id)).State);
        handler.RejectRefresh = false;
        await coordinator.RefreshSignedSessionsAsync();
        Assert.Equal(2, handler.RefreshCalls);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var events = await db.ExtensionLogs.Where(item => item.ExtensionPackageId == package.Id &&
                (item.EventCode == "session.renewal-failed" || item.EventCode == "session.renewed")).ToListAsync();
            Assert.Equal(2, events.Count);
            Assert.Contains(events, item => item.EventCode == "session.renewal-failed" && item.Level == "warning");
            Assert.Contains(events, item => item.EventCode == "session.renewed");
            Assert.All(events, item => Assert.DoesNotContain("secret-1", item.Message, StringComparison.Ordinal));
        }
        // Put it back in the due window before removing it from the runtime.
        await coordinator.CompleteSignedSessionGrantAsync(package.Id, package.Revision, "grant-1");
        await coordinator.DisableAsync(package.Id, package.Revision);
        package = (await _service.ListPackagesAsync()).Single(item => item.Id == package.Id);
        if (uninstall) await coordinator.UninstallAsync(package.Id, package.Revision);
        var calls = handler.Calls;
        await coordinator.RefreshSignedSessionsAsync();
        Assert.Equal(calls, handler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpdateRetiresFilesButPreservesSignInAccountsAndPermissionHistory(bool disabled)
    {
        var handler = new SignedSessionHandler();
        var coordinator = Coordinator(handler);
        var first = await ReviewedPackage("1.0.0", "first", signedSession: true);
        first = await coordinator.ActivateAsync(first.Id, first.Revision);
        var signedIn = await coordinator.CompleteSignedSessionGrantAsync(first.Id, first.Revision, "grant-1");
        var runtimeState = Path.Combine(_root, "extensions", ".runtime", first.ExtensionId);
        var sessionPath = Assert.Single(Directory.GetFiles(runtimeState, "signed-session-*.protected", SearchOption.AllDirectories));
        var protectedSession = File.ReadAllBytes(sessionPath);
        var account = await AddAccountAsync(first.ExtensionId);
        var firstReviews = await _service.ListPermissionReviewsAsync(first.Id);
        if (disabled) await coordinator.DisableAsync(first.Id, first.Revision);

        var second = await ReviewedPackage("2.0.0", "second", signedSession: true);
        Assert.Equal(first.Id, second.PreviousPackageId);
        second = await coordinator.ActivateAsync(second.Id, second.Revision);

        var packages = await _service.ListPackagesAsync();
        Assert.Equal(second.Id, Assert.Single(packages, item => item.State is
            ExtensionPackageState.Active or ExtensionPackageState.Disabled).Id);
        Assert.Equal(ExtensionPackageState.Uninstalled, packages.Single(item => item.Id == first.Id).State);
        Assert.False(Directory.Exists(first.PackagePath));
        Assert.True(Directory.Exists(second.PackagePath));
        Assert.Equal(protectedSession, File.ReadAllBytes(sessionPath));
        var retainedSession = await coordinator.SignedSessionStatusAsync(second.Id);
        Assert.Equal(ExtensionSessionStates.SignedIn, retainedSession.State);
        Assert.Equal(signedIn.ExpiresAt, retainedSession.ExpiresAt);
        Assert.Equal(1, handler.Calls); // The update must not start verification or exchange another grant.
        Assert.Equal(firstReviews.Select(item => item.Id),
            (await _service.ListPermissionReviewsAsync(first.Id)).Select(item => item.Id));

        // A subsequent review can still compare both historical versions, without their files.
        var third = await _service.StageAsync(Package("3.0.0", "third", signedSession: true));
        Assert.Equal(second.Id, third.PreviousPackageId);
        Assert.All(await _service.ListPermissionReviewsAsync(third.Id),
            review => Assert.Equal(ExtensionPermissionDecision.Pending, review.Decision));
        Assert.All(await _service.ListPermissionReviewsAsync(second.PreviousPackageId!.Value),
            review => Assert.Equal(ExtensionPermissionDecision.Approved, review.Decision));
        Assert.All(await _service.ListPermissionReviewsAsync(third.PreviousPackageId!.Value),
            review => Assert.Equal(ExtensionPermissionDecision.Approved, review.Decision));
        await _service.AbandonStagingAsync(third.Id, third.Revision);
        await coordinator.DisableAsync(second.Id, second.Revision);
        second = (await _service.ListPackagesAsync()).Single(item => item.Id == second.Id);
        await coordinator.UninstallAsync(second.Id, second.Revision);
        Assert.False(Directory.Exists(runtimeState));
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(2, await db.ProviderAccounts.CountAsync());
        Assert.Equal(account.SettingsJson, (await db.ProviderAccounts.SingleAsync(item => item.Id == account.Id)).SettingsJson);
        Assert.True(await db.ProviderAccounts.AnyAsync(item => item.Id == signedIn.AccountId));
        Assert.Contains(await _service.ListLogsAsync(first.Id), item => item.EventCode == "session.granted");
        Assert.Contains(await _service.ListLogsAsync(first.Id), item => item.EventCode == "package.superseded");
    }

    [Theory]
    [InlineData(ExtensionPackageState.Active)]
    [InlineData(ExtensionPackageState.Disabled)]
    public async Task StartupReconcilesLegacyVersionsOnceAndPreservesProtectedState(ExtensionPackageState retainedState)
    {
        var handler = new SignedSessionHandler();
        var coordinator = Coordinator(handler);
        var first = await ReviewedPackage("1.0.0", "legacy", signedSession: true);
        first = await coordinator.ActivateAsync(first.Id, first.Revision);
        await coordinator.CompleteSignedSessionGrantAsync(first.Id, first.Revision, "grant-1");
        var sessionPath = Assert.Single(Directory.GetFiles(Path.Combine(_root, "extensions", ".runtime"),
            "signed-session-*.protected", SearchOption.AllDirectories));
        var protectedSession = File.ReadAllBytes(sessionPath);
        var account = await AddAccountAsync(first.ExtensionId);
        var second = await ReviewedPackage("1.1.0", "retained", signedSession: true);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            (await db.ExtensionPackages.SingleAsync(item => item.Id == first.Id)).State = ExtensionPackageState.RolledBack;
            (await db.ExtensionPackages.SingleAsync(item => item.Id == second.Id)).State = retainedState;
            await db.SaveChangesAsync();
        }

        await coordinator.StartAsync(default);
        var retired = (await _service.ListPackagesAsync()).Single(item => item.Id == first.Id);
        Assert.Equal(ExtensionPackageState.Uninstalled, retired.State);
        Assert.False(Directory.Exists(first.PackagePath));
        Assert.True(Directory.Exists(second.PackagePath));
        Assert.Equal(protectedSession, File.ReadAllBytes(sessionPath));
        Assert.Equal(1, handler.Calls);
        var logs = await _service.ListLogsAsync(first.Id);
        Assert.Single(logs, item => item.EventCode == "package.superseded");
        Assert.Single(logs, item => item.EventCode == "package.contents-removed");
        Assert.Contains(logs, item => item.EventCode == "session.granted");
        Assert.All(await _service.ListPermissionReviewsAsync(first.Id),
            item => Assert.Equal(ExtensionPermissionDecision.Approved, item.Decision));
        await coordinator.StartAsync(default);
        Assert.Equal(retired.Revision, (await _service.ListPackagesAsync()).Single(item => item.Id == first.Id).Revision);
        Assert.Equal(logs.Count, (await _service.ListLogsAsync(first.Id)).Count);
        await using var check = await _factory.CreateDbContextAsync();
        Assert.Equal(account.SettingsJson, (await check.ProviderAccounts.SingleAsync(item => item.Id == account.Id)).SettingsJson);
    }

    [Theory]
    [InlineData(null, ExtensionPackageState.Active, false)]
    [InlineData("0.9.0", ExtensionPackageState.Active, false)]
    [InlineData("1.0.0", ExtensionPackageState.Disabled, false)]
    [InlineData("2.0.0", ExtensionPackageState.Staged, false)]
    [InlineData("2.0.0", ExtensionPackageState.Failed, false)]
    [InlineData("1.0.0-beta", ExtensionPackageState.Active, false)]
    [InlineData("1.0.1", ExtensionPackageState.Disabled, true)]
    public async Task ReconciliationRequiresAnActiveOrDisabledNewerVersion(
        string? replacementVersion, ExtensionPackageState replacementState, bool removed)
    {
        var old = await ReviewedPackage("1.0.0", "old");
        ExtensionPackageRecord? replacement = replacementVersion == null
            ? null : await ReviewedPackage(replacementVersion, "candidate");
        await using (var db = await _factory.CreateDbContextAsync())
        {
            (await db.ExtensionPackages.SingleAsync(item => item.Id == old.Id)).State = ExtensionPackageState.RolledBack;
            if (replacement != null)
                (await db.ExtensionPackages.SingleAsync(item => item.Id == replacement.Id)).State = replacementState;
            await db.SaveChangesAsync();
        }
        await _service.ReconcileSupersededPackagesAsync(default);
        Assert.Equal(removed ? ExtensionPackageState.Uninstalled : ExtensionPackageState.RolledBack,
            (await _service.ListPackagesAsync()).Single(item => item.Id == old.Id).State);
        Assert.Equal(!removed, Directory.Exists(old.PackagePath));
        if (replacement != null) Assert.True(Directory.Exists(replacement.PackagePath));
    }

    [Theory]
    [InlineData("runtime")]
    [InlineData("active")]
    [InlineData("linked")]
    public async Task InterruptedCleanupRetriesWithoutDeletingRetainedContent(string protectedPath)
    {
        var old = await ReviewedPackage("1.0.0", "cleanup-old");
        var current = await ReviewedPackage("2.0.0", "cleanup-new");
        var runtimeState = Path.Combine(_root, "extensions", ".runtime", old.ExtensionId);
        Directory.CreateDirectory(runtimeState);
        var marker = Path.Combine(runtimeState, "protected-state");
        File.WriteAllText(marker, "fixture");
        var cleanupPath = protectedPath == "active" ? current.PackagePath : runtimeState;
        if (protectedPath == "linked")
        {
            var link = Path.Combine(_root, "extensions", ".staging", "linked-runtime");
            Directory.CreateSymbolicLink(link, Path.GetDirectoryName(runtimeState)!);
            cleanupPath = Path.Combine(link, old.ExtensionId);
        }
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var historical = await db.ExtensionPackages.SingleAsync(item => item.Id == old.Id);
            historical.State = ExtensionPackageState.RolledBack;
            historical.PackagePath = cleanupPath;
            (await db.ExtensionPackages.SingleAsync(item => item.Id == current.Id)).State = ExtensionPackageState.Active;
            await db.SaveChangesAsync();
        }
        await _service.ReconcileSupersededPackagesAsync(default);
        Assert.Equal("fixture", File.ReadAllText(marker));
        Assert.True(File.Exists(Path.Combine(current.PackagePath, "index.js")));
        Assert.Contains(await _service.ListLogsAsync(old.Id), item => item.EventCode == "package.cleanup-pending");
        await using (var db = await _factory.CreateDbContextAsync())
        {
            (await db.ExtensionPackages.SingleAsync(item => item.Id == old.Id)).PackagePath = old.PackagePath;
            await db.SaveChangesAsync();
        }
        await _service.ReconcileSupersededPackagesAsync(default);
        Assert.False(Directory.Exists(old.PackagePath));
        Assert.True(File.Exists(marker));
        Assert.Single((await _service.ListLogsAsync(old.Id)), item => item.EventCode == "package.superseded");
    }

    [Theory]
    [InlineData("1.0.0-beta.9", "1.0.0-beta.10", true)]
    [InlineData("1.0.0-beta.10", "1.0.0-beta.9", false)]
    [InlineData("1.0.0-rc.1", "1.0.0", true)]
    [InlineData("1.0.0--1", "1.0.0-0", false)]
    public async Task ReconciliationHonorsPrereleaseVersionOrder(string previous, string current, bool removed)
    {
        var old = await ReviewedPackage(previous, "old-prerelease");
        var retained = await ReviewedPackage(current, "current-prerelease");
        await using (var db = await _factory.CreateDbContextAsync())
        {
            (await db.ExtensionPackages.SingleAsync(item => item.Id == old.Id)).State = ExtensionPackageState.RolledBack;
            (await db.ExtensionPackages.SingleAsync(item => item.Id == retained.Id)).State = ExtensionPackageState.Active;
            await db.SaveChangesAsync();
        }
        await _service.ReconcileSupersededPackagesAsync(default);
        Assert.Equal(!removed, Directory.Exists(old.PackagePath));
        Assert.True(Directory.Exists(retained.PackagePath));
    }

    private async Task<ExtensionPackageRecord> ReviewedPackage(string version, string suffix, bool signedSession = false)
    {
        var package = await _service.StageAsync(Package(version, suffix, signedSession));
        return await _service.ReviewAsync(package.Id, _reviewer, package.Revision,
            [new("network", "https://api.example.test/", true), new("secret", "accountToken", true)]);
    }

    private ExtensionRuntimeCoordinator Coordinator(SignedSessionHandler handler, IDataProtectionProvider? protection = null)
    {
        var registry = new ProviderRegistry([]);
        var clients = new Mock<IHttpClientFactory>();
        clients.Setup(item => item.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler, false));
        var options = new ProviderDownloadWorkspaceOptions { RootPath = Path.Combine(_root, "downloads") };
        return new ExtensionRuntimeCoordinator(_factory, _service, registry, registry, clients.Object,
            Mock.Of<allstarr.Core.Providers.Spotify.IProviderAccountSecretAccessor>(),
            new ProviderDownloadArtifactResolver(Mock.Of<IProviderDownloadArtifactStore>(), options), options,
            _configuration, NullLogger<ExtensionRuntimeCoordinator>.Instance, protection);
    }

    private async Task<ProviderAccountRecord> AddAccountAsync(string provider)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var account = new ProviderAccountRecord
        {
            Id = Guid.CreateVersion7(),
            ProviderId = provider,
            DisplayName = "Fixture",
            Enabled = true,
            SettingsJson = "{\"quality\":\"lossless\"}",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.ProviderAccounts.Add(account);
        await db.SaveChangesAsync();
        return account;
    }

    private sealed class SignedSessionHandler : HttpMessageHandler
    {
        public string ExpiresAt { get; init; } = "2099-01-01T00:00:00Z";
        public bool RejectRefresh { get; set; }
        public int RefreshCalls { get; private set; }
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var refreshing = request.RequestUri!.AbsolutePath == "/v1/refresh";
            if (refreshing) RefreshCalls++;
            var body = request.RequestUri.AbsolutePath.EndsWith("/bootstrap", StringComparison.Ordinal)
                ? "{\"challenge_id\":\"challenge-1\"}"
                : JsonSerializer.Serialize(new
                {
                    session_id = "session-1",
                    session_secret = "secret-1",
                    expires_at = refreshing ? DateTimeOffset.UtcNow.AddHours(6).ToString("O") : ExpiresAt
                });
            return Task.FromResult(new HttpResponseMessage(refreshing && RejectRefresh
                ? System.Net.HttpStatusCode.ServiceUnavailable : System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
                RequestMessage = request
            });
        }
    }

    private VerifiedExtensionPackage Package(string version, string suffix, bool signedSession = false, bool refreshSession = false)
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
            permissions = new[] { new { kind = "network", value = "https://api.example.test/", required = true }, new { kind = "secret", value = "accountToken", required = true } },
            requiredRuntimeFeatures = signedSession ? new[] { "signedSession@1", "sessionGrant@1" } : null,
            signedSession = signedSession
                ? new { @namespace = "fixture-v1", baseUrl = "https://api.example.test/v1", appVersion = "fixture@1.0.0", endpoints = new { refresh = refreshSession ? "/refresh" : "" } }
                : null
        }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }));
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
