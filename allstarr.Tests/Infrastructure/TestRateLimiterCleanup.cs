using System.Reflection;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;

namespace allstarr.Tests;

// The framework middleware leaves its partitioned limiters' heartbeat timers running.
// Dispose them with the fixture so the timers cannot retain each stopped host's pipeline.
internal sealed class TestRateLimiterCleanup : IStartupFilter, IDisposable
{
    private static readonly Type MiddlewareType = typeof(RateLimiterOptions).Assembly
        .GetType("Microsoft.AspNetCore.RateLimiting.RateLimitingMiddleware", throwOnError: true)!;
    private static readonly FieldInfo[] LimiterFields = new[] { "_globalLimiter", "_endpointLimiter" }
        .Select(name => MiddlewareType.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(MiddlewareType.FullName, name)).ToArray();

    private readonly HashSet<PartitionedRateLimiter<HttpContext>> _limiters = new(ReferenceEqualityComparer.Instance);

    public IReadOnlyCollection<PartitionedRateLimiter<HttpContext>> Limiters => _limiters;

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(pipeline =>
        {
            Capture(pipeline, new HashSet<object>(ReferenceEqualityComparer.Instance));
            if (_limiters.Count == 0)
                throw new InvalidOperationException("The test host rate limiters could not be located for cleanup.");
            return pipeline;
        });
        next(app);
    };

    private void Capture(object? value, HashSet<object> visited)
    {
        if (value == null || !visited.Add(value)) return;
        if (value is Delegate callback)
        {
            foreach (var entry in callback.GetInvocationList()) Capture(entry.Target, visited);
            return;
        }
        if (MiddlewareType.IsInstanceOfType(value))
        {
            foreach (var field in LimiterFields)
                if (field.GetValue(value) is PartitionedRateLimiter<HttpContext> limiter) _limiters.Add(limiter);
            return;
        }
        foreach (var field in value.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            if (typeof(Delegate).IsAssignableFrom(field.FieldType) || field.FieldType.Name.EndsWith("Middleware", StringComparison.Ordinal))
                Capture(field.GetValue(value), visited);
    }

    public void Dispose()
    {
        foreach (var limiter in _limiters) limiter.Dispose();
        _limiters.Clear();
    }
}
