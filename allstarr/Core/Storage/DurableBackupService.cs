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
    public string Status => Verification;
    public DateTimeOffset? VerifiedAt { get; init; }
    public bool IncludesKeyRing => true;
    public string Origin { get; init; } = "legacy";
    public string[] RetentionBuckets { get; init; } = [];
    public string Verification { get; init; } = "unverified";
}

public sealed class BackupVerificationException(string message) : InvalidOperationException(message);

public sealed partial class DurableBackupService(
    IDbContextFactory<AllstarrDbContext> contextFactory,
    StorageOptions options,
    DurableStorageState storageState,
    SecretStoreOptions secretOptions,
    allstarr.Core.Settings.DurableRuntimeSettingsService? runtimeSettings = null,
    allstarr.Core.Jobs.DurableJobQueue? jobs = null,
    allstarr.Core.Operations.IPlatformClock? clock = null,
    allstarr.Core.ManagedFiles.IManagedFileOperations? fileOperations = null,
    IConfiguration? configuration = null) : IDisposable
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
        await _gate.WaitAsync(cancellationToken);
        try
        {
            RequireReady();
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var policy = await ReadPolicyAsync(db, cancellationToken);
            var directory = ResolveDirectory(policy);
            if (directory.Contains('\'')) throw new BackupVerificationException("The backup directory must not contain a single quote.");
            var row = await NewArtifactAsync(db, policy, "manual", null, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return await CreateReservedAsync(row, null, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    internal async Task<BackupManifest> VerifyArchiveAsync(string path, CancellationToken cancellationToken)
    {
        await using var file = OpenArchiveFile(path);
        return await VerifyArchiveStreamAsync(file, cancellationToken);
    }

    private async Task<BackupManifest> VerifyArchiveStreamAsync(Stream file, CancellationToken cancellationToken)
    {
        using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
        var manifest = await ReadManifestAsync(archive, cancellationToken);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!context.Database.GetMigrations().Contains(manifest.LastMigrationId, StringComparer.Ordinal))
            throw new BackupVerificationException("The backup schema is not supported by this build.");
        foreach (var (name, expected) in manifest.Sha256)
        {
            var entry = archive.GetEntry(name)!;
            await using var content = entry.Open();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            long total = 0;
            int count;
            while ((count = await content.ReadAsync(buffer, cancellationToken)) != 0)
            {
                total += count;
                if (total > EntryLimit(name)) throw new BackupVerificationException("The backup exceeds its size limit.");
                hash.AppendData(buffer, 0, count);
            }
            if (total != entry.Length) throw new BackupVerificationException("The backup entry length is invalid.");
            var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
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
        if (archive.Entries.Any(item => item.Length < 0 || item.Length > EntryLimit(item.FullName)))
            throw new BackupVerificationException("Backup metadata exceeds its size limit.");
        try
        {
            await using var stream = entry.Open();
            using var bounded = new MemoryStream();
            await CopyBoundedAsync(stream, bounded, EntryLimit(ManifestFile), cancellationToken);
            bounded.Position = 0;
            using var document = await JsonDocument.ParseAsync(bounded, cancellationToken: cancellationToken);
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

    internal async Task<string> VerifySnapshotAsync(string databasePath, string keyPath, CancellationToken cancellationToken, bool allowPendingMigrations = false)
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
        var applied = (await snapshot.Database.GetAppliedMigrationsAsync(cancellationToken)).ToArray();
        var known = snapshot.Database.GetMigrations().ToArray();
        if ((!allowPendingMigrations && !compatibility.IsCurrent) || applied.Length == 0 ||
            !applied.SequenceEqual(known.Take(applied.Length), StringComparer.Ordinal))
            throw new BackupVerificationException("The backup database schema is not supported by this build.");
        var ring = await new FileSecretKeyRingProvider(new SecretStoreOptions { KeyRingPath = keyPath }).LoadAsync(cancellationToken);
        try
        {
            var required = await snapshot.SecretVersions.Select(item => item.KeyId).Distinct().ToArrayAsync(cancellationToken);
            if (required.Any(id => !ring.Keys.ContainsKey(id)))
                throw new BackupVerificationException("The backup key ring is missing a key required by saved credentials.");
        }
        finally { foreach (var key in ring.Keys.Values) CryptographicOperations.ZeroMemory(key); }
        return compatibility.AppliedSchemaVersion;
    }

    private static BackupArtifact ToArtifact(string path, BackupManifest manifest) =>
        new(manifest.Id, Path.GetFileName(path), new FileInfo(path).Length, null, manifest.LastMigrationId, manifest.AppVersion, manifest.CreatedAt);
    private static string FileName(BackupManifest manifest) => $"allstarr-backup-{manifest.CreatedAt.UtcDateTime:yyyyMMdd-HHmmss}-{manifest.Id:N}.zip";
    internal static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = OpenArchiveFile(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }
    internal static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    internal static FileStream CreatePrivateFile(string path, FileShare share = FileShare.None)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = share, Options = FileOptions.Asynchronous };
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
