using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using allstarr.Controllers;
using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Extensions;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using allstarr.Services.Admin;
using allstarr.Services.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace allstarr.Tests;

public sealed class ExtensionRetryTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "extension-retry", Guid.NewGuid().ToString("N"));
    private readonly Guid _reviewer = Guid.CreateVersion7();
    private SqliteTestDatabase _database = null!;
    private Factory _factory = null!;
    private IConfiguration _configuration = null!;
    private ExtensionControlPlaneService _service = null!;
    private ExtensionManager _manager = null!;
    private readonly PackageHandler _handler = new();
    private IHttpClientFactory _clients = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _database = await SqliteTestDatabase.CreateAsync();
        _factory = new Factory(_database.Options);
        _configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Extensions:Directory"] = Path.Combine(_root, "extensions") }).Build();
        _service = new ExtensionControlPlaneService(_factory, new SystemPlatformClock(), _configuration);
        var clients = new Mock<IHttpClientFactory>();
        clients.Setup(item => item.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(_handler, false));
        _clients = clients.Object;
        _manager = new ExtensionManager(_clients, NullLogger<ExtensionManager>.Instance, _configuration, _service);
        await using var db = _factory.CreateDbContext();
        db.Users.Add(new UserRecord
        {
            Id = _reviewer,
            Enabled = true,
            IsAdmin = true,
            DisplayName = "Reviewer",
            BackendType = "jellyfin",
            BackendInstanceId = "fixture",
            BackendPrincipalId = "reviewer"
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task RetryAfterRuntimeFeatureFailure_RefetchesSameVersionAndPreservesGrantsAndSession()
    {
        var package = await FailedPackageAsync();
        var session = Path.Combine(_root, "extensions", ".runtime", package.ExtensionId,
            "sessions", package.RegistryId!.Value.ToString("N"), "session.protected");
        Directory.CreateDirectory(Path.GetDirectoryName(session)!);
        await File.WriteAllTextAsync(session, "opaque-session-fixture");
        var originalReviews = await _service.ListPermissionReviewsAsync(package.Id);

        var retried = await _manager.RetryExtensionAsync(package.Id, package.Revision, true);

        Assert.Equal(package.Id, retried.Id);
        Assert.Equal(package.Version, retried.Version);
        Assert.Equal(package.Sha256, retried.Sha256);
        Assert.Equal(package.Revision + 1, retried.Revision);
        Assert.Equal(ExtensionPackageState.Staged, retried.State);
        Assert.Null(retried.FailureCode);
        Assert.True(File.Exists(Path.Combine(retried.PackagePath, "index.js")));
        Assert.Equal(2, _handler.Requests);
        var reviews = await _service.ListPermissionReviewsAsync(package.Id);
        Assert.Equal(originalReviews.Select(r => (r.Id, r.Decision, r.Revision)), reviews.Select(r => (r.Id, r.Decision, r.Revision)));
        Assert.Equal("opaque-session-fixture", await File.ReadAllTextAsync(session));
        Assert.Contains(await _service.ListLogsAsync(package.Id), item => item.EventCode == "package.retry-staged");
        var registry = new ProviderRegistry([]);
        var workspace = new ProviderDownloadWorkspaceOptions { RootPath = Path.Combine(_root, "downloads") };
        var runtime = new ExtensionRuntimeCoordinator(_factory, _service, registry, registry, _clients,
            Mock.Of<allstarr.Core.Providers.Spotify.IProviderAccountSecretAccessor>(),
            new ProviderDownloadArtifactResolver(Mock.Of<IProviderDownloadArtifactStore>(), workspace), workspace,
            _configuration, NullLogger<ExtensionRuntimeCoordinator>.Instance);
        Assert.Equal(ExtensionPackageState.Active, (await runtime.ActivateAsync(retried.Id, retried.Revision)).State);
        Assert.True(registry.TryGet(package.ExtensionId, out _));
    }

    [Fact]
    public async Task RetryAfterRequiredPermissionDenial_ReturnsToReviewWithoutApprovingAnything()
    {
        var package = await FailedPackageAsync(denyRequired: true);
        var retried = await _manager.RetryExtensionAsync(package.Id, package.Revision, true);
        Assert.Equal(ExtensionPackageState.ReviewRequired, retried.State);
        var reviews = await _service.ListPermissionReviewsAsync(package.Id);
        Assert.Equal(ExtensionPermissionDecision.Denied, Assert.Single(reviews, r => r.PermissionKind == "cache").Decision);
        Assert.Equal(ExtensionPermissionDecision.Approved, Assert.Single(reviews, r => r.PermissionKind == "network").Decision);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.ActivateAsync(retried.Id, retried.Revision));
    }

    [Fact]
    public async Task Retry_StillUnsupportedFeatureRemainsFailedWithoutFetching()
    {
        var package = await FailedPackageAsync(feature: "futureFeature@999");
        await Assert.ThrowsAsync<ExtensionSdkValidationException>(() => _manager.RetryExtensionAsync(package.Id, package.Revision, true));
        Assert.Equal(0, _handler.Requests);
        Assert.Equal(ExtensionPackageState.Failed, (await _service.ListPackagesAsync()).Single().State);
    }

    [Fact]
    public async Task Retry_RevisionConflictAndNonAdministratorsAreRefused()
    {
        var package = await FailedPackageAsync();
        Assert.IsType<UnauthorizedObjectResult>(await Controller(null).Retry(package.Id, new() { ExpectedRevision = package.Revision }, default));
        var refused = Assert.IsType<ObjectResult>(await Controller(false).Retry(package.Id, new() { ExpectedRevision = package.Revision }, default));
        Assert.Equal(403, refused.StatusCode);
        Assert.IsType<ConflictObjectResult>(await Controller(true).Retry(package.Id, new() { ExpectedRevision = package.Revision - 1 }, default));
        Assert.Equal(0, _handler.Requests);
    }

    [Fact]
    public async Task Retry_MissingLocalContentRequiresAnotherUpload()
    {
        var package = await FailedPackageAsync(local: true);
        var result = Assert.IsType<BadRequestObjectResult>(await Controller(true).Retry(package.Id, new() { ExpectedRevision = package.Revision }, default));
        Assert.Contains("Upload the package again", JsonSerializer.Serialize(result.Value), StringComparison.Ordinal);
        Assert.Equal(0, _handler.Requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Retry_RejectsChangedRegistryHashOrArchive(bool advertisedHashChanged)
    {
        var package = await FailedPackageAsync();
        if (advertisedHashChanged) _handler.Hash = new string('a', 64);
        else _handler.Archive = [1, 2, 3];
        await Assert.ThrowsAsync<ExtensionSdkValidationException>(() => _manager.RetryExtensionAsync(package.Id, package.Revision, true));
        Assert.Equal(ExtensionPackageState.Failed, (await _service.ListPackagesAsync()).Single().State);
        Assert.Empty(Directory.GetDirectories(Path.Combine(_root, "extensions"), ".install-*"));
    }

    [Fact]
    public async Task UploadingSameLocalPackageAgain_RestoresFailedRecordAndRequiresReview()
    {
        var package = await FailedPackageAsync(denyRequired: true, local: true);
        var verified = ExtensionSdkV1.VerifyArchive(Path.Combine(_root, "package.zip"), package.Sha256,
            Path.Combine(_root, "extensions", "uploaded-again"));
        var restored = await _service.StageAsync(verified);
        Assert.Equal(package.Id, restored.Id);
        Assert.Equal(ExtensionPackageState.ReviewRequired, restored.State);
        Assert.True(File.Exists(Path.Combine(restored.PackagePath, "index.js")));
        Assert.Single(await _service.ListPackagesAsync());
    }

    [Fact]
    public async Task Retry_ConcurrentChangeDuringDownloadCannotReplacePackage()
    {
        var package = await FailedPackageAsync();
        _handler.BeforeArchive = async () =>
        {
            await using var db = _factory.CreateDbContext();
            (await db.ExtensionPackages.SingleAsync()).Revision++; await db.SaveChangesAsync();
        };
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => _manager.RetryExtensionAsync(package.Id, package.Revision, true));
        Assert.Equal(ExtensionPackageState.Failed, (await _service.ListPackagesAsync()).Single().State);
        Assert.Empty(Directory.GetDirectories(Path.Combine(_root, "extensions"), ".install-*"));
    }

    [Fact]
    public async Task SameArchiveFromAnotherRegistry_DoesNotReuseFailedPackageGrants()
    {
        var failed = await FailedPackageAsync();
        var registry = await _service.AddRegistryAsync(new("Other source", "https://other.example.test/index.json"));
        var verified = ExtensionSdkV1.VerifyArchive(Path.Combine(_root, "package.zip"), failed.Sha256,
            Path.Combine(_root, "extensions", "other-source"));
        var staged = await _service.StageAsync(verified, registry.Id);
        Assert.NotEqual(failed.Id, staged.Id);
        Assert.Equal(registry.Id, staged.RegistryId);
        Assert.Equal(ExtensionPackageState.ReviewRequired, staged.State);
        Assert.All(await _service.ListPermissionReviewsAsync(staged.Id),
            review => Assert.Equal(ExtensionPermissionDecision.Pending, review.Decision));
    }

    private async Task<ExtensionPackageRecord> FailedPackageAsync(bool denyRequired = false, bool local = false, string feature = "signedSession@3")
    {
        var source = Path.Combine(_root, "source"); Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "manifest.json"), JsonSerializer.Serialize(new
        {
            id = "retry-fixture",
            displayName = "Retry fixture",
            version = "1.0.0",
            sdkVersion = "1",
            entryPoint = "index.js",
            requiredRuntimeFeatures = new[] { feature },
            capabilities = new[] { new { kind = "metadata", hooks = new[] { "searchTracks", "getTrack" }, accountScopes = Array.Empty<string>(), accountRequired = false } },
            permissions = new[] { new { kind = "network", value = "https://api.example.test/", required = true }, new { kind = "cache", value = "*", required = true } }
        }));
        await File.WriteAllTextAsync(Path.Combine(source, "index.js"), "registerExtension({ searchTracks: function() { return []; }, getTrack: function() { return null; } });");
        var archive = Path.Combine(_root, "package.zip"); ZipFile.CreateFromDirectory(source, archive);
        _handler.Archive = await File.ReadAllBytesAsync(archive);
        _handler.Hash = Convert.ToHexString(SHA256.HashData(_handler.Archive)).ToLowerInvariant();
        var verified = ExtensionSdkV1.VerifyArchive(archive, _handler.Hash, Path.Combine(_root, "extensions", "original"));
        var registry = local ? null : await _service.AddRegistryAsync(new("Fixture", "https://registry.example.test/index.json"));
        var package = await _service.StageAsync(verified, registry?.Id);
        package = await _service.ReviewAsync(package.Id, _reviewer, package.Revision,
            [new("network", "https://api.example.test/", true), new("cache", "*", !denyRequired)]);
        if (!denyRequired) await _service.FailStagingAsync(package.Id, package.Revision, "runtime_validation_failed");
        return (await _service.ListPackagesAsync()).Single();
    }

    private ExtensionController Controller(bool? administrator)
    {
        var context = new DefaultHttpContext(); context.Connection.RemoteIpAddress = IPAddress.Loopback;
        if (administrator.HasValue) context.Items[AdminAuthSessionService.HttpContextSessionItemKey] = new AdminAuthSession
        {
            SessionId = "fixture",
            UserId = "reviewer",
            UserName = "Reviewer",
            IsAdministrator = administrator.Value,
            AllstarrUserId = _reviewer,
            JellyfinAccessToken = "fixture",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        };
        return new(_manager, _service, null, NullLogger<ExtensionController>.Instance)
        { ControllerContext = new ControllerContext { HttpContext = context } };
    }

    public async Task DisposeAsync()
    {
        _manager.Dispose(); _handler.Dispose();
        await _database.DisposeAsync();
        Directory.Delete(_root, true);
    }

    private sealed class Factory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    { public AllstarrDbContext CreateDbContext() => new(options); }

    private sealed class PackageHandler : HttpMessageHandler
    {
        public byte[] Archive { get; set; } = [];
        public string Hash { get; set; } = "";
        public int Requests { get; private set; }
        public Func<Task>? BeforeArchive { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            if (request.RequestUri!.AbsolutePath.EndsWith(".zip", StringComparison.Ordinal))
            {
                if (BeforeArchive != null) await BeforeArchive();
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Archive) };
            }
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    extensions = new[] {
                new { id = "retry-fixture", version = "1.0.0", downloadUrl = "https://registry.example.test/package.zip", sha256 = Hash }
            }
                }))
            };
        }
    }
}
