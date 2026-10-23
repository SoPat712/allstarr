namespace allstarr.Core.Music;

internal static class PlaybackStartHedge
{
    internal static async Task<T> FirstReadyAsync<T>(
        Func<CancellationToken, CancellationToken, Task<T>> first,
        Func<CancellationToken, CancellationToken, Task<T>> second,
        Func<T, bool> isReady, Action<T> discard, TimeSpan delay, TimeProvider clock,
        CancellationToken cancellationToken)
    {
        using var firstCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var secondCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var firstAbandonment = new CancellationTokenSource();
        using var secondAbandonment = new CancellationTokenSource();
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var primary = first(firstCancellation.Token, firstAbandonment.Token);
        Task<T>? secondary = null;
        var retained = false;
        try
        {
            var timer = Task.Delay(delay, clock, waiting.Token);
            if (await Task.WhenAny(primary, timer).WaitAsync(cancellationToken) == primary)
            {
                var result = await primary;
                retained = true;
                return result;
            }
            cancellationToken.ThrowIfCancellationRequested();
            secondary = second(secondCancellation.Token, secondAbandonment.Token);
            var completed = await Task.WhenAny(primary, secondary).WaitAsync(cancellationToken);
            var other = ReferenceEquals(completed, primary) ? secondary : primary;
            var value = await completed;
            if (!isReady(value))
            {
                var replacement = await other.WaitAsync(cancellationToken);
                discard(value);
                retained = true;
                return replacement;
            }
            var losingCancellation = ReferenceEquals(completed, primary) ? secondCancellation : firstCancellation;
            var abandonment = ReferenceEquals(completed, primary) ? secondAbandonment : firstAbandonment;
            abandonment.Cancel();
            losingCancellation.Cancel();
            DiscardWhenComplete(other, discard);
            retained = true;
            return value;
        }
        finally
        {
            waiting.Cancel();
            if (!retained)
            {
                firstCancellation.Cancel();
                secondCancellation.Cancel();
                DiscardWhenComplete(primary, discard);
                if (secondary != null) DiscardWhenComplete(secondary, discard);
            }
        }
    }

    private static void DiscardWhenComplete<T>(Task<T> pending, Action<T> discard) =>
        _ = pending.ContinueWith(completed =>
        {
            if (completed.IsCompletedSuccessfully) discard(completed.Result);
            else _ = completed.Exception;
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
}
