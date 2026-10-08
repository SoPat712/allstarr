using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using allstarr.Core.Secrets;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Storage;

public sealed record BackupArtifact(
    Guid Id, string FileName, long Bytes, string? Sha256, string SchemaVersion,
    string ApplicationVersion, DateTimeOffset CreatedAt)
{
    public string Status => "verified";
    public DateTimeOffset VerifiedAt => CreatedAt;
    public bool IncludesKeyRing => true;
}

public sealed class BackupVerificationException(string message) : InvalidOperationException(message);

public sealed class DurableBackupService(
    IDbContextFactory<AllstarrDbContext> contextFactory,
    StorageOptions options,
    DurableStorageState storageState,
    SecretStoreOptions secretOptions)
{
    internal const string DatabaseFile = "allstarr.db";
    internal const string KeyRingFile = "keyring.json";
    internal const string ManifestFile = "manifest.json";
    private static readonly string[] ArchiveFiles = [DatabaseFile, KeyRingFile, ManifestFile];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<BackupArtifact> CreateAsync(CancellationToken cancellationToken = default)
    {
        if (storageState.GetSnapshot().Readiness != DurableStorageReadiness.Ready)
            throw new InvalidOperationException("A backup cannot start while durable storage is unready.");
        await _gate.WaitAsync(cancellationToken);
        string? temporary = null;
        string? partial = null;
        try
        {
            options.Validate();
            var directory = Path.GetFullPath(options.BackupDirectory);
            if (directory.Contains('\'')) throw new BackupVerificationException("The backup directory must not contain a single quote.");
            Directory.CreateDirectory(directory);
            var id = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            temporary = Path.Combine(directory, $".tmp-{id:N}");
            CreatePrivateDirectory(temporary);
            var snapshotPath = Path.Combine(temporary, DatabaseFile);
            await using (var source = await contextFactory.CreateDbContextAsync(cancellationToken))
            {
                var compatibility = await DurableSchemaCompatibility.InspectAsync(source, cancellationToken);
                if (!compatibility.IsCurrent)
                    throw new BackupVerificationException("A backup requires a compatible database schema.");
                await source.Database.ExecuteSqlInterpolatedAsync($"VACUUM INTO {snapshotPath}", cancellationToken);
            }
            MakePrivate(snapshotPath);
            var keyPath = Path.Combine(temporary, KeyRingFile);
            await using (var source = File.OpenRead(secretOptions.KeyRingPath))
            await using (var target = CreatePrivateFile(keyPath))
                await source.CopyToAsync(target, cancellationToken);
            var schema = await VerifySnapshotAsync(snapshotPath, keyPath, cancellationToken);
            var manifest = new BackupManifest(1, id, AppVersion.Version, schema, now,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [DatabaseFile] = await HashAsync(snapshotPath, cancellationToken),
                    [KeyRingFile] = await HashAsync(keyPath, cancellationToken)
                });
            var name = FileName(manifest);
            partial = Path.Combine(directory, $".{id:N}.partial");
            await using (var output = CreatePrivateFile(partial))
            {
                using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
                {
                    foreach (var file in new[] { DatabaseFile, KeyRingFile })
                    {
                        await using var source = File.OpenRead(Path.Combine(temporary, file));
                        await using var entry = archive.CreateEntry(file, CompressionLevel.Fastest).Open();
                        await source.CopyToAsync(entry, cancellationToken);
                    }
                    await using var metadata = archive.CreateEntry(ManifestFile, CompressionLevel.Fastest).Open();
                    await JsonSerializer.SerializeAsync(metadata, manifest, JsonOptions, cancellationToken);
                }
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
            }
            await VerifyArchiveAsync(partial, cancellationToken);
            var hash = await HashAsync(partial, cancellationToken);
            var path = Path.Combine(directory, name);
            File.Move(partial, path, overwrite: false);
            partial = null;
            var artifact = ToArtifact(path, manifest) with { Sha256 = hash };
            await RotateAsync(cancellationToken);
            return artifact;
        }
        finally
        {
            try
            {
                if (partial is not null) File.Delete(partial);
                if (temporary is not null && Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
            }
            finally { _gate.Release(); }
        }
    }

    public async Task<IReadOnlyList<BackupArtifact>> ListAsync(CancellationToken cancellationToken = default)
    {
        var directory = options.BackupDirectory;
        if (!Directory.Exists(directory)) return [];
        var artifacts = new List<BackupArtifact>();
        foreach (var path in Directory.EnumerateFiles(directory, "allstarr-backup-*.zip"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) continue;
                await using var file = File.OpenRead(path);
                using var archive = new ZipArchive(file, ZipArchiveMode.Read);
                var manifest = await ReadManifestAsync(archive, cancellationToken);
                if (Path.GetFileName(path) == FileName(manifest)) artifacts.Add(ToArtifact(path, manifest));
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or BackupVerificationException)
            {
                // Leave unknown or damaged files untouched; they are never retention candidates.
            }
        }
        return artifacts.OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.Id).ToArray();
    }

    public async Task<(Stream Stream, string FileName)> OpenVerifiedAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var artifact = (await ListAsync(cancellationToken)).SingleOrDefault(item => item.Id == id)
                ?? throw new FileNotFoundException("The backup is unavailable.");
            var path = Path.Combine(options.BackupDirectory, artifact.FileName);
            await VerifyArchiveAsync(path, cancellationToken);
            return (File.OpenRead(path), artifact.FileName);
        }
        finally { _gate.Release(); }
    }

    internal async Task<BackupManifest> VerifyArchiveAsync(string path, CancellationToken cancellationToken)
    {
        await using var file = File.OpenRead(path);
        using var archive = new ZipArchive(file, ZipArchiveMode.Read);
        var manifest = await ReadManifestAsync(archive, cancellationToken);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!context.Database.GetMigrations().Contains(manifest.LastMigrationId, StringComparer.Ordinal))
            throw new BackupVerificationException("The backup schema is not supported by this build.");
        foreach (var (name, expected) in manifest.Sha256)
        {
            await using var content = archive.GetEntry(name)!.Open();
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(content, cancellationToken)).ToLowerInvariant();
            if (!actual.Equals(expected, StringComparison.Ordinal))
                throw new BackupVerificationException("Backup checksum verification failed.");
        }
        return manifest;
    }

    internal static async Task<BackupManifest> ReadManifestAsync(ZipArchive archive, CancellationToken cancellationToken)
    {
        if (archive.Entries.Count != ArchiveFiles.Length ||
            archive.Entries.Select(item => item.FullName).Distinct(StringComparer.Ordinal).Count() != ArchiveFiles.Length ||
            archive.Entries.Any(item => !ArchiveFiles.Contains(item.FullName, StringComparer.Ordinal) ||
                ((item.ExternalAttributes >> 16) & 0xF000) == 0xA000))
            throw new BackupVerificationException("The backup must contain only the database, key ring, and manifest.");
        var entry = archive.GetEntry(ManifestFile)!;
        if (entry.Length > 64 * 1024 || archive.GetEntry(KeyRingFile)!.Length > 1024 * 1024)
            throw new BackupVerificationException("Backup metadata exceeds its size limit.");
        try
        {
            await using var stream = entry.Open();
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                document.RootElement.EnumerateObject().Count() != 6 ||
                document.RootElement.EnumerateObject().Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != 6)
                throw new BackupVerificationException("The backup manifest is invalid.");
            var manifest = document.Deserialize<BackupManifest>(JsonOptions);
            if (manifest is null || manifest.FormatVersion != 1 || manifest.Id == Guid.Empty ||
                string.IsNullOrWhiteSpace(manifest.AppVersion) || string.IsNullOrWhiteSpace(manifest.LastMigrationId) ||
                manifest.CreatedAt == default || manifest.Sha256 is null || manifest.Sha256.Count != 2 ||
                !manifest.Sha256.ContainsKey(DatabaseFile) || !manifest.Sha256.ContainsKey(KeyRingFile) ||
                document.RootElement.GetProperty("sha256").EnumerateObject().Count() != 2 ||
                manifest.Sha256.Values.Any(hash => hash is null || hash.Length != 64 ||
                    hash.Any(value => !char.IsAsciiHexDigit(value)) || hash != hash.ToLowerInvariant()))
                throw new BackupVerificationException("The backup manifest is invalid.");
            return manifest;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new BackupVerificationException("The backup manifest is invalid.");
        }
    }

    internal async Task<string> VerifySnapshotAsync(string databasePath, string keyPath, CancellationToken cancellationToken)
    {
        var connection = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        await using var snapshot = new AllstarrDbContext(new DbContextOptionsBuilder<AllstarrDbContext>()
            .UseSqlite(connection.ConnectionString).Options);
        await snapshot.Database.OpenConnectionAsync(cancellationToken);
        await using var check = snapshot.Database.GetDbConnection().CreateCommand();
        check.CommandText = "PRAGMA integrity_check;";
        await using (var reader = await check.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken) || reader.GetString(0) != "ok" || await reader.ReadAsync(cancellationToken))
                throw new BackupVerificationException("The backup database failed its integrity check.");
        }
        var compatibility = await DurableSchemaCompatibility.InspectAsync(snapshot, cancellationToken);
        if (!compatibility.IsCurrent)
            throw new BackupVerificationException("The backup database schema is not supported by this build.");
        var ring = await new FileSecretKeyRingProvider(new SecretStoreOptions { KeyRingPath = keyPath }).LoadAsync(cancellationToken);
        try
        {
            var required = await snapshot.SecretVersions.Select(item => item.KeyId).Distinct().ToArrayAsync(cancellationToken);
            if (required.Any(id => !ring.Keys.ContainsKey(id)))
                throw new BackupVerificationException("The backup key ring is missing a key required by saved credentials.");
        }
        finally { foreach (var key in ring.Keys.Values) CryptographicOperations.ZeroMemory(key); }
        return compatibility.CurrentSchemaVersion;
    }

    private async Task RotateAsync(CancellationToken cancellationToken)
    {
        foreach (var old in (await ListAsync(cancellationToken)).Skip(options.BackupRetentionCount))
        {
            try
            {
                var path = Path.Combine(options.BackupDirectory, old.FileName);
                await VerifyArchiveAsync(path, cancellationToken);
                File.Delete(path);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or BackupVerificationException)
            {
                // Keep damaged archives and files held open by an active download.
            }
        }
    }

    private static BackupArtifact ToArtifact(string path, BackupManifest manifest) =>
        new(manifest.Id, Path.GetFileName(path), new FileInfo(path).Length, null, manifest.LastMigrationId, manifest.AppVersion, manifest.CreatedAt);
    private static string FileName(BackupManifest manifest) => $"allstarr-backup-{manifest.CreatedAt.UtcDateTime:yyyyMMdd-HHmmss}-{manifest.Id:N}.zip";
    internal static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }
    internal static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    internal static FileStream CreatePrivateFile(string path)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.None, Options = FileOptions.Asynchronous };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path, options);
    }
    private static void MakePrivate(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    internal sealed record BackupManifest(int FormatVersion, Guid Id, string AppVersion, string LastMigrationId,
        DateTimeOffset CreatedAt, Dictionary<string, string> Sha256);
}
