using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using allstarr.Core.Jobs;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace allstarr.Tests;

public sealed partial class DurableBackupServiceTests
{
    [Fact]
    public async Task ActiveDownloadPinsSurviveServicePublicationAndPreventRetentionUntilClosed()
    {
        _options.BackupRetentionCount = 1;
        using var service = Service();
        var first = await service.CreateAsync();
        var download = await service.OpenVerifiedAsync(first.Id);
        await service.CreateAsync();
        var path = Path.Combine(_options.BackupDirectory, first.FileName);
        Assert.True(File.Exists(path));
        var prefix = new byte[4];
        await download.Stream.ReadExactlyAsync(prefix);
        Assert.Equal("PK\x03\x04"u8.ToArray(), prefix);
        await download.Stream.DisposeAsync();
        await download.Stream.DisposeAsync();
        await service.CreateAsync();
        Assert.False(File.Exists(path));
        Assert.Single(await service.ListAsync());
    }

    [Fact]
    public async Task SameInodeSameLengthEditsAreChangedWithoutReadingEveryArchivePayloadDuringListing()
    {
        using var service = Service();
        var artifact = await service.CreateAsync();
        var path = Path.Combine(_options.BackupDirectory, artifact.FileName);
        var files = new PhysicalManagedFileOperations();
        Assert.True(files.TryGetFileIdentity(path, out var before));
        var length = new FileInfo(path).Length;
        var modified = File.GetLastWriteTimeUtc(path).AddSeconds(1);
        await using (var output = new FileStream(path, FileMode.Open, FileAccess.Write))
        { output.Position = 10; await output.WriteAsync(new byte[] { 127 }); }
        File.SetLastWriteTimeUtc(path, modified);
        Assert.True(files.TryGetFileIdentity(path, out var after));
        Assert.Equal(before.DeviceId, after.DeviceId);
        Assert.Equal(before.FileId.Split(':')[0], after.FileId.Split(':')[0]);
        Assert.Equal(length, new FileInfo(path).Length);
        Assert.Equal("changed", Assert.Single(await service.ListAsync()).Verification);
        await Assert.ThrowsAsync<BackupVerificationException>(() => service.OpenVerifiedAsync(artifact.Id));
    }

    [Fact]
    public async Task ValidReplacementWithTheSameManifestIdIsProtectedAndCannotDownloadAsTheOwnedPublication()
    {
        _options.BackupRetentionCount = 1;
        using var service = Service();
        var original = await service.CreateAsync();
        var path = Path.Combine(_options.BackupDirectory, original.FileName);
        var preserved = path + ".operator";
        File.Move(path, preserved);
        File.Copy(preserved, path);
        Assert.Equal(await DurableBackupService.HashAsync(path, default), original.Sha256);
        Assert.Equal("changed", Assert.Single(await service.ListAsync()).Verification);
        await Assert.ThrowsAsync<BackupVerificationException>(() => service.OpenVerifiedAsync(original.Id));
        await service.CreateAsync();
        Assert.True(File.Exists(path));
        Assert.True(File.Exists(preserved));
    }

    [Fact]
    public async Task RestartRetentionPreservesUnregisteredCorruptUnknownAndLinkedArchives()
    {
        _options.BackupRetentionCount = 1;
        using var initial = Service();
        var original = await initial.CreateAsync();
        var legacy = await LegacyCopyAsync(original);
        var unknown = Path.Combine(_options.BackupDirectory, "operator.zip");
        await File.WriteAllTextAsync(unknown, "operator-owned");
        var target = Path.Combine(_options.DataDirectory, "backend-original.zip");
        File.Copy(Path.Combine(_options.BackupDirectory, original.FileName), target);
        var targetHash = await DurableBackupService.HashAsync(target, default);
        var link = Path.Combine(_options.BackupDirectory, "allstarr-backup-linked.zip");
        File.CreateSymbolicLink(link, target);
        await RewriteEntryAsync(original, "allstarr.db", Encoding.UTF8.GetBytes("corrupt"));
        using var restarted = Service();
        await restarted.CreateAsync();
        Assert.True(File.Exists(Path.Combine(_options.BackupDirectory, original.FileName)));
        Assert.True(File.Exists(legacy));
        Assert.True(File.Exists(unknown));
        Assert.NotNull(new FileInfo(link).LinkTarget);
        Assert.Equal(targetHash, await DurableBackupService.HashAsync(target, default));
        var old = Assert.Single(await restarted.ListAsync(), item => item.FileName == Path.GetFileName(legacy));
        Assert.Equal("legacy", old.Origin);
        Assert.Equal("unverified", old.Verification);
        Assert.Empty(old.RetentionBuckets);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.False(await db.BackupArtifacts.AnyAsync(item => item.Id == old.Id));
    }

