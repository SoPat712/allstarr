namespace allstarr.Services.Common;

internal sealed class PlatformLinkRateLimiter(int maximum, TimeSpan window, TimeProvider clock)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Queue<DateTimeOffset> _requests = new();
    private DateTimeOffset _retryAt;

    public async Task WaitAsync(CancellationToken cancellationToken, TimeSpan? interval = null)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TimeSpan delay;
            await _gate.WaitAsync(cancellationToken);
            try
            {
                var now = clock.GetUtcNow();
                var period = interval ?? window;
                while (_requests.TryPeek(out var oldest) && now - oldest >= period) _requests.Dequeue();
                delay = _retryAt - now;
                if (_requests.Count >= maximum)
                    delay = TimeSpan.FromTicks(Math.Max(delay.Ticks, (_requests.Peek() + period - now).Ticks));
                if (delay <= TimeSpan.Zero)
                {
                    _requests.Enqueue(now);
                    return;
                }
            }
            finally { _gate.Release(); }
            await Task.Delay(delay, clock, cancellationToken);
        }
    }

    public async Task BackoffAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var retryAt = clock.GetUtcNow().AddSeconds(Math.Clamp(delay.TotalSeconds, 1, 3600));
            if (retryAt > _retryAt) _retryAt = retryAt;
        }
        finally { _gate.Release(); }
    }
}
