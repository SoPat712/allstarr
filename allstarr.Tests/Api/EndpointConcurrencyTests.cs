using allstarr.Services.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace allstarr.Tests;

public sealed class EndpointConcurrencyTests
{
    [Fact]
    public async Task BenchmarkEndpointsAsync_BoundsActiveEndpointPings()
    {
        var active = 0;
        var maximum = 0;
        var service = new EndpointBenchmarkService(NullLogger<EndpointBenchmarkService>.Instance);
        var endpoints = Enumerable.Range(0, 24).Select(index => $"https://endpoint-{index}.example").ToList();

        var results = await service.BenchmarkEndpointsAsync(endpoints, async (_, cancellationToken) =>
        {
            var current = Interlocked.Increment(ref active);
            UpdateMaximum(ref maximum, current);
            try { await Task.Delay(10, cancellationToken); }
            finally { Interlocked.Decrement(ref active); }
            return true;
        }, pingCount: 1);

        Assert.Equal(24, results.Count);
        Assert.InRange(maximum, 1, 8);
    }

    [Fact]
    public async Task ProcessInParallelAsync_BoundsActiveEndpointWorkersWithoutDroppingItems()
    {
        var active = 0;
        var maximum = 0;
        var helper = new RoundRobinFallbackHelper(
            Enumerable.Range(0, 24).Select(index => $"https://endpoint-{index}.example").ToList(),
            NullLogger.Instance,
            "test");

        var results = await helper.ProcessInParallelAsync(
            Enumerable.Range(0, 40).ToList(),
            async (_, item, cancellationToken) =>
            {
                var current = Interlocked.Increment(ref active);
                UpdateMaximum(ref maximum, current);
                try { await Task.Delay(5, cancellationToken); }
                finally { Interlocked.Decrement(ref active); }
                return item;
            });

        Assert.Equal(40, results.Count);
        Assert.Equal(Enumerable.Range(0, 40), results.OrderBy(value => value));
        Assert.InRange(maximum, 1, 8);
    }

    private static void UpdateMaximum(ref int maximum, int candidate)
    {
        var current = Volatile.Read(ref maximum);
        while (candidate > current)
        {
            var observed = Interlocked.CompareExchange(ref maximum, candidate, current);
            if (observed == current) return;
            current = observed;
        }
    }
}