    [Fact]
    public async Task ReplacedRootIsRejectedByHeldAndRestartedDirectoryProofWithoutTouchingEitherFolder()
    {
        using var initial = Service();
        var artifact = await initial.CreateAsync();
        var original = _options.BackupDirectory;
        var moved = original + ".preserved";
        Directory.Move(original, moved);
        Directory.CreateDirectory(original);
        var unknown = Path.Combine(original, "operator.txt");
        await File.WriteAllTextAsync(unknown, "keep");
        await Assert.ThrowsAsync<BackupDirectoryException>(() => initial.CreateAsync());
        using var restarted = Service();
        await Assert.ThrowsAsync<BackupDirectoryException>(() => restarted.CreateAsync());
        Assert.True(File.Exists(Path.Combine(moved, artifact.FileName)));
        Assert.Equal("keep", await File.ReadAllTextAsync(unknown));
        Assert.Empty(Directory.GetFiles(original, "*.zip"));
    }

    [Fact]
    public async Task RestoreReservationDoesNotAdoptOrPruneArchivesPublishedAfterTheSnapshot()
    {
        _options.BackupRetentionCount = 1;
        using var service = Service();
        var restore = await service.CreateAsync();
        var upload = await File.ReadAllBytesAsync(Path.Combine(_options.BackupDirectory, restore.FileName));
        var newer = await service.CreateAsync();
        var newerPath = Path.Combine(_options.BackupDirectory, newer.FileName);
        var newerHash = await DurableBackupService.HashAsync(newerPath, default);
        await using (var input = new MemoryStream(upload)) await service.StageRestoreAsync(input);
        await RestartAsync();
        using var restored = Service();
        Assert.Equal("legacy", Assert.Single(await restored.ListAsync(), item => item.Id == newer.Id).Origin);
        await restored.CreateAsync();
        Assert.True(File.Exists(newerPath));
        Assert.Equal(newerHash, await DurableBackupService.HashAsync(newerPath, default));
        await using var db = await _factory.CreateDbContextAsync();
        Assert.False(await db.BackupArtifacts.AnyAsync(item => item.Id == newer.Id));
    }

