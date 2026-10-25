using System.Globalization;
using System.IO.Compression;
using Microsoft.EntityFrameworkCore;
using Microsoft.Win32.SafeHandles;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Jobs;

namespace allstarr.Core.Storage;

public sealed partial class DurableBackupService
{
    private sealed class RootLease(SafeFileHandle handle, ManagedFileSystemIdentity identity) : IDisposable
    {
        public ManagedFileSystemIdentity Identity => identity;
        public void Dispose() => handle.Dispose();
    }
    private readonly object rootGate = new();
    private readonly Dictionary<string, RootLease> rootAnchors = new(StringComparer.Ordinal);
    public void Dispose()
    {
        lock (rootGate) { foreach (var root in rootAnchors.Values) root.Dispose(); rootAnchors.Clear(); }
    }
    private RootLease OpenDirectory(string directory, bool retain = false)
    {
        try
        {
            RejectLinkedPath(directory);
            if (!physical.TryOpenDirectory(directory, out var handle, out var identity))
            { handle.Dispose(); throw new BackupDirectoryException(); }
            lock (rootGate)
            {
                if (rootAnchors.TryGetValue(directory, out var prior) &&
                    (prior.Identity.DeviceId != identity.DeviceId || prior.Identity.FileId != identity.FileId))
                { handle.Dispose(); throw new BackupDirectoryException(true); }
                if (retain && !rootAnchors.ContainsKey(directory))
                {
                    rootAnchors.Add(directory, new(handle, identity));
                    if (!physical.TryOpenDirectory(directory, out handle, out var after) || after.DeviceId != identity.DeviceId || after.FileId != identity.FileId)
                    { handle.Dispose(); throw new BackupDirectoryException(true); }
                }
            }
            return new(handle, identity);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or BackupVerificationException) { throw new BackupDirectoryException(); }
    }
    private bool RootMatches(string directory, ManagedFileSystemIdentity expected)
    {
        try { using var actual = OpenDirectory(directory); return actual.Identity.DeviceId == expected.DeviceId && actual.Identity.FileId == expected.FileId; }
        catch (IOException) { return false; }
    }
    private void RequireRoot(BackupArtifactRecord row, ManagedFileSystemIdentity expected)
    { if (!RootMatches(row.Directory, expected)) throw new BackupDirectoryException(true); }

    private async Task<RootLease> OpenRootAsync(BackupArtifactRecord row, BackupPolicy policy, CancellationToken token)
    {
        try
        {
            RejectLinkedPath(row.Directory);
            if (!Directory.Exists(row.Directory))
            {
                if (policy.Directory.Length != 0) throw new BackupDirectoryException();
                CreatePrivateDirectory(row.Directory);
            }
            var root = OpenDirectory(row.Directory, retain: true);
            try
            {
                await using var db = await contextFactory.CreateDbContextAsync(token);
                var saved = row.DirectoryDeviceId != null ? row : await db.BackupArtifacts.AsNoTracking()
                    .Where(item => item.Directory == row.Directory && item.StorageBinding == row.StorageBinding && item.DirectoryDeviceId != null)
                    .OrderBy(item => item.CreatedAt).FirstOrDefaultAsync(token);
                if (saved != null && (saved.DirectoryDeviceId != root.Identity.DeviceId || saved.DirectoryFileId != root.Identity.FileId))
                    throw new BackupDirectoryException(true);
                if (row.DirectoryDeviceId == null)
                {
                    row.DirectoryDeviceId = root.Identity.DeviceId;
                    row.DirectoryFileId = root.Identity.FileId;
                    await PersistArtifactAsync(row, null, token);
                }
                return root;
            }
            catch { root.Dispose(); throw; }
        }
        catch (Exception error) when (error is UnauthorizedAccessException or BackupVerificationException) { throw new BackupDirectoryException(); }
    }
    private bool FileMatches(BackupArtifactRecord row, string path)
    {
        try
        {
            RejectLinkedPath(path);
            if (!physical.TryGetFileIdentity(path, out var actual) || actual.LinkCount != 1 ||
                actual.DeviceId != row.FileDeviceId || actual.FileId != row.FileId) return false;
            return new FileInfo(path).Length == row.Bytes && File.GetLastWriteTimeUtc(path).Ticks == row.FileWriteTicks &&
                row.DirectoryDeviceId != null && row.DirectoryFileId != null &&
                RootMatches(row.Directory, new(row.DirectoryDeviceId, row.DirectoryFileId, 1));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or BackupVerificationException) { return false; }
    }
    private async Task<bool> MatchesOwnedArchiveAsync(BackupArtifactRecord row, CancellationToken token)
    {
        var path = Path.Combine(row.Directory, row.FileName);
        if (row.VerifiedAt == null || row.Sha256 == null || !FileMatches(row, path)) return false;
        try
        {
            await using var file = OpenArchiveFile(path);
            if (!physical.TryGetFileIdentity(file, out var identity) || identity.DeviceId != row.FileDeviceId || identity.FileId != row.FileId) return false;
            var manifest = await VerifyArchiveStreamAsync(file, token);
            file.Position = 0;
            var hash = Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(file, token));
            return manifest.Id == row.Id && FileName(manifest) == row.FileName && hash == row.Sha256 && FileMatches(row, path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or BackupVerificationException or InvalidDataException) { return false; }
    }

