using allstarr.Controllers;
using allstarr.Core.Capabilities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Text.Json;

namespace allstarr.Tests;

public class DownloadsControllerPathSecurityTests
{
    [Fact]
    public async Task DownloadFile_PathTraversalIntoPrefixedSibling_IsRejected()
    {
        var testRoot = CreateTestRoot();
        var downloadsRoot = Path.Combine(testRoot, "downloads");
        var keptRoot = Path.Combine(downloadsRoot, "kept");
        var siblingRoot = Path.Combine(downloadsRoot, "kept-malicious");

        Directory.CreateDirectory(keptRoot);
        Directory.CreateDirectory(siblingRoot);
        File.WriteAllText(Path.Combine(siblingRoot, "attack.mp3"), "not-allowed");

        try
        {
            var controller = CreateController(downloadsRoot);
            var result = await controller.DownloadFile("../kept-malicious/attack.mp3");

            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Equal(StatusCodes.Status400BadRequest, badRequest.StatusCode);
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    [Fact]
    public void DeleteDownload_PathTraversalIntoPrefixedSibling_IsRejected()
    {
        var testRoot = CreateTestRoot();
        var downloadsRoot = Path.Combine(testRoot, "downloads");
        var keptRoot = Path.Combine(downloadsRoot, "kept");
        var siblingRoot = Path.Combine(downloadsRoot, "kept-malicious");
        var siblingFile = Path.Combine(siblingRoot, "attack.mp3");

        Directory.CreateDirectory(keptRoot);
        Directory.CreateDirectory(siblingRoot);
        File.WriteAllText(siblingFile, "not-allowed");

        try
        {
            var controller = CreateController(downloadsRoot);
            var result = controller.DeleteDownload("../kept-malicious/attack.mp3");

            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Equal(StatusCodes.Status400BadRequest, badRequest.StatusCode);
            Assert.True(File.Exists(siblingFile));
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    [Fact]
    public async Task DownloadFile_ValidPathInsideKeptFolder_AllowsDownload()
    {
        var testRoot = CreateTestRoot();
        var downloadsRoot = Path.Combine(testRoot, "downloads");
        var artistDir = Path.Combine(downloadsRoot, "kept", "Artist");
        var validFile = Path.Combine(artistDir, "track.mp3");

        Directory.CreateDirectory(artistDir);
        File.WriteAllText(validFile, "ok");

        try
        {
            var controller = CreateController(downloadsRoot);
            var result = await controller.DownloadFile("Artist/track.mp3");

            Assert.IsType<FileStreamResult>(result);
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    [Fact]
    public void GetDownloads_UsesNullableMillisecondsForUnreadableDuration()
    {
        var testRoot = CreateTestRoot();
        var downloadsRoot = Path.Combine(testRoot, "downloads");
        var artistDir = Path.Combine(downloadsRoot, "kept", "Artist");
        Directory.CreateDirectory(artistDir);
        File.WriteAllText(Path.Combine(artistDir, "track.mp3"), "not-a-complete-audio-file");

        try
        {
            var result = Assert.IsType<OkObjectResult>(CreateController(downloadsRoot).GetDownloads());
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(
                result.Value,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            var file = Assert.Single(document.RootElement.GetProperty("files").EnumerateArray());
            Assert.Equal(JsonValueKind.Null, file.GetProperty("durationMilliseconds").ValueKind);
            Assert.False(file.TryGetProperty("durationSeconds", out _));
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    [Fact]
    public void GetDownloads_ProjectsProviderArtworkThroughTheSharedResolverRoute()
    {
        var testRoot = CreateTestRoot();
        var downloadsRoot = Path.Combine(testRoot, "downloads");
        var artistDir = Path.Combine(downloadsRoot, "kept", "Artist");
        Directory.CreateDirectory(artistDir);
        File.WriteAllText(
            Path.Combine(artistDir, "track [future-extension-native-1].mp3"),
            "not-a-complete-audio-file");

        try
        {
            var provider = new ProviderDescriptor(
                    "future-extension",
                    "Future Extension",
                    "Test provider",
                    ProviderOrigin.Extension,
                    "1",
                    "1.0",
                    [],
                    new ProviderPermissionDescriptor(),
                    entryPoint: "index.js");
            var registry = new Mock<IProviderRegistry>(MockBehavior.Strict);
            registry.SetupGet(item => item.Providers).Returns([provider]);
            var result = Assert.IsType<OkObjectResult>(
                CreateController(downloadsRoot, registry.Object).GetDownloads());
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(
                result.Value,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            var file = Assert.Single(document.RootElement.GetProperty("files").EnumerateArray());
            Assert.Equal("future-extension", file.GetProperty("provider").GetString());
            Assert.Equal("native-1", file.GetProperty("externalId").GetString());
            Assert.Equal(
                "/api/admin/downloads/artwork/ext-future-extension-song-native-1",
                file.GetProperty("artworkUrl").GetString());
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    private static DownloadsController CreateController(
        string downloadsRoot,
        IProviderRegistry? providerRegistry = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Library:DownloadPath"] = downloadsRoot
            })
            .Build();

        return new DownloadsController(
            NullLogger<DownloadsController>.Instance,
            config,
            providerRegistry: providerRegistry)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
    }

    private static string CreateTestRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "allstarr-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTestRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
