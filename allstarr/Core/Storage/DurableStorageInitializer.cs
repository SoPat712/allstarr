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
                    ex,
                    "Durable storage initialization failed for {StorageProvider} ({ExceptionType}): {Failure}",
                    _options.ParseProvider(),
                    ex.GetType().Name,
                    ex.GetBaseException().Message);
                return;
            }
        }
    }

    private async Task InitializeOnceAsync(CancellationToken cancellationToken)
    {
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
            if (compatibility.Status != DurableSchemaCompatibilityStatus.Current)
            {
                _logger.LogWarning(
                    "Durable storage schema compatibility check: Status={Status}, Unknown=[{Unknown}], Missing=[{Missing}]",
                    compatibility.Status,
                    string.Join(", ", compatibility.UnknownMigrations),
                    string.Join(", ", compatibility.MissingMigrations));
            }
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

}
