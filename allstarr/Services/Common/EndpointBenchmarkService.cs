using System.Diagnostics;

namespace allstarr.Services.Common;

/// <summary>
/// Benchmarks API endpoints on startup and maintains performance metrics.
/// Used to prioritize faster endpoints in racing scenarios.
/// </summary>
public class EndpointBenchmarkService
{
    private const int MaximumConcurrentEndpointOperations = 8;
    private readonly ILogger<EndpointBenchmarkService> _logger;
    private readonly Dictionary<string, EndpointMetrics> _metrics = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    public EndpointBenchmarkService(ILogger<EndpointBenchmarkService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Benchmarks a list of endpoints by making test requests.
    /// Returns endpoints sorted by average response time (fastest first).
    /// 
    /// IMPORTANT: The testFunc should implement its own timeout to prevent slow endpoints
    /// from blocking startup. Recommended: 5-10 second timeout per ping.
    /// </summary>
    public async Task<List<string>> BenchmarkEndpointsAsync(
        List<string> endpoints,
        Func<string, CancellationToken, Task<bool>> testFunc,
        int pingCount = 3,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("🏁 Benchmarking {Count} endpoints with {Pings} pings each...", endpoints.Count, pingCount);

        using var endpointGate = new SemaphoreSlim(MaximumConcurrentEndpointOperations);
        var tasks = endpoints.Select(async endpoint =>
        {
            await endpointGate.WaitAsync(cancellationToken);
            try
            {
                var sw = Stopwatch.StartNew();
                var successCount = 0;
                var totalMs = 0L;

                for (int i = 0; i < pingCount; i++)
                {
                    try
                    {
                        var pingStart = Stopwatch.GetTimestamp();
                        var success = await testFunc(endpoint, cancellationToken);
                        var pingMs = Stopwatch.GetElapsedTime(pingStart).TotalMilliseconds;

                        if (success)
                        {
                            successCount++;
                            totalMs += (long)pingMs;
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Benchmark ping failed for {Endpoint}", endpoint);
                    }

                    if (i < pingCount - 1)
                    {
                        await Task.Delay(100, cancellationToken);
                    }
                }

                sw.Stop();

                var avgMs = successCount > 0 ? totalMs / successCount : long.MaxValue;
                var metrics = new EndpointMetrics
                {
                    Endpoint = endpoint,
                    AverageResponseMs = avgMs,
                    SuccessRate = (double)successCount / pingCount,
                    LastBenchmark = DateTime.UtcNow
                };

                await _lock.WaitAsync(cancellationToken);
                try
                {
                    _metrics[endpoint] = metrics;
                }
                finally
                {
                    _lock.Release();
                }

                _logger.LogDebug("  {Endpoint}: {AvgMs}ms avg, {SuccessRate:P0} success rate",
                    endpoint, avgMs, metrics.SuccessRate);

                return metrics;
            }
            finally
            {
                endpointGate.Release();
            }
        }).ToList();

        var results = await Task.WhenAll(tasks);

        // Sort by: success rate first (must be > 0), then by average response time
        var sorted = results
            .Where(m => m.SuccessRate > 0)
            .OrderByDescending(m => m.SuccessRate)
            .ThenBy(m => m.AverageResponseMs)
            .Select(m => m.Endpoint)
            .ToList();

        _logger.LogInformation("✅ Benchmark complete. Fastest: {Fastest} ({Ms}ms)",
            sorted.FirstOrDefault() ?? "none",
            results.Where(m => m.SuccessRate > 0).MinBy(m => m.AverageResponseMs)?.AverageResponseMs ?? 0);

        return sorted;
    }

    /// <summary>
    /// Gets the metrics for a specific endpoint.
    /// </summary>
    public EndpointMetrics? GetMetrics(string endpoint)
    {
        _metrics.TryGetValue(endpoint, out var metrics);
        return metrics;
    }

    /// <summary>
    /// Gets all endpoint metrics sorted by performance.
    /// </summary>
    public List<EndpointMetrics> GetAllMetrics()
    {
        return _metrics.Values
            .OrderByDescending(m => m.SuccessRate)
            .ThenBy(m => m.AverageResponseMs)
            .ToList();
    }
}

public class EndpointMetrics
{
    public string Endpoint { get; set; } = string.Empty;
    public long AverageResponseMs { get; set; }
    public double SuccessRate { get; set; }
    public DateTime LastBenchmark { get; set; }
}
