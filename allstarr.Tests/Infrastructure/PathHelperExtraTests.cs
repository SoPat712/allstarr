using allstarr.Services.Common;

namespace allstarr.Tests;

public sealed class PathHelperExtraTests : IDisposable
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
        var provider = "prov/../ider";
        var externalId = "..\evil|id";

        var path = PathHelper.BuildTrackPath(downloadPath, artist, album, title, 1, ".mp3", provider, externalId);

        var fileName = Path.GetFileName(path);
        Assert.Contains("[", fileName);
        Assert.DoesNotContain("..", fileName);
        Assert.DoesNotContain("/", fileName);
        Assert.DoesNotContain("\\", fileName);
    }

    [Fact]
    public void ResolveUniquePath_HandlesNoDirectoryProvided()
    {
        var originalCurrent = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(_testPath);
            var baseName = "song.mp3";
            File.WriteAllText(Path.Combine(_testPath, baseName), "x");

            var unique = PathHelper.ResolveUniquePath(baseName);

            Assert.NotEqual(baseName, unique);
            Assert.Contains("song (1).mp3", unique);
        }
        finally
        {
            Directory.SetCurrentDirectory(originalCurrent);
        }
    }

}
