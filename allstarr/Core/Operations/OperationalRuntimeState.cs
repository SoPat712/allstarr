namespace allstarr.Core.Operations;

public sealed record OperationalRuntimeSnapshot(
    long MigrationAttempts,
    long MigrationFailures,
    double LastMigrationDurationMilliseconds,
    bool ValkeyConfigured,
    bool ValkeyAvailable,
    long ValkeyDegradationEvents,
    long ValkeyRecoveryEvents,
    long SidecarDegradationEvents,
    long SidecarRecoveryEvents);

public sealed class OperationalRuntimeState
{
    private readonly object _gate = new();
    private long _migrationAttempts;
    private long _migrationFailures;
    private double _lastMigrationDurationMilliseconds;
    private bool _valkeyInitialized;
    private bool _valkeyConfigured;
    private bool _valkeyAvailable;
    private long _valkeyDegradationEvents;
    private long _valkeyRecoveryEvents;
    private long _sidecarDegradationEvents;
    private long _sidecarRecoveryEvents;

    public void RecordMigration(TimeSpan duration, bool succeeded)
    {
        lock (_gate)
        {
            _migrationAttempts++;
            if (!succeeded)
            {
                _migrationFailures++;
            }

            _lastMigrationDurationMilliseconds = duration.TotalMilliseconds;
        }
    }

    public void RecordValkey(bool configured, bool available)
    {
        lock (_gate)
        {
            if (_valkeyInitialized && _valkeyConfigured && _valkeyAvailable != available)
            {
                if (available)
                {
                    _valkeyRecoveryEvents++;
                }
                else
                {
                    _valkeyDegradationEvents++;
                }
            }

            _valkeyInitialized = true;
            _valkeyConfigured = configured;
            _valkeyAvailable = configured && available;
        }
    }

    public void RecordSidecarTransition(bool recovered)
    {
        lock (_gate)
        {
            if (recovered)
            {
                _sidecarRecoveryEvents++;
            }
            else
            {
                _sidecarDegradationEvents++;
            }
        }
    }

    public OperationalRuntimeSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return new OperationalRuntimeSnapshot(
                _migrationAttempts,
                _migrationFailures,
                _lastMigrationDurationMilliseconds,
                _valkeyConfigured,
                _valkeyAvailable,
                _valkeyDegradationEvents,
                _valkeyRecoveryEvents,
                _sidecarDegradationEvents,
                _sidecarRecoveryEvents);
        }
    }
}
