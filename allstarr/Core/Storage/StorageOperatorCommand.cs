using System.Text.Json;
using allstarr.Core.Secrets;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Storage;

public static class StorageOperatorCommand
{
    public static bool IsStorageCommand(string[] args) =>
        args.Length > 0 && args[0].Equals("storage", StringComparison.OrdinalIgnoreCase);

    public static async Task<int> RunAsync(
        IServiceCollection services,
        string[] args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        if (args.Length < 2 || args[1] is "help" or "--help" or "-h")
        {
            await output.WriteLineAsync(HelpText());
            return 0;
        }

        try
        {
            var arguments = ParseArguments(args.Skip(2).ToArray());
            await using var provider = services.BuildServiceProvider();
            return args[1].ToLowerInvariant() switch
            {
                "backup" => await Backup(provider, output, cancellationToken),
                "export" => await Export(provider, arguments, output, cancellationToken),
                "restore-sqlite" => await RestoreSqlite(
                    provider, arguments, output, cancellationToken),
                "restore-postgres" => await RestorePostgres(
                    provider, arguments, output, cancellationToken),
                "import" => await Import(provider, arguments, output, cancellationToken),
                "rotate-secrets" => await RotateSecrets(
                    provider, arguments, output, cancellationToken),
                _ => await UnknownCommand(args[1], error)
            };
        }
        catch (Exception ex)
        {
            await error.WriteLineAsync(JsonSerializer.Serialize(new
            {
                status = "failed",
                errorCode = ErrorCode(ex),
                exceptionType = ex.GetType().Name
            }));
            return 1;
        }
    }

    private static async Task<int> Backup(
        IServiceProvider provider,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        await EnsureReady(provider, cancellationToken);
        var artifact = await provider.GetRequiredService<DurableBackupService>()
            .CreateAsync(cancellationToken);
        await WriteJson(output, new
        {
            status = "verified",
            provider = artifact.Provider.ToString(),
            artifactPath = artifact.ArtifactPath,
            manifestPath = artifact.ManifestPath,
            sha256 = artifact.Sha256,
            schemaVersion = artifact.SchemaVersion,
            createdAt = artifact.CreatedAt
        });
        return 0;
    }

    private static async Task<int> Export(
        IServiceProvider provider,
        IReadOnlyDictionary<string, string?> arguments,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        RequireFlag(arguments, "confirm-writes-stopped");
        var destination = RequireValue(arguments, "output");
        await EnsureReady(provider, cancellationToken);
        var artifact = await provider.GetRequiredService<DurableStateTransferService>()
            .ExportAsync(destination, writesQuiesced: true, cancellationToken);
        await WriteJson(output, new
        {
            status = "verified",
            artifactPath = artifact.Path,
            sha256 = artifact.Sha256,
            sourceProvider = artifact.SourceProvider,
            schemaVersion = artifact.SchemaVersion,
            createdAt = artifact.CreatedAt
        });
        return 0;
    }

    private static async Task<int> RestoreSqlite(
        IServiceProvider provider,
        IReadOnlyDictionary<string, string?> arguments,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        RequireFlag(arguments, "confirm-target-offline");
        var service = provider.GetRequiredService<DurableBackupService>();
        var artifact = await BackupArtifactFromArguments(
            service,
            arguments,
            DurableStorageProvider.Sqlite,
            cancellationToken);
        var targetPath = Path.GetFullPath(RequireValue(arguments, "target"));
        var targetConnection = new SqliteConnectionStringBuilder
        {
            DataSource = targetPath
        }.ToString();
        var compatibility = await service.RestoreSqliteToAsync(
            artifact,
            targetConnection,
            overwrite: arguments.ContainsKey("overwrite"),
            cancellationToken);
        await WriteJson(output, new
        {
            status = "verified",
            provider = "Sqlite",
            targetPath,
            sha256 = artifact.Sha256,
            schemaVersion = compatibility.CurrentSchemaVersion
        });
        return 0;
    }

    private static async Task<int> RestorePostgres(
        IServiceProvider provider,
        IReadOnlyDictionary<string, string?> arguments,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        RequireFlag(arguments, "confirm-destructive-restore");
        var isolatedTargetDatabase = RequireValue(
            arguments,
            "confirm-isolated-target-database");
        var service = provider.GetRequiredService<DurableBackupService>();
        var artifact = await BackupArtifactFromArguments(
            service,
            arguments,
            DurableStorageProvider.Postgres,
            cancellationToken);
        var environmentName = RequireValue(arguments, "target-connection-env");
        var targetConnection = Environment.GetEnvironmentVariable(environmentName);
        if (string.IsNullOrWhiteSpace(targetConnection))
        {
            throw new InvalidOperationException(
                "The target connection environment variable is missing or empty.");
        }

        var compatibility = await service.RestorePostgresAsync(
            artifact,
            targetConnection,
            destructiveRestoreConfirmed: true,
            isolatedTargetDatabaseConfirmation: isolatedTargetDatabase,
            cancellationToken);
        await WriteJson(output, new
        {
            status = "verified",
            provider = "Postgres",
            targetConnectionSource = environmentName,
            sha256 = artifact.Sha256,
            schemaVersion = compatibility.CurrentSchemaVersion
        });
        return 0;
    }