    internal static Dictionary<Guid, string[]> SelectRetention(IEnumerable<BackupArtifactRecord> records, BackupPolicy policy)
    {
        var rows = records.Where(item => item.VerifiedAt != null && item.RemovedAt == null)
            .OrderByDescending(item => item.VerifiedAt).ThenByDescending(item => item.CreatedAt).ThenByDescending(item => item.Id).ToArray();
        var result = new Dictionary<Guid, List<string>>();
        void Keep(BackupArtifactRecord row, string bucket)
        {
            if (!result.TryGetValue(row.Id, out var buckets)) result[row.Id] = buckets = [];
            buckets.Add(bucket);
        }
        var scheduled = rows.Where(item => item.Origin == "scheduled").ToArray();
        foreach (var row in scheduled.GroupBy(item => item.VerifiedAt!.Value.UtcDateTime.Date)
            .OrderByDescending(group => group.Key).Take(policy.DailyRetentionCount).Select(group => group.First())) Keep(row, "daily");
        foreach (var row in scheduled.GroupBy(item =>
        {
            var date = item.VerifiedAt!.Value.UtcDateTime;
            return (Year: ISOWeek.GetYear(date), Week: ISOWeek.GetWeekOfYear(date));
        }).OrderByDescending(group => group.Key.Year).ThenByDescending(group => group.Key.Week)
            .Take(policy.WeeklyRetentionCount).Select(group => group.First())) Keep(row, "weekly");
        foreach (var row in rows.Where(item => item.Origin == "manual").Take(policy.ManualRetentionCount)) Keep(row, "manual");
        return result.ToDictionary(item => item.Key, item => item.Value.ToArray());
    }

