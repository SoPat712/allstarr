using System.Diagnostics;
using Jint;

namespace allstarr.Core.Extensions;

internal sealed class ExtensionInvocationBudget : IDisposable
{
    private static readonly AsyncLocal<ExtensionInvocationBudget?> Ambient = new();
    private readonly ExtensionInvocationBudget? prior = Ambient.Value;
    private readonly CancellationTokenSource cancellation;
    private readonly TimeProvider clock;
    private readonly long started;
    private readonly CancellationTokenSource timeout;
    private readonly TimeSpan duration;

    public static ExtensionInvocationBudget? Current => Ambient.Value;
    public CancellationToken Token => cancellation.Token;
    public long RemainingMilliseconds => Math.Max(0, (long)(duration - clock.GetElapsedTime(started)).TotalMilliseconds);

    public ExtensionInvocationBudget(TimeSpan duration, CancellationToken token, TimeProvider clock)
    {
        this.duration = duration;
        this.clock = clock;
        started = clock.GetTimestamp();
        timeout = new CancellationTokenSource(duration, clock);
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        Ambient.Value = this;
    }

    public Task DelayAsync(int milliseconds) => Task.Delay(TimeSpan.FromMilliseconds(milliseconds), clock, Token);

    public void Check()
    {
        if (clock.GetElapsedTime(started) >= duration) cancellation.Cancel();
        cancellation.Token.ThrowIfCancellationRequested();
    }

    public void Dispose()
    {
        Ambient.Value = prior;
        cancellation.Dispose();
        timeout.Dispose();
    }

    internal sealed class ExecutionConstraint : Constraint
    {
        private readonly Stopwatch fallback = Stopwatch.StartNew();
        public override void Reset() => fallback.Restart();
        public override void Check()
        {
            if (Current is { } budget) budget.Check();
            else if (fallback.Elapsed > TimeSpan.FromSeconds(12)) throw new TimeoutException("Extension invocation timed out.");
        }
    }
}
