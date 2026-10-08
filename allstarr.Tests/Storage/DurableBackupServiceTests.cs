using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using allstarr.Controllers;
using allstarr.Core.Operations;
using allstarr.Core.Secrets;
using allstarr.Core.Storage;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed class DurableBackupServiceTests : IAsyncLifetime
{
    private SqliteTestDatabase _database = null!;
    private TestDbContextFactory _factory = null!;
    private StorageOptions _options = null!;
    private SecretStoreOptions _secrets = null!;
    private DurableStorageState _state = null!;
    private Guid _secretId;

    public async Task InitializeAsync()
    {
        _database = await SqliteTestDatabase.CreateAsync();
        _factory = new TestDbContextFactory(_database.Options);
        _options = _database.StorageOptions;
        _secrets = new SecretStoreOptions { KeyRingPath = Path.Combine(_options.DataDirectory, "keyring.json") };
        var provider = new FileSecretKeyRingProvider(_secrets);
        await provider.CreateIfMissingAsync(false);
        var store = new EncryptedSecretStore(_factory, provider, _secrets, new SystemPlatformClock());
        _secretId = (await store.StoreAsync(null, "backup.fixture", Encoding.UTF8.GetBytes("backup-fixture-secret"))).Id;
        _state = new DurableStorageState(_options);
        _state.Set(DurableStorageReadiness.Ready);
    }

    [Fact]
    public async Task Creation_ProducesPrivateConsistentSnapshotWithUsableCredentials()
    {
        var service = Service();
        var artifact = await service.CreateAsync();
        var path = Path.Combine(_options.BackupDirectory, artifact.FileName);
        Assert.Equal("verified", artifact.Status);
        Assert.True(artifact.IncludesKeyRing);
        Assert.Equal(await DurableBackupService.HashAsync(path, default), artifact.Sha256);
        Assert.Equal(artifact.Id, Assert.Single(await Service().ListAsync()).Id);
        Assert.Empty(Directory.GetDirectories(_options.BackupDirectory));
        Assert.Empty(Directory.GetFiles(_options.BackupDirectory, "*.partial"));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));

        var extracted = Path.Combine(_options.DataDirectory, "extracted");
        ZipFile.ExtractToDirectory(path, extracted);
        var keyPath = Path.Combine(extracted, "keyring.json");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var schema = await service.VerifySnapshotAsync(Path.Combine(extracted, "allstarr.db"), keyPath, default);
        Assert.Equal(artifact.SchemaVersion, schema);
        var connection = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(extracted, "allstarr.db"),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        };
        var restoredFactory = new TestDbContextFactory(new DbContextOptionsBuilder<AllstarrDbContext>().UseSqlite(connection.ToString()).Options);
        var restoredOptions = new SecretStoreOptions { KeyRingPath = keyPath };
        var restoredStore = new EncryptedSecretStore(restoredFactory, new FileSecretKeyRingProvider(restoredOptions), restoredOptions, new SystemPlatformClock());
        using var lease = await restoredStore.OpenAsync(_secretId, new SecretAccessContext(null, AllowGlobal: true));
        Assert.Equal("backup-fixture-secret", lease.ReadUtf8());
    }

    [Theory]
    [InlineData("allstarr.db")]
    [InlineData("keyring.json")]
    public async Task Download_RejectsTamperedPayload(string entry)
    {
        var service = Service();
        var artifact = await service.CreateAsync();
        await RewriteEntryAsync(artifact, entry, Encoding.UTF8.GetBytes("tampered"));
        var error = await Assert.ThrowsAsync<BackupVerificationException>(() => service.OpenVerifiedAsync(artifact.Id));
        Assert.Contains("checksum", error.Message);
        Assert.Equal(422, Assert.IsType<UnprocessableEntityObjectResult>(await Controller(true).DownloadBackup(artifact.Id)).StatusCode);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("schema")]
    [InlineData("duplicate")]
    [InlineData("hash")]
    [InlineData("version")]
    public async Task Manifest_RejectsUnknownFieldsSchemaDuplicatesAndInvalidHashes(string kind)
    {
        var service = Service();
        var artifact = await service.CreateAsync();
        var path = Path.Combine(_options.BackupDirectory, artifact.FileName);
        string json;
        using (var archive = ZipFile.OpenRead(path))
        using (var reader = new StreamReader(archive.GetEntry("manifest.json")!.Open())) json = await reader.ReadToEndAsync();
        var document = JsonNode.Parse(json)!;
        switch (kind)
        {
            case "unknown": document["unexpected"] = true; break;
            case "schema": document["lastMigrationId"] = "unknown-schema"; break;
            case "hash": document["sha256"]!["allstarr.db"] = "invalid"; break;
            case "version": document["formatVersion"] = 99; break;
        }
        json = kind == "duplicate" ? json.Replace("\"formatVersion\":1", "\"formatVersion\":1,\"formatVersion\":1", StringComparison.Ordinal) : document.ToJsonString();
        await RewriteEntryAsync(artifact, "manifest.json", Encoding.UTF8.GetBytes(json));
        await Assert.ThrowsAsync<BackupVerificationException>(() => service.VerifyArchiveAsync(path, default));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("allstarr.db")]
    public async Task Archive_RejectsExtraOrDuplicateEntries(string name)
    {
        var artifact = await Service().CreateAsync();
        var path = Path.Combine(_options.BackupDirectory, artifact.FileName);
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update)) archive.CreateEntry(name);
        await Assert.ThrowsAsync<BackupVerificationException>(() => Service().VerifyArchiveAsync(path, default));
    }

    [Fact]
    public async Task Retention_OnlyRemovesOldOwnedArchives()
    {
        _options.BackupRetentionCount = 2;
        var service = Service();
        var first = await service.CreateAsync();
        var damaged = Path.Combine(_options.BackupDirectory, "allstarr-backup-damaged.zip");
        var unrelated = Path.Combine(_options.BackupDirectory, "keep.zip");
        await File.WriteAllTextAsync(damaged, "invalid");
        await File.WriteAllTextAsync(unrelated, "operator-owned");
        var second = await service.CreateAsync();
        var third = await service.CreateAsync();
        Assert.Equal(new[] { third.Id, second.Id }, (await service.ListAsync()).Select(item => item.Id));
        Assert.False(File.Exists(Path.Combine(_options.BackupDirectory, first.FileName)));
        Assert.True(File.Exists(damaged));
        Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public async Task Retention_PreservesOwnedArchiveIfItsPayloadIsDamaged()
    {
        _options.BackupRetentionCount = 1;
        var service = Service();
        var damaged = await service.CreateAsync();
        await RewriteEntryAsync(damaged, "allstarr.db", Encoding.UTF8.GetBytes("damaged"));
        await service.CreateAsync();
        Assert.True(File.Exists(Path.Combine(_options.BackupDirectory, damaged.FileName)));
    }

    [Fact]
    public async Task Creation_RejectsUnreadyStorageAndMissingKeysWithoutArtifacts()
    {
        var service = Service();
        _state.Set(DurableStorageReadiness.Unavailable);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync());
        Assert.False(Directory.Exists(_options.BackupDirectory));
        _state.Set(DurableStorageReadiness.Ready);
        File.Delete(_secrets.KeyRingPath);
        await Assert.ThrowsAsync<FileNotFoundException>(() => service.CreateAsync());
        Assert.Empty(Directory.GetFileSystemEntries(_options.BackupDirectory));
        await new FileSecretKeyRingProvider(_secrets).CreateIfMissingAsync(false);
        await Assert.ThrowsAsync<BackupVerificationException>(() => service.CreateAsync());
        Assert.Empty(Directory.GetFileSystemEntries(_options.BackupDirectory));
    }

    [Fact]
    public async Task Creation_RejectsQuotedDirectoryBeforeWriting()
    {
        _options.DataDirectory = Path.Combine(_options.DataDirectory, "invalid'path");
        await Assert.ThrowsAsync<BackupVerificationException>(() => Service().CreateAsync());
        Assert.False(Directory.Exists(_options.DataDirectory));
    }

    [Theory]
    [InlineData(null, 401)]
    [InlineData(false, 403)]
    public async Task Api_RequiresAdministratorForStatusCreationAndDownload(bool? administrator, int expected)
    {
        var controller = Controller(administrator);
        Assert.Equal(expected, Assert.IsAssignableFrom<ObjectResult>(await controller.Get()).StatusCode);
        Assert.Equal(expected, Assert.IsAssignableFrom<ObjectResult>(await controller.CreateBackup()).StatusCode);
        Assert.Equal(expected, Assert.IsAssignableFrom<ObjectResult>(await controller.DownloadBackup(Guid.NewGuid())).StatusCode);
        Assert.False(Directory.Exists(_options.BackupDirectory));
    }

    [Fact]
    public async Task Api_CreatesAndDownloadsBackupAndHidesInternalPathsOnFailure()
    {
        var controller = Controller(true);
        var artifact = Assert.IsType<BackupArtifact>(Assert.IsType<OkObjectResult>(await controller.CreateBackup()).Value);
        var download = Assert.IsType<FileStreamResult>(await controller.DownloadBackup(artifact.Id));
        await using (download.FileStream)
        {
            Assert.Equal("application/zip", download.ContentType);
            Assert.Equal(artifact.FileName, download.FileDownloadName);
            Assert.True(download.FileStream.Length > 0);
        }
        Assert.IsType<NotFoundObjectResult>(await controller.DownloadBackup(Guid.NewGuid()));
        File.Delete(_secrets.KeyRingPath);
        var unavailable = Assert.IsType<ObjectResult>(await controller.CreateBackup());
        Assert.Equal(503, unavailable.StatusCode);
        Assert.DoesNotContain(_options.DataDirectory, System.Text.Json.JsonSerializer.Serialize(unavailable.Value));
    }

    private StorageController Controller(bool? administrator)
    {
        var controller = new StorageController(_state, Service()) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        if (administrator.HasValue)
            controller.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = new AdminAuthSession
            {
                SessionId = "fixture",
                UserId = "fixture",
                UserName = "fixture",
                IsAdministrator = administrator.Value,
                JellyfinAccessToken = "fixture",
                ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
            };
        return controller;
    }
    private DurableBackupService Service() => new(_factory, _options, _state, _secrets);
    private async Task RewriteEntryAsync(BackupArtifact artifact, string name, byte[] bytes)
    {
        using var archive = ZipFile.Open(Path.Combine(_options.BackupDirectory, artifact.FileName), ZipArchiveMode.Update);
        archive.GetEntry(name)!.Delete();
        await using var stream = archive.CreateEntry(name).Open();
        await stream.WriteAsync(bytes);
    }
    public async Task DisposeAsync() => await _database.DisposeAsync();
    private sealed class TestDbContextFactory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
        public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AllstarrDbContext(options));
    }
}
