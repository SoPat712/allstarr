using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Storage;

public sealed class DurableStorageInitializer(
    IDbContextFactory<AllstarrDbContext> contextFactory,
    StorageOptions options,
    DurableStorageState state,
    ILogger<DurableStorageInitializer> logger,
    DurableBackupService backups) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            options.Validate();
            Directory.CreateDirectory(options.DataDirectory);
            var path = Path.GetFullPath(options.DataDirectory);
            var drive = DriveInfo.GetDrives()
                .Where(item => path == item.RootDirectory.FullName ||
                    path.StartsWith(Path.TrimEndingDirectorySeparator(item.RootDirectory.FullName) +
                                    Path.DirectorySeparatorChar, StringComparison.Ordinal))
                .OrderByDescending(item => item.RootDirectory.FullName.Length)
                .FirstOrDefault();
            if (drive != null && IsNetworkFileSystem(drive.DriveType, drive.DriveFormat))
            {
                state.Set(DurableStorageReadiness.Unavailable, errorCode: "database_requires_local_disk");
                logger.LogError("The database data directory must be on local disk; network filesystems are unsupported");
                return;
            }

            await backups.ApplyPendingRestoreAsync(cancellationToken);
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            await context.Database.OpenConnectionAsync(cancellationToken);
            await context.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken);
            var before = await DurableSchemaCompatibility.InspectAsync(context, cancellationToken);
            if (before.Status == DurableSchemaCompatibilityStatus.UnsupportedVersion)
            {
                SetIncompatible(before);
                return;
            }
            if (options.AutoMigrate)
                await context.Database.MigrateAsync(cancellationToken);
            var current = await DurableSchemaCompatibility.InspectAsync(context, cancellationToken);
            if (!current.IsCurrent)
            {
                SetIncompatible(current);
                return;
            }

            await using var check = context.Database.GetDbConnection().CreateCommand();
            check.CommandText = "PRAGMA quick_check;";
            if (!string.Equals(await check.ExecuteScalarAsync(cancellationToken) as string, "ok", StringComparison.Ordinal))
            {
                state.Set(DurableStorageReadiness.Unavailable, errorCode: "database_integrity_check_failed");
                logger.LogError("SQLite startup integrity check failed");
                return;
            }
            state.Set(DurableStorageReadiness.Ready, current.CurrentSchemaVersion);
            logger.LogInformation("SQLite storage ready at schema {SchemaVersion}", current.CurrentSchemaVersion);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var restoring = backups.HasPendingRestore;
            state.Set(DurableStorageReadiness.Unavailable, errorCode: restoring ? "restore_application_failed" : "database_initialization_failed");
            logger.LogError("SQLite storage initialization failed ({ExceptionType}); restore pending: {RestorePending}", exception.GetType().Name, restoring);
        }
    }

    internal static bool IsNetworkFileSystem(DriveType type, string format) =>
        type == DriveType.Network || format.ToLowerInvariant() is "nfs" or "nfs4" or "cifs" or "smb" or "smbfs" or "smb2" or "smb3";

    private void SetIncompatible(DurableSchemaCompatibilitySnapshot compatibility) =>
        state.Set(DurableStorageReadiness.SchemaIncompatible, compatibility.AppliedSchemaVersion,
            DurableSchemaCompatibility.ErrorCode(compatibility));

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
