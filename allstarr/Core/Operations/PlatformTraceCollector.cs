using System.Collections.Concurrent;
using System.Diagnostics;

namespace allstarr.Core.Operations;

public sealed record PlatformTraceSummary(
    string Operation,
    string TraceId,
    string SpanId,
    double DurationMilliseconds,
    bool Failed,
    DateTimeOffset StartedAt);

public sealed class PlatformTraceCollector : IHostedService, IDisposable
{
    private const int Capacity = 1000;
    private readonly ConcurrentQueue<PlatformTraceSummary> _spans = new();
    private readonly ActivityListener _listener;

    public PlatformTraceCollector()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == PlatformDiagnostics.SourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = static (ref ActivityCreationOptions<string> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                _spans.Enqueue(new PlatformTraceSummary(
                    activity.OperationName,
                    activity.TraceId.ToString(),
                    activity.SpanId.ToString(),
                    activity.Duration.TotalMilliseconds,
                    activity.Status == ActivityStatusCode.Error,
                    new DateTimeOffset(activity.StartTimeUtc, TimeSpan.Zero)));
                while (_spans.Count > Capacity && _spans.TryDequeue(out _))
                {
                }
            }
        };
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        ActivitySource.AddActivityListener(_listener);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public IReadOnlyList<PlatformTraceSummary> GetSnapshot() => _spans.ToArray();

    public void Dispose() => _listener.Dispose();
}
