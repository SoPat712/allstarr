using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace allstarr.Core.Storage;

public sealed record BackupArtifact(
    Guid Id,
    DurableStorageProvider Provider,
    string ArtifactPath,
    string ManifestPath,
    string Sha256,
    string SchemaVersion,
    DateTimeOffset CreatedAt)
{
    public string ApplicationVersion { get; init; } = AppVersion.Version;
}

public sealed class BackupVerificationException(string message) : InvalidOperationException(message);

public sealed class DurableBackupService
{
    private readonly IDbContextFactory<AllstarrDbContext> _contextFactory;
    private readonly DurableStorageOptions _options;
    private readonly DurableStorageState _storageState;
    private readonly IStorageProcessRunner _processRunner;
    private readonly IDurableRestoreTargetVerifier _restoreTargetVerifier;

    public DurableBackupService(
        IDbContextFactory<AllstarrDbContext> contextFactory,
        DurableStorageOptions options,
        DurableStorageState storageState,
        IStorageProcessRunner processRunner,
        IDurableRestoreTargetVerifier? restoreTargetVerifier = null)
    {
        _contextFactory = contextFactory;
        _options = options;
        _storageState = storageState;
        _processRunner = processRunner;
        _restoreTargetVerifier = restoreTargetVerifier ?? new DurableRestoreTargetVerifier();
    }

    public async Task<BackupArtifact> CreateAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = _storageState.GetSnapshot();
        if (snapshot.Readiness != DurableStorageReadiness.Ready)
        {
            throw new InvalidOperationException("A backup cannot start while durable storage is unready.");
        }

        var provider = _options.ParseProvider();
        var now = DateTimeOffset.UtcNow;
        var id = Guid.CreateVersion7();
        var directory = Path.GetFullPath(_options.BackupDirectory);
        Directory.CreateDirectory(directory);
        var extension = ".dump";
        var baseName = $"allstarr-{provider.ToString().ToLowerInvariant()}-{now:yyyyMMddTHHmmssZ}-{id:N}";
        var artifactPath = Path.Combine(directory, baseName + extension);
        var temporaryPath = artifactPath + ".partial";

