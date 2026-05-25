using allstarr.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

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

    private static DownloadsController CreateController(string downloadsRoot)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Library:DownloadPath"] = downloadsRoot
            })
            .Build();

        return new DownloadsController(
            NullLogger<DownloadsController>.Instance,
            config)
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
