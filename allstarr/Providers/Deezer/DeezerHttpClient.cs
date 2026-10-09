using System.Net;
using System.Text.Json;

namespace allstarr.Core.Providers.Deezer;

// Adapted from V1ck3s/octo-fiesta, revision 6841a2ea9c227b212157d81acc4aafa5965419b2.
// Modified 2026-10-08; see docs/architecture/reference-projects.md#deezer-implementation-notice.

/// <summary>Spaces Deezer API requests and bounds retries for transient provider throttling.</summary>
public sealed class DeezerHttpClient
{
    private static readonly TimeSpan[] RetryDelays =
        [TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)];
    private static readonly TimeSpan RetryWaitBudget = TimeSpan.FromMilliseconds(3500);
    private readonly HttpClient _http;
    private readonly TimeSpan _requestInterval;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private DateTimeOffset? _lastRequestAt;

    public DeezerHttpClient(
        HttpClient http,
        int minRequestIntervalMs = 200,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentOutOfRangeException.ThrowIfNegative(minRequestIntervalMs);
        _http = http;
        _requestInterval = TimeSpan.FromMilliseconds(minRequestIntervalMs);
        _delay = delay ?? Task.Delay;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public Task<HttpResponseMessage> GetAsync(string url, CancellationToken cancellationToken) =>
        SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken);

    public async Task<HttpResponseMessage> SendAsync(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requestFactory);
        var waited = TimeSpan.Zero;
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = await SendOnceAsync(requestFactory, cancellationToken);
            bool quotaError;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                quotaError = response.StatusCode == HttpStatusCode.OK &&
                    await IsQuotaErrorAsync(response, cancellationToken);
            }
            catch
            {
                response.Dispose();
                throw;
            }

            var transient = quotaError || response.StatusCode == HttpStatusCode.TooManyRequests ||
                (int)response.StatusCode >= 500;
            if (!transient) return response;

            if (attempt >= RetryDelays.Length)
            {
                if (!quotaError) return response;
                response.Dispose();
                throw Throttled();
            }

            var delay = RetryDelays[attempt];
            var retryAfter = response.Headers.RetryAfter;
            var requestedDelay = retryAfter?.Delta ??
                (retryAfter?.Date is { } date ? date - _utcNow() : TimeSpan.Zero);
            if (requestedDelay > delay) delay = requestedDelay;
            response.Dispose();
            if (delay > RetryWaitBudget - waited) throw Throttled();
            await _delay(delay, cancellationToken);
            waited += delay;
        }
    }

    private async Task<HttpResponseMessage> SendOnceAsync(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken)
    {
        HttpRequestMessage? request = null;
        Task<HttpResponseMessage> pendingResponse;
        await _requestGate.WaitAsync(cancellationToken);
        try
        {
            if (_lastRequestAt is { } previous)
            {
                var remaining = _requestInterval - (_utcNow() - previous);
                if (remaining > TimeSpan.Zero) await _delay(remaining, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            request = requestFactory();
            _lastRequestAt = _utcNow();
            pendingResponse = _http.SendAsync(request, cancellationToken);
        }
        catch
        {
            request?.Dispose();
            throw;
        }
        finally
        {
            _requestGate.Release();
        }
        using (request)
            return await pendingResponse;
    }

    private static async Task<bool> IsQuotaErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(cancellationToken));
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("error", out var error) &&
                error.ValueKind == JsonValueKind.Object &&
                error.TryGetProperty("code", out var code) &&
                code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var value) && value == 4;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static HttpRequestException Throttled() =>
        new("Deezer request is temporarily rate limited.", null, HttpStatusCode.TooManyRequests);
}
