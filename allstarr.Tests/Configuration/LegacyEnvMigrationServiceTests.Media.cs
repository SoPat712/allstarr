using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Configuration;
using allstarr.Core.Downloads;
using allstarr.Core.Jobs;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Matching;
using allstarr.Core.Operations;
using allstarr.Core.Secrets;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using allstarr.Core.Protocols;
using allstarr.Controllers;
using allstarr.Services.Lyrics;
using Moq;

namespace allstarr.Tests;

public sealed partial class LegacyEnvMigrationServiceTests
{
    [Fact]
    public async Task Media_PreviewIsReadOnlyApplyQueuesAndReplayNeverDuplicatesOriginals()
    {
        using var media = MediaFixture();
        var known = media.File("kept/Artist/Album/01 - Song [deezer-123].flac");
        var unknown = media.File("downloads/Unidentified.mp3");
        var modified = File.GetLastWriteTimeUtc(known);
        var source = Source("STORAGE_MODE=Permanent\nREDIS_CONNECTION_STRING=unused.invalid:6379");
        var migration = CreateService(mediaInventory: media.Inventory, mediaImport: media.Import);
        var preview = await migration.PreviewAsync(source, Actor());
        Assert.Equal(2, preview.Media!.Files);
        Assert.Equal(1, preview.Media.Identified);
        Assert.Equal(1, preview.Media.NeedsIdentification);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Empty(await db.ManagedFiles.ToListAsync());
            Assert.Empty(await db.LegacyEnvImports.ToListAsync());
            Assert.Empty(await db.Jobs.ToListAsync());
        }
        var applied = await migration.ApplyAsync(preview.PreviewToken, preview.Revision, true, Actor());
        Assert.Equal(2, applied.MediaFilesQueued);
        Assert.NotNull(applied.MediaJobId);
        await using (var db = await _factory.CreateDbContextAsync()) Assert.Empty(await db.ManagedFiles.ToListAsync());
        var claim = (await media.Queue.ClaimNextAsync("media-fixture"))!;
        var result = await media.Import.ExecuteAsync(new(claim, media.Services), default);
        Assert.Equal(DurableJobCompletionKind.Succeeded, result.Kind);
        await media.Queue.CompleteAsync(claim, result);
        var restarted = CreateService(mediaInventory: media.Inventory, mediaImport: media.Import);
        preview = await restarted.PreviewAsync(source, Actor());
        var replay = await restarted.ApplyAsync(preview.PreviewToken, preview.Revision, true, Actor());
        Assert.True(replay.AlreadyApplied);
        Assert.Equal(applied.MediaJobId, replay.MediaJobId);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(2, await db.ManagedFiles.CountAsync());
            var rows = await db.Set<LegacyMediaAdoptionRecord>().ToArrayAsync();
            Assert.Equal(2, rows.Length);
            Assert.Single(rows, row => row.CanonicalRecordingId != null);
            Assert.Equal(2, await db.ManagedFileReferences.CountAsync());
            Assert.All(await db.ManagedFiles.ToArrayAsync(), file =>
            {
                Assert.Equal(ManagedFilePlacementMethod.Adopted, file.PlacementMethod);
                Assert.Null(file.SourceJobId);
                Assert.Null(file.OwnerUserId);
                Assert.Equal(1, file.ReferenceCount);
            });
            Assert.Single(await db.Jobs.ToListAsync());
            Assert.Empty(await db.ProviderAccounts.ToListAsync());
        }
        Assert.Equal("original fixture bytes", await System.IO.File.ReadAllTextAsync(known));
        Assert.Equal("original fixture bytes", await System.IO.File.ReadAllTextAsync(unknown));
        Assert.Equal(modified, System.IO.File.GetLastWriteTimeUtc(known));
        Assert.False(System.IO.File.Exists(Path.ChangeExtension(known, ".lrc")));
        var reader = media.Reader();
        var actor = MediaActor(_userId, "jellyfin-user-id");
        var read = await reader.OpenForPlaybackAsync(actor, new("deezer", ProviderResourceKind.Track, "123"), false);
        Assert.NotNull(read?.Content);
        using (var stream = read!.Content!) using (var text = new StreamReader(stream))
            Assert.Equal("original fixture bytes", await text.ReadToEndAsync());
        Assert.Equal(known, (await reader.FindForLyricsAsync(actor, "ext-deezer-123", default))!.Path);
        Assert.Null(await reader.FindAsync(new(ProviderActorKind.PublicRead, null), read.File.RecordingId));
        Assert.Equal("Song", (await reader.FindSongAsync(actor, "ext-deezer-song-123"))!.Title);
    }

    [Fact]
    public async Task Media_InterruptedJobResumesWithOneOwnershipAndReferencePerOriginal()
    {
        using var media = MediaFixture();
        media.File("kept/Artist/Album/A [deezer-123].flac");
        media.File("kept/Artist/Album/B [qobuz-456].flac");
        await QueueMediaAsync(media);
        var old = (await media.Queue.ClaimNextAsync("interrupted"))!;
        using var stop = new CancellationTokenSource();
        var execution = new DurableJobExecutionContext(old, media.Services)
        {
            ReportProgressAsync = (_, _) => { stop.Cancel(); return Task.FromResult(true); }
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => media.Import.ExecuteAsync(execution, stop.Token));
        await using (var db = await _factory.CreateDbContextAsync()) Assert.Single(await db.ManagedFiles.ToArrayAsync());
        await media.Queue.RecoverInterruptedAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => media.Import.ExecuteAsync(new(old, media.Services), default));
        var next = (await media.Queue.ClaimNextAsync("restarted"))!;
        var restarted = new LegacyMediaImport(_factory, media.Services.GetRequiredService<IServiceScopeFactory>(), media.Queue,
            media.Identities, new SystemPlatformClock(), media.Inventory);
        Assert.Equal(DurableJobCompletionKind.Succeeded, (await restarted.ExecuteAsync(new(next, media.Services), default)).Kind);
        await using var saved = await _factory.CreateDbContextAsync();
        Assert.Equal(2, await saved.ManagedFiles.CountAsync());
        Assert.Equal(2, await saved.ManagedFileReferences.CountAsync());
        Assert.Equal(2, await saved.Set<LegacyMediaAdoptionRecord>().CountAsync());
        Assert.Equal(2, await saved.CanonicalRecordings.CountAsync());
    }

    [Fact]
    public async Task Media_ChangedOriginalCannotBeAdoptedFromAnOlderPreview()
    {
        using var media = MediaFixture();
        var path = media.File("kept/Artist/Album/Song [deezer-123].flac");
        var migration = CreateService(mediaInventory: media.Inventory, mediaImport: media.Import);
        var source = Source("STORAGE_MODE=Permanent");
        var preview = await migration.PreviewAsync(source, Actor());
        var priorFingerprint = preview.SourceSha256;
        await migration.ApplyAsync(preview.PreviewToken, preview.Revision, true, Actor());
        System.IO.File.Move(path, path + ".original");
        await System.IO.File.WriteAllTextAsync(path, "replacement fixture bytes");
        var claim = (await media.Queue.ClaimNextAsync("changed"))!;
        var result = await media.Import.ExecuteAsync(new(claim, media.Services), default);
        Assert.Equal("legacy_media_needs_review", result.ErrorCode);
        await using (var db = await _factory.CreateDbContextAsync()) Assert.Empty(await db.ManagedFiles.ToListAsync());
        Assert.Equal("replacement fixture bytes", await System.IO.File.ReadAllTextAsync(path));
        Assert.Equal("original fixture bytes", await System.IO.File.ReadAllTextAsync(path + ".original"));
        preview = await migration.PreviewAsync(source, Actor());
        Assert.NotEqual(priorFingerprint, preview.SourceSha256);
    }

    [Theory]
    [Trait("Lane", "ReleaseCritical")]
    [InlineData("path")]
    [InlineData("removed")]
    [InlineData("reference")]
    [InlineData("proof-update")]
    [InlineData("proof-delete")]
    public async Task Media_OriginalOwnershipAndPermanentReferenceCannotBeRetargetedOrDeleted(string change)
    {
        using var media = MediaFixture();
        var path = media.File("kept/Artist/Album/Song [deezer-123].flac");
        await QueueMediaAsync(media);
        var claim = (await media.Queue.ClaimNextAsync("protect"))!;
        Assert.Equal(DurableJobCompletionKind.Succeeded, (await media.Import.ExecuteAsync(new(claim, media.Services), default)).Kind);
        await using var db = await _factory.CreateDbContextAsync();
        var file = await db.ManagedFiles.SingleAsync();
        var sql = change switch
        {
            "path" => "UPDATE managed_files SET CanonicalPath=CanonicalPath || '.other'",
            "removed" => "UPDATE managed_files SET RemovedAt=1",
            "reference" => "UPDATE managed_file_references SET ReleasedAt=1",
            "proof-update" => "UPDATE legacy_media_adoptions SET MetadataJson='changed'",
            _ => "DELETE FROM legacy_media_adoptions"
        };
        var error = await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlRawAsync(sql));
        Assert.Contains("CK_legacy", error.Message);
        using var scope = media.Services.CreateScope();
        await Assert.ThrowsAnyAsync<Exception>(() => scope.ServiceProvider.GetRequiredService<ManagedFileRemovalService>()
            .RemoveAsync(file.Id, file.ScopeKey, true));
        Assert.Equal("original fixture bytes", await System.IO.File.ReadAllTextAsync(path));
        Assert.Equal(1, await db.ManagedFiles.Select(item => item.ReferenceCount).SingleAsync());
    }

    [Fact]
    public async Task Media_BackupRestoreAndRestartPreserveOriginalProofAndEncryptedAccountRevision()
    {
        using var media = MediaFixture();
        var path = media.File("kept/Artist/Album/Song [deezer-123].flac");
        await QueueMediaAsync(media, "STORAGE_MODE=Permanent\nDEEZER_ARL=fixture-private-arl");
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var account = await db.ProviderAccounts.SingleAsync();
            account.Revision = 8;
            account.Enabled = false;
            await db.SaveChangesAsync();
        }
        var claim = (await media.Queue.ClaimNextAsync("backup"))!;
        var result = await media.Import.ExecuteAsync(new(claim, media.Services), default);
        Assert.Equal(DurableJobCompletionKind.Succeeded, result.Kind);
        await media.Queue.CompleteAsync(claim, result);
        var state = new DurableStorageState(_database.StorageOptions);
        state.Set(DurableStorageReadiness.Ready);
        var keys = new SecretStoreOptions { KeyRingPath = _keyRingPath };
        var backupService = new DurableBackupService(_factory, _database.StorageOptions, state, keys);
        var backup = await backupService.CreateAsync();
        await using (var upload = System.IO.File.OpenRead(Path.Combine(_database.StorageOptions.BackupDirectory, backup.FileName)))
            await backupService.StageRestoreAsync(upload);
        state.Set(DurableStorageReadiness.Initializing);
        await new DurableStorageInitializer(_factory, _database.StorageOptions, state,
            NullLogger<DurableStorageInitializer>.Instance, backupService).StartAsync(default);
        Assert.Equal(DurableStorageReadiness.Ready, state.GetSnapshot().Readiness);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Single(await db.Set<LegacyMediaAdoptionRecord>().ToListAsync());
            var account = await db.ProviderAccounts.SingleAsync();
            Assert.Equal(8, account.Revision);
            Assert.False(account.Enabled);
            using var lease = await CreateSecretStore().OpenAsync(account.SecretReferenceId!.Value,
                new SecretAccessContext(null, $"provider-account:deezer:{account.Id:N}", AllowShared: true));
            Assert.Contains("fixture-private-arl", lease.ReadUtf8());
        }
        Assert.Equal(path, (await media.Reader().FindBySourceAsync(MediaActor(_userId, "jellyfin-user-id"),
            new("deezer", ProviderResourceKind.Track, "123")))!.Path);
        Assert.Equal("original fixture bytes", await System.IO.File.ReadAllTextAsync(path));
    }

    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task Media_PlaybackRejectsASymbolicLinkAboveTheRecordedRoot()
    {
        if (OperatingSystem.IsWindows()) return;
        using var media = MediaFixture();
        var path = media.File("kept/Artist/Album/Song [deezer-123].flac");
        await QueueMediaAsync(media);
        var claim = (await media.Queue.ClaimNextAsync("ancestor-link"))!;
        Assert.Equal(DurableJobCompletionKind.Succeeded, (await media.Import.ExecuteAsync(new(claim, media.Services), default)).Kind);
        Directory.Move(media.Root, media.Root + ".original");
        Directory.CreateSymbolicLink(media.Root, media.Root + ".original");
        Assert.Null(await media.Reader().OpenForPlaybackAsync(MediaActor(_userId, "jellyfin-user-id"),
            new("deezer", ProviderResourceKind.Track, "123"), false));
        Assert.Equal("original fixture bytes", await System.IO.File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Media_MergesAndAnotherHouseholdListenerKeepReadingTheOriginalAndItsLyrics()
    {
        using var media = MediaFixture();
        var actor = MediaActor(_userId, "jellyfin-user-id");
        var older = await media.Identities.CreateRecordingAsync(actor, "older-recording", null, null, "ext-qobuz-song-789");
        var path = media.File("kept/Artist/Album/Song [deezer-123].flac");
        var sidecar = Path.ChangeExtension(path, ".lrc");
        await System.IO.File.WriteAllTextAsync(sidecar, "[00:01.00]Original lyric\n");
        var sidecarTime = System.IO.File.GetLastWriteTimeUtc(sidecar);
        await QueueMediaAsync(media);
        var claim = (await media.Queue.ClaimNextAsync("household"))!;
        Assert.Equal(DurableJobCompletionKind.Succeeded, (await media.Import.ExecuteAsync(new(claim, media.Services), default)).Kind);
        Guid adopted;
        var other = Guid.CreateVersion7();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            adopted = (await db.Set<LegacyMediaAdoptionRecord>().SingleAsync()).CanonicalRecordingId!.Value;
            db.Users.Add(new UserRecord
            {
                Id = other,
                BackendType = "jellyfin",
                BackendInstanceId = "primary",
                BackendPrincipalId = "other-listener",
                DisplayName = "Listener",
                Enabled = true,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }
        var merged = await media.Identities.MergeRecordingsAsync(actor, older.Recording.Id, adopted, "merge-original");
        Assert.Equal(older.Recording.Id, merged.Id);
        var reader = media.Reader();
        var read = await reader.OpenForPlaybackAsync(MediaActor(other, "other-listener"), new("deezer", ProviderResourceKind.Track, "123"), true);
        Assert.Equal(merged.Id, read!.File.RecordingId);
        Assert.Equal(path, read.File.Path);
        Assert.Null(read.Content);
        var song = (await reader.FindSongAsync(MediaActor(other, "other-listener"), "ext-deezer-123"))!;
        var context = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "primary", "other-listener",
            new(other, "jellyfin", "primary", "other-listener", "Listener", false), "imported-lyrics", DateTimeOffset.UtcNow.AddMinutes(1), default);
        var lyrics = await new KeptLyricsReader(_factory, new ConfigurationBuilder().Build(), reader)
            .ReadHouseholdAsync(context, song, []);
        Assert.Contains("Original lyric", lyrics!.SyncedLyrics);
        Assert.Equal(sidecarTime, System.IO.File.GetLastWriteTimeUtc(sidecar));
        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Empty(await db.ProviderDownloadArtifacts.ToListAsync());
            Assert.Empty(await db.ProviderAccounts.ToListAsync());
            await db.Users.Where(item => item.Id == other).ExecuteUpdateAsync(update => update.SetProperty(item => item.Enabled, false));
        }
        Assert.Null(await reader.FindAsync(MediaActor(other, "other-listener"), merged.Id));
    }

    [Fact]
    public async Task Media_DownloadsListAndExportAreReadOnlyAndNeverGenerateSidecars()
    {
        using var media = MediaFixture();
        var original = media.File("kept/Artist/Album/Song [deezer-123].flac");
        await QueueMediaAsync(media);
        var claim = (await media.Queue.ClaimNextAsync("export"))!;
        Assert.Equal(DurableJobCompletionKind.Succeeded, (await media.Import.ExecuteAsync(new(claim, media.Services), default)).Kind);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Library:DownloadPath"] = Path.Combine(media.Root, "new-owned"),
            ["Library:KeptPath"] = Path.Combine(media.Root, "new-owned-kept")
        }).Build();
        var sidecars = new Mock<IKeptLyricsSidecarService>(MockBehavior.Strict);
        var controller = new DownloadsController(NullLogger<DownloadsController>.Instance, configuration,
            sidecars.Object, contextFactory: _factory)
        { ControllerContext = new() { HttpContext = new DefaultHttpContext { RequestServices = media.Services } } };
        var response = Assert.IsType<OkObjectResult>(await controller.GetDownloads());
        var json = JsonSerializer.SerializeToElement(response.Value);
        var file = Assert.Single(json.GetProperty("files").EnumerateArray());
        var reference = file.GetProperty("Path").GetString()!;
        Assert.StartsWith("imported/", reference);
        Assert.False(file.GetProperty("Removable").GetBoolean());
        Assert.Equal("Imported", file.GetProperty("PublicationState").GetString());
        Assert.DoesNotContain(original, json.GetRawText());
        var download = Assert.IsType<FileStreamResult>(await controller.DownloadFile(reference));
        using (var read = new StreamReader(download.FileStream)) Assert.Equal("original fixture bytes", await read.ReadToEndAsync());
        Assert.IsType<ConflictObjectResult>(await controller.DeleteDownload(reference));
        var all = Assert.IsType<FileStreamResult>(await controller.DownloadAllFiles());
        using (all.FileStream)
        using (var archive = new System.IO.Compression.ZipArchive(all.FileStream, System.IO.Compression.ZipArchiveMode.Read))
        using (var read = new StreamReader(Assert.Single(archive.Entries).Open()))
            Assert.Equal("original fixture bytes", await read.ReadToEndAsync());
        System.IO.File.Move(original, original + ".saved");
        await System.IO.File.WriteAllTextAsync(original, "replacement fixture bytes");
        Assert.IsType<ConflictObjectResult>(await controller.DownloadFile(reference));
        Assert.IsType<ConflictObjectResult>(await controller.DownloadAllFiles());
        System.IO.File.Delete(original);
        System.IO.File.Move(original + ".saved", original);
        sidecars.VerifyNoOtherCalls();
        Assert.False(System.IO.File.Exists(Path.ChangeExtension(original, ".lrc")));
        Assert.Equal("original fixture bytes", await System.IO.File.ReadAllTextAsync(original));
    }

    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task Media_PopulatedDowngradeRequiresBackupRestoreWithoutLosingOriginalProof()
    {
        using var media = MediaFixture();
        var original = media.File("kept/Artist/Album/Song [deezer-123].flac");
        await QueueMediaAsync(media);
        var claim = (await media.Queue.ClaimNextAsync("downgrade"))!;
        Assert.Equal(DurableJobCompletionKind.Succeeded, (await media.Import.ExecuteAsync(new(claim, media.Services), default)).Kind);
        await using var db = await _factory.CreateDbContextAsync();
        var before = await db.Database.SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type='trigger' ORDER BY name").ToArrayAsync();
        string[] laterGuards = ["TR_backup_schedule_owner_insert", "TR_backup_schedule_owner_update",
            "TR_favorite_event_scope_immutable", "TR_provider_favorite_link_member_immutable"];
        Assert.All(laterGuards, name => Assert.Contains(name, before));
        var error = await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() =>
            db.GetService<IMigrator>().MigrateAsync("20261015214512_VerifyUncertainLastFmDelivery"));
        Assert.Contains("CK_legacy_media_requires_backup_restore", error.Message);
        Assert.Single(await db.Set<LegacyMediaAdoptionRecord>().ToListAsync());
        var after = await db.Database.SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type='trigger' ORDER BY name").ToArrayAsync();
        Assert.All(laterGuards, name => Assert.DoesNotContain(name, after));
        Assert.Equal(before.Except(laterGuards).ToArray(), after);
        Assert.Equal("original fixture bytes", await System.IO.File.ReadAllTextAsync(original));
        Assert.Equal(original, (await media.Reader().FindBySourceAsync(MediaActor(_userId, "jellyfin-user-id"),
            new("deezer", ProviderResourceKind.Track, "123")))!.Path);
    }

    private async Task QueueMediaAsync(MediaImportFixture media, string source = "STORAGE_MODE=Permanent")
    {
        var migration = CreateService(mediaInventory: media.Inventory, mediaImport: media.Import);
        var preview = await migration.PreviewAsync(Source(source), Actor());
        Assert.True(preview.CanApply);
        await migration.ApplyAsync(preview.PreviewToken, preview.Revision, true, Actor());
    }

    private MediaImportFixture MediaFixture(string? sourceRoot = null, IManagedFileOperations? operations = null)
    {
        var root = sourceRoot ?? Path.Combine(_root, "legacy");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Legacy:AllowedRoots"] = root,
            ["Legacy:KeptPath"] = Path.Combine(root, "kept"),
            ["Legacy:DownloadPath"] = Path.Combine(root, "downloads")
        }).Build();
        var registrations = new ServiceCollection().AddDbContext<AllstarrDbContext>(options => options.UseSqlite(_database.ConnectionString))
            .AddManagedFilePlacement();
        if (operations != null) registrations.AddSingleton(operations);
        var services = registrations.BuildServiceProvider();
        var clock = new SystemPlatformClock();
        var options = new DurableJobOptions();
        var queue = new DurableJobQueue(_factory, options, new JobPayloadPolicy(options), clock);
        var state = new DurableStorageState(_database.StorageOptions);
        state.Set(DurableStorageReadiness.Ready);
        var identities = new TrackIdentityService(_factory, state, clock);
        var inventory = new LegacyMediaInventory(configuration, services.GetRequiredService<IServiceScopeFactory>());
        var import = new LegacyMediaImport(_factory, services.GetRequiredService<IServiceScopeFactory>(), queue, identities, clock, inventory);
        var artifacts = new ProviderDownloadArtifactResolver(new EfProviderDownloadArtifactStore(_factory), new() { RootPath = Path.Combine(root, "workspaces") });
        RecordingKeptFiles Reader() => new(_factory, artifacts, services.GetRequiredService<IServiceScopeFactory>(),
            new() { RootId = Guid.CreateVersion7(), RootPath = Path.Combine(root, "owned") }, identities, clock, jobs: queue);
        return new(root, services, inventory, import, queue, identities, Reader);
    }

    private static ProviderActorContext MediaActor(Guid user, string principal) => new(ProviderActorKind.User, user, new("jellyfin", "primary", principal));
    private sealed record MediaImportFixture(string Root, ServiceProvider Services, LegacyMediaInventory Inventory,
        LegacyMediaImport Import, DurableJobQueue Queue, TrackIdentityService Identities, Func<RecordingKeptFiles> Reader) : IDisposable
    {
        public string File(string relative)
        {
            var path = Path.Combine(Root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllText(path, "original fixture bytes");
            return path;
        }
        public void Dispose() => Services.Dispose();
    }
}
