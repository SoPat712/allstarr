using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using allstarr.Core.Operations;
using allstarr.Core.Secrets;
using allstarr.Core.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace allstarr.Tests;

public sealed partial class DurableBackupServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restore_RoundTripWaitsForStartupAndPreservesPreviousFiles(bool externalKeyPath)
    {
        if (externalKeyPath)
        {
            var external = Path.Combine(_options.DataDirectory, "external");
            Directory.CreateDirectory(external);
            var path = Path.Combine(external, "custom-key.json");
            File.Move(_secrets.KeyRingPath, path);
            _secrets.KeyRingPath = path;
        }
        var service = Service();
        var backup = await service.CreateAsync();
        var newerSecret = await AddNewKeyAndSecretAsync();
        var previousDatabase = await File.ReadAllBytesAsync(_database.DatabasePath);
        var previousKey = await File.ReadAllBytesAsync(_secrets.KeyRingPath);
        await StageAsync(service, backup);
        Assert.True(service.HasPendingRestore);
        Assert.Equal(previousDatabase, await File.ReadAllBytesAsync(_database.DatabasePath));
        Assert.Equal(previousKey, await File.ReadAllBytesAsync(_secrets.KeyRingPath));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyPendingRestoreAsync());
        await Assert.ThrowsAsync<RestorePendingException>(() => StageAsync(service, backup));
        await Assert.ThrowsAsync<RestorePendingException>(() => service.CreateAsync());
        Assert.Empty(Directory.GetDirectories(_options.DataDirectory, "pre-restore-*"));

        await RestartAsync();
        Assert.Equal(DurableStorageReadiness.Ready, _state.GetSnapshot().Readiness);
        Assert.False(Service().HasPendingRestore);
        var previous = Assert.Single(Directory.GetDirectories(_options.DataDirectory, "pre-restore-*"));
        Assert.Equal(previousDatabase, await File.ReadAllBytesAsync(Path.Combine(previous, "allstarr.db")));
        Assert.Equal(previousKey, await File.ReadAllBytesAsync(Path.Combine(previous, "keyring.json")));
        Assert.False(Directory.Exists(Path.Combine(previous, "applied")));
        var current = new EncryptedSecretStore(_factory, new FileSecretKeyRingProvider(_secrets), _secrets, new SystemPlatformClock());
        using var restored = await current.OpenAsync(_secretId, new SecretAccessContext(_userId, "backup.fixture"));
        Assert.Equal("backup-fixture-secret", restored.ReadUtf8());
        await using var context = await _factory.CreateDbContextAsync();
        Assert.Equal(_userId, (await context.Users.SingleAsync()).Id);
        Assert.Equal("false", (await context.RuntimeSettings.SingleAsync()).ValueJson);
        Assert.False(await context.SecretReferences.AnyAsync(item => item.Id == newerSecret));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_secrets.KeyRingPath));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_database.DatabasePath));
        }
        await RestartAsync();
        Assert.Single(Directory.GetDirectories(_options.DataDirectory, "pre-restore-*"));
        Assert.Equal(DurableStorageReadiness.Ready, _state.GetSnapshot().Readiness);
    }

    [Fact]
    public async Task InterruptedApplication_RetriesFromPreservedPairBeforeOpeningStorage()
    {
        var service = Service();
        var backup = await service.CreateAsync();
        await AddNewKeyAndSecretAsync();
        var previousDatabase = await File.ReadAllBytesAsync(_database.DatabasePath);
        var previousKey = await File.ReadAllBytesAsync(_secrets.KeyRingPath);
        await StageAsync(service, backup);
        // A filesystem obstruction stops application after key replacement but before database replacement.
        var obstruction = _database.DatabasePath + "-wal";
        Directory.CreateDirectory(obstruction);
        await RestartAsync();
        Assert.Equal(DurableStorageReadiness.Unavailable, _state.GetSnapshot().Readiness);
        Assert.Equal("restore_application_failed", _state.GetSnapshot().ErrorCode);
        Assert.True(Service().HasPendingRestore);
        Assert.Equal(previousDatabase, await File.ReadAllBytesAsync(_database.DatabasePath));
        var previous = Assert.Single(Directory.GetDirectories(_options.DataDirectory, "pre-restore-*"));
        Assert.Equal(previousKey, await File.ReadAllBytesAsync(Path.Combine(previous, "keyring.json")));
        Assert.NotEqual(previousKey, await File.ReadAllBytesAsync(_secrets.KeyRingPath));

        Directory.Delete(obstruction);
        await RestartAsync();
        Assert.Equal(DurableStorageReadiness.Ready, _state.GetSnapshot().Readiness);
        Assert.False(Service().HasPendingRestore);
        Assert.Equal(previousDatabase, await File.ReadAllBytesAsync(Path.Combine(previous, "allstarr.db")));
        Assert.Equal(previousKey, await File.ReadAllBytesAsync(Path.Combine(previous, "keyring.json")));
        Assert.Single(Directory.GetDirectories(_options.DataDirectory, "pre-restore-*"));
    }

    [Theory]
    [InlineData("checksum")]
    [InlineData("database")]
    [InlineData("schema")]
    [InlineData("symlink")]
    [InlineData("zip")]
    public async Task InvalidRestore_NeverPublishesPendingOrChangesLiveFiles(string corruption)
    {
        var backup = await Service().CreateAsync();
        var path = Path.Combine(_options.BackupDirectory, backup.FileName);
        switch (corruption)
        {
            case "zip": await File.WriteAllTextAsync(path, "invalid"); break;
            case "checksum": await RewriteEntryAsync(backup, "allstarr.db", Encoding.UTF8.GetBytes("tampered")); break;
            case "symlink":
                using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
                    archive.GetEntry("keyring.json")!.ExternalAttributes = 0xA000 << 16;
                break;
            case "database":
            case "schema":
                var bytes = Encoding.UTF8.GetBytes("not a database");
                if (corruption == "schema")
                {
                    var copy = Path.Combine(_options.DataDirectory, "schema-fixture.db");
                    using (var archive = ZipFile.OpenRead(path)) archive.GetEntry("allstarr.db")!.ExtractToFile(copy);
                    await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = copy, Pooling = false }.ToString()))
                    {
                        await connection.OpenAsync();
                        await using var command = connection.CreateCommand();
                        command.CommandText = "DELETE FROM __EFMigrationsHistory;";
                        await command.ExecuteNonQueryAsync();
                    }
                    bytes = await File.ReadAllBytesAsync(copy);
                }
                await RewriteEntryAsync(backup, "allstarr.db", bytes);
                string json;
                using (var archive = ZipFile.OpenRead(path))
                using (var reader = new StreamReader(archive.GetEntry("manifest.json")!.Open())) json = await reader.ReadToEndAsync();
                var manifest = JsonNode.Parse(json)!;
                manifest["sha256"]!["allstarr.db"] = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                await RewriteEntryAsync(backup, "manifest.json", Encoding.UTF8.GetBytes(manifest.ToJsonString()));
                break;
        }
        var before = await File.ReadAllBytesAsync(_database.DatabasePath);
        var key = await File.ReadAllBytesAsync(_secrets.KeyRingPath);
        await Assert.ThrowsAnyAsync<Exception>(() => StageAsync(Service(), backup));
        Assert.False(Service().HasPendingRestore);
        Assert.Empty(Directory.GetDirectories(_options.DataDirectory, ".restore-upload-*"));
        Assert.Equal(before, await File.ReadAllBytesAsync(_database.DatabasePath));
        Assert.Equal(key, await File.ReadAllBytesAsync(_secrets.KeyRingPath));
    }

    [Fact]
    public async Task PendingTampering_BlocksStartupWithoutOpeningOrReplacingDatabase()
    {
        await StageAsync(Service(), await Service().CreateAsync());
        var before = await File.ReadAllBytesAsync(_database.DatabasePath);
        await File.AppendAllTextAsync(Path.Combine(_options.DataDirectory, "restore-pending", "allstarr.db"), "tampered");
        await RestartAsync();
        Assert.Equal("restore_application_failed", _state.GetSnapshot().ErrorCode);
        Assert.Equal(before, await File.ReadAllBytesAsync(_database.DatabasePath));
        Assert.Empty(Directory.GetDirectories(_options.DataDirectory, "pre-restore-*"));
    }

    [Fact]
    public async Task LinkedPendingDirectory_IsNeverFollowedOrRemoved()
    {
        var other = Path.Combine(_options.DataDirectory, "operator-owned");
        Directory.CreateDirectory(other);
        var sentinel = Path.Combine(other, "keep.txt");
        await File.WriteAllTextAsync(sentinel, "preserve");
        var pending = Path.Combine(_options.DataDirectory, "restore-pending");
        Directory.CreateSymbolicLink(pending, other);
        try
        {
            await RestartAsync();
            Assert.Equal("restore_application_failed", _state.GetSnapshot().ErrorCode);
            Assert.Equal("preserve", await File.ReadAllTextAsync(sentinel));
        }
        finally { Directory.Delete(pending); }
    }

    [Fact]
    public async Task RestoreApi_ValidatesUploadAndReportsPendingConflict()
    {
        var controller = Controller(true);
        Assert.IsType<BadRequestObjectResult>(await controller.StageRestore(null));
        using var empty = new MemoryStream();
        Assert.Equal(413, Assert.IsType<ObjectResult>(await controller.StageRestore(
            new FormFile(empty, 0, DurableBackupService.MaximumArchiveBytes + 1, "backup", "too-large.zip"))).StatusCode);
        using var invalid = new MemoryStream(Encoding.UTF8.GetBytes("invalid"));
        Assert.IsType<UnprocessableEntityObjectResult>(await controller.StageRestore(new FormFile(invalid, 0, invalid.Length, "backup", "invalid.zip")));
        var artifact = await Service().CreateAsync();
        await using var file = File.OpenRead(Path.Combine(_options.BackupDirectory, artifact.FileName));
        var upload = new FormFile(file, 0, file.Length, "backup", "valid.zip");
        var response = Assert.IsType<OkObjectResult>(await controller.StageRestore(upload));
        Assert.Contains("Restart Allstarr", System.Text.Json.JsonSerializer.Serialize(response.Value));
        Assert.IsType<ConflictObjectResult>(await controller.StageRestore(upload));
    }

    private async Task StageAsync(DurableBackupService service, BackupArtifact artifact)
    {
        await using var file = File.OpenRead(Path.Combine(_options.BackupDirectory, artifact.FileName));
        await service.StageRestoreAsync(file);
    }
    private async Task<Guid> AddNewKeyAndSecretAsync()
    {
        var ring = JsonNode.Parse(await File.ReadAllTextAsync(_secrets.KeyRingPath))!;
        ring["activeKeyId"] = "new-key";
        ring["keys"]!["new-key"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        await File.WriteAllTextAsync(_secrets.KeyRingPath, ring.ToJsonString());
        var store = new EncryptedSecretStore(_factory, new FileSecretKeyRingProvider(_secrets), _secrets, new SystemPlatformClock());
        return (await store.StoreAsync(null, "newer.fixture", Encoding.UTF8.GetBytes("newer-fixture-secret"))).Id;
    }
    private async Task RestartAsync()
    {
        _state = new DurableStorageState(_options);
        await new DurableStorageInitializer(_factory, _options, _state, NullLogger<DurableStorageInitializer>.Instance, Service()).StartAsync(default);
    }
}
