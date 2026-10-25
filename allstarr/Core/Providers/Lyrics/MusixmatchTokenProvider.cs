using System.Text.Json;
using allstarr.Core.Capabilities;

namespace allstarr.Core.Providers.Lyrics;

internal sealed class MusixmatchTokenProvider(HttpClient http, TimeProvider? timeProvider = null, CancellationToken shutdown = default)
{
    private readonly object _gate = new();
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private MusixmatchCredential? _credential;
    private Task<ProviderOutcome<MusixmatchCredential>>? _pending;
    private DateTimeOffset _retryAt;

    internal Task<ProviderOutcome<MusixmatchCredential>> GetAsync(string? rejectedToken, CancellationToken cancellationToken) =>
        GetAsync(rejectedToken, cancellationToken, LyricsCompletionMode.RequestBound);
    internal async Task<ProviderOutcome<MusixmatchCredential>> GetAsync(string? rejectedToken, CancellationToken cancellationToken, LyricsCompletionMode completion)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<ProviderOutcome<MusixmatchCredential>> pending;
        lock (_gate)
        {
            if (rejectedToken != null && _credential?.UserToken == rejectedToken)
                _credential = null;
            if (_credential != null)
                return ProviderOutcome<MusixmatchCredential>.Success(_credential);
            if (_retryAt > _time.GetUtcNow())
                return Backoff();
            if (_pending?.IsCompleted == true) _pending = null;
            pending = _pending ??= FetchAsync();
        }
        return await LyricsAwait.CompleteAsync(pending, completion, cancellationToken);
    }

    internal void Reject(string token)
    {
        lock (_gate)
        {
            if (_credential?.UserToken != token) return;
            _credential = null;
            _retryAt = _time.GetUtcNow().AddMinutes(5);
        }
    }

    private ProviderOutcome<MusixmatchCredential> Backoff() => ProviderOutcome<MusixmatchCredential>.Failure(
        new(ProviderErrorKind.RateLimited, _retryAt - _time.GetUtcNow()));

    private async Task<ProviderOutcome<MusixmatchCredential>> FetchAsync()
    {
        var delay = TimeSpan.FromMinutes(1);
        try
        {
            using var timer = new CancellationTokenSource(MusixmatchMobileApi.RequestTimeout, _time);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(timer.Token, shutdown);
            using var request = MusixmatchMobileApi.CreateRequest(MusixmatchMobileApi.TokenEndpoint);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if ((int)response.StatusCode is 401 or 429)
            {
                delay = TimeSpan.FromMinutes(5);
                if (response.Headers.RetryAfter?.Delta is { } retry && retry > delay)
                    delay = retry > TimeSpan.FromHours(1) ? TimeSpan.FromHours(1) : retry;
            }
            else if (response.IsSuccessStatusCode)
            {
                var body = await MusixmatchLyricsCapabilityAdapter.ReadBoundedBodyAsync(response.Content, timeout.Token);
                if (body != null)
                {
                    using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 8 });
                    var root = document.RootElement;
                    if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object &&
                        message.TryGetProperty("header", out var header) && header.ValueKind == JsonValueKind.Object &&
                        header.TryGetProperty("status_code", out var status) && status.TryGetInt32(out var code))
                    {
                        if (code == 401) delay = TimeSpan.FromMinutes(5);
                        if (code == 200 && message.TryGetProperty("body", out var tokenBody) && tokenBody.ValueKind == JsonValueKind.Object &&
                            tokenBody.TryGetProperty("user_token", out var token) && token.ValueKind == JsonValueKind.String &&
                            MusixmatchCredential.TryParse(token.GetString(), out var credential))
                        {
                            lock (_gate) _credential = credential;
                            return ProviderOutcome<MusixmatchCredential>.Success(credential);
                        }
                    }
                }
            }
        }
        catch
        {
            // Token requests are shared and bounded independently of each caller.
        }
        lock (_gate)
        {
            _retryAt = _time.GetUtcNow().Add(delay);
            return Backoff();
        }
    }
}
