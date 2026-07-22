namespace allstarr.Services.Common;

/// <summary>
/// A non-queuing concurrency gate for expensive, user-triggered operations.
/// Callers can reject excess work immediately and leases release exactly once.
/// </summary>
public sealed class BoundedOperationGate
{
    private readonly SemaphoreSlim semaphore;

    public BoundedOperationGate(int capacity)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        semaphore = new SemaphoreSlim(capacity, capacity);
    }

    public async ValueTask<IDisposable?> TryEnterAsync(CancellationToken cancellationToken = default)
    {
        if (!await semaphore.WaitAsync(TimeSpan.Zero, cancellationToken)) return null;
        return new Lease(semaphore);
    }

    private sealed class Lease(SemaphoreSlim semaphore) : IDisposable
    {
        private SemaphoreSlim? owner = semaphore;

        public void Dispose() => Interlocked.Exchange(ref owner, null)?.Release();
    }
}
