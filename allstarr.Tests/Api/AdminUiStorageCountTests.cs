using allstarr.Controllers;
using allstarr.Core.Storage;
using allstarr.Core.ManagedFiles;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed class AdminUiStorageCountTests
{
    [Fact]
    public async Task CurrentManagedKeptFilesRefreshWithoutLegacyDownloadMappings()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var root = Path.Combine(Path.GetTempPath(), $"allstarr-home-managed-{Guid.NewGuid():N}");
        var downloadRoot = Path.Combine(root, "downloads");
        var keptRoot = Path.Combine(root, "kept");
        Directory.CreateDirectory(keptRoot);
        var path = Path.Combine(keptRoot, "song.flac");
        File.WriteAllText(path, "fixture");
        try
        {
            await using var db = new AllstarrDbContext(database.Options);
            var before = await AdminUiController.ReadExistingStorageCountsAsync(db, downloadRoot, keptRoot);
            Assert.Equal(0, before.KeptTracks);
            var file = new ManagedFileOwnershipEntity
            {
                Id = Guid.CreateVersion7(),
                RootId = Guid.CreateVersion7(),
                TargetRootPath = keptRoot,
                CanonicalPath = path,
                ContentSha256 = new string('a', 64),
                Length = 7,
                ScopeKey = "household",
                IsManaged = true,
                CreatedAt = DateTimeOffset.UtcNow,
                Revision = 1
            };
            db.ManagedFiles.Add(file);
            await db.SaveChangesAsync();
            var after = await AdminUiController.ReadExistingStorageCountsAsync(db, downloadRoot, keptRoot);
            Assert.Equal(1, after.KeptTracks);
            file.RemovedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            Assert.Equal(0, (await AdminUiController.ReadExistingStorageCountsAsync(db, downloadRoot, keptRoot)).KeptTracks);
            file.RemovedAt = null;
            await db.SaveChangesAsync();
            File.Delete(path);
            Assert.Equal(0, (await AdminUiController.ReadExistingStorageCountsAsync(db, downloadRoot, keptRoot)).KeptTracks);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void StorageCountsDoNotCountTheSameOwnedPathTwice()
    {
        var root = Path.Combine(Path.GetTempPath(), $"allstarr-home-duplicate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "cache"));
        var path = Path.Combine(root, "cache", "song.mp3");
        File.WriteAllText(path, "fixture");
        try { Assert.Equal(1, AdminUiController.CountExistingStorageMappings([path, path], root).CacheTracks); }
        finally { Directory.Delete(root, recursive: true); }
    }

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
