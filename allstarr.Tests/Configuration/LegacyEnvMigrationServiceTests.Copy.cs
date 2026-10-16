using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Jobs;
using allstarr.Core.ManagedFiles;
using Microsoft.Extensions.DependencyInjection;
using allstarr.Core.Secrets;
using allstarr.Core.Storage;
using allstarr.Services.Admin;
using allstarr.Core.Settings;
using allstarr.Core.Operations;
using Microsoft.Extensions.Configuration;
using allstarr.Controllers;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Hosting;
using Moq;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed partial class LegacyEnvMigrationServiceTests
{
    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task CopyingTheWholeDataFolderPreservesOriginalPlaybackAndEncryptedAccountRevisions()
    {
        var data = _database.StorageOptions.DataDirectory;
        using var media = MediaFixture(data);
        var path = media.File("kept/Artist/Album/Song [deezer-123].flac");
        await QueueMediaAsync(media, "STORAGE_MODE=Permanent\nDEEZER_ARL=private-copy-fixture\nSCROBBLING_LISTENBRAINZ_USER_TOKEN=private-personal-fixture");
        var import = (await media.Queue.ClaimNextAsync("copy-import"))!;
        var imported = await media.Import.ExecuteAsync(new(import, media.Services), default);
        Assert.Equal(DurableJobCompletionKind.Succeeded, imported.Kind);
        await media.Queue.CompleteAsync(import, imported);
        System.IO.File.Copy(_keyRingPath, Path.Combine(data, "keyring.json"));
        var secrets = CopySecrets(data);
        Dictionary<Guid, long> revisions;
        string oldFileIdentity;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var shared = await db.ProviderAccounts.SingleAsync(item => item.ProviderId == "deezer");
            await using var transaction = await db.Database.BeginTransactionAsync();
            await secrets.StoreWithinTransactionAsync(db, null, $"provider-account:deezer:{shared.Id:N}",
                JsonSerializer.SerializeToUtf8Bytes(new { arl = "rotated-private-copy-fixture" }), shared.SecretReferenceId);
            shared.Revision++;
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            revisions = await db.ProviderAccounts.ToDictionaryAsync(item => item.Id, item => item.Revision);
            oldFileIdentity = (await db.ManagedFiles.SingleAsync()).FileSystemFileId!;
        }
        var actor = MediaActor(_userId, "jellyfin-user-id");
        Assert.NotNull(await media.Reader().FindBySourceAsync(actor, new("deezer", ProviderResourceKind.Track, "123")));
        await InitializeCopiedStorageAsync(media);
        var bytes = await System.IO.File.ReadAllBytesAsync(path);
        var backup = ReplaceWithCopiedDataFolder(data);
        try
        {
            await InitializeCopiedStorageAsync(media);
            await CompleteCopiedStorageAsync(media);
            var read = await media.Reader().OpenForPlaybackAsync(actor, new("deezer", ProviderResourceKind.Track, "123"), false);
            Assert.NotNull(read?.Content);
            using (var stream = read!.Content!)
            using (var output = new MemoryStream())
            {
                await stream.CopyToAsync(output);
                Assert.Equal(bytes, output.ToArray());
            }
            await using var db = await _factory.CreateDbContextAsync();
            var file = await db.ManagedFiles.SingleAsync();
            Assert.NotEqual(oldFileIdentity, file.FileSystemFileId);
            Assert.Equal(path, file.CanonicalPath);
            Assert.Equal(bytes, await System.IO.File.ReadAllBytesAsync(path));
            Assert.Single(await db.ManagedFileReferences.ToArrayAsync());
            Assert.Single(await db.Set<allstarr.Core.ManagedFiles.LegacyMediaAdoptionRecord>().ToArrayAsync());
            Assert.Equal(revisions.OrderBy(item => item.Key), (await db.ProviderAccounts.ToDictionaryAsync(item => item.Id, item => item.Revision)).OrderBy(item => item.Key));
            Assert.Equal(3, await db.SecretVersions.CountAsync());
            foreach (var account in await db.ProviderAccounts.ToArrayAsync())
            {
                var purpose = $"provider-account:{account.ProviderId}:{account.Id:N}";
                using var lease = await CopySecrets(data).OpenAsync(account.SecretReferenceId!.Value,
                    new(account.OwnerUserId, purpose, account.OwnerUserId == null));
                Assert.Contains("private", lease.ReadUtf8(), StringComparison.Ordinal);
                if (account.OwnerUserId != null)
                    await Assert.ThrowsAsync<UnauthorizedAccessException>(() => CopySecrets(data).OpenAsync(account.SecretReferenceId.Value, new(null, purpose, true)));
            }
        }
        finally { Directory.Delete(backup, true); }
    }

    [Theory]
    [InlineData("bytes")]
    [InlineData("file-link")]
    [InlineData("parent-link")]
    [InlineData("missing")]
    [Trait("Lane", "ReleaseCritical")]
    public async Task CopiedOriginalRecoveryRejectsChangedOrUnsafeFilesAndKeepsOtherOriginalsAvailable(string change)
    {
        var data = _database.StorageOptions.DataDirectory;
        using var media = MediaFixture(data);
        var bad = media.File("kept/Artist/Album/Song [deezer-123].flac");
        var good = media.File("kept/Other/Album/Song [qobuz-456].flac");
        await InitializeCopiedStorageAsync(media);
        await QueueMediaAsync(media);
        var import = (await media.Queue.ClaimNextAsync("unsafe-copy-import"))!;
        await media.Queue.CompleteAsync(import, await media.Import.ExecuteAsync(new(import, media.Services), default));
        string? before;
        await using (var db = await _factory.CreateDbContextAsync()) before = (await db.ManagedFiles.SingleAsync(item => item.CanonicalPath == bad)).FileSystemFileId;
        var original = await System.IO.File.ReadAllBytesAsync(bad);
        var backup = ReplaceWithCopiedDataFolder(data);
        try
        {
            var preserved = Path.Combine(backup, Path.GetRelativePath(data, bad));
            if (change == "bytes")
            {
                var changed = original.ToArray();
                changed[0] ^= 1;
                await System.IO.File.WriteAllBytesAsync(bad, changed);
            }
            else if (change == "parent-link")
            {
                var parent = Path.GetDirectoryName(bad)!;
                Directory.Move(parent, parent + ".copied");
                Directory.CreateSymbolicLink(parent, Path.GetDirectoryName(preserved)!);
            }
            else
            {
                System.IO.File.Delete(bad);
                if (change == "file-link") System.IO.File.CreateSymbolicLink(bad, preserved);
            }
            await InitializeCopiedStorageAsync(media);
            var claim = (await media.Queue.ClaimNextAsync("unsafe-copy", [ManagedStorageRecovery.JobType]))!;
            var result = await CopyRecovery(media).ExecuteAsync(new(claim, media.Services), default);
            Assert.Equal(DurableJobCompletionKind.Failed, result.Kind);
            Assert.Equal("storage_files_need_review", result.ErrorCode);
            await media.Queue.CompleteAsync(claim, result);
            var actor = MediaActor(_userId, "jellyfin-user-id");
            Assert.Null(await media.Reader().OpenForPlaybackAsync(actor, new("deezer", ProviderResourceKind.Track, "123"), false));
            var available = await media.Reader().OpenForPlaybackAsync(actor, new("qobuz", ProviderResourceKind.Track, "456"), false);
            Assert.NotNull(available?.Content);
            available!.Content!.Dispose();
            Assert.Equal(original, await System.IO.File.ReadAllBytesAsync(preserved));
            await using (var db = await _factory.CreateDbContextAsync())
            {
                Assert.Equal(before, (await db.ManagedFiles.SingleAsync(item => item.CanonicalPath == bad)).FileSystemFileId);
                Assert.Equal(2, await db.ManagedFileReferences.CountAsync(item => item.ReleasedAt == null));
                Assert.Equal(2, await db.Set<LegacyMediaAdoptionRecord>().CountAsync());
            }
            Assert.True((await CopyRecovery(media).GetStatusAsync(CopyAdministrator(), default)).NeedsReview);
            if (change == "bytes")
            {
                await System.IO.File.WriteAllBytesAsync(bad, original);
                var recovery = CopyRecovery(media);
                var repair = await recovery.RequestVerificationAsync(CopyAdministrator());
                Assert.NotNull(repair);
                Assert.Equal(repair, await recovery.RequestVerificationAsync(CopyAdministrator()));
                await CompleteCopiedStorageAsync(media);
                var recovered = await media.Reader().OpenForPlaybackAsync(actor, new("deezer", ProviderResourceKind.Track, "123"), false);
                Assert.NotNull(recovered?.Content);
                recovered!.Content!.Dispose();
            }
        }
        finally { Directory.Delete(backup, true); }
    }

    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task CopiedOriginalRecoveryResumesAfterInterruptionWithoutRepeatingProofOrLosingOriginalReferences()
    {
        var data = _database.StorageOptions.DataDirectory;
        using var media = MediaFixture(data);
        await InitializeCopiedStorageAsync(media);
        for (var index = 0; index < 3; index++) media.File($"kept/Artist/Album/Song [deezer-{index}].flac");
        await QueueMediaAsync(media);
        var import = (await media.Queue.ClaimNextAsync("resume-copy-import"))!;
        await media.Queue.CompleteAsync(import, await media.Import.ExecuteAsync(new(import, media.Services), default));
        var backup = ReplaceWithCopiedDataFolder(data);
        try
        {
            await InitializeCopiedStorageAsync(media);
            var recovery = CopyRecovery(media);
            var first = (await media.Queue.ClaimNextAsync("interrupted-copy", [ManagedStorageRecovery.JobType]))!;
            using var stop = new CancellationTokenSource();
            var execution = new DurableJobExecutionContext(first, media.Services)
            {
                ReportProgressAsync = (_, _) => { stop.Cancel(); return Task.FromResult(true); }
            };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recovery.ExecuteAsync(execution, stop.Token));
            await using (var db = await _factory.CreateDbContextAsync())
                Assert.Single(await db.AuditEvents.Where(item => item.Action == "file.verify-copy").ToArrayAsync());
            await media.Queue.RecoverInterruptedAsync();
            var next = (await media.Queue.ClaimNextAsync("resumed-copy", [ManagedStorageRecovery.JobType]))!;
            await Assert.ThrowsAsync<DurableJobExecutionChangedException>(() => recovery.ExecuteAsync(new(first, media.Services), default));
            var completed = await recovery.ExecuteAsync(new(next, media.Services), default);
            Assert.Equal(DurableJobCompletionKind.Succeeded, completed.Kind);
            await media.Queue.CompleteAsync(next, completed);
            await using var saved = await _factory.CreateDbContextAsync();
            Assert.Equal(3, await saved.AuditEvents.CountAsync(item => item.Action == "file.verify-copy"));
            Assert.Equal(3, await saved.ManagedFileReferences.CountAsync(item => item.ReleasedAt == null));
            Assert.Equal(3, await saved.Set<LegacyMediaAdoptionRecord>().CountAsync());
        }
        finally { Directory.Delete(backup, true); }
    }

    [Fact]
    public async Task CopiedOriginalRecoveryVisitsEveryFileAcrossMultipleBatchesAndLeavesUnownedFilesUntouched()
    {
        var data = _database.StorageOptions.DataDirectory;
        using var media = MediaFixture(data);
        await InitializeCopiedStorageAsync(media);
        for (var index = 0; index < 105; index++) media.File($"kept/Artist/Album/Song [deezer-{index}].flac");
        await QueueMediaAsync(media);
        var import = (await media.Queue.ClaimNextAsync("batch-copy-import"))!;
        await media.Queue.CompleteAsync(import, await media.Import.ExecuteAsync(new(import, media.Services), default));
        var unowned = Path.Combine(data, "unowned.flac");
        await System.IO.File.WriteAllTextAsync(unowned, "unowned fixture bytes");
        var backup = ReplaceWithCopiedDataFolder(data);
        try
        {
            await InitializeCopiedStorageAsync(media);
            await CompleteCopiedStorageAsync(media);
            await using var db = await _factory.CreateDbContextAsync();
            Assert.Equal(105, await db.AuditEvents.CountAsync(item => item.Action == "file.verify-copy"));
            Assert.Equal(105, await db.ManagedFiles.CountAsync());
            Assert.False(await db.ManagedFiles.AnyAsync(item => item.CanonicalPath == unowned));
            Assert.Equal("unowned fixture bytes", await System.IO.File.ReadAllTextAsync(unowned));
            var identity = await db.Set<ManagedStorageIdentityRecord>().SingleAsync();
            await InitializeCopiedStorageAsync(media);
            Assert.Equal(identity.Revision, (await db.Set<ManagedStorageIdentityRecord>().AsNoTracking().SingleAsync()).Revision);
        }
        finally { Directory.Delete(backup, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Lane", "ReleaseCritical")]
    public async Task CopiedFileHashingKeepsTheWriterAvailableAndRejectsAChangedOwnershipRevision(bool changeFileRevision)
    {
        var data = _database.StorageOptions.DataDirectory;
        var operations = new PausingManagedFileOperations();
        using var media = MediaFixture(data, operations);
        var original = media.File("kept/Artist/Album/Song [deezer-123].flac");
        await InitializeCopiedStorageAsync(media);
        await QueueMediaAsync(media);
        var import = (await media.Queue.ClaimNextAsync("copy-writer-import"))!;
        await media.Queue.CompleteAsync(import, await media.Import.ExecuteAsync(new(import, media.Services), default));
        Guid fileId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            fileId = (await db.ManagedFiles.SingleAsync()).Id;
            await db.ManagedFiles.Where(item => item.Id == fileId).ExecuteUpdateAsync(update => update.SetProperty(item => item.Revision, item => item.Revision + 1));
        }
        var settings = new DurableRuntimeSettingsService(_factory, new ConfigurationBuilder().Build(), new SystemPlatformClock(), new RuntimeSettingsChangeSignal());
        await settings.ApplyBatchAsync([new("Cache:LyricsDays", "14")], "copy-writer", _userId);
        var backup = ReplaceWithCopiedDataFolder(data);
        try
        {
            await InitializeCopiedStorageAsync(media);
            var recovery = CopyRecovery(media);
            var claim = (await media.Queue.ClaimNextAsync("copy-writer", [ManagedStorageRecovery.JobType]))!;
            operations.Arm(original);
            var running = recovery.ExecuteAsync(new(claim, media.Services), default);
            await operations.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            try
            {
                await Task.Run(async () =>
                {
                    if (changeFileRevision)
                    {
                        await using var independent = await _factory.CreateDbContextAsync();
                        await independent.ManagedFiles.Where(item => item.Id == fileId).ExecuteUpdateAsync(update => update.SetProperty(item => item.Revision, item => item.Revision + 1));
                    }
                    else await settings.ApplyBatchAsync([new("Cache:LyricsDays", "15", 1)], "copy-writer", _userId);
                }).WaitAsync(TimeSpan.FromSeconds(3));
            }
            finally { operations.Resume.TrySetResult(); }
            if (changeFileRevision)
            {
                await Assert.ThrowsAsync<DurableJobExecutionChangedException>(() => running);
                await using (var untouched = await _factory.CreateDbContextAsync())
                    Assert.Empty(await untouched.AuditEvents.Where(item => item.Action == "file.verify-copy").ToArrayAsync());
                await media.Queue.RecoverInterruptedAsync();
                claim = (await media.Queue.ClaimNextAsync("copy-writer-resume", [ManagedStorageRecovery.JobType]))!;
                running = recovery.ExecuteAsync(new(claim, media.Services), default);
            }
            var result = await running;
            Assert.Equal(DurableJobCompletionKind.Succeeded, result.Kind);
            await media.Queue.CompleteAsync(claim, result);
            Assert.NotNull(await media.Reader().FindBySourceAsync(MediaActor(_userId, "jellyfin-user-id"), new("deezer", ProviderResourceKind.Track, "123")));
            await using var saved = await _factory.CreateDbContextAsync();
            Assert.Single(await saved.AuditEvents.Where(item => item.Action == "file.verify-copy").ToArrayAsync());
            Assert.Single(await saved.ManagedFileReferences.Where(item => item.ReleasedAt == null).ToArrayAsync());
        }
        finally { operations.Resume.TrySetResult(); Directory.Delete(backup, true); }
    }

    [Fact]
    public async Task StorageVerificationApiRequiresAnActiveAdministratorAndAntiforgeryAndReusesPendingWork()
    {
        var data = _database.StorageOptions.DataDirectory;
        using var media = MediaFixture(data);
        var recovery = CopyRecovery(media);
        await recovery.InitializeAsync();
        media.File("kept/Artist/Song [deezer-123].flac");
        await QueueMediaAsync(media);
        var import = (await media.Queue.ClaimNextAsync("copy-api-import"))!;
        await media.Queue.CompleteAsync(import, await media.Import.ExecuteAsync(new(import, media.Services), default));
        var antiforgery = new Mock<IAntiforgery>();
        antiforgery.Setup(item => item.GetAndStoreTokens(It.IsAny<HttpContext>()))
            .Returns(new AntiforgeryTokenSet("copy-csrf", "copy-cookie", "field", "X-Allstarr-CSRF"));
        var controller = new ManagedStorageController(recovery, antiforgery.Object)
        { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };
        Assert.Equal(403, Assert.IsType<ObjectResult>(await controller.Status(default)).StatusCode);
        controller.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = CopyAdministrator();
        var status = Assert.IsType<OkObjectResult>(await controller.Status(default));
        Assert.DoesNotContain(data, JsonSerializer.Serialize(status.Value), StringComparison.Ordinal);
        Assert.Equal("no-store", controller.Response.Headers.CacheControl);
        Assert.IsType<BadRequestObjectResult>(await controller.Verify(default));
        antiforgery.Setup(item => item.IsRequestValidAsync(It.IsAny<HttpContext>())).ReturnsAsync(true);
        var first = Assert.IsType<AcceptedResult>(await controller.Verify(default));
        var second = Assert.IsType<AcceptedResult>(await controller.Verify(default));
        Assert.Equal(JsonSerializer.Serialize(first.Value), JsonSerializer.Serialize(second.Value));
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Single(await db.Jobs.Where(item => item.Type == ManagedStorageRecovery.JobType).ToArrayAsync());
        await db.Users.Where(item => item.Id == _userId).ExecuteUpdateAsync(update => update.SetProperty(item => item.IsAdmin, false));
        Assert.Equal(403, Assert.IsType<ObjectResult>(await controller.Verify(default)).StatusCode);
        Assert.Equal(403, Assert.IsType<ObjectResult>(await controller.Status(default)).StatusCode);
    }

    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task OrdinaryDurableWorkWaitsForCopiedFileVerificationBeforeReadingOwnedAudio()
    {
        var data = _database.StorageOptions.DataDirectory;
        using var media = MediaFixture(data);
        var recovery = CopyRecovery(media);
        await recovery.InitializeAsync();
        media.File("kept/Artist/Song [deezer-123].flac");
        await QueueMediaAsync(media);
        var import = (await media.Queue.ClaimNextAsync("copy-gate-import"))!;
        await media.Queue.CompleteAsync(import, await media.Import.ExecuteAsync(new(import, media.Services), default));
        var ordinary = await media.Queue.EnqueueAsync(new DurableJobEnqueueRequest<object>("fixture.after-copy", "copy-gate-ordinary", new { }, _userId, Priority: 1000));
        var backup = ReplaceWithCopiedDataFolder(data);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await recovery.InitializeAsync();
            var state = new DurableStorageState(_database.StorageOptions);
            state.Set(DurableStorageReadiness.Ready);
            IDurableJobHandler[] handlers =
            [
                new StorageCallbackHandler(ManagedStorageRecovery.JobType, async (execution, token) =>
                {
                    entered.TrySetResult();
                    await resume.Task.WaitAsync(token);
                    return await recovery.ExecuteAsync(execution, token);
                }),
                new StorageCallbackHandler("fixture.after-copy", async (_, token) =>
                    await media.Reader().FindBySourceAsync(MediaActor(_userId, "jellyfin-user-id"), new("deezer", ProviderResourceKind.Track, "123"), token) == null
                        ? DurableJobCompletion.Failure("copied_file_unavailable", "Verification must run first.") : DurableJobCompletion.Success())
            ];
            using var worker = new DurableJobWorker(media.Queue, new() { MaximumConcurrentJobs = 2, PollIntervalMilliseconds = 50 },
                state, media.Services, handlers, NullLogger<DurableJobWorker>.Instance, recovery);
            await worker.StartAsync(default);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await using (var db = await _factory.CreateDbContextAsync())
                    Assert.Equal(DurableJobState.Pending, (await db.Jobs.SingleAsync(item => item.Id == ordinary.JobId)).State);
                resume.TrySetResult();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (true)
                {
                    await using var db = await _factory.CreateDbContextAsync(timeout.Token);
                    var job = await db.Jobs.AsNoTracking().SingleAsync(item => item.Id == ordinary.JobId, timeout.Token);
                    if (job.State == DurableJobState.Succeeded) break;
                    Assert.NotEqual(DurableJobState.Failed, job.State);
                    await Task.Delay(50, timeout.Token);
                }
            }
            finally { resume.TrySetResult(); await worker.StopAsync(default); }
        }
        finally { resume.TrySetResult(); Directory.Delete(backup, true); }
    }

    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task CopiedFileProofCannotBeForgedAndPopulatedMigrationRollbackRequiresAnIsolatedRestore()
    {
        var data = _database.StorageOptions.DataDirectory;
        using var media = MediaFixture(data);
        var original = media.File("kept/Artist/Song [deezer-123].flac");
        await InitializeCopiedStorageAsync(media);
        await QueueMediaAsync(media);
        var import = (await media.Queue.ClaimNextAsync("copy-proof-import"))!;
        await media.Queue.CompleteAsync(import, await media.Import.ExecuteAsync(new(import, media.Services), default));
        var recovery = CopyRecovery(media);
        await recovery.RequestVerificationAsync(CopyAdministrator());
        var claim = (await media.Queue.ClaimNextAsync("copy-proof", [ManagedStorageRecovery.JobType]))!;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var before = await db.ManagedFiles.AsNoTracking().SingleAsync();
            var generation = (await db.Set<ManagedStorageIdentityRecord>().SingleAsync()).Generation;
            db.AuditEvents.Add(new AuditEventRecord
            {
                Id = Guid.CreateVersion7(),
                Category = "managed-storage",
                Action = "file.verify-copy",
                Outcome = "succeeded",
                CorrelationId = $"file-identity:{before.Id:N}:{before.Revision}",
                CreatedAt = DateTimeOffset.UtcNow,
                DetailsJson = JsonSerializer.Serialize(new
                {
                    fileId = before.Id,
                    rootId = before.RootId,
                    scopeKey = before.ScopeKey,
                    sha256 = new string('0', 64),
                    length = before.Length,
                    oldRevision = before.Revision,
                    oldDevice = before.FileSystemDeviceId,
                    oldFile = before.FileSystemFileId,
                    newDevice = before.FileSystemDeviceId,
                    newFile = "unverified-fixture",
                    newLinks = before.FileSystemLinkCount,
                    jobId = claim.JobId,
                    attempt = claim.AttemptNumber,
                    generation
                })
            });
            await db.SaveChangesAsync();
            var denied = await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE managed_files SET FileSystemFileId='unverified-fixture',Revision=Revision+1 WHERE Id={before.Id}"));
            Assert.Contains("CK_legacy_original_file", denied.Message, StringComparison.Ordinal);
            Assert.Equal(before.FileSystemFileId, (await db.ManagedFiles.AsNoTracking().SingleAsync()).FileSystemFileId);
            var downgrade = await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.GetService<IMigrator>().MigrateAsync("20261015225842_AdoptLegacyMedia"));
            Assert.Contains("requires_backup_restore", downgrade.Message, StringComparison.Ordinal);
            Assert.Single(await db.Set<ManagedStorageIdentityRecord>().ToArrayAsync());
            Assert.Single(await db.Set<LegacyMediaAdoptionRecord>().ToArrayAsync());
        }
        await media.Queue.CompleteAsync(claim, DurableJobCompletion.Cancelled());
        System.IO.File.Copy(_keyRingPath, Path.Combine(data, "keyring.json"));
        var state = new DurableStorageState(_database.StorageOptions);
        state.Set(DurableStorageReadiness.Ready);
        var backups = new DurableBackupService(_factory, _database.StorageOptions, state, new() { KeyRingPath = Path.Combine(data, "keyring.json") });
        var backup = await backups.CreateAsync();
        var settings = new DurableRuntimeSettingsService(_factory, new ConfigurationBuilder().Build(), new SystemPlatformClock(), new RuntimeSettingsChangeSignal());
        await settings.ApplyBatchAsync([new("Cache:LyricsDays", "99")], "copy-restore", _userId);
        await using (var upload = System.IO.File.OpenRead(Path.Combine(_database.StorageOptions.BackupDirectory, backup.FileName)))
            await backups.StageRestoreAsync(upload);
        var restarted = new ConfigurationManager { ["Storage:DataDirectory"] = data };
        await allstarr.Core.Configuration.StartupConfiguration.PrepareAsync(restarted, Mock.Of<IHostEnvironment>());
        Assert.Null(restarted["Cache:LyricsDays"]);
        Assert.Equal("original fixture bytes", await System.IO.File.ReadAllTextAsync(original));
        Assert.NotNull(await media.Reader().FindBySourceAsync(MediaActor(_userId, "jellyfin-user-id"), new("deezer", ProviderResourceKind.Track, "123")));
        _database.ClearHostPool();
    }

    private sealed class StorageCallbackHandler(string type, Func<DurableJobExecutionContext, CancellationToken, Task<DurableJobCompletion>> execute) : IDurableJobHandler
    {
        public string JobType => type;
        public Task<DurableJobCompletion> ExecuteAsync(DurableJobExecutionContext context, CancellationToken token) => execute(context, token);
    }

    [Fact]
    public async Task CopyDuringAnInterruptedImportKeepsAdoptedFilesAndRequestsANewPreviewOnlyForUnverifiedOriginals()
    {
        var data = _database.StorageOptions.DataDirectory;
        using var media = MediaFixture(data);
        await InitializeCopiedStorageAsync(media);
        for (var index = 0; index < 3; index++) media.File($"kept/Artist/Song [deezer-{index}].flac");
        await QueueMediaAsync(media);
        var first = (await media.Queue.ClaimNextAsync("interrupted-import-before-copy"))!;
        using var stop = new CancellationTokenSource();
        var execution = new DurableJobExecutionContext(first, media.Services)
        { ReportProgressAsync = (_, _) => { stop.Cancel(); return Task.FromResult(true); } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => media.Import.ExecuteAsync(execution, stop.Token));
        var backup = ReplaceWithCopiedDataFolder(data);
        try
        {
            await media.Queue.RecoverInterruptedAsync();
            await InitializeCopiedStorageAsync(media);
            await CompleteCopiedStorageAsync(media);
            var resumed = (await media.Queue.ClaimNextAsync("interrupted-import-after-copy", [LegacyMediaImport.JobType]))!;
            var result = await media.Import.ExecuteAsync(new(resumed, media.Services), default);
            Assert.Equal("legacy_media_needs_review", result.ErrorCode);
            Assert.StartsWith("2 files", result.SafeMessage, StringComparison.Ordinal);
            await using var db = await _factory.CreateDbContextAsync();
            Assert.Single(await db.ManagedFiles.ToArrayAsync());
            Assert.Single(await db.ManagedFileReferences.Where(item => item.ReleasedAt == null).ToArrayAsync());
            Assert.Equal(3, Directory.GetFiles(Path.Combine(data, "kept"), "*.flac", SearchOption.AllDirectories).Length);
        }
        finally { Directory.Delete(backup, true); }
    }

    private AdminAuthSession CopyAdministrator() => new()
    {
        SessionId = "copy-administrator",
        AllstarrUserId = _userId,
        UserId = "jellyfin-user-id",
        UserName = "Administrator",
        IsAdministrator = true,
        BackendType = "Jellyfin",
        BackendInstanceId = "primary",
        JellyfinAccessToken = "",
        ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
    };

    private EncryptedSecretStore CopySecrets(string data)
    {
        var options = new SecretStoreOptions { KeyRingPath = Path.Combine(data, "keyring.json") };
        return new(_factory, new FileSecretKeyRingProvider(options), options, new allstarr.Core.Operations.SystemPlatformClock());
    }

    private static string ReplaceWithCopiedDataFolder(string data) => CopiedDataFolderFixture.ReplaceWithCopy(data);

    private ManagedStorageRecovery CopyRecovery(MediaImportFixture media)
    {
        var state = new DurableStorageState(_database.StorageOptions);
        state.Set(DurableStorageReadiness.Ready);
        return new(_factory, media.Services.GetRequiredService<IServiceScopeFactory>(), media.Queue,
            _database.StorageOptions, state, new allstarr.Core.Operations.SystemPlatformClock());
    }
    private Task InitializeCopiedStorageAsync(MediaImportFixture media) => CopyRecovery(media).InitializeAsync();
    private async Task CompleteCopiedStorageAsync(MediaImportFixture media)
    {
        var completed = false;
        while (await media.Queue.ClaimNextAsync("copied-storage", [ManagedStorageRecovery.JobType], default) is { } claim)
        {
            var result = await CopyRecovery(media).ExecuteAsync(new(claim, media.Services), default);
            Assert.Contains(result.Kind, new[] { DurableJobCompletionKind.Succeeded, DurableJobCompletionKind.Cancelled });
            completed |= result.Kind == DurableJobCompletionKind.Succeeded;
            await media.Queue.CompleteAsync(claim, result);
        }
        Assert.True(completed);
    }
}
