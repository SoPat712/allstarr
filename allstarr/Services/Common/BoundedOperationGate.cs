namespace allstarr.Services.Common;

/// <summary>
/// A non-queuing concurrency gate for expensive, user-triggered operations.
/// Callers can reject excess work immediately and leases release exactly once.
/// </summary>
public sealed class BoundedOperationGate
{
    private readonly Func<int> capacity;
    private readonly object gate = new();
    private int active;

    public BoundedOperationGate(int capacity) : this(() => capacity)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
    }

    public BoundedOperationGate(Func<int> capacity) => this.capacity = capacity;

    public ValueTask<IDisposable?> TryEnterAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (active >= Math.Max(1, capacity())) return ValueTask.FromResult<IDisposable?>(null);
            active++;
            return ValueTask.FromResult<IDisposable?>(new Lease(() => { lock (gate) active--; }));
        }
    }

    private sealed class Lease(Action release) : IDisposable
    {
        private Action? remaining = release;
        public void Dispose() => Interlocked.Exchange(ref remaining, null)?.Invoke();
    }
}