    private static async Task<int> Import(
        IServiceProvider provider,
        IReadOnlyDictionary<string, string?> arguments,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        RequireFlag(arguments, "confirm-empty-target");
        await EnsureReady(provider, cancellationToken);
        var path = Path.GetFullPath(RequireValue(arguments, "artifact"));
        var hash = ValidateSha256(RequireValue(arguments, "sha256"));
        var factory = provider.GetRequiredService<IDbContextFactory<AllstarrDbContext>>();
        var artifact = await DurableStateTransferService.LoadArtifactAsync(
            path,
            hash,
            cancellationToken);
        await DurableStateTransferService.ImportAsync(
            artifact,
            factory,
            targetConfirmedEmpty: true,
            cancellationToken);
        await WriteJson(output, new
        {
            status = "imported",
            artifactPath = path,
            sha256 = hash
        });
        return 0;
    }

    private static async Task<int> RotateSecrets(
        IServiceProvider provider,
        IReadOnlyDictionary<string, string?> arguments,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        RequireFlag(arguments, "confirm-writes-stopped");
        await EnsureReady(provider, cancellationToken);
        var result = await provider.GetRequiredService<EncryptedSecretStore>()
            .RotateAllEncryptionAsync(cancellationToken);
        await WriteJson(output, new
        {
            status = "verified",
            activeKeyId = result.ActiveKeyId,
            result.Examined,
            result.Rotated,
            result.AlreadyActive
        });
        return 0;
    }

    private static async Task EnsureReady(
        IServiceProvider provider,
        CancellationToken cancellationToken)
    {
        var initializer = provider.GetRequiredService<DurableStorageInitializer>();
        await initializer.StartAsync(cancellationToken);
        var snapshot = provider.GetRequiredService<DurableStorageState>().GetSnapshot();
        if (snapshot.Readiness != DurableStorageReadiness.Ready)
        {
            throw new InvalidOperationException(
                $"Durable storage is not ready ({snapshot.ErrorCode ?? "unknown"}).");
        }
    }

    private static Task<BackupArtifact> BackupArtifactFromArguments(
        DurableBackupService service,
        IReadOnlyDictionary<string, string?> arguments,
        DurableStorageProvider provider,
        CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(RequireValue(arguments, "artifact"));
        var hash = ValidateSha256(RequireValue(arguments, "sha256"));
        var manifest = arguments.TryGetValue("manifest", out var suppliedManifest) &&
                       !string.IsNullOrWhiteSpace(suppliedManifest)
            ? Path.GetFullPath(suppliedManifest)
            : path + ".manifest.json";
        return service.LoadArtifactAsync(
            path,
            manifest,
            provider,
            hash,
            cancellationToken);
    }

    private static string ValidateSha256(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length != 64 || normalized.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("A 64-character SHA-256 value is required.");
        }

        return normalized;
    }

    private static IReadOnlyDictionary<string, string?> ParseArguments(string[] args)
    {
        var parsed = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index++)
        {
            var token = args[index];
            if (!token.StartsWith("--", StringComparison.Ordinal) || token.Length == 2)
            {
                throw new ArgumentException("Storage command options must use --name syntax.");
            }

            var key = token[2..];
            if (!parsed.TryAdd(key, null))
            {
                throw new ArgumentException($"Storage command option '--{key}' was repeated.");
            }

            if (index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                parsed[key] = args[++index];
            }
        }

        return parsed;
    }

    private static string RequireValue(
        IReadOnlyDictionary<string, string?> arguments,
        string name)
    {
        if (!arguments.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Storage command option '--{name}' requires a value.");
        }

        return value;
    }

    private static void RequireFlag(
        IReadOnlyDictionary<string, string?> arguments,
        string name)
    {
        if (!arguments.TryGetValue(name, out var value) || value != null)
        {
            throw new InvalidOperationException(
                $"Storage command requires the explicit '--{name}' confirmation flag.");
        }
    }

    private static string ErrorCode(Exception exception) => exception switch
    {
        BackupVerificationException => "backup_verification_failed",
        MigrationLockException => "migration_lock_unavailable",
        UnauthorizedAccessException => "access_denied",
        ArgumentException => "invalid_arguments",
        FileNotFoundException => "artifact_not_found",
        _ => "storage_command_failed"
    };

    private static async Task<int> UnknownCommand(string command, TextWriter error)
    {
        await error.WriteLineAsync($"Unknown storage command '{command}'. Run 'storage help'.");
        return 2;
    }

    private static Task WriteJson(TextWriter output, object value) =>
        output.WriteLineAsync(JsonSerializer.Serialize(value));

    private static string HelpText() =>
        """
        Allstarr offline storage commands

          storage backup
          storage export --output <directory> --confirm-writes-stopped
          storage restore-sqlite --artifact <file> --sha256 <hash> --target <file> --confirm-target-offline [--overwrite]
          storage restore-postgres --artifact <file> --sha256 <hash> --target-connection-env <name> --confirm-isolated-target-database <name> --confirm-destructive-restore
          storage import --artifact <file> --sha256 <hash> --confirm-empty-target
          storage rotate-secrets --confirm-writes-stopped

        Stop normal Allstarr instances before export, restore, or import. Postgres target
        credentials must be passed through the named environment variable, never command arguments.
        The isolated Postgres database name confirmation must exactly match the target connection
        and must differ from the database configured for the running Allstarr instance.
        """;
}
