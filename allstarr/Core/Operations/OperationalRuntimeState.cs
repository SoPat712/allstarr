namespace allstarr.Core.Operations;

public sealed record OperationalRuntimeSnapshot(
    long MigrationAttempts,
    long MigrationFailures,
    double LastMigrationDurationMilliseconds,
    long SidecarDegradationEvents,
    long SidecarRecoveryEvents);

public sealed class OperationalRuntimeState
{
    private readonly object _gate = new();
    private long _migrationAttempts;
    private long _migrationFailures;
    private double _lastMigrationDurationMilliseconds;
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
                _sidecarDegradationEvents,
                _sidecarRecoveryEvents);
        }
    }
}
