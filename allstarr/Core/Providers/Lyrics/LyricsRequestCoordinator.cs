using allstarr.Core.Capabilities;

namespace allstarr.Core.Providers.Lyrics;

internal sealed class LyricsRequestCoordinator(TimeProvider clock, TimeSpan interval, int concurrency = 1,
    TimeSpan? failureCooldown = null)
{
    private readonly SemaphoreSlim _gate = new(concurrency, concurrency);
    private readonly SemaphoreSlim _starts = new(1, 1);
    private readonly object _sync = new();
    private readonly Dictionary<string, (ProviderLyricsResult Value, DateTimeOffset Expires)> _results = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Key, bool Refresh), Task<ProviderOutcome<ProviderLyricsResult>>> _pending = new();
    private DateTimeOffset _nextRequest;
    private DateTimeOffset _retryAt;
    private ProviderError? _failure;

    internal async Task<ProviderOutcome<ProviderLyricsResult>> RunAsync(string key,
        Func<CancellationToken, Task<ProviderOutcome<ProviderLyricsResult>>> read, CancellationToken token, bool refresh = false)
    {
        token.ThrowIfCancellationRequested();
        var pendingKey = (key, refresh);
        TaskCompletionSource<ProviderOutcome<ProviderLyricsResult>>? completion = null;
        Task<ProviderOutcome<ProviderLyricsResult>> pending;
        lock (_sync)
        {
            if (Cached(key, refresh) is { } cached) return cached;
            if (!_pending.TryGetValue(pendingKey, out pending!))
            {
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _pending[pendingKey] = pending = completion.Task;
            }
        }
        if (completion == null)
        {
            try { return await pending.WaitAsync(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            { return ProviderOutcome<ProviderLyricsResult>.Failure(ProviderError.LocalCapacity()); }
        }
        try
        {
            var result = await ExecuteAsync(key, read, token, refresh);
            completion.SetResult(result);
            return result;
        }
        catch (OperationCanceledException exception) { completion.SetCanceled(exception.CancellationToken); throw; }
        catch (Exception exception) { completion.SetException(exception); throw; }
        finally { lock (_sync) _pending.Remove(pendingKey); }
    }

    private async Task<ProviderOutcome<ProviderLyricsResult>> ExecuteAsync(string key,
        Func<CancellationToken, Task<ProviderOutcome<ProviderLyricsResult>>> read, CancellationToken token, bool refresh)
    {
        var admitted = false;
        var started = false;
        try
        {
            using (allstarr.Core.Operations.RequestTiming.Current?.Stage("lock"))
            {
                await _gate.WaitAsync(token);
                admitted = true;
                await _starts.WaitAsync(token);
            }
            Task<ProviderOutcome<ProviderLyricsResult>> request;
            try
            {
                var now = clock.GetUtcNow();
                if (_nextRequest > now) await Task.Delay(_nextRequest - now, clock, token);
                token.ThrowIfCancellationRequested();
                lock (_sync)
                    if (Cached(key, refresh) is { } cached) return cached;
                _nextRequest = clock.GetUtcNow() + interval;
                started = true;
                request = read(token);
            }
            finally { _starts.Release(); }
            var result = await request;
            lock (_sync)
            {
                if (result.IsSuccess)
                {
                    if (refresh) { _failure = null; _retryAt = default; }
                    if (_results.Count >= 256) _results.Clear();
                    var value = result.RequireValue();
                    _results[key] = (value, clock.GetUtcNow().Add(value.Content == null ? TimeSpan.FromMinutes(1) : TimeSpan.FromMinutes(10)));
                }
                else if (result.Error is { Kind: ProviderErrorKind.RateLimited or ProviderErrorKind.AccountNeedsReauthentication or ProviderErrorKind.TransientFailure } error)
                {
                    _failure = error;
                    var minimum = error.Kind == ProviderErrorKind.TransientFailure ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(5);
                    var delay = error.RetryAfter > minimum ? error.RetryAfter.Value : minimum;
                    _retryAt = clock.GetUtcNow() + (failureCooldown ?? (delay > TimeSpan.FromHours(1) ? TimeSpan.FromHours(1) : delay));
                }
            }
            return result;
        }
        catch (OperationCanceledException) when (!started && token.IsCancellationRequested)
        { return ProviderOutcome<ProviderLyricsResult>.Failure(ProviderError.LocalCapacity()); }
        finally { if (admitted) _gate.Release(); }
    }

    private ProviderOutcome<ProviderLyricsResult>? Cached(string key, bool refresh)
    {
        if (refresh) return null;
        var now = clock.GetUtcNow();
        if (_results.TryGetValue(key, out var stored) && stored.Expires > now)
            return ProviderOutcome<ProviderLyricsResult>.Success(stored.Value);
        return _failure != null && _retryAt > now
            ? ProviderOutcome<ProviderLyricsResult>.Failure(failureCooldown.HasValue
                ? ProviderError.LocalCapacity() : new(_failure.Kind, _retryAt - now)) : null;
    }
}
