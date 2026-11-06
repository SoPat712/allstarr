using allstarr.Core.Capabilities;

namespace allstarr.Core.Providers.Lyrics;

internal sealed class LyricsRequestCoordinator(TimeProvider clock, TimeSpan interval)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, (ProviderLyricsResult Value, DateTimeOffset Expires)> _results = new(StringComparer.Ordinal);
    private DateTimeOffset _nextRequest;
    private DateTimeOffset _retryAt;
    private ProviderError? _failure;

    internal async Task<ProviderOutcome<ProviderLyricsResult>> RunAsync(string key,
        Func<CancellationToken, Task<ProviderOutcome<ProviderLyricsResult>>> read, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            var now = clock.GetUtcNow();
            if (_results.TryGetValue(key, out var stored) && stored.Expires > now)
                return ProviderOutcome<ProviderLyricsResult>.Success(stored.Value);
            if (_failure != null && _retryAt > now)
                return ProviderOutcome<ProviderLyricsResult>.Failure(new(_failure.Kind, _retryAt - now));
            if (_nextRequest > now) await Task.Delay(_nextRequest - now, clock, token);
            _nextRequest = clock.GetUtcNow() + interval;
            var result = await read(token);
            if (result.IsSuccess)
            {
                if (_results.Count >= 256) _results.Clear();
                var value = result.RequireValue();
                _results[key] = (value, clock.GetUtcNow().Add(value.Content == null ? TimeSpan.FromMinutes(1) : TimeSpan.FromMinutes(10)));
            }
            else if (result.Error is { Kind: ProviderErrorKind.RateLimited or ProviderErrorKind.AccountNeedsReauthentication or ProviderErrorKind.TransientFailure } error)
            {
                _failure = error;
                var minimum = error.Kind == ProviderErrorKind.TransientFailure ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(5);
                var delay = error.RetryAfter > minimum ? error.RetryAfter.Value : minimum;
                _retryAt = clock.GetUtcNow() + (delay > TimeSpan.FromHours(1) ? TimeSpan.FromHours(1) : delay);
            }
            return result;
        }
        finally { _gate.Release(); }
    }
}
