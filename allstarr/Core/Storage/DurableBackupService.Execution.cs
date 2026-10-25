using System.IO.Compression;
using System.Text.Json;
using allstarr.Core.Jobs;
using allstarr.Core.ManagedFiles;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Storage;

public sealed partial class DurableBackupService
{
    private void RequireReady()
    {
        if (storageState.GetSnapshot().Readiness != DurableStorageReadiness.Ready)
            throw new InvalidOperationException("A backup cannot start while durable storage is unready.");
        options.Validate();
        if (HasPendingRestore) throw new RestorePendingException();
    }

    internal async Task<DurableJobCompletion> ExecuteBackupJobAsync(DurableJobExecutionContext execution, CancellationToken token)
    {
        BackupArtifactRecord? row = null;
        try
        {
            var payload = execution.Claim.Payload.Deserialize<BackupJobPayload>();
            if (payload == null || payload.ArtifactId == Guid.Empty) return DurableJobCompletion.Failure("backup_request_invalid", "The backup request is invalid.");
            await _gate.WaitAsync(token);
            try
            {
                RequireReady();
                await using var db = await contextFactory.CreateDbContextAsync(token);
                row = await db.BackupArtifacts.AsNoTracking().SingleOrDefaultAsync(item => item.Id == payload.ArtifactId && item.JobId == execution.Claim.JobId, token);
                if (row == null || row.StorageBinding != await StorageBindingAsync(db, token)) return DurableJobCompletion.Cancelled();
                if (row.Origin == "scheduled" && !(await ReadPolicyAsync(db, token)).Enabled) return DurableJobCompletion.Cancelled();
                await CreateReservedAsync(row, execution.Claim, token);
                return DurableJobCompletion.Success();
            }
            finally { _gate.Release(); }
        }
        catch (OperationCanceledException) { throw; }
        catch (DurableJobExecutionChangedException) { return DurableJobCompletion.Cancelled(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            var (code, message, terminal) = error switch
            {
                BackupDirectoryException { Changed: true } => ("backup_directory_changed", "The backup directory changed. Check the selected volume before creating another backup.", true),
                BackupDirectoryException => ("backup_directory_unavailable", "The backup directory is unavailable. Check its mount, permissions and free space.", false),
                BackupUnclaimedException => ("backup_unclaimed", "An unclaimed archive was preserved. Create a new backup or inspect the backup folder.", true),
                RestorePendingException => ("restore_pending", "Restart Allstarr to finish the pending restore.", true),
                BackupVerificationException => ("backup_verification_failed", "Backup verification failed. Check storage readiness and the key ring.", true),
                _ => ("backup_unavailable", "The backup could not be created. Check storage readiness, disk space and key-ring access.", false)
            };
            if (row != null)
            {
                await using var db = await contextFactory.CreateDbContextAsync(token);
                var claim = execution.Claim;
                var changed = await db.BackupArtifacts.Where(item => item.Id == row.Id && item.Revision == row.Revision &&
                    db.Jobs.Any(job => job.Id == claim.JobId && job.State == DurableJobState.Running &&
                        job.AttemptCount == claim.AttemptNumber && job.CancellationRequestedAt == null)).ExecuteUpdateAsync(update => update
                    .SetProperty(item => item.LastFailureCode, code).SetProperty(item => item.LastFailureMessage, message)
                    .SetProperty(item => item.LastFailureAt, backupClock.UtcNow).SetProperty(item => item.Revision, item => item.Revision + 1), token);
                if (changed == 0) return DurableJobCompletion.Cancelled();
            }
            return terminal ? DurableJobCompletion.Failure(code, message) : DurableJobCompletion.Retry(code, message, TimeSpan.FromMinutes(5));
        }
    }

    private async Task<BackupArtifact> CreateReservedAsync(BackupArtifactRecord row, DurableJobClaim? claim, CancellationToken token)
    {
        var policy = ValidatePolicy(JsonSerializer.Deserialize<BackupPolicy>(row.PolicyJson) ?? throw new BackupVerificationException("The backup policy is invalid."));
        await RequireExecutionAsync(claim, token);
        using var root = await OpenRootAsync(row, policy, token);
        var final = Path.Combine(row.Directory, row.FileName);
        if (row.VerifiedAt != null)
        {
            await RequireExecutionAsync(claim, token);
            if (!await MatchesOwnedArchiveAsync(row, token)) throw new BackupUnclaimedException();
            return Artifact(row, SelectRetention([row], policy).GetValueOrDefault(row.Id) ?? []);
        }
        if (Path.Exists(final)) throw new BackupUnclaimedException();
        var temporary = Path.Combine(row.Directory, $".tmp-{row.Id:N}");
        string? partial = Path.Combine(row.Directory, $".{row.Id:N}.partial");
        if (Path.Exists(temporary) || Path.Exists(partial)) throw new BackupUnclaimedException();
        var owned = new Dictionary<string, ManagedFileSystemIdentity>();
        RootLease? stage = null;
        FileStream? output = null;
        try
        {
            await RequireExecutionAsync(claim, token);
            RequireRoot(row, root.Identity);
            CreatePrivateDirectory(temporary);
            stage = OpenDirectory(temporary);
            var snapshotPath = Path.Combine(temporary, DatabaseFile);
            await using (var source = await contextFactory.CreateDbContextAsync(token))
            {
                if (!(await DurableSchemaCompatibility.InspectAsync(source, token)).IsCurrent)
                    throw new BackupVerificationException("A backup requires a compatible database schema.");
                await source.Database.ExecuteSqlInterpolatedAsync($"VACUUM INTO {snapshotPath}", token);
            }
            MakePrivate(snapshotPath);
            Capture(snapshotPath, owned);
            var keyPath = Path.Combine(temporary, KeyRingFile);
            await using (var source = File.OpenRead(secretOptions.KeyRingPath))
            await using (var target = CreatePrivateFile(keyPath)) await source.CopyToAsync(target, token);
            Capture(keyPath, owned);
            var schema = await VerifySnapshotAsync(snapshotPath, keyPath, token);
            var manifest = new BackupManifest(1, row.Id, AppVersion.Version, schema, row.CreatedAt,
                new Dictionary<string, string>(StringComparer.Ordinal)
                { [DatabaseFile] = await HashAsync(snapshotPath, token), [KeyRingFile] = await HashAsync(keyPath, token) });
            RequireRoot(row, root.Identity);
            output = CreatePrivateFile(partial, FileShare.Read | FileShare.Delete);
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var name in new[] { DatabaseFile, KeyRingFile })
                {
                    await using var source = File.OpenRead(Path.Combine(temporary, name));
                    await using var entry = archive.CreateEntry(name, CompressionLevel.Fastest).Open();
                    await source.CopyToAsync(entry, token);
                }
                await using var metadata = archive.CreateEntry(ManifestFile, CompressionLevel.Fastest).Open();
                await JsonSerializer.SerializeAsync(metadata, manifest, JsonOptions, token);
            }
            await output.FlushAsync(token);
            output.Flush(true);
            await VerifyArchiveAsync(partial, token);
            row.Sha256 = await HashAsync(partial, token);
            row.Bytes = output.Length;
            row.SchemaVersion = schema;
            row.ApplicationVersion = AppVersion.Version;
            await PersistArtifactAsync(row, claim, token);
            await RequireExecutionAsync(claim, token);
            RequireRoot(row, root.Identity);
            physical.MoveNoReplace(partial, final);
            partial = null;
            if (!physical.TryGetFileIdentity(output, out var identity) || identity.LinkCount != 1 ||
                !physical.TryGetFileIdentity(final, out var current) || current != identity) throw new BackupUnclaimedException();
            row.FileDeviceId = identity.DeviceId;
            row.FileId = identity.FileId;
            row.FileWriteTicks = File.GetLastWriteTimeUtc(final).Ticks;
            row.VerifiedAt = backupClock.UtcNow;
            await PersistArtifactAsync(row, claim, token);
            await RotateOwnedAsync(policy, row.Directory, row.StorageBinding, token);
            return Artifact(row, SelectRetention([row], policy).GetValueOrDefault(row.Id) ?? []);
        }
        finally
        {
            var identity = output != null && physical.TryGetFileIdentity(output, out var known) ? known : null;
            if (output != null) await output.DisposeAsync();
            if (partial != null && identity != null && RootMatches(row.Directory, root.Identity)) DeleteExact(partial, identity);
            if (stage != null)
            {
                try
                {
                    if (RootMatches(temporary, stage.Identity) && Directory.EnumerateFileSystemEntries(temporary).All(path =>
                        owned.TryGetValue(path, out var expected) && physical.TryGetFileIdentity(path, out var actual) && actual == expected))
                    {
                        foreach (var (path, expected) in owned) DeleteExact(path, expected);
                        if (!Directory.EnumerateFileSystemEntries(temporary).Any()) Directory.Delete(temporary);
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                stage.Dispose();
            }
        }
    }

    private async Task RequireExecutionAsync(DurableJobClaim? claim, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (claim != null && (jobs == null || !await jobs.IsExecutionCurrentAsync(claim, token))) throw new DurableJobExecutionChangedException();
    }
    private async Task PersistArtifactAsync(BackupArtifactRecord row, DurableJobClaim? claim, CancellationToken token)
    {
        await RequireExecutionAsync(claim, token);
        await using var db = await contextFactory.CreateDbContextAsync(token);
        var prior = row.Revision;
        db.Attach(row);
        db.Entry(row).State = EntityState.Modified;
        row.Revision++;
        db.Entry(row).Property(item => item.Revision).OriginalValue = prior;
        await db.SaveChangesAsync(token);
    }
    private void Capture(string path, IDictionary<string, ManagedFileSystemIdentity> owned)
    { if (physical.TryGetFileIdentity(path, out var identity)) owned[path] = identity; }
    private bool DeleteExact(string path, ManagedFileSystemIdentity expected)
    {
        try
        {
            RejectLinkedPath(path);
            if (expected.LinkCount != 1 || !physical.TryGetFileIdentity(path, out var current) || current != expected) return false;
            File.Delete(path);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or BackupVerificationException) { return false; }
    }
    private static FileStream OpenArchiveFile(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
        81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
}

public sealed class DurableBackupJobHandler(DurableBackupService service) : IDurableJobHandler
{
    public string JobType => DurableBackupService.JobType;
    public Task<DurableJobCompletion> ExecuteAsync(DurableJobExecutionContext context, CancellationToken cancellationToken) =>
        service.ExecuteBackupJobAsync(context, cancellationToken);
}
