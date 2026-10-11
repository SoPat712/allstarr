using System.Diagnostics;
using Jint;

namespace allstarr.Core.Extensions;

internal sealed class ExtensionInvocationBudget : IDisposable
{
    private static readonly AsyncLocal<ExtensionInvocationBudget?> Ambient = new();
    private readonly ExtensionInvocationBudget? prior = Ambient.Value;
    private readonly CancellationTokenSource cancellation;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly TimeSpan duration;

    public static ExtensionInvocationBudget? Current => Ambient.Value;
    public CancellationToken Token => cancellation.Token;
    public long RemainingMilliseconds => Math.Max(0, (long)(duration - clock.Elapsed).TotalMilliseconds);

    public ExtensionInvocationBudget(TimeSpan duration, CancellationToken token)
    {
        this.duration = duration;
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        cancellation.CancelAfter(duration);
        Ambient.Value = this;
    }

    public void Check()
    {
        if (clock.Elapsed >= duration) cancellation.Cancel();
        cancellation.Token.ThrowIfCancellationRequested();
    }

    public void Dispose()
    {
        Ambient.Value = prior;
        cancellation.Dispose();
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
