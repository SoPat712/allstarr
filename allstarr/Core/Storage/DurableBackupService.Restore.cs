using System.IO.Compression;
using System.Text.Json;

namespace allstarr.Core.Storage;

public sealed class RestorePendingException() : InvalidOperationException("A restore is already waiting for restart.");

public sealed partial class DurableBackupService
{
    public const long MaximumArchiveBytes = 1024L * 1024 * 1024;
    internal const long MaximumDatabaseBytes = 4 * MaximumArchiveBytes;
    private const string RestoreIdFile = "restore-id";
    private const string PreparationFile = "restore-preparation.json";
    private string PendingDirectory => Path.Combine(Path.GetFullPath(options.DataDirectory), "restore-pending");
    public bool HasPendingRestore => Path.Exists(PendingDirectory);

    public async Task StageRestoreAsync(Stream upload, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        string? temporary = null;
        try
        {
            options.Validate();
            RejectLinkedPath(options.DataDirectory);
            _ = RestoreTargets();
            if (HasPendingRestore) throw new RestorePendingException();
            var id = Guid.NewGuid();
            var directory = Path.Combine(Path.GetFullPath(options.DataDirectory), $".restore-upload-{id:N}");
            if (Path.Exists(directory)) throw new IOException("Restore staging is unavailable.");
            CreatePrivateDirectory(directory);
            temporary = directory;
            var archivePath = Path.Combine(directory, "upload.zip");
            await using (var output = CreatePrivateFile(archivePath))
            {
                await CopyBoundedAsync(upload, output, MaximumArchiveBytes, cancellationToken);
                await FlushAsync(output, cancellationToken);
            }
            var manifest = await VerifyArchiveAsync(archivePath, cancellationToken);
            using (var archive = ZipFile.OpenRead(archivePath))
            {
                foreach (var name in ArchiveFiles)
                {
                    await using var input = archive.GetEntry(name)!.Open();
                    await using var output = CreatePrivateFile(Path.Combine(directory, name));
                    await CopyBoundedAsync(input, output, EntryLimit(name), cancellationToken);
                    await FlushAsync(output, cancellationToken);
                }
            }
            var schema = await VerifySnapshotAsync(Path.Combine(directory, DatabaseFile), Path.Combine(directory, KeyRingFile),
                cancellationToken, allowPendingMigrations: true);
            if (schema != manifest.LastMigrationId)
                throw new BackupVerificationException("The backup manifest does not match the database schema.");
            File.Delete(archivePath);
            await using (var output = CreatePrivateFile(Path.Combine(directory, RestoreIdFile)))
            {
                await output.WriteAsync(System.Text.Encoding.ASCII.GetBytes(id.ToString("N")), cancellationToken);
                await FlushAsync(output, cancellationToken);
            }
            Directory.Move(directory, PendingDirectory);
            temporary = null;
        }
        finally
        {
            try { if (temporary is not null) Directory.Delete(temporary, recursive: true); }
            finally { _gate.Release(); }
        }
    }

