using System.Diagnostics;
using System.Data.Common;
using allstarr.Core.Operations;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Storage;

public sealed class DurableStorageInitializer : IHostedService
{
    private readonly IDbContextFactory<AllstarrDbContext> _contextFactory;
    private readonly DurableStorageOptions _options;
    private readonly DurableStorageState _state;
    private readonly DurableMigrationLock _migrationLock;
    private readonly OperationalRuntimeState? _runtimeState;
    private readonly ILogger<DurableStorageInitializer> _logger;

    public DurableStorageInitializer(
        IDbContextFactory<AllstarrDbContext> contextFactory,
        DurableStorageOptions options,
        DurableStorageState state,
        ILogger<DurableStorageInitializer> logger,
        DurableMigrationLock? migrationLock = null,
        OperationalRuntimeState? runtimeState = null)
    {
        _contextFactory = contextFactory;
        _options = options;
        _state = state;
        _logger = logger;
        _migrationLock = migrationLock ?? new DurableMigrationLock(options);
        _runtimeState = runtimeState;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await InitializeOnceAsync(cancellationToken);
                return;
            }
            catch (Exception ex) when (
                attempt < _options.ConnectionRetryCount &&
                IsTransientConnectionFailure(ex) &&
                !cancellationToken.IsCancellationRequested)
            {
                var delay = TimeSpan.FromSeconds(Math.Min(5, Math.Pow(2, attempt)));
                _logger.LogWarning(
                    "Durable storage initialization attempt {Attempt} failed transiently ({ExceptionType}); retrying",
                    attempt + 1,
                    ex.GetType().Name);
                await Task.Delay(delay, cancellationToken);
            }
            catch (MigrationLockException ex)
            {
                _state.Set(DurableStorageReadiness.Unavailable, errorCode: "migration_lock_unavailable");
                _logger.LogError(
                    "Durable storage migration lock failed for {StorageProvider} ({ExceptionType})",
                    _options.ParseProvider(),
                    ex.GetType().Name);
                return;
            }
            catch (Exception ex)
            {
                _state.Set(DurableStorageReadiness.Unavailable, errorCode: "database_initialization_failed");
                _logger.LogError(
                    "Durable storage initialization failed for {StorageProvider} ({ExceptionType})",
                    _options.ParseProvider(),
                    ex.GetType().Name);
                return;
            }
        }
    }

    private async Task InitializeOnceAsync(CancellationToken cancellationToken)
    {
        var bootstrapConfirmation = PrepareSqliteDatabase();
        if (bootstrapConfirmation.Blocked)
        {
            return;
        }

        EnsureSqliteDirectory();
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        if (_options.AutoMigrate)
        {
            if (!await ApplyMigrationsAsync(context, cancellationToken))
            {
                return;
            }
        }
        else
        {
            if (!await context.Database.CanConnectAsync(cancellationToken))
            {
                _state.Set(DurableStorageReadiness.Unavailable, errorCode: "database_unavailable");
                return;
            }

            var compatibility = await DurableSchemaCompatibility.InspectAsync(
                context,
                cancellationToken);
            if (!compatibility.IsCurrent)
            {
                SetSchemaIncompatible(compatibility);
                return;
            }
        }

        var current = await DurableSchemaCompatibility.InspectAsync(context, cancellationToken);
        if (!current.IsCurrent)
        {
            SetSchemaIncompatible(current);
            return;
        }

        if (!ConsumeSqliteBootstrapConfirmation(bootstrapConfirmation.ConfirmationPath))
        {
            return;
        }

        var schemaVersion = current.CurrentSchemaVersion;
        _state.Set(DurableStorageReadiness.Ready, schemaVersion);
        _logger.LogInformation(
            "Durable storage ready using {StorageProvider} at schema {SchemaVersion}",
            _options.ParseProvider(),
            schemaVersion);
    }

    private static bool IsTransientConnectionFailure(Exception exception) =>
        exception is TimeoutException ||
        exception is DbException databaseException && databaseException.IsTransient ||
        exception.InnerException != null && IsTransientConnectionFailure(exception.InnerException);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task<bool> ApplyMigrationsAsync(
        AllstarrDbContext context,
        CancellationToken cancellationToken)
    {
        using var activity = PlatformDiagnostics.ActivitySource.StartActivity("storage.migrate");
        var started = Stopwatch.GetTimestamp();
        var succeeded = false;
        try
        {
            await using var migrationLease = await _migrationLock.AcquireAsync(cancellationToken);
            var compatibility = await DurableSchemaCompatibility.InspectAsync(
                context,
                cancellationToken);
            if (compatibility.Status == DurableSchemaCompatibilityStatus.UnsupportedVersion)
            {
                SetSchemaIncompatible(compatibility);
                activity?.SetStatus(ActivityStatusCode.Error);
                return false;
            }

            await context.Database.MigrateAsync(cancellationToken);
            succeeded = true;
            activity?.SetStatus(ActivityStatusCode.Ok);
            return true;
        }
        catch
        {
            activity?.SetStatus(ActivityStatusCode.Error);
            throw;
        }
        finally
        {
            activity?.SetTag("outcome", succeeded ? "success" : "error");
            _runtimeState?.RecordMigration(Stopwatch.GetElapsedTime(started), succeeded);
        }
    }

    private void SetSchemaIncompatible(DurableSchemaCompatibilitySnapshot compatibility)
    {
        _state.Set(
            DurableStorageReadiness.SchemaIncompatible,
            compatibility.AppliedSchemaVersion,
            DurableSchemaCompatibility.ErrorCode(compatibility));
    }

    private void EnsureSqliteDirectory()
    {
        var path = _options.GetSqlitePath();
        var directory = path == null ? null : Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private SqliteBootstrapPreparation PrepareSqliteDatabase()
    {
        var databasePath = _options.GetSqlitePath();
        if (databasePath == null)
        {
            return new SqliteBootstrapPreparation(false, null);
        }

        var confirmationPath = ValidBootstrapConfirmationPath();
        if (File.Exists(databasePath))
        {
            return new SqliteBootstrapPreparation(false, confirmationPath);
        }

        if (!_options.AutoMigrate)
        {
            SetMissingSqliteDatabase();
            return new SqliteBootstrapPreparation(true, null);
        }

        if (confirmationPath == null)
        {
            var configuredConfirmation = _options.GetSqliteBootstrapConfirmationPath();
            _state.Set(
                DurableStorageReadiness.Unavailable,
                errorCode: configuredConfirmation == null || !File.Exists(configuredConfirmation)
                    ? "sqlite_database_missing"
                    : "sqlite_bootstrap_confirmation_invalid");
            _logger.LogWarning(
                "SQLite database is missing and no valid one-shot bootstrap confirmation is available");
            return new SqliteBootstrapPreparation(true, null);
        }

        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        try
        {
            using var database = new FileStream(
                databasePath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.WriteThrough);
            database.Flush(flushToDisk: true);
        }
        catch (IOException) when (File.Exists(databasePath))
        {
            // Another instance with the same confirmation won the bootstrap race.
        }

        return new SqliteBootstrapPreparation(false, confirmationPath);
    }

    private string? ValidBootstrapConfirmationPath()
    {
        var confirmationPath = _options.GetSqliteBootstrapConfirmationPath();
        if (confirmationPath == null || !File.Exists(confirmationPath))
        {
            return null;
        }

        try
        {
            var file = new FileInfo(confirmationPath);
            if (file.Length > 256)
            {
                return null;
            }

            return string.Equals(
                File.ReadAllText(confirmationPath).Trim(),
                DurableStorageOptions.SqliteBootstrapConfirmation,
                StringComparison.Ordinal)
                ? confirmationPath
                : null;
        }
        catch
        {
            return null;
        }
    }

    private bool ConsumeSqliteBootstrapConfirmation(string? confirmationPath)
    {
        if (confirmationPath == null)
        {
            return true;
        }

        try
        {
            File.Delete(confirmationPath);
            if (!File.Exists(confirmationPath))
            {
                return true;
            }
        }
        catch
        {
        }

        _state.Set(
            DurableStorageReadiness.Unavailable,
            errorCode: "sqlite_bootstrap_confirmation_not_consumed");
        _logger.LogError(
            "SQLite bootstrap confirmation could not be consumed after initialization");
        return false;
    }

    private void SetMissingSqliteDatabase()
    {
        _state.Set(
            DurableStorageReadiness.Unavailable,
            errorCode: "sqlite_database_missing");
        _logger.LogWarning(
            "SQLite database is missing and automatic migration is disabled");
    }

    private readonly record struct SqliteBootstrapPreparation(
        bool Blocked,
        string? ConfirmationPath);
}
