using allstarr.Core.Operations;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Storage;

public interface IDurableStorageRuntimeProbe
{
    Task<DurableStorageSnapshot> CheckAsync(CancellationToken cancellationToken = default);
}

public sealed class DurableStorageRuntimeProbe : IDurableStorageRuntimeProbe, IDisposable
{
    private readonly IDbContextFactory<AllstarrDbContext> _contextFactory;
    private readonly DurableStorageOptions _options;
    private readonly DurableStorageState _state;
    private readonly IPlatformClock _clock;
    private readonly ILogger<DurableStorageRuntimeProbe> _logger;
    private readonly SemaphoreSlim _probeGate = new(1, 1);
    private long _nextProbeAtUtcTicks = DateTimeOffset.MinValue.UtcTicks;

    public DurableStorageRuntimeProbe(
        IDbContextFactory<AllstarrDbContext> contextFactory,
        DurableStorageOptions options,
        DurableStorageState state,
        ILogger<DurableStorageRuntimeProbe> logger)
        : this(contextFactory, options, state, new SystemPlatformClock(), logger)
    {
    }

    public DurableStorageRuntimeProbe(
        IDbContextFactory<AllstarrDbContext> contextFactory,
        DurableStorageOptions options,
        DurableStorageState state,
        IPlatformClock clock,
        ILogger<DurableStorageRuntimeProbe> logger)
    {
        _contextFactory = contextFactory;
        _options = options;
        _state = state;
        _clock = clock;
        _logger = logger;
    }

    public async Task<DurableStorageSnapshot> CheckAsync(
        CancellationToken cancellationToken = default)
    {
        if (_clock.UtcNow.UtcTicks < Volatile.Read(ref _nextProbeAtUtcTicks))
        {
            return _state.GetSnapshot();
        }

        await _probeGate.WaitAsync(cancellationToken);
        try
        {
            var now = _clock.UtcNow;
            if (now.UtcTicks < Volatile.Read(ref _nextProbeAtUtcTicks))
            {
                return _state.GetSnapshot();
            }

            Volatile.Write(
                ref _nextProbeAtUtcTicks,
                now.AddSeconds(_options.RuntimeProbeIntervalSeconds).UtcTicks);
            await ProbeSelectedDatabaseAsync(cancellationToken);
            return _state.GetSnapshot();
        }
        finally
        {
            _probeGate.Release();
        }
    }

    private async Task ProbeSelectedDatabaseAsync(CancellationToken cancellationToken)
    {
        var currentState = _state.GetSnapshot();
        var bootstrapConfirmation = _options.GetSqliteBootstrapConfirmationPath();
        if (currentState.ErrorCode == "sqlite_bootstrap_confirmation_not_consumed" &&
            bootstrapConfirmation != null &&
            File.Exists(bootstrapConfirmation))
        {
            return;
        }

        var sqlitePath = _options.GetSqlitePath();
        if (sqlitePath != null && !File.Exists(sqlitePath))
        {
            SqliteConnection.ClearAllPools();
            _state.Set(
                DurableStorageReadiness.Unavailable,
                errorCode: "sqlite_database_missing");
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.RuntimeProbeTimeoutSeconds));
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync(timeout.Token);
            if (!await context.Database.CanConnectAsync(timeout.Token))
            {
                SetUnavailable();
                return;
            }

            var compatibility = await DurableSchemaCompatibility.InspectAsync(
                context,
                timeout.Token);
            if (!compatibility.IsCurrent)
            {
                _state.Set(
                    DurableStorageReadiness.SchemaIncompatible,
                    compatibility.AppliedSchemaVersion,
                    DurableSchemaCompatibility.ErrorCode(compatibility));
                return;
            }

            _state.Set(
                DurableStorageReadiness.Ready,
                compatibility.CurrentSchemaVersion);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            SetUnavailable();
            _logger.LogWarning(
                "Durable storage runtime probe failed for {StorageProvider} ({ExceptionType})",
                _options.ParseProvider(),
                ex.GetType().Name);
        }
    }

    private void SetUnavailable()
    {
        if (_options.ParseProvider() == DurableStorageProvider.Sqlite)
        {
            SqliteConnection.ClearAllPools();
        }

        _state.Set(DurableStorageReadiness.Unavailable, errorCode: "database_unavailable");
    }

    public void Dispose() => _probeGate.Dispose();
}

public sealed class DurableStorageRuntimeMonitor(
    IDurableStorageRuntimeProbe probe,
    DurableStorageOptions options,
    ILogger<DurableStorageRuntimeMonitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await probe.CheckAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    "Durable storage runtime monitor failed ({ExceptionType})",
                    ex.GetType().Name);
            }

            try
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(options.RuntimeProbeIntervalSeconds),
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }
}
