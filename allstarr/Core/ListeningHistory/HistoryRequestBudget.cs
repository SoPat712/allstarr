using System.Net;

namespace allstarr.Core.Intelligence;

internal sealed class HistoryRequestBudget : IDisposable
{
    private static readonly AsyncLocal<HistoryRequestBudget?> Ambient = new();
    private static readonly HistoryRequestRate SharedRate = new(TimeProvider.System);
    private readonly HistoryRequestBudget? previous = Ambient.Value;
    internal static HistoryRequestBudget? Current => Ambient.Value;
    internal HistoryRequestRate Rate { get; }
    private readonly CancellationTokenSource finished = new();
    internal CancellationToken Token { get; }
    private int calls;
    public int Calls => Volatile.Read(ref calls);

    public HistoryRequestBudget(HistoryRequestRate? rate = null)
    {
        Rate = rate ?? SharedRate;
        Token = finished.Token;
        Ambient.Value = this;
    }

    internal void Count() => Interlocked.Increment(ref calls);
    public void Dispose()
    {
        Ambient.Value = previous;
        finished.Cancel();
        finished.Dispose();
    }
}

internal sealed class HistoryRequestRate(TimeProvider clock)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, DateTimeOffset> retryAfter = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset next;
    internal DateTimeOffset UtcNow => clock.GetUtcNow();

    internal async Task AcquireAsync(string host, CancellationToken token)
    {
        while (true)
        {
            TimeSpan delay;
            await gate.WaitAsync(token);
            try
            {
                var allowed = next > retryAfter.GetValueOrDefault(host) ? next : retryAfter.GetValueOrDefault(host);
                delay = allowed - clock.GetUtcNow();
                if (delay <= TimeSpan.Zero)
                {
                    next = clock.GetUtcNow().AddSeconds(1);
                    return;
                }
            }
            finally { gate.Release(); }
            await Task.Delay(delay, clock, token);
        }
    }

    internal async Task BackoffAsync(string host, TimeSpan delay, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            var until = clock.GetUtcNow() + delay;
            if (until > retryAfter.GetValueOrDefault(host)) retryAfter[host] = until;
        }
        finally { gate.Release(); }
    }
}

internal sealed class HistoryRequestBudgetHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var budget = HistoryRequestBudget.Current;
        if (budget == null) return await base.SendAsync(request, token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, budget.Token);
        token = cancellation.Token;
        var host = request.RequestUri!.Host;
        await budget.Rate.AcquireAsync(host, token);
        budget.Count();
        var response = await base.SendAsync(request, token);
        if (response.StatusCode == HttpStatusCode.TooManyRequests || response.Headers.RetryAfter != null)
        {
            var delay = response.Headers.RetryAfter?.Delta ??
                (response.Headers.RetryAfter?.Date - budget.Rate.UtcNow) ?? TimeSpan.FromMinutes(1);
            await budget.Rate.BackoffAsync(host, delay > TimeSpan.Zero ? delay : TimeSpan.FromSeconds(1), token);
        }
        return response;
    }
}