        try
        {
            await BackupPostgresAsync(temporaryPath, cancellationToken);

            File.Move(temporaryPath, artifactPath, overwrite: false);
            var hash = await ComputeSha256Async(artifactPath, cancellationToken);
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
            var compatibility = await DurableSchemaCompatibility.InspectAsync(context, cancellationToken);
            if (!compatibility.IsCurrent)
            {
                throw new BackupVerificationException(
                    "A backup cannot be created from an incompatible durable schema.");
            }

            var schemaVersion = compatibility.CurrentSchemaVersion;
            var manifestPath = artifactPath + ".manifest.json";
            var manifest = new BackupManifest
            {
                FormatVersion = BackupManifest.CurrentFormatVersion,
                Id = id,
                Provider = provider,
                ArtifactFile = Path.GetFileName(artifactPath),
                Sha256 = hash,
                SchemaVersion = schemaVersion,
                ApplicationVersion = AppVersion.Version,
                CreatedAt = now,
                SecretKeyMaterialIncluded = false
            };
            await File.WriteAllTextAsync(
                manifestPath,
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken);
            var artifact = new BackupArtifact(
                id,
                provider,
                artifactPath,
                manifestPath,
                hash,
                schemaVersion,
                now);
            await VerifyAsync(artifact, cancellationToken);
            await RecordAsync(artifact, cancellationToken);
            return artifact;
        }
        catch
        {
            TryDelete(temporaryPath);
            TryDelete(artifactPath);
            TryDelete(artifactPath + ".manifest.json");
            throw;
        }
    }

    public async Task<BackupArtifact> LoadArtifactAsync(
        string artifactPath,
        string manifestPath,
        DurableStorageProvider expectedProvider,
        string expectedSha256,
        CancellationToken cancellationToken = default)
    {
        artifactPath = Path.GetFullPath(artifactPath);
        manifestPath = Path.GetFullPath(manifestPath);
        if (!File.Exists(artifactPath))
        {
            throw new BackupVerificationException("Backup artifact or manifest is missing.");
        }

        var manifest = await ReadManifestAsync(manifestPath, cancellationToken);
        await ValidateManifestSchemaAsync(manifest, cancellationToken);
        if (manifest.Provider != expectedProvider ||
            !manifest.Sha256.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new BackupVerificationException(
                "Backup manifest metadata does not match the requested restore artifact.");
        }

        if (!manifest.ArtifactFile.Equals(Path.GetFileName(artifactPath), StringComparison.Ordinal))
        {
            throw new BackupVerificationException(
                "Backup manifest artifact name does not match the restore artifact.");
        }

        return new BackupArtifact(
                manifest.Id,
                manifest.Provider,
                artifactPath,
                manifestPath,
                manifest.Sha256,
                manifest.SchemaVersion,
                manifest.CreatedAt)
            with
        { ApplicationVersion = manifest.ApplicationVersion };
    }

    public async Task VerifyAsync(
        BackupArtifact artifact,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(artifact.ArtifactPath) || !File.Exists(artifact.ManifestPath))
        {
            throw new BackupVerificationException("Backup artifact or manifest is missing.");
        }


        var manifest = await ReadManifestAsync(artifact.ManifestPath, cancellationToken);
        await ValidateManifestSchemaAsync(manifest, cancellationToken);
        ValidateManifestMatchesArtifact(manifest, artifact);

        var actualHash = await ComputeSha256Async(artifact.ArtifactPath, cancellationToken);
        if (!actualHash.Equals(artifact.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new BackupVerificationException("Backup checksum verification failed.");
        }

        if (artifact.Provider != DurableStorageProvider.Postgres)
        {
            throw new BackupVerificationException("Only PostgreSQL backup artifacts are supported.");
        }

        var result = await _processRunner.RunAsync(new StorageProcessRequest(
            "pg_restore",
            ["--list", artifact.ArtifactPath],
            new Dictionary<string, string?>()), cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new BackupVerificationException(
                result.SafeError ?? "Postgres backup catalog verification failed.");
        }
    }

    public async Task<DurableSchemaCompatibilitySnapshot> RestorePostgresAsync(
        BackupArtifact artifact,
        string targetConnectionString,
        bool destructiveRestoreConfirmed,
        string isolatedTargetDatabaseConfirmation,
        CancellationToken cancellationToken = default)
    {
        if (artifact.Provider != DurableStorageProvider.Postgres)
        {
            throw new InvalidOperationException("The selected backup is not a Postgres artifact.");
        }

        if (!destructiveRestoreConfirmed)
        {
            throw new InvalidOperationException("Postgres restore requires explicit destructive confirmation.");
        }

        var connection = new NpgsqlConnectionStringBuilder(targetConnectionString);
        var targetDatabase = connection.Database?.Trim();
        if (string.IsNullOrWhiteSpace(targetDatabase))
        {
            throw new ArgumentException(
                "The Postgres restore target must name a database.",
                nameof(targetConnectionString));
        }

        if (!string.Equals(
                isolatedTargetDatabaseConfirmation?.Trim(),
                targetDatabase,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Postgres restore requires the isolated target database name to be confirmed exactly.");
        }

        var current = new NpgsqlConnectionStringBuilder(_options.ConnectionString);
        if (string.Equals(
                current.Database?.Trim(),
                targetDatabase,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Postgres restore refuses the configured current database name. Restore into a new isolated database name.");
        }

        await VerifyAsync(artifact, cancellationToken);
        await RecordRestoreStatusAsync(artifact, "verification_pending", null, cancellationToken);
        var restoreCompleted = false;
        try
        {
            var result = await _processRunner.RunAsync(new StorageProcessRequest(
                "pg_restore",
                [
                    "--clean",
                    "--if-exists",
                    "--no-owner",
                    "--no-privileges",
                    "--dbname",
                    connection.Database!,
                    artifact.ArtifactPath
                ],
                PostgresEnvironment(connection)), cancellationToken);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(result.SafeError ?? "Postgres restore failed.");
            }

            restoreCompleted = true;
            var compatibility = await _restoreTargetVerifier.VerifyAsync(
                DurableStorageProvider.Postgres,
                targetConnectionString,
                cancellationToken);
            await RecordRestoreStatusAsync(
                artifact,
                "verified",
                DateTimeOffset.UtcNow,
                cancellationToken);
            return compatibility;
        }
        catch
        {
            await TryRecordRestoreFailureAsync(
                artifact,
                restoreCompleted ? "verification_failed" : "restore_failed",
                CancellationToken.None);
            throw;
        }
    }

    private async Task BackupPostgresAsync(string destinationPath, CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnectionStringBuilder(_options.ConnectionString);
        var result = await _processRunner.RunAsync(new StorageProcessRequest(
            "pg_dump",
            [
                "--format=custom",
                "--no-owner",
                "--no-privileges",
                "--file",
                destinationPath,
                connection.Database!
            ],
            PostgresEnvironment(connection)), cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(result.SafeError ?? "Postgres backup failed.");
        }
    }

    private static Dictionary<string, string?> PostgresEnvironment(
        NpgsqlConnectionStringBuilder connection) => new(StringComparer.Ordinal)
        {
            ["PGHOST"] = connection.Host,
            ["PGPORT"] = connection.Port.ToString(),
            ["PGDATABASE"] = connection.Database,
            ["PGUSER"] = connection.Username,
            ["PGPASSWORD"] = connection.Password,
            ["PGSSLMODE"] = connection.SslMode.ToString().ToLowerInvariant()
        };

    private async Task RecordAsync(BackupArtifact artifact, CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        context.Backups.Add(new BackupRecord
        {
            Id = artifact.Id,
            StorageProvider = artifact.Provider.ToString(),
            ArtifactPath = artifact.ArtifactPath,
            Sha256 = artifact.Sha256,
            SchemaVersion = artifact.SchemaVersion,
            ApplicationVersion = AppVersion.Version,
            Status = "verified",
            CreatedAt = artifact.CreatedAt,
            VerifiedAt = DateTimeOffset.UtcNow
        });
        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task RecordRestoreStatusAsync(
        BackupArtifact artifact,
        string status,
        DateTimeOffset? verifiedAt,
        CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var record = await context.Backups.SingleOrDefaultAsync(
            item => item.Id == artifact.Id,
            cancellationToken);
        if (record == null)
        {
            record = new BackupRecord
            {
                Id = artifact.Id,
                StorageProvider = artifact.Provider.ToString(),
                ArtifactPath = artifact.ArtifactPath,
                Sha256 = artifact.Sha256,
                SchemaVersion = artifact.SchemaVersion,
                ApplicationVersion = artifact.ApplicationVersion,
                Status = "verified",
                CreatedAt = artifact.CreatedAt,
                VerifiedAt = DateTimeOffset.UtcNow
            };
            context.Backups.Add(record);
        }
        else if (!record.StorageProvider.Equals(artifact.Provider.ToString(), StringComparison.Ordinal) ||
                 !record.Sha256.Equals(artifact.Sha256, StringComparison.OrdinalIgnoreCase) ||
                 !record.SchemaVersion.Equals(artifact.SchemaVersion, StringComparison.Ordinal) ||
                 record.CreatedAt != artifact.CreatedAt)
        {
            throw new BackupVerificationException(
                "Backup manifest identity conflicts with the existing backup catalog record.");
        }

        record.RestoreStatus = status;
        record.RestoreVerifiedAt = verifiedAt;
        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task TryRecordRestoreFailureAsync(
        BackupArtifact artifact,
        string status,
        CancellationToken cancellationToken)
    {
        try
        {
            await RecordRestoreStatusAsync(
                artifact,
                status,
                null,
                cancellationToken);
        }
        catch
        {
            // Preserve the restore or verification exception as the operator-facing failure.
        }
    }

    private async Task ValidateManifestSchemaAsync(
        BackupManifest manifest,
        CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var known = context.Database.GetMigrations().ToArray();
        if (known.Length == 0 ||
            !manifest.SchemaVersion.Equals(known[^1], StringComparison.Ordinal))
        {
            throw new BackupVerificationException(
                "Backup manifest schema does not match this Allstarr build.");
        }
    }

    private static void ValidateManifestMatchesArtifact(
        BackupManifest manifest,
        BackupArtifact artifact)
    {
        if (manifest.Id != artifact.Id ||
            manifest.Provider != artifact.Provider ||
            !manifest.ArtifactFile.Equals(Path.GetFileName(artifact.ArtifactPath), StringComparison.Ordinal) ||
            !manifest.Sha256.Equals(artifact.Sha256, StringComparison.OrdinalIgnoreCase) ||
            !manifest.SchemaVersion.Equals(artifact.SchemaVersion, StringComparison.Ordinal) ||
            manifest.CreatedAt != artifact.CreatedAt ||
            !manifest.ApplicationVersion.Equals(
                artifact.ApplicationVersion,
                StringComparison.Ordinal))
        {
            throw new BackupVerificationException(
                "Backup manifest metadata does not match the restore artifact.");
        }
    }

    private static async Task<BackupManifest> ReadManifestAsync(
        string manifestPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(manifestPath))
        {
            throw new BackupVerificationException("Backup artifact or manifest is missing.");
        }

        var info = new FileInfo(manifestPath);
        if (info.Length is <= 0 or > 1_048_576)
        {
            throw new BackupVerificationException("Backup manifest size is invalid.");
        }

        try
        {
            await using var stream = new FileStream(
                manifestPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                8192,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var document = await JsonDocument.ParseAsync(
                stream,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 8
                },
                cancellationToken);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new BackupVerificationException("Backup manifest must be a JSON object.");
            }

            var expected = new HashSet<string>(StringComparer.Ordinal)
            {
                "FormatVersion",
                "Id",
                "Provider",
                "ArtifactFile",
                "Sha256",
                "SchemaVersion",
                "ApplicationVersion",
                "CreatedAt",
                "SecretKeyMaterialIncluded"
            };
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!expected.Contains(property.Name) || !seen.Add(property.Name))
                {
                    throw new BackupVerificationException(
                        "Backup manifest contains an unknown or repeated field.");
                }
            }

            if (seen.Count != expected.Count ||
                !root.GetProperty("FormatVersion").TryGetInt32(out var formatVersion) ||
                root.GetProperty("Id").ValueKind != JsonValueKind.String ||
                !root.GetProperty("Id").TryGetGuid(out var id) || id == Guid.Empty ||
                root.GetProperty("Provider").ValueKind != JsonValueKind.String ||
                !Enum.TryParse<DurableStorageProvider>(
                    root.GetProperty("Provider").GetString(),
                    ignoreCase: false,
                    out var provider) ||
                root.GetProperty("ArtifactFile").ValueKind != JsonValueKind.String ||
                root.GetProperty("Sha256").ValueKind != JsonValueKind.String ||
                root.GetProperty("SchemaVersion").ValueKind != JsonValueKind.String ||
                root.GetProperty("ApplicationVersion").ValueKind != JsonValueKind.String ||
                root.GetProperty("CreatedAt").ValueKind != JsonValueKind.String ||
                !root.GetProperty("CreatedAt").TryGetDateTimeOffset(out var createdAt) ||
                root.GetProperty("SecretKeyMaterialIncluded").ValueKind is not
                    (JsonValueKind.True or JsonValueKind.False))
            {
                throw new BackupVerificationException(
                    "Backup manifest is missing a required field or contains an invalid field type.");
            }

            var artifactFile = root.GetProperty("ArtifactFile").GetString()!;
            var sha256 = root.GetProperty("Sha256").GetString()!;
            var schemaVersion = root.GetProperty("SchemaVersion").GetString()!;
            var applicationVersion = root.GetProperty("ApplicationVersion").GetString()!;
            var includesSecretMaterial = root.GetProperty("SecretKeyMaterialIncluded").GetBoolean();
            if (formatVersion != BackupManifest.CurrentFormatVersion ||
                !Enum.IsDefined(provider) ||
                !provider.ToString().Equals(
                    root.GetProperty("Provider").GetString(),
                    StringComparison.Ordinal) ||
                includesSecretMaterial ||
                string.IsNullOrWhiteSpace(artifactFile) ||
                artifactFile.Length > 255 ||
                artifactFile is "." or ".." ||
                artifactFile.Contains('/') || artifactFile.Contains('\\') ||
                !artifactFile.Equals(Path.GetFileName(artifactFile), StringComparison.Ordinal) ||
                sha256.Length != 64 || sha256.Any(character => !Uri.IsHexDigit(character)) ||
                string.IsNullOrWhiteSpace(schemaVersion) || schemaVersion.Length > 200 ||
                schemaVersion.Any(char.IsControl) ||
                string.IsNullOrWhiteSpace(applicationVersion) || applicationVersion.Length > 50 ||
                applicationVersion.Any(char.IsControl) ||
                createdAt.Offset != TimeSpan.Zero)
            {
                throw new BackupVerificationException("Backup manifest values are invalid.");
            }

            return new BackupManifest
            {
                FormatVersion = formatVersion,
                Id = id,
                Provider = provider,
                ArtifactFile = artifactFile,
                Sha256 = sha256.ToLowerInvariant(),
                SchemaVersion = schemaVersion,
                ApplicationVersion = applicationVersion,
                CreatedAt = createdAt,
                SecretKeyMaterialIncluded = includesSecretMaterial
            };
        }
        catch (JsonException)
        {
            throw new BackupVerificationException("Backup manifest JSON is invalid.");
        }
    }

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private sealed class BackupManifest
    {
        public const int CurrentFormatVersion = 1;

        public int FormatVersion { get; set; }
        public Guid Id { get; set; }
        [JsonConverter(typeof(JsonStringEnumConverter<DurableStorageProvider>))]
        public DurableStorageProvider Provider { get; set; }
        public string ArtifactFile { get; set; } = string.Empty;
        public string Sha256 { get; set; } = string.Empty;
        public string SchemaVersion { get; set; } = string.Empty;
        public string ApplicationVersion { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
        public bool SecretKeyMaterialIncluded { get; set; }
    }
}
