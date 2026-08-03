using System;
using System.IO;
using allstarr.Services.Common;

namespace allstarr.Tests;

public class PathHelperExtraTests : IDisposable
{
    private readonly string _testPath;

    public PathHelperExtraTests()
    {
        _testPath = Path.Combine(Path.GetTempPath(), "allstarr-pathhelper-extra-" + Guid.NewGuid());
        Directory.CreateDirectory(_testPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testPath)) Directory.Delete(_testPath, true);
    }

    [Fact]
    public void BuildTrackPath_WithProviderAndExternalId_SanitizesSuffix()
    {
        var downloadPath = _testPath;
        var artist = "Artist";
        var album = "Album";
        var title = "Song";
        var provider = "prov/../ider"; // contains slashes and dots
        var externalId = "..\evil|id"; // contains traversal and invalid chars

        var path = PathHelper.BuildTrackPath(downloadPath, artist, album, title, 1, ".mp3", provider, externalId);

        // Ensure the path contains sanitized provider/external id and no directory separators in the filename
        var fileName = Path.GetFileName(path);
        Assert.Contains("[", fileName);
        Assert.DoesNotContain("..", fileName);
        Assert.DoesNotContain("/", fileName);
        Assert.DoesNotContain("\\", fileName);
    }

    [Fact]
    public void ResolveUniquePath_HandlesNoDirectoryProvided()
    {
        // Arrange - create files in current directory
        var originalCurrent = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(_testPath);
            var baseName = "song.mp3";
            File.WriteAllText(Path.Combine(_testPath, baseName), "x");

            // Act
            var unique = PathHelper.ResolveUniquePath(baseName);

            // Assert
            Assert.NotEqual(baseName, unique);
            Assert.Contains("song (1).mp3", unique);
        }
        finally
        {
            Directory.SetCurrentDirectory(originalCurrent);
        }
    }

}
