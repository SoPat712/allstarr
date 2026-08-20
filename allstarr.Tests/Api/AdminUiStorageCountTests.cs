using allstarr.Controllers;

namespace allstarr.Tests;

public sealed class AdminUiStorageCountTests
{
    [Fact]
    public void ExistingStorageMappings_ExcludeMissingAndOutsideFiles()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"allstarr-home-storage-{Guid.NewGuid():N}");
        var downloadRoot = Path.Combine(testRoot, "downloads");
        var paths = new[]
        {
            Path.Combine(downloadRoot, "cache", "cached.mp3"),
            Path.Combine(downloadRoot, "transcoded", "transcoded.m4a"),
            Path.Combine(downloadRoot, "permanent", "permanent.flac"),
            Path.Combine(downloadRoot, "kept", "legacy.ogg"),
            Path.Combine(downloadRoot, "cache", "missing.mp3"),
            Path.Combine(testRoot, "outside.mp3")
        };

        try
        {
            foreach (var path in paths.Where(path => !path.EndsWith("missing.mp3", StringComparison.Ordinal)))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "fixture");
            }

            var counts = AdminUiController.CountExistingStorageMappings(paths, downloadRoot);

            Assert.Equal(2, counts.CacheTracks);
            Assert.Equal(2, counts.KeptTracks);
        }
        finally
        {
            if (Directory.Exists(testRoot)) Directory.Delete(testRoot, recursive: true);
        }
    }
}