    private async Task RotateOwnedAsync(BackupPolicy policy, string directory, string binding, CancellationToken token)
    {
        await using var db = await contextFactory.CreateDbContextAsync(token);
        if (ResolveDirectory(await ReadPolicyAsync(db, token)) != directory || await StorageBindingAsync(db, token) != binding) return;
        var rows = await db.BackupArtifacts.AsNoTracking().Where(item => item.Directory == directory && item.StorageBinding == binding &&
            item.VerifiedAt != null && item.RemovedAt == null).ToArrayAsync(token);
        var verified = new List<BackupArtifactRecord>();
        foreach (var row in rows) if (await MatchesOwnedArchiveAsync(row, token)) verified.Add(row);
        var retained = SelectRetention(verified, policy);
        foreach (var row in verified.Where(item => !retained.ContainsKey(item.Id)).OrderBy(item => item.CreatedAt))
        {
            lock (pinGate) if (readers.GetValueOrDefault(row.Id) != 0) continue;
            await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, token);
            if (!await db.BackupArtifacts.AnyAsync(item => item.Id == row.Id && item.Revision == row.Revision && item.RemovedAt == null, token))
            { await transaction.RollbackAsync(token); continue; }
            var path = Path.Combine(row.Directory, row.FileName);
            if (!FileMatches(row, path) || !DeleteExact(path, new(row.FileDeviceId!, row.FileId!, 1)))
            { await transaction.RollbackAsync(token); continue; }
            await db.BackupArtifacts.Where(item => item.Id == row.Id && item.Revision == row.Revision).ExecuteUpdateAsync(update =>
                update.SetProperty(item => item.RemovedAt, backupClock.UtcNow).SetProperty(item => item.Revision, item => item.Revision + 1), token);
            await transaction.CommitAsync(token);
        }
    }

    private static BackupArtifact Artifact(BackupArtifactRecord row, string[] buckets) => new(row.Id, row.FileName, row.Bytes, row.Sha256,
        row.SchemaVersion, row.ApplicationVersion, row.CreatedAt)
    { Origin = row.Origin, RetentionBuckets = buckets, Verification = "verified", VerifiedAt = row.VerifiedAt };

    public async Task<IReadOnlyList<BackupArtifact>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var policy = await ReadPolicyAsync(db, cancellationToken);
        var rows = await db.BackupArtifacts.AsNoTracking().Where(item => item.RemovedAt == null).ToArrayAsync(cancellationToken);
        var intact = rows.Where(row => row.VerifiedAt != null && FileMatches(row, Path.Combine(row.Directory, row.FileName))).ToArray();
        var currentBinding = await StorageBindingAsync(db, cancellationToken);
        var buckets = SelectRetention(intact.Where(row => row.Directory == ResolveDirectory(policy) && row.StorageBinding == currentBinding), policy);
        var artifacts = new Dictionary<Guid, BackupArtifact>();
        foreach (var row in rows.Where(row => row.VerifiedAt != null && File.Exists(Path.Combine(row.Directory, row.FileName))))
            artifacts[row.Id] = Artifact(row, buckets.GetValueOrDefault(row.Id) ?? []) with
            { Verification = intact.Any(item => item.Id == row.Id) ? "verified" : "changed" };
        foreach (var directory in new[] { options.BackupDirectory, ResolveDirectory(policy) }.Distinct(StringComparer.Ordinal))
        {
            if (!Directory.Exists(directory)) continue;
            try
            {
                using var root = OpenDirectory(directory);
                foreach (var path in Directory.EnumerateFiles(directory, "allstarr-backup-*.zip").Take(10000))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        if (rows.Any(row => row.VerifiedAt != null && row.Directory == directory && row.FileName == Path.GetFileName(path))) continue;
                        RejectLinkedPath(path);
                        await using var file = OpenArchiveFile(path);
                        using var archive = new ZipArchive(file, ZipArchiveMode.Read);
                        var manifest = await ReadManifestAsync(archive, cancellationToken);
                        if (Path.GetFileName(path) == FileName(manifest) && !artifacts.ContainsKey(manifest.Id))
                            artifacts.Add(manifest.Id, ToArtifact(path, manifest));
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or BackupVerificationException) { }
                }
            }
            catch (IOException) { }
        }
        return artifacts.Values.OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.Id).ToArray();
    }

    public async Task<(Stream Stream, string FileName)> OpenVerifiedAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        FileStream? file = null;
        try
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var row = await db.BackupArtifacts.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id && item.RemovedAt == null, cancellationToken);
            var artifact = (await ListAsync(cancellationToken)).SingleOrDefault(item => item.Id == id)
                ?? throw new FileNotFoundException("The backup is unavailable.");
            var directories = row != null ? new[] { row.Directory } : new[] { options.BackupDirectory, ResolveDirectory(await ReadPolicyAsync(db, cancellationToken)) };
            foreach (var directory in directories.Distinct(StringComparer.Ordinal))
            {
                var path = Path.Combine(directory, artifact.FileName);
                if (!File.Exists(path)) continue;
                RejectLinkedPath(path);
                using var root = OpenDirectory(directory);
                file = OpenArchiveFile(path);
                if (row?.VerifiedAt != null && (!FileMatches(row, path) || !physical.TryGetFileIdentity(file, out var identity) ||
                    identity.DeviceId != row.FileDeviceId || identity.FileId != row.FileId))
                    throw new BackupVerificationException("Backup checksum or publication identity verification failed.");
                var manifest = await VerifyArchiveStreamAsync(file, cancellationToken);
                if (row?.VerifiedAt != null)
                {
                    file.Position = 0;
                    var hash = Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(file, cancellationToken));
                    if (hash != row.Sha256 || !FileMatches(row, path))
                        throw new BackupVerificationException("Backup checksum or publication identity verification failed.");
                }
                if (manifest.Id != id || FileName(manifest) != artifact.FileName) throw new BackupVerificationException("The backup is unavailable.");
                file.Position = 0;
                lock (pinGate) readers[id] = readers.GetValueOrDefault(id) + 1;
                var stream = new BackupReadLease(file, () =>
                {
                    lock (pinGate)
                    {
                        if (readers[id] == 1) readers.Remove(id);
                        else readers[id]--;
                    }
                });
                file = null;
                return (stream, artifact.FileName);
            }
            throw new FileNotFoundException("The backup is unavailable.");
        }
        finally { file?.Dispose(); _gate.Release(); }
    }

    public async Task<BackupStatus> GetStatusAsync(CancellationToken token = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(token);
        var policy = await ReadPolicyAsync(db, token);
        var active = await db.Jobs.AsNoTracking().Where(item => item.Type == JobType &&
            (item.State == DurableJobState.Pending || item.State == DurableJobState.Running || item.State == DurableJobState.RetryScheduled))
            .OrderBy(item => item.CreatedAt).FirstOrDefaultAsync(token);
        var successful = await db.BackupArtifacts.AsNoTracking().Where(item => item.VerifiedAt != null)
            .OrderByDescending(item => item.VerifiedAt).FirstOrDefaultAsync(token);
        var failed = await db.BackupArtifacts.AsNoTracking().Where(item => item.LastFailureAt != null)
            .OrderByDescending(item => item.LastFailureAt).FirstOrDefaultAsync(token);
        var schedule = await db.JobSchedules.AsNoTracking().SingleOrDefaultAsync(item => item.Id == ScheduleId, token);
        var directoryState = "unchecked";
        var directory = ResolveDirectory(policy);
        try
        {
            if (!Directory.Exists(directory)) directoryState = policy.Directory.Length == 0 ? "unchecked" : "unavailable";
            else
            {
                using var root = OpenDirectory(directory);
                var binding = await StorageBindingAsync(db, token);
                var recorded = await db.BackupArtifacts.AsNoTracking().Where(item => item.Directory == directory && item.StorageBinding == binding && item.DirectoryDeviceId != null)
                    .OrderBy(item => item.CreatedAt).FirstOrDefaultAsync(token);
                directoryState = recorded != null && (recorded.DirectoryDeviceId != root.Identity.DeviceId || recorded.DirectoryFileId != root.Identity.FileId) ? "changed" : "ready";
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or BackupVerificationException) { directoryState = "unavailable"; }
        var state = HasPendingRestore ? "restore-pending" : storageState.GetSnapshot().Readiness != DurableStorageReadiness.Ready ||
            directoryState is "changed" or "unavailable" ? "unavailable" : active?.State == DurableJobState.Running ? "running" : active != null ? "queued" : "idle";
        return new(state, directoryState, active?.Id, successful?.VerifiedAt, policy.Enabled ? schedule?.NextRunAt : null,
            successful?.Bytes, failed?.LastFailureAt is { } at ? new(failed.LastFailureCode!, failed.LastFailureMessage!, at) : null);
    }

    private sealed class BackupReadLease(Stream source, Action closed) : Stream
    {
        private int disposed;
        public override bool CanRead => source.CanRead;
        public override bool CanSeek => source.CanSeek;
        public override bool CanWrite => false;
        public override long Length => source.Length;
        public override long Position { get => source.Position; set => source.Position = value; }
        public override long Seek(long offset, SeekOrigin origin) => source.Seek(offset, origin);
        public override int Read(byte[] buffer, int offset, int count) => source.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => source.ReadAsync(buffer, token);
        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref disposed, 1) == 0) { try { source.Dispose(); } finally { closed(); } }
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) { try { await source.DisposeAsync(); } finally { closed(); } }
            GC.SuppressFinalize(this);
        }
        public override void Flush() => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
