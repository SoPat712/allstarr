using System.Net;
using System.Text;
using allstarr.Core.Extensions;
using allstarr.Models.Settings;
using allstarr.Services.Admin;
using allstarr.Services.Common;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace allstarr.Tests;

public class ExtensionManagerSecurityTests
{
    [Fact]
    public async Task ValidateStoreRegistryAsync_RejectsGitHubRepositoryPagesBeforeRequestingThem()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var httpClientFactory = new Mock<IHttpClientFactory>(MockBehavior.Strict);
            var manager = CreateManager(testRoot, httpClientFactory.Object);

            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                manager.ValidateStoreRegistryAsync("https://github.com/spotiflacapp/SpotiFLAC-Extension"));

            Assert.Contains("GitHub project page", exception.Message, StringComparison.Ordinal);
            Assert.Contains("raw registry.json", exception.Message, StringComparison.Ordinal);
            httpClientFactory.VerifyNoOtherCalls();
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    [Fact]
    public async Task ValidateStoreRegistryAsync_ExplainsRawGitHubFolderUrlsBeforeRequestingThem()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var httpClientFactory = new Mock<IHttpClientFactory>(MockBehavior.Strict);
            var manager = CreateManager(testRoot, httpClientFactory.Object);

            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                manager.ValidateStoreRegistryAsync("https://raw.githubusercontent.com/spotiflacapp/SpotiFLAC-Extension/"));

            Assert.Contains("folders return 404", exception.Message, StringComparison.Ordinal);
            Assert.Contains("/main/registry.json", exception.Message, StringComparison.Ordinal);
            httpClientFactory.VerifyNoOtherCalls();
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    [Fact]
    public async Task ValidateStoreRegistryAsync_AcceptsSpotiFlacCatalogAndDerivesChecksum()
    {
        const string registry = """
        {
          "extensions": [
            {
              "id": "spotify-web",
              "download_url": "https://raw.githubusercontent.com/example/extensions/main/spotify-web.sflx"
            }
          ]
        }
        """;
        var testRoot = CreateTestRoot();
        try
        {
            var manager = CreateManager(
                testRoot,
                CreateHttpClientFactory(Encoding.UTF8.GetBytes(registry)));

            var count = await manager.ValidateStoreRegistryAsync(
                "https://raw.githubusercontent.com/spotiflacapp/SpotiFLAC-Extension/main/registry.json");

            Assert.Equal(1, count);
            var item = Assert.Single(ExtensionManager.ParseStoreRegistry(registry));
            Assert.Equal("spotiflac-spotify-web", item.Id);
            Assert.Equal(SpotiFlacExtensionCompatibility.Marker, item.PackageFormat);
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    [Fact]
    public async Task LocalPackageFolders_DoNotBypassDurableSdkReview()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var extensionDirectory = Path.Combine(testRoot, "extensions", "safe-local");
            Directory.CreateDirectory(extensionDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(extensionDirectory, "manifest.json"),
                """{ "id": "safe-local", "displayName": "Safe Local", "version": "1.0.0" }""");
            await File.WriteAllTextAsync(
                Path.Combine(extensionDirectory, "index.js"),
                "registerExtension({ searchTracks: function() { return []; } });");

            var manager = CreateManager(
                testRoot,
                new Mock<IHttpClientFactory>(MockBehavior.Strict).Object);

            Assert.False(manager.RemoteInstallEnabled);
            Assert.Null(manager.GetExtension("safe-local"));
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    [Fact]
    public void ParseStoreRegistry_SkipsUnsafeOrAmbiguousExtensionIds()
    {
        const string json = """
        {
          "items": [
            { "id": "safe-extension", "downloadUrl": "https://example.test/safe.zip", "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" },
            { "id": ".", "downloadUrl": "https://example.test/dot.zip" },
            { "id": "..", "downloadUrl": "https://example.test/parent.zip" },
            { "id": "nested/extension", "downloadUrl": "https://example.test/nested.zip" },
            { "id": "nested\\extension", "downloadUrl": "https://example.test/backslash.zip" },
            { "id": "/rooted", "downloadUrl": "https://example.test/rooted.zip" },
            { "id": "Upper-Case", "downloadUrl": "https://example.test/upper.zip" },
            { "id": "under_score", "downloadUrl": "https://example.test/underscore.zip" }
          ]
        }
        """;

        var items = ExtensionManager.ParseStoreRegistry(json);

        var item = Assert.Single(items);
        Assert.Equal("safe-extension", item.Id);
    }

    [Fact]
    public void RuntimeBridge_EnforcesNetworkCacheAndSecretPermissions()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var requests = 0;
            var factory = new Mock<IHttpClientFactory>();
            factory.Setup(item => item.CreateClient("ExtensionSdkV1"))
                .Returns(() => new HttpClient(new CountingResponseHandler(() => requests++)));
            var permissions = new ExtensionRuntimePermissionSet(
                new HashSet<string>(["https://api.example.test/"], StringComparer.Ordinal),
                new HashSet<string>(["metadataCache"], StringComparer.Ordinal),
                new HashSet<string>(["accountToken"], StringComparer.Ordinal),
                key => key == "accountToken" ? "ephemeral-token" : null);
            var bridge = new ExtensionHostBridge(
                Path.Combine(testRoot, "runtime"), factory.Object,
                Mock.Of<ILogger<ExtensionManager>>(), permissions);

            bridge.StorageSet("metadataCache", "value");
            Assert.Equal("value", bridge.StorageGet("metadataCache"));
            Assert.Equal((1, 18L), bridge.StorageUsage());
            Assert.Throws<UnauthorizedAccessException>(() => bridge.StorageSet("otherCache", "value"));
            Assert.Equal("{{allstarr-secret:accountToken}}", bridge.SecretGet("accountToken"));
            Assert.Throws<UnauthorizedAccessException>(() => bridge.SecretGet("otherToken"));

            var allowed = System.Text.Json.JsonSerializer.Serialize(bridge.HttpGet("https://api.example.test/v1", null));
            var denied = System.Text.Json.JsonSerializer.Serialize(bridge.HttpGet("https://other.example.test/v1", null));
            Assert.Contains("ok", allowed, StringComparison.Ordinal);
            Assert.Contains("permission_denied", denied, StringComparison.Ordinal);
            Assert.Equal(1, requests);
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    private static ExtensionManager CreateManager(
        string testRoot,
        IHttpClientFactory httpClientFactory)
    {
        var extensionsDirectory = Path.Combine(testRoot, "extensions");
        var settings = new Dictionary<string, string?>
        {
            ["Extensions:Directory"] = extensionsDirectory
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        return new ExtensionManager(
            httpClientFactory,
            Mock.Of<ILogger<ExtensionManager>>(),
            configuration);
    }

    private static IHttpClientFactory CreateHttpClientFactory(byte[] package)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(item => item.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(new StaticResponseHandler(package)));
        return factory.Object;
    }

    private static string CreateTestRoot()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"allstarr-extension-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testRoot);
        return testRoot;
    }

    private static void DeleteTestRoot(string testRoot)
    {
        if (Directory.Exists(testRoot))
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    private sealed class StaticResponseHandler(byte[] package) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(package)
            });
        }
    }

    private sealed class CountingResponseHandler(Action count) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            count();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("ok", Encoding.UTF8, "application/json")
            });
        }
    }

}
