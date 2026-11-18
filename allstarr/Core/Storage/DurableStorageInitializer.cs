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
            await allstarr.Core.Settings.RetiredProviderSettings.RemoveAsync(context, cancellationToken);
            await GatherStatisticsAsync(context, cancellationToken);
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

    // Without table statistics SQLite guesses that a listener's id narrows any search and walks every row they own
    // instead of the matching index (100 ms where 0.1 ms would do). Gathering them takes well under a second, and
    // optimize later refreshes only what changed; the server starts fresh each day.
    private async Task GatherStatisticsAsync(AllstarrDbContext context, CancellationToken cancellationToken)
    {
        try
        {
            await using var command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT count(*) FROM sqlite_master WHERE name = 'sqlite_stat1';";
            var gathered = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) > 0;
            await context.Database.ExecuteSqlRawAsync(gathered ? "PRAGMA optimize=0x10002;" : "ANALYZE;", cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("SQLite statistics could not be gathered ({ExceptionType})", exception.GetType().Name);
        }
    }

    internal static bool IsNetworkFileSystem(DriveType type, string format) =>
        type == DriveType.Network || format.ToLowerInvariant() is "nfs" or "nfs4" or "cifs" or "smb" or "smbfs" or "smb2" or "smb3";

    private void SetIncompatible(DurableSchemaCompatibilitySnapshot compatibility) =>
        state.Set(DurableStorageReadiness.SchemaIncompatible, compatibility.AppliedSchemaVersion,
            DurableSchemaCompatibility.ErrorCode(compatibility));

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