    internal async Task ApplyPendingRestoreAsync(CancellationToken cancellationToken = default)
    {
        if (!HasPendingRestore) return;
        if (storageState.GetSnapshot().Readiness != DurableStorageReadiness.Initializing)
            throw new InvalidOperationException("A restore can only be applied before storage initialization.");
        RejectLinkedPath(PendingDirectory);
        var pendingFiles = Directory.GetFileSystemEntries(PendingDirectory);
        if (pendingFiles.Length != 4 || pendingFiles.Any(path =>
                !ArchiveFiles.Append(RestoreIdFile).Contains(Path.GetFileName(path), StringComparer.Ordinal)))
            throw new BackupVerificationException("The pending restore is incomplete.");
        foreach (var path in pendingFiles) RejectLinkedPath(path);
        var idPath = Path.Combine(PendingDirectory, RestoreIdFile);
        if (new FileInfo(idPath).Length != 32 || !Guid.TryParseExact(await File.ReadAllTextAsync(idPath, cancellationToken), "N", out var id))
            throw new BackupVerificationException("The pending restore identifier is invalid.");
        var manifestPath = Path.Combine(PendingDirectory, ManifestFile);
        if (new FileInfo(manifestPath).Length > 64 * 1024)
            throw new BackupVerificationException("The pending restore manifest is invalid.");
        BackupManifest manifest;
        await using (var stream = File.OpenRead(manifestPath))
            manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(stream, JsonOptions, cancellationToken)
                ?? throw new BackupVerificationException("The pending restore manifest is invalid.");
        if (manifest.FormatVersion != 1 || manifest.Id == Guid.Empty || manifest.Sha256 is null ||
            manifest.Sha256.Count != 2 || !manifest.Sha256.ContainsKey(DatabaseFile) || !manifest.Sha256.ContainsKey(KeyRingFile))
            throw new BackupVerificationException("The pending restore manifest is invalid.");
        foreach (var (name, hash) in manifest.Sha256)
            if (await HashAsync(Path.Combine(PendingDirectory, name), cancellationToken) != hash)
                throw new BackupVerificationException("Pending restore checksum verification failed.");
        var schema = await VerifySnapshotAsync(Path.Combine(PendingDirectory, DatabaseFile), Path.Combine(PendingDirectory, KeyRingFile),
            cancellationToken, allowPendingMigrations: true);
        if (schema != manifest.LastMigrationId)
            throw new BackupVerificationException("The pending restore schema does not match its manifest.");

        var targets = RestoreTargets();
        foreach (var path in targets.Values) RejectLinkedPath(path);
        var previous = Path.Combine(Path.GetFullPath(options.DataDirectory), $"pre-restore-{id:N}");
        await PreparePreviousFilesAsync(previous, id, targets, cancellationToken);
        // The prior pair is safely preserved before either live file is replaced. Pending
        // files remain intact until both replacements finish, so interrupted startup can retry.
        await ReplaceFromPendingAsync(KeyRingFile, targets[KeyRingFile], cancellationToken);
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" }) File.Delete(options.DatabasePath + suffix);
        await ReplaceFromPendingAsync(DatabaseFile, targets[DatabaseFile], cancellationToken);
        var applied = Path.Combine(previous, "applied");
        Directory.Move(PendingDirectory, applied);
        // Publishing completion precedes cleanup; a failed cleanup must not repeat the restore.
        try { Directory.Delete(applied, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private Dictionary<string, string> RestoreTargets()
    {
        var database = Path.GetFullPath(options.DatabasePath);
        var key = Path.GetFullPath(secretOptions.KeyRingPath);
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var targets = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [DatabaseFile] = database,
            [DatabaseFile + "-wal"] = database + "-wal",
            [DatabaseFile + "-shm"] = database + "-shm",
            [DatabaseFile + "-journal"] = database + "-journal",
            [KeyRingFile] = key
        };
        if (targets.Values.Distinct(comparison).Count() != targets.Count ||
            key.StartsWith(PendingDirectory + Path.DirectorySeparatorChar, comparison == StringComparer.Ordinal ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
            throw new BackupVerificationException("The configured restore targets overlap.");
        return targets;
    }

    private async Task PreparePreviousFilesAsync(string previous, Guid id, Dictionary<string, string> targets, CancellationToken cancellationToken)
    {
        if (!Path.Exists(previous))
        {
            var staging = previous + $".tmp-{Guid.NewGuid():N}";
            CreatePrivateDirectory(staging);
            try
            {
                var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var (name, path) in targets)
                {
                    if (!File.Exists(path)) continue;
                    var copy = Path.Combine(staging, name);
                    await CopyPrivateAsync(path, copy, cancellationToken);
                    hashes[name] = await HashAsync(copy, cancellationToken);
                }
                await using (var output = CreatePrivateFile(Path.Combine(staging, PreparationFile)))
                {
                    await JsonSerializer.SerializeAsync(output, new RestorePreparation(id, targets, hashes), JsonOptions, cancellationToken);
                    await FlushAsync(output, cancellationToken);
                }
                Directory.Move(staging, previous);
            }
            finally { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
        }
        RejectLinkedPath(previous);
        var preparationPath = Path.Combine(previous, PreparationFile);
        RejectLinkedPath(preparationPath);
        if (new FileInfo(preparationPath).Length > 64 * 1024)
            throw new BackupVerificationException("The previous-file preparation is invalid.");
        await using var input = File.OpenRead(preparationPath);
        var prepared = await JsonSerializer.DeserializeAsync<RestorePreparation>(input, JsonOptions, cancellationToken);
        if (prepared is null || prepared.Id != id || prepared.Targets is null || prepared.Sha256 is null ||
            prepared.Targets.Count != targets.Count || targets.Any(item => !prepared.Targets.TryGetValue(item.Key, out var path) || path != item.Value) ||
            prepared.Sha256.Keys.Any(name => !targets.ContainsKey(name)))
            throw new BackupVerificationException("Restore configuration changed after preparation; restore the previous configuration before retrying.");
        foreach (var (name, hash) in prepared.Sha256)
        {
            var path = Path.Combine(previous, name);
            RejectLinkedPath(path);
            if (await HashAsync(path, cancellationToken) != hash)
                throw new BackupVerificationException("The preserved previous files failed verification.");
        }
    }

    private async Task ReplaceFromPendingAsync(string name, string target, CancellationToken cancellationToken)
    {
        var temporary = target + $".restore-{Guid.NewGuid():N}";
        var created = false;
        try
        {
            await using (var source = File.OpenRead(Path.Combine(PendingDirectory, name)))
            await using (var output = CreatePrivateFile(temporary))
            {
                created = true;
                await source.CopyToAsync(output, cancellationToken);
                await FlushAsync(output, cancellationToken);
            }
            File.Move(temporary, target, overwrite: true);
        }
        finally { if (created) File.Delete(temporary); }
    }

    private static async Task CopyPrivateAsync(string source, string target, CancellationToken cancellationToken)
    {
        await using var input = File.OpenRead(source);
        await using var output = CreatePrivateFile(target);
        await input.CopyToAsync(output, cancellationToken);
        await FlushAsync(output, cancellationToken);
    }
    private static async Task FlushAsync(FileStream stream, CancellationToken cancellationToken)
    {
        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
    }
    private static long EntryLimit(string name) => name == DatabaseFile ? MaximumDatabaseBytes : name == KeyRingFile ? 1024 * 1024 : 64 * 1024;
    private static async Task CopyBoundedAsync(Stream source, Stream target, long maximumBytes, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        long total = 0;
        int count;
        while ((count = await source.ReadAsync(buffer, cancellationToken)) != 0)
        {
            total += count;
            if (total > maximumBytes) throw new BackupVerificationException("The backup exceeds its size limit.");
            await target.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }
    }
    private static void RejectLinkedPath(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if (Path.Exists(current) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new BackupVerificationException("Restore paths must not use symbolic links.");
            // macOS exposes /tmp and /var through system-owned aliases; the configured
            // data directory itself and every descendant must still be ordinary paths.
            if (current == Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)) break;
        }
    }
    private sealed record RestorePreparation(Guid Id, Dictionary<string, string> Targets, Dictionary<string, string> Sha256);
}
