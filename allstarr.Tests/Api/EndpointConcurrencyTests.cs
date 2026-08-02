using allstarr.Services.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace allstarr.Tests;

public sealed class EndpointConcurrencyTests
{
    [Fact]
    public async Task BenchmarkEndpointsAsync_BoundsActiveEndpointPings()
    {
        var synchronizationTimeout = TimeSpan.FromSeconds(2);
        var active = 0;
        var maximum = 0;
        var enteredBarrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new EndpointBenchmarkService(NullLogger<EndpointBenchmarkService>.Instance);
        var endpoints = Enumerable.Range(0, 24).Select(index => $"https://endpoint-{index}.example").ToList();

        var benchmark = service.BenchmarkEndpointsAsync(endpoints, async (_, cancellationToken) =>
        {
            var current = Interlocked.Increment(ref active);
            UpdateMaximum(ref maximum, current);
            if (current == 8)
            {
                enteredBarrier.TrySetResult();
            }

            try { await release.Task.WaitAsync(cancellationToken); }
            finally { Interlocked.Decrement(ref active); }
            return true;
        }, pingCount: 1);

        try
        {
            await enteredBarrier.Task.WaitAsync(synchronizationTimeout);
            Assert.Equal(8, maximum);
        }
        finally
        {
            release.TrySetResult();
        }

        var results = await benchmark;
        Assert.Equal(24, results.Count);
        Assert.Equal(8, maximum);
    }

    [Fact]
    public async Task ProcessInParallelAsync_BoundsActiveEndpointWorkersWithoutDroppingItems()
    {
        var synchronizationTimeout = TimeSpan.FromSeconds(2);
        var active = 0;
        var maximum = 0;
        var enteredBarrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var helper = new RoundRobinFallbackHelper(
            Enumerable.Range(0, 24).Select(index => $"https://endpoint-{index}.example").ToList(),
            NullLogger.Instance,
            "test");

        var processing = helper.ProcessInParallelAsync(
            Enumerable.Range(0, 40).ToList(),
            async (_, item, cancellationToken) =>
            {
                var current = Interlocked.Increment(ref active);
                UpdateMaximum(ref maximum, current);
                if (current == 8)
                {
                    enteredBarrier.TrySetResult();
                }

                try { await release.Task.WaitAsync(cancellationToken); }
                finally { Interlocked.Decrement(ref active); }
                return item;
            });

        try
        {
            await enteredBarrier.Task.WaitAsync(synchronizationTimeout);
            Assert.Equal(8, maximum);
        }
        finally
        {
            release.TrySetResult();
        }

        var results = await processing;
        Assert.Equal(40, results.Count);
        Assert.Equal(Enumerable.Range(0, 40), results.OrderBy(value => value));
        Assert.Equal(8, maximum);
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