    [Fact]
    public async Task AnUnclaimedPublicationIsProtectedOnRetryAndNeverCountedAsSuccess()
    {
        var physical = new MoveFailureFiles(fail: true);
        var (service, queue, _) = ScheduledService(physical);
        using (service)
        {
            await EnableAdministratorAsync();
            await service.EnqueueAsync(BackupActor());
            var claim = (await queue.ClaimNextAsync("backup-fixture", [DurableBackupService.JobType]))!;
            using var services = new ServiceCollection().BuildServiceProvider();
            var execution = new DurableJobExecutionContext(claim, services);
            var handler = new DurableBackupJobHandler(service);
            Assert.Equal(DurableJobCompletionKind.Retry, (await handler.ExecuteAsync(execution, default)).Kind);
            var retry = await handler.ExecuteAsync(execution, default);
            Assert.Equal(DurableJobCompletionKind.Failed, retry.Kind);
            Assert.Equal("backup_unclaimed", retry.ErrorCode);
            var orphan = Assert.Single(await service.ListAsync());
            Assert.Equal("legacy", orphan.Origin);
            Assert.Empty(orphan.RetentionBuckets);
            Assert.Null((await service.GetStatusAsync()).LastSuccessAt);
            var path = Path.Combine(_options.BackupDirectory, orphan.FileName);
            await service.CreateAsync();
            Assert.True(File.Exists(path));
            Assert.Equal(2, Directory.GetFiles(_options.BackupDirectory, "*.zip").Length);
        }
    }

    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("execution")]
    public async Task OutdatedFailureCannotOverwriteANewerArtifactOrExecution(string newer)
    {
        var files = new MoveFailureFiles(fail: true);
        var (service, queue, clock) = ScheduledService(files);
        using (service)
        {
            await EnableAdministratorAsync();
            var queued = await service.EnqueueAsync(BackupActor());
            var claim = (await queue.ClaimNextAsync("backup-fixture", [DurableBackupService.JobType]))!;
            BackupArtifactRecord? expected = null;
            files.AfterMove = () =>
            {
                using var db = _factory.CreateDbContext();
                var row = db.BackupArtifacts.Single(item => item.JobId == queued.JobId);
                if (newer == "execution")
                    db.Jobs.Where(item => item.Id == claim.JobId).ExecuteUpdate(update => update
                        .SetProperty(item => item.AttemptCount, claim.AttemptNumber + 1));
                else
                {
                    row.Revision++;
                    row.VerifiedAt = newer == "success" ? clock.UtcNow : null;
                    row.LastFailureCode = newer == "failure" ? "backup_verification_failed" : null;
                    row.LastFailureMessage = newer == "failure" ? "Newer verification failed." : null;
                    row.LastFailureAt = newer == "failure" ? clock.UtcNow : null;
                    db.SaveChanges();
                }
                expected = row;
            };
            using var services = new ServiceCollection().BuildServiceProvider();
            Assert.Equal(DurableJobCompletionKind.Cancelled,
                (await new DurableBackupJobHandler(service).ExecuteAsync(new(claim, services), default)).Kind);
            Assert.NotNull(expected);
            await using var current = await _factory.CreateDbContextAsync();
            var actual = await current.BackupArtifacts.AsNoTracking().SingleAsync(item => item.JobId == queued.JobId);
            Assert.Equal(expected.Revision, actual.Revision);
            Assert.Equal(expected.VerifiedAt, actual.VerifiedAt);
            Assert.Equal(expected.LastFailureCode, actual.LastFailureCode);
            Assert.Equal(expected.LastFailureMessage, actual.LastFailureMessage);
            Assert.Equal(expected.LastFailureAt, actual.LastFailureAt);
        }
    }

    [Fact]
    public async Task ReplacementAfterChecksumVerificationBeforeUnlinkPreservesTheFileAndOwnershipRow()
    {
        _options.BackupRetentionCount = 1;
        var files = new MoveFailureFiles();
        using var service = new DurableBackupService(_factory, _options, _state, _secrets, fileOperations: files);
        var first = await service.CreateAsync();
        var path = Path.Combine(_options.BackupDirectory, first.FileName);
        var checks = 0;
        files.BeforePathIdentity = current =>
        {
            if (current != path || ++checks != 4) return;
            File.Move(path, path + ".preserved");
            File.Copy(path + ".preserved", path);
        };
        await service.CreateAsync();
        Assert.Equal(4, checks);
        Assert.True(File.Exists(path));
        Assert.True(File.Exists(path + ".preserved"));
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Null((await db.BackupArtifacts.SingleAsync(item => item.Id == first.Id)).RemovedAt);
    }

    [Fact]
    public async Task OldStorageBindingsStayProtectedAndDoNotConsumeCurrentRetentionBuckets()
    {
        _options.BackupRetentionCount = 1;
        using var service = Service();
        var previous = await service.CreateAsync();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.Add(new ManagedStorageIdentityRecord
            {
                Id = 1,
                Generation = Guid.NewGuid(),
                MarkerDeviceId = "fixture",
                MarkerFileId = "fixture",
                Revision = 1,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }
        var protectedArchive = Assert.Single(await service.ListAsync());
        Assert.Equal("manual", protectedArchive.Origin);
        Assert.Empty(protectedArchive.RetentionBuckets);
        await service.CreateAsync();
        Assert.True(File.Exists(Path.Combine(_options.BackupDirectory, previous.FileName)));
        Assert.Empty(Assert.Single(await service.ListAsync(), item => item.Id == previous.Id).RetentionBuckets);
    }

    [Fact]
    public async Task UnavailableDatabaseStatusReturnsOnlyASafeRecoverableError()
    {
        var controller = Controller(true);
        _state.Set(DurableStorageReadiness.Unavailable);
        await using var connection = new AllstarrDbContext(_database.Options);
        await connection.Database.ExecuteSqlRawAsync("DROP TABLE backup_artifacts");
        var result = Assert.IsAssignableFrom<ObjectResult>(await controller.Get());
        Assert.Equal(503, result.StatusCode);
        var text = System.Text.Json.JsonSerializer.Serialize(result.Value);
        Assert.DoesNotContain(_options.DataDirectory, text);
        Assert.DoesNotContain("SQLite", text);
        Assert.Contains("backup_unavailable", text);
    }

    private async Task<string> LegacyCopyAsync(BackupArtifact original)
    {
        var id = Guid.NewGuid();
        var path = Path.Combine(_options.BackupDirectory, $"allstarr-backup-{original.CreatedAt.UtcDateTime:yyyyMMdd-HHmmss}-{id:N}.zip");
        File.Copy(Path.Combine(_options.BackupDirectory, original.FileName), path);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Update);
        var entry = archive.GetEntry("manifest.json")!;
        JsonNode value;
        using (var reader = new StreamReader(entry.Open())) value = JsonNode.Parse(await reader.ReadToEndAsync())!;
        entry.Delete();
        value["id"] = id;
        await using var output = archive.CreateEntry("manifest.json").Open();
        await output.WriteAsync(Encoding.UTF8.GetBytes(value.ToJsonString()));
        return path;
    }
    private sealed class MoveFailureFiles(bool fail = false) : IManagedFileOperations
    {
        private readonly PhysicalManagedFileOperations physical = new();
        public Action<string>? BeforePathIdentity { get; set; }
        public Action? AfterMove { get; set; }
        public bool TryOpenDirectory(string path, out Microsoft.Win32.SafeHandles.SafeFileHandle handle, out ManagedFileSystemIdentity identity) => physical.TryOpenDirectory(path, out handle, out identity);
        public bool TryGetFileIdentity(string path, out ManagedFileSystemIdentity identity)
        { BeforePathIdentity?.Invoke(path); return physical.TryGetFileIdentity(path, out identity); }
        public bool TryGetFileIdentity(Stream stream, out ManagedFileSystemIdentity identity) => physical.TryGetFileIdentity(stream, out identity);
        public bool TryCreateHardLink(string target, string source) => physical.TryCreateHardLink(target, source);
        public bool TryCreateReflink(string target, string source) => physical.TryCreateReflink(target, source);
        public Task CopyAsync(string source, string target, CancellationToken token) => physical.CopyAsync(source, target, token);
        public void MoveNoReplace(string source, string target)
        {
            physical.MoveNoReplace(source, target);
            AfterMove?.Invoke();
            if (fail) { fail = false; throw new IOException("fixture publication interruption"); }
        }
    }
}
