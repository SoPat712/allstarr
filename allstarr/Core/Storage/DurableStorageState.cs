namespace allstarr.Core.Storage;

public enum DurableStorageReadiness
{
    Initializing,
    Ready,
    Unavailable,
    SchemaIncompatible
}

public sealed record DurableStorageSnapshot(
    DurableStorageProvider Provider,
    DurableStorageReadiness Readiness,
    string? SchemaVersion,
    string? ErrorCode,
    DateTimeOffset CheckedAt);

public sealed class DurableStorageState
{
    private readonly object _gate = new();
    private DurableStorageSnapshot _snapshot;

    public DurableStorageState(DurableStorageOptions options)
    {
        _snapshot = new DurableStorageSnapshot(
            options.ParseProvider(),
            DurableStorageReadiness.Initializing,
            null,
            null,
            DateTimeOffset.UtcNow);
    }

    public DurableStorageSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return _snapshot;
        }
    }

    public void Set(
        DurableStorageReadiness readiness,
        string? schemaVersion = null,
        string? errorCode = null)
    {
        lock (_gate)
        {
            _snapshot = _snapshot with
            {
                Readiness = readiness,
                SchemaVersion = schemaVersion,
                ErrorCode = errorCode,
                CheckedAt = DateTimeOffset.UtcNow
            };
        }
    }
}
